using UnityEngine;
using System;
using System.Runtime.InteropServices;

public class GetFMODSpectrumData : MonoBehaviour
{
    public FMODUnity.EventReference _eventPath;

    public int _windowSize = 512;

    public FMOD.DSP_FFT_WINDOW_TYPE _windowShape =
        FMOD.DSP_FFT_WINDOW_TYPE.HANNING;

    private FMOD.Studio.EventInstance _event;
    private FMOD.ChannelGroup _channelGroup;
    private FMOD.DSP _dsp;

    private bool _dspAttached = false;

    public float[] _samples;

    private void Start()
    {
        _samples = new float[_windowSize];

        _event = FMODUnity.RuntimeManager.CreateInstance(_eventPath);

        CheckResult(
            _event.set3DAttributes(
                FMODUnity.RuntimeUtils.To3DAttributes(transform)),
            "Set 3D attributes"
        );

        CheckResult(_event.start(), "Start event");

        FMOD.RESULT result =
            FMODUnity.RuntimeManager.CoreSystem.createDSPByType(
                FMOD.DSP_TYPE.FFT, out _dsp);

        if (result != FMOD.RESULT.OK)
        {
            Debug.LogError("Failed to create FFT DSP: " + result);
            return;
        }

        CheckResult(
            _dsp.setParameterInt(
                (int)FMOD.DSP_FFT.WINDOW,
                (int)_windowShape),
            "Set FFT window"
        );

        CheckResult(
            _dsp.setParameterInt(
                (int)FMOD.DSP_FFT.WINDOWSIZE,
                _windowSize * 2),
            "Set FFT window size"
        );

        // Apply pending Studio commands before retrieving the channel group.
        FMODUnity.RuntimeManager.StudioSystem.flushCommands();
    }

    private void Update()
    {
        if (!_dsp.hasHandle())
            return;

        // The event's channel group may not be ready immediately.
        if (!_dspAttached)
        {
            FMOD.RESULT result = _event.getChannelGroup(out _channelGroup);

            if (result != FMOD.RESULT.OK)
                return;

            result = _channelGroup.addDSP(
                FMOD.CHANNELCONTROL_DSP_INDEX.HEAD, _dsp);

            if (result != FMOD.RESULT.OK)
            {
                Debug.LogError("Failed to attach FFT DSP: " + result);
                return;
            }

            _dspAttached = true;
            Debug.Log("FFT DSP successfully attached to event.");
        }

        GetSpectrumData();
    }

    private void GetSpectrumData()
    {
        IntPtr data;
        uint length;

        // IMPORTANT: Use SPECTRUMDATA, not the literal index 2.
        FMOD.RESULT result = _dsp.getParameterData(
            (int)FMOD.DSP_FFT.SPECTRUMDATA,
            out data,
            out length
        );

        if (result != FMOD.RESULT.OK || data == IntPtr.Zero)
            return;

        FMOD.DSP_PARAMETER_FFT fft =
            (FMOD.DSP_PARAMETER_FFT)Marshal.PtrToStructure(
                data, typeof(FMOD.DSP_PARAMETER_FFT));

        if (fft.numchannels <= 0 || fft.length <= 0)
            return;

        int bins = Mathf.Min(_windowSize, fft.length);

        // Use FMOD's wrapper to copy spectrum data safely.
        float[] channelSpectrum = new float[fft.length];

        Array.Clear(_samples, 0, _samples.Length);

        for (int c = 0; c < fft.numchannels; c++)
        {
            fft.getSpectrum(c, ref channelSpectrum);

            for (int s = 0; s < bins; s++)
            {
                _samples[s] += channelSpectrum[s] / fft.numchannels;
            }
        }
    }

    private void CheckResult(FMOD.RESULT result, string operation)
    {
        if (result != FMOD.RESULT.OK)
        {
            Debug.LogError(operation + " failed: " + result);
        }
    }

    private void OnDestroy()
    {
        if (_dsp.hasHandle())
        {
            if (_dspAttached && _channelGroup.hasHandle())
                _channelGroup.removeDSP(_dsp);

            _dsp.release();
        }

        if (_event.isValid())
        {
            _event.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            _event.release();
        }
    }
}
