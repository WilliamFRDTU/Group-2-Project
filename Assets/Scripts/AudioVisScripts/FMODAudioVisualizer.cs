using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FMOD;
using FMOD.Studio;
using FMODUnity;

public class FMODAudioVisualizer : MonoBehaviour
{
    [Header("FMOD Event")]
    [SerializeField] private EventReference eventPath;
    [SerializeField] private bool playOnAwake = true;

    [Header("FFT Settings")]
    [Tooltip("Number of spectrum bins (power of two, 64-8192). FFT size is twice this. " +
             "4096 gives ~6 Hz resolution at 48 kHz, which the low bands need.")]
    [SerializeField] private int windowSize = 4096;
    [SerializeField] private DSP_FFT_WINDOW_TYPE windowShape =
        DSP_FFT_WINDOW_TYPE.HANNING;

    [Header("Frequency Axis (logarithmic)")]
    [SerializeField] private float minFrequency = 20f;
    [SerializeField] private float maxFrequency = 20000f;
    [Tooltip("31 is roughly 1/3 octave across 20 Hz - 20 kHz.")]
    [SerializeField] private int numberOfBands = 31;

    [Header("Level Scaling (dB)")]
    [Tooltip("Level that maps to zero bar height.")]
    [SerializeField] private float minDb = -90f;
    [Tooltip("Level that maps to full bar height.")]
    [SerializeField] private float maxDb = 0f;
    [Tooltip("Calibration offset added to every band. Raise it if the bars sit too low.")]
    [SerializeField] private float dbOffset = 0f;

    [Header("Tilt Compensation")]
    [Tooltip("dB boost per octave above the reference frequency (cut below it). " +
             "4.5 is the common analyzer setting, 3 is pink-noise flat.")]
    [SerializeField] private float tiltDbPerOctave = 4.5f;
    [SerializeField] private float tiltReferenceHz = 1000f;

    [Header("Visualizer")]
    [SerializeField] private GameObject meterPrefab;
    [Tooltip("Bar height at 0..1 normalized level is startScale + level * scaleMultiplier.")]
    [SerializeField] private float scaleMultiplier = 10f;
    [SerializeField] private float startScale = 1f;
    [SerializeField] private float spaceBetweenMeters = 0.2f;

    [Header("Smoothing (applied to the 0..1 normalized dB level)")]
    [SerializeField] private float bufferStartSpeed = 0.005f;
    [SerializeField] private float bufferAccelRate = 1.2f;

    [Header("Debug")]
    [Tooltip("Logs the loudest band in dB once a second so you can set Db Offset.")]
    [SerializeField] private bool showDebugLogs = false;


    // FMOD
    private EventInstance audioEvent;
    private ChannelGroup channelGroup;
    private DSP fftDSP;
    private bool dspAttached;
    private bool eventStarted;
    private int sampleRate = 48000;

    // FFT data
    private float[] samples;
    private int spectrumLength;
    private float binWidth;

    // Log band layout (precomputed)
    private float[] bandCenterHz;
    private float[] bandTiltDb;
    private int[] bandFirstBin;
    private int[] bandLastBin;
    private float[] bandCenterBin;

    // Band levels
    private float[] frequencyBands;   // normalized 0..1 (after dB scaling)
    private float[] bandDb;           // dB (after tilt + offset)
    private float[] bandBuffer;
    private float[] bufferDecrease;

    // Visual objects
    private GameObject[] meters;
    private Vector3[] initialScales;
    private float[] initialPositionsY;

    private bool initialized;
    private float debugTimer;

    // Optional public access to band data
    public float[] FrequencyBands => frequencyBands;
    public float[] BandBuffer => bandBuffer;
    public float[] BandDb => bandDb;
    public float[] BandCenterFrequencies => bandCenterHz;

    private void Start()
    {
        if (meterPrefab == null)
        {
            UnityEngine.Debug.LogError("Assign a Meter Prefab in the Inspector.", this);
            enabled = false;
            return;
        }

        // FMOD's FFT window must be a power of two between 128 and 16384,
        // and we ask FMOD for windowSize * 2.
        int clamped = Mathf.Clamp(Mathf.ClosestPowerOfTwo(windowSize), 64, 8192);
        if (clamped != windowSize)
        {
            UnityEngine.Debug.LogWarning(
                "FFT window size adjusted from " + windowSize + " to " + clamped + ".", this);
            windowSize = clamped;
        }

        numberOfBands = Mathf.Max(1, numberOfBands);

        samples = new float[windowSize];
        frequencyBands = new float[numberOfBands];
        bandDb = new float[numberOfBands];
        bandBuffer = new float[numberOfBands];
        bufferDecrease = new float[numberOfBands];

        bandCenterHz = new float[numberOfBands];
        bandTiltDb = new float[numberOfBands];
        bandFirstBin = new int[numberOfBands];
        bandLastBin = new int[numberOfBands];
        bandCenterBin = new float[numberOfBands];

        ReadSampleRate();
        CalculateBandLayout();
        SpawnMeters();

        InitializeFMOD();

        initialized = true;
    }

    private void ReadSampleRate()
    {
        FMOD.SPEAKERMODE speakerMode;
        int rawSpeakers;
        int rate;

        FMOD.RESULT result = RuntimeManager.CoreSystem.getSoftwareFormat(
            out rate, out speakerMode, out rawSpeakers);

        if (result == FMOD.RESULT.OK && rate > 0)
            sampleRate = rate;
        else
            UnityEngine.Debug.LogWarning(
                "Could not read FMOD sample rate, assuming " + sampleRate + " Hz.", this);
    }

    private void InitializeFMOD()
    {
        audioEvent = RuntimeManager.CreateInstance(eventPath);

        if (!audioEvent.isValid())
        {
            UnityEngine.Debug.LogError("Failed to create FMOD event.", this);
            return;
        }

        FMOD.RESULT result = audioEvent.set3DAttributes(
            RuntimeUtils.To3DAttributes(transform));

        if (result != FMOD.RESULT.OK)
        {
            UnityEngine.Debug.LogError("Failed to set event attributes: " + result);
            return;
        }

        result = RuntimeManager.CoreSystem.createDSPByType(
            DSP_TYPE.FFT, out fftDSP);

        if (result != FMOD.RESULT.OK)
        {
            UnityEngine.Debug.LogError("Failed to create FFT DSP: " + result);
            return;
        }

        result = fftDSP.setParameterInt(
            (int)DSP_FFT.WINDOW, (int)windowShape);

        if (result != FMOD.RESULT.OK)
            UnityEngine.Debug.LogError("Failed to set FFT window: " + result);

        result = fftDSP.setParameterInt(
            (int)DSP_FFT.WINDOWSIZE, windowSize * 2);

        if (result != FMOD.RESULT.OK)
            UnityEngine.Debug.LogError("Failed to set FFT size: " + result);

        if (playOnAwake)
            PlayFMODEvent();
    }

    /// <summary>
    /// Builds logarithmically spaced bands between minFrequency and maxFrequency
    /// and works out, for each band, which FFT bins it covers (in real Hz, not
    /// as a fraction of the bin count) plus its tilt-compensation gain.
    /// </summary>
    private void CalculateBandLayout()
    {
        float nyquist = sampleRate * 0.5f;
        float minF = Mathf.Max(1f, minFrequency);
        float maxF = Mathf.Min(maxFrequency, nyquist);

        if (maxF <= minF)
        {
            UnityEngine.Debug.LogWarning(
                "Invalid frequency range, falling back to 20 Hz - 20 kHz.", this);
            minF = 20f;
            maxF = Mathf.Min(20000f, nyquist);
        }

        // FMOD returns windowSize bins covering 0 Hz to Nyquist.
        binWidth = sampleRate / (windowSize * 2f);

        float ratio = maxF / minF;
        float refHz = Mathf.Max(1f, tiltReferenceHz);

        for (int i = 0; i < numberOfBands; i++)
        {
            float lo = minF * Mathf.Pow(ratio, (float)i / numberOfBands);
            float hi = minF * Mathf.Pow(ratio, (float)(i + 1) / numberOfBands);
            float center = Mathf.Sqrt(lo * hi); // geometric centre

            bandCenterHz[i] = center;
            bandTiltDb[i] = tiltDbPerOctave * Mathf.Log(center / refHz, 2f);

            // Skip bin 0 (DC).
            bandFirstBin[i] = Mathf.Max(1, Mathf.CeilToInt(lo / binWidth));
            bandLastBin[i] = Mathf.Min(windowSize - 1, Mathf.FloorToInt(hi / binWidth));
            bandCenterBin[i] = center / binWidth;
        }
    }

    private void SpawnMeters()
    {
        meters = new GameObject[numberOfBands];
        initialScales = new Vector3[numberOfBands];
        initialPositionsY = new float[numberOfBands];

        for (int i = 0; i < numberOfBands; i++)
        {
            Vector3 position = transform.position +
                transform.right * (i * spaceBetweenMeters);

            meters[i] = Instantiate(
                meterPrefab,
                position,
                transform.rotation,
                transform
            );

            initialScales[i] = meters[i].transform.localScale;
            initialPositionsY[i] = meters[i].transform.localPosition.y;

            // Generate the HSB spectrum, starting at red.
            float hue = (float)i / numberOfBands;
            Color bandColor = Color.HSVToRGB(hue, 1f, 1f);

            // Apply the color to all renderers on the meter and its children.
            Renderer[] renderers =
                meters[i].GetComponentsInChildren<Renderer>(true);

            foreach (Renderer rend in renderers)
            {
                rend.material.color = bandColor;
            }
        }
    }


    private void Update()
    {
        if (!initialized || !fftDSP.hasHandle())
            return;

        AttachDSPIfReady();

        if (!dspAttached)
            return;

        ReadSpectrum();
        CalculateFrequencyBands();
        UpdateMeters();

        if (showDebugLogs)
            LogPeakBand();
    }

    private void AttachDSPIfReady()
    {
        if (dspAttached)
            return;

        FMOD.RESULT result = audioEvent.getChannelGroup(
            out channelGroup);

        if (result != FMOD.RESULT.OK || !channelGroup.hasHandle())
            return;

        result = channelGroup.addDSP(
            CHANNELCONTROL_DSP_INDEX.HEAD, fftDSP);

        if (result == FMOD.RESULT.OK)
        {
            dspAttached = true;

            if (showDebugLogs)
                UnityEngine.Debug.Log("FFT DSP attached successfully.", this);
        }
        else if (showDebugLogs)
        {
            UnityEngine.Debug.LogWarning("Waiting for event channel group: " + result);
        }
    }

    private void ReadSpectrum()
    {
        IntPtr data;
        uint length;

        FMOD.RESULT result = fftDSP.getParameterData(
            (int)DSP_FFT.SPECTRUMDATA,
            out data,
            out length);

        if (result != FMOD.RESULT.OK || data == IntPtr.Zero)
            return;

        DSP_PARAMETER_FFT fftData =
            Marshal.PtrToStructure<DSP_PARAMETER_FFT>(data);

        if (fftData.numchannels <= 0 ||
            fftData.length <= 0 ||
            fftData.spectrum == null)
            return;

        spectrumLength = Mathf.Min(windowSize, fftData.length);

        for (int i = 0; i < spectrumLength; i++)
        {
            float total = 0f;

            for (int channel = 0; channel < fftData.numchannels; channel++)
                total += fftData.spectrum[channel][i];

            samples[i] = total / fftData.numchannels;
        }

        for (int i = spectrumLength; i < samples.Length; i++)
            samples[i] = 0f;
    }

    private float BinPower(int bin)
    {
        float a = samples[bin];
        return a * a;
    }

    /// <summary>
    /// For every log band: average the power of the FFT bins inside it,
    /// convert to dB, apply tilt compensation and calibration offset,
    /// then map the fixed dB range (minDb..maxDb) to 0..1.
    /// </summary>
    private void CalculateFrequencyBands()
    {
        if (spectrumLength <= 0)
            return;

        int lastValid = spectrumLength - 1;

        for (int band = 0; band < numberOfBands; band++)
        {
            int first = bandFirstBin[band];
            int last = Mathf.Min(bandLastBin[band], lastValid);
            float power;

            if (last >= first)
            {
                // Band covers one or more whole bins: average their power.
                float sum = 0f;
                for (int bin = first; bin <= last; bin++)
                    sum += BinPower(bin);

                power = sum / (last - first + 1);
            }
            else
            {
                // Band is narrower than a bin (low end): interpolate between
                // the two neighbouring bins at the band's centre frequency.
                float pos = bandCenterBin[band];
                int i0 = Mathf.Clamp(Mathf.FloorToInt(pos), 0, lastValid);
                int i1 = Mathf.Min(i0 + 1, lastValid);
                float t = Mathf.Clamp01(pos - i0);

                power = Mathf.Lerp(BinPower(i0), BinPower(i1), t);
            }

            // 10*log10(power) == 20*log10(amplitude). The tiny floor avoids -infinity.
            float db = 10f * Mathf.Log10(power + 1e-12f);
            db += bandTiltDb[band] + dbOffset;

            bandDb[band] = db;
            frequencyBands[band] = Mathf.InverseLerp(minDb, maxDb, db); // clamped 0..1
        }
    }

    private void UpdateMeters()
    {
        for (int i = 0; i < numberOfBands; i++)
        {
            if (frequencyBands[i] > bandBuffer[i])
            {
                bandBuffer[i] = frequencyBands[i];
                bufferDecrease[i] = bufferStartSpeed;
            }
            else
            {
                bandBuffer[i] -= bufferDecrease[i] * Time.deltaTime * 60f;
                bufferDecrease[i] *= Mathf.Pow(
                    bufferAccelRate, Time.deltaTime * 60f);

                bandBuffer[i] = Mathf.Max(0f, bandBuffer[i]);
            }

            float height = startScale +
                bandBuffer[i] * scaleMultiplier;

            Transform meterTransform = meters[i].transform;

            Vector3 scale = initialScales[i];
            scale.y = height;
            meterTransform.localScale = scale;

            Vector3 position = meterTransform.localPosition;
            position.y = initialPositionsY[i] +
                (height - initialScales[i].y) * 0.5f;

            meterTransform.localPosition = position;
        }
    }

    private void LogPeakBand()
    {
        debugTimer += Time.deltaTime;
        if (debugTimer < 1f)
            return;

        debugTimer = 0f;

        int peak = 0;
        for (int i = 1; i < numberOfBands; i++)
            if (bandDb[i] > bandDb[peak])
                peak = i;

        UnityEngine.Debug.Log(
            "Loudest band: " + bandCenterHz[peak].ToString("F0") + " Hz at " +
            bandDb[peak].ToString("F1") + " dB (range " + minDb + " to " + maxDb + ")",
            this);
    }

    public void PlayFMODEvent()
    {
        if (!audioEvent.isValid())
            return;

        audioEvent.start();
        eventStarted = true;
    }

    public void StopFMODEvent()
    {
        if (!audioEvent.isValid())
            return;

        audioEvent.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
        eventStarted = false;
    }

    public void PauseFMODEvent()
    {
        if (!audioEvent.isValid())
            return;

        bool paused;
        audioEvent.getPaused(out paused);
        audioEvent.setPaused(!paused);
    }

    private void OnDestroy()
    {
        if (fftDSP.hasHandle())
        {
            if (dspAttached && channelGroup.hasHandle())
                channelGroup.removeDSP(fftDSP);

            fftDSP.release();
        }

        if (audioEvent.isValid())
        {
            if (eventStarted)
                audioEvent.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);

            audioEvent.release();
        }
    }
}