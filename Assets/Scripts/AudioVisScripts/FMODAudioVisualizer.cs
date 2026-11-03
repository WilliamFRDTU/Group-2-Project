using UnityEngine;
using System;
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
    [SerializeField] private int windowSize = 4096;
    [SerializeField] private DSP_FFT_WINDOW_TYPE windowShape = DSP_FFT_WINDOW_TYPE.HANNING;

    [Header("Frequency Axis (logarithmic)")]
    [SerializeField] private float minFrequency = 20f;
    [SerializeField] private float maxFrequency = 20000f;
    [SerializeField] private int numberOfBands = 31;

    [Header("Level Scaling (dB)")]
    [SerializeField] private float minDb = -90f;
    [SerializeField] private float maxDb = 0f;
    [SerializeField] private float dbOffset = 0f;

    [Header("Tilt Compensation")]
    [SerializeField] private float tiltDbPerOctave = 4.5f;
    [SerializeField] private float tiltReferenceHz = 1000f;

    [Header("Visualizer")]
    [SerializeField] private GameObject meterPrefab;
    [SerializeField] private float scaleMultiplier = 10f;
    [SerializeField] private float startScale = 1f;
    [SerializeField] private float spaceBetweenMeters = 0.2f;

    [Header("Smoothing")]
    [SerializeField] private float bufferStartSpeed = 0.005f;
    [SerializeField] private float bufferAccelRate = 1.2f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    private EventInstance audioEvent;
    private ChannelGroup channelGroup;
    private DSP fftDSP;
    private bool dspAttached;
    private bool eventStarted;
    private int sampleRate = 48000;

    private float[] samples;
    private int spectrumLength;
    private float binWidth;
    private float[] bandCenterHz;
    private float[] bandTiltDb;
    private int[] bandFirstBin;
    private int[] bandLastBin;
    private float[] bandCenterBin;
    private float[] frequencyBands; //Contains normalized values (0-1) for each frequency band
    private float[] bandDb;         //Contains dB values for each frequency band
    private float[] bandBuffer;     //Contains smoothed values (0-1) for each frequency band
    private float[] bufferDecrease; //Contains the current decrease speed for each frequency band
    private GameObject[] meters;
    private Vector3[] initialScales;
    private float[] initialPositionsY;
    private bool initialized;
    private float debugTimer;

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

        windowSize = Mathf.Clamp(Mathf.ClosestPowerOfTwo(windowSize), 64, 8192);
        numberOfBands = Mathf.Max(1, numberOfBands);
        minDb = Mathf.Min(minDb, maxDb - 0.001f);
        maxDb = Mathf.Max(maxDb, minDb + 0.001f);

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
            UnityEngine.Debug.LogWarning("Could not read FMOD sample rate, assuming " + sampleRate + " Hz.", this);
    }

    private void InitializeFMOD()
    {
        audioEvent = RuntimeManager.CreateInstance(eventPath);
        if (!audioEvent.isValid())
        {
            UnityEngine.Debug.LogError("Failed to create FMOD event.", this);
            return;
        }

        FMOD.RESULT result = audioEvent.set3DAttributes(RuntimeUtils.To3DAttributes(transform));
        if (result != FMOD.RESULT.OK)
        {
            UnityEngine.Debug.LogError("Failed to set event attributes: " + result, this);
            return;
        }

        result = RuntimeManager.CoreSystem.createDSPByType(DSP_TYPE.FFT, out fftDSP);
        if (result != FMOD.RESULT.OK)
        {
            UnityEngine.Debug.LogError("Failed to create FFT DSP: " + result, this);
            return;
        }

        result = fftDSP.setParameterInt((int)DSP_FFT.WINDOW, (int)windowShape);
        if (result != FMOD.RESULT.OK)
            UnityEngine.Debug.LogError("Failed to set FFT window: " + result, this);

        result = fftDSP.setParameterInt((int)DSP_FFT.WINDOWSIZE, windowSize * 2);
        if (result != FMOD.RESULT.OK)
            UnityEngine.Debug.LogError("Failed to set FFT size: " + result, this);

        if (playOnAwake)
            PlayFMODEvent();
    }

    private void CalculateBandLayout()
    {
        float nyquist = sampleRate * 0.5f;
        float minimum = Mathf.Max(1f, minFrequency);
        float maximum = Mathf.Min(maxFrequency, nyquist);
        if (maximum <= minimum)
        {
            minimum = 20f;
            maximum = Mathf.Min(20000f, nyquist);
        }

        binWidth = sampleRate / (windowSize * 2f);
        float ratio = maximum / minimum;
        float referenceFrequency = Mathf.Max(1f, tiltReferenceHz);

        for (int i = 0; i < numberOfBands; i++)
        {
            float low = minimum * Mathf.Pow(ratio, (float)i / numberOfBands);
            float high = minimum * Mathf.Pow(ratio, (float)(i + 1) / numberOfBands);
            float center = Mathf.Sqrt(low * high);
            bandCenterHz[i] = center;
            bandTiltDb[i] = tiltDbPerOctave * Mathf.Log(center / referenceFrequency, 2f);
            bandFirstBin[i] = Mathf.Max(1, Mathf.CeilToInt(low / binWidth));
            bandLastBin[i] = Mathf.Min(windowSize - 1, Mathf.FloorToInt(high / binWidth));
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
            Vector3 position = transform.position + transform.right * (i * spaceBetweenMeters);
            meters[i] = Instantiate(meterPrefab, position, transform.rotation, transform);
            initialScales[i] = meters[i].transform.localScale;
            initialPositionsY[i] = meters[i].transform.localPosition.y;
            Color bandColor = Color.HSVToRGB((float)i / numberOfBands, 1f, 1f);
            Renderer[] renderers = meters[i].GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
                renderer.material.color = bandColor;
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
        FMOD.RESULT result = audioEvent.getChannelGroup(out channelGroup);
        if (result != FMOD.RESULT.OK || !channelGroup.hasHandle())
            return;
        result = channelGroup.addDSP(CHANNELCONTROL_DSP_INDEX.HEAD, fftDSP);
        if (result == FMOD.RESULT.OK)
        {
            dspAttached = true;
            if (showDebugLogs)
                UnityEngine.Debug.Log("FFT DSP attached successfully.", this);
        }
        else if (showDebugLogs)
        {
            UnityEngine.Debug.LogWarning("Waiting for event channel group: " + result, this);
        }
    }

    private void ReadSpectrum()
    {
        IntPtr data;
        uint length;
        FMOD.RESULT result = fftDSP.getParameterData((int)DSP_FFT.SPECTRUMDATA, out data, out length);
        if (result != FMOD.RESULT.OK || data == IntPtr.Zero)
            return;

        DSP_PARAMETER_FFT fftData = Marshal.PtrToStructure<DSP_PARAMETER_FFT>(data);
        if (fftData.numchannels <= 0 || fftData.length <= 0 || fftData.spectrum == null)
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
        float amplitude = samples[bin];
        return amplitude * amplitude;
    }

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
                float sum = 0f;
                for (int bin = first; bin <= last; bin++)
                    sum += BinPower(bin);
                power = sum / (last - first + 1);
            }
            else
            {
                float position = bandCenterBin[band];
                int firstBin = Mathf.Clamp(Mathf.FloorToInt(position), 0, lastValid);
                int secondBin = Mathf.Min(firstBin + 1, lastValid);
                power = Mathf.Lerp(BinPower(firstBin), BinPower(secondBin), Mathf.Clamp01(position - firstBin));
            }

            float db = 10f * Mathf.Log10(power + 1e-12f) + bandTiltDb[band] + dbOffset;
            bandDb[band] = db;
            frequencyBands[band] = Mathf.InverseLerp(minDb, maxDb, db);
        }
    }

    private void UpdateMeters()
    {
        for (int i = 0; i < numberOfBands; i++)
        {
            if (eventStarted && frequencyBands[i] > bandBuffer[i])
            {
                bandBuffer[i] = frequencyBands[i];
                bufferDecrease[i] = bufferStartSpeed;
            }
            else
            {
                bandBuffer[i] -= bufferDecrease[i] * Time.deltaTime * 60f;
                bufferDecrease[i] *= Mathf.Pow(bufferAccelRate, Time.deltaTime * 60f);
                bandBuffer[i] = Mathf.Max(0f, bandBuffer[i]);
            }

            float height = startScale + bandBuffer[i] * scaleMultiplier;
            Transform meterTransform = meters[i].transform;
            Vector3 scale = initialScales[i];
            scale.y = height;
            meterTransform.localScale = scale;
            Vector3 position = meterTransform.localPosition;
            position.y = initialPositionsY[i] + (height - initialScales[i].y) * 0.5f;
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
        {
            if (bandDb[i] > bandDb[peak])
                peak = i;
        }
        UnityEngine.Debug.Log("Loudest band: " + bandCenterHz[peak].ToString("F0") + " Hz at " + bandDb[peak].ToString("F1") + " dB", this);
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
