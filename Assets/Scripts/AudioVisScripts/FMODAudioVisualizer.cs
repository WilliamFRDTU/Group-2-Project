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
    [SerializeField] private int windowSize = 512;
    [SerializeField] private DSP_FFT_WINDOW_TYPE windowShape =
        DSP_FFT_WINDOW_TYPE.HANNING;

    [Header("Visualizer")]
    [SerializeField] private GameObject meterPrefab;
    [SerializeField] private int numberOfBands = 7;
    [SerializeField] private float meterIntensity = 10f;
    [SerializeField] private float scaleMultiplier = 10f;
    [SerializeField] private float startScale = 1f;
    [SerializeField] private float spaceBetweenMeters = 0.5f;

    [Header("Smoothing")]
    [SerializeField] private float bufferStartSpeed = 0.005f;
    [SerializeField] private float bufferAccelRate = 1.2f;

    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;

    // FMOD
    private EventInstance audioEvent;
    private ChannelGroup channelGroup;
    private DSP fftDSP;
    private bool dspAttached;
    private bool eventStarted;

    // FFT data
    private float[] samples;
    private int spectrumLength;

    // Frequency bands
    private float[] frequencyBands;
    private float[] bandBuffer;
    private float[] bufferDecrease;
    private int[] samplesPerBand;

    // Visual objects
    private GameObject[] meters;
    private Vector3[] initialScales;
    private float[] initialPositionsY;

    private bool initialized;

    // Optional public access to frequency values
    public float[] FrequencyBands => frequencyBands;
    public float[] BandBuffer => bandBuffer;

    private void Start()
    {
        if (meterPrefab == null)
        {
            UnityEngine.Debug.LogError("Assign a Meter Prefab in the Inspector.", this);
            enabled = false;
            return;
        }

        if (windowSize < 64)
        {
            UnityEngine.Debug.LogError("FFT window size must be at least 64.", this);
            enabled = false;
            return;
        }

        numberOfBands = Mathf.Max(1, numberOfBands);

        samples = new float[windowSize];
        frequencyBands = new float[numberOfBands];
        bandBuffer = new float[numberOfBands];
        bufferDecrease = new float[numberOfBands];
        samplesPerBand = new int[numberOfBands];

        CalculateBandSizes();
        SpawnMeters();

        InitializeFMOD();

        initialized = true;
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

    private void CalculateBandSizes()
    {
        // Divide the available positive-frequency bins into
        // approximately logarithmic bands.
        int totalBins = windowSize;
        int previousBin = 0;

        for (int i = 0; i < numberOfBands; i++)
        {
            float startFraction = (float)i / numberOfBands;
            float endFraction = (float)(i + 1) / numberOfBands;

            int startBin = Mathf.RoundToInt(
                Mathf.Pow(totalBins, startFraction));

            int endBin = Mathf.RoundToInt(
                Mathf.Pow(totalBins, endFraction));

            startBin = Mathf.Clamp(startBin, previousBin, totalBins - 1);
            endBin = Mathf.Clamp(endBin, startBin + 1, totalBins);

            samplesPerBand[i] = endBin - startBin;
            previousBin = endBin;
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
                meterPrefab, position, transform.rotation, transform);

            initialScales[i] = meters[i].transform.localScale;
            initialPositionsY[i] = meters[i].transform.localPosition.y;
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

    private void CalculateFrequencyBands()
    {
        int binsPerBand = Mathf.Max(1, windowSize / numberOfBands);
        int startBin = 0;

        for (int band = 0; band < numberOfBands; band++)
        {
            int endBin = band == numberOfBands - 1
                ? spectrumLength
                : Mathf.Min(spectrumLength, startBin + binsPerBand);

            float sum = 0f;
            int count = 0;

            for (int bin = startBin; bin < endBin; bin++)
            {
                // Weight higher bins slightly more.
                sum += samples[bin] * (bin + 1);
                count++;
            }

            frequencyBands[band] = count > 0
                ? (sum / count) * meterIntensity
                : 0f;

            startBin = endBin;
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

