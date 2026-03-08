using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace BassRouter.Audio;

/// <summary>
/// Captures system audio via WASAPI loopback and routes it to a primary output and a lowpassed subwoofer output.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WasapiLoopbackCapture? Capture;
    private WasapiOut? HeadphoneOut;
    private WasapiOut? SubOut;
    private BufferedWaveProvider? HeadphoneBuffer;
    private BufferedWaveProvider? SubBuffer;

    private BiQuadFilter? LeftFilter;
    private BiQuadFilter? RightFilter;

    private DelayBuffer HeadphoneDelayL = new(0);
    private DelayBuffer HeadphoneDelayR = new(0);
    private DelayBuffer SubDelayL = new(0);
    private DelayBuffer SubDelayR = new(0);

    private readonly object _Lock = new();
    private bool IsDisposed;
    private bool _IsRunning;

    // Config
    private MMDevice? CaptureDevice;
    private MMDevice? HeadphoneDevice;
    private MMDevice? SubDevice;
    private int HeadphoneDelayMs;
    private int SubDelayMs;
    private float LowPassFrequency = 120f;

    public bool IsRunning
    {
        get { lock (_Lock) return _IsRunning; }
    }

    /// <summary>
    /// Raised when the engine stops unexpectedly (e.g. device disconnected).
    /// The string argument contains the error message.
    /// </summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Raised when the engine stops (either normally or due to error).
    /// </summary>
    public event EventHandler? Stopped;

    public void SetHeadphoneDevice(MMDevice? device) { lock (_Lock) HeadphoneDevice = device; }
    public void SetSubDevice(MMDevice? device) { lock (_Lock) SubDevice = device; }

    public void SetHeadphoneDelayMs(int ms)
    {
        lock (_Lock)
        {
            HeadphoneDelayMs = Math.Clamp(ms, 0, 5000);
            if (_IsRunning && Capture != null)
            {
                int channels = Capture.WaveFormat.Channels;
                int samples = Capture.WaveFormat.SampleRate * HeadphoneDelayMs / 1000;
                HeadphoneDelayL.Resize(samples);
                HeadphoneDelayR.Resize(samples);
            }
        }
    }

    public void SetSubDelayMs(int ms)
    {
        lock (_Lock)
        {
            SubDelayMs = Math.Clamp(ms, 0, 5000);
            if (_IsRunning && Capture != null)
            {
                int samples = Capture.WaveFormat.SampleRate * SubDelayMs / 1000;
                SubDelayL.Resize(samples);
                SubDelayR.Resize(samples);
            }
        }
    }

    public void SetLowPassFrequency(float frequency)
    {
        lock (_Lock)
        {
            LowPassFrequency = Math.Clamp(frequency, 10f, 300f);
            if (_IsRunning && Capture != null)
            {
                int sr = Capture.WaveFormat.SampleRate;
                LeftFilter = BiQuadFilter.LowPassFilter(sr, LowPassFrequency, 0.7f);
                RightFilter = BiQuadFilter.LowPassFilter(sr, LowPassFrequency, 0.7f);
            }
        }
    }

    /// <summary>
    /// Sets the device volume (0.0 to 1.0) for the headphone output device.
    /// This controls the system endpoint volume, not the stream volume.
    /// </summary>
    public void SetHeadphoneVolume(float volume)
    {
        lock (_Lock)
        {
            try
            {
                if (HeadphoneDevice?.State == DeviceState.Active)
                    HeadphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
            }
            catch { }
        }
    }

    /// <summary>
    /// Sets the device volume (0.0 to 1.0) for the subwoofer output device.
    /// </summary>
    public void SetSubVolume(float volume)
    {
        lock (_Lock)
        {
            try
            {
                if (SubDevice?.State == DeviceState.Active)
                    SubDevice.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f);
            }
            catch { }
        }
    }

    /// <summary>
    /// Gets the current device volume for the headphone device, or -1 if unavailable.
    /// </summary>
    public float GetHeadphoneVolume()
    {
        lock (_Lock)
        {
            try
            {
                if (HeadphoneDevice?.State == DeviceState.Active)
                    return HeadphoneDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
            catch { }
            return -1f;
        }
    }

    /// <summary>
    /// Gets the current device volume for the sub device, or -1 if unavailable.
    /// </summary>
    public float GetSubVolume()
    {
        lock (_Lock)
        {
            try
            {
                if (SubDevice?.State == DeviceState.Active)
                    return SubDevice.AudioEndpointVolume.MasterVolumeLevelScalar;
            }
            catch { }
            return -1f;
        }
    }

    /// <summary>
    /// Starts capturing from the default render device and routing audio.
    /// Throws if required devices are not set.
    /// </summary>
    public void Start()
    {
        lock (_Lock)
        {
            if (_IsRunning)
                return;

            if (HeadphoneDevice == null)
                throw new InvalidOperationException("Primary audio output device is not set.");
            if (SubDevice == null)
                throw new InvalidOperationException("Subwoofer audio output device is not set.");

            try
            {
                var enumerator = new MMDeviceEnumerator();
                CaptureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                Capture = new WasapiLoopbackCapture(CaptureDevice);

                var format = Capture.WaveFormat;

                HeadphoneBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
                SubBuffer = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };

                int hpDelaySamples = format.SampleRate * HeadphoneDelayMs / 1000;
                int subDelaySamples = format.SampleRate * SubDelayMs / 1000;

                HeadphoneDelayL = new DelayBuffer(hpDelaySamples);
                HeadphoneDelayR = new DelayBuffer(hpDelaySamples);
                SubDelayL = new DelayBuffer(subDelaySamples);
                SubDelayR = new DelayBuffer(subDelaySamples);

                LeftFilter = BiQuadFilter.LowPassFilter(format.SampleRate, LowPassFrequency, 0.7f);
                RightFilter = BiQuadFilter.LowPassFilter(format.SampleRate, LowPassFrequency, 0.7f);

                HeadphoneOut = new WasapiOut(HeadphoneDevice, AudioClientShareMode.Shared, false, 50);
                SubOut = new WasapiOut(SubDevice, AudioClientShareMode.Shared, false, 50);

                HeadphoneOut.Init(HeadphoneBuffer);
                SubOut.Init(SubBuffer);

                Capture.DataAvailable += OnAudioDataAvailable;
                Capture.RecordingStopped += OnRecordingStopped;

                HeadphoneOut.Play();
                SubOut.Play();
                Capture.StartRecording();

                _IsRunning = true;
            }
            catch
            {
                CleanupResources();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops routing and releases audio resources.
    /// </summary>
    public void Stop()
    {
        lock (_Lock)
        {
            if (!_IsRunning)
                return;

            _IsRunning = false;
            CleanupResources();
        }

        Stopped?.Invoke(this, EventArgs.Empty);
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (!_IsRunning) return;

            int sampleCount = e.BytesRecorded / 4;
            if (sampleCount < 2) return;

            float[] input = new float[sampleCount];
            Buffer.BlockCopy(e.Buffer, 0, input, 0, e.BytesRecorded);

            float[] headphoneSamples = new float[sampleCount];
            float[] subSamples = new float[sampleCount];

            var hpDelayL = HeadphoneDelayL;
            var hpDelayR = HeadphoneDelayR;
            var subDelayL = SubDelayL;
            var subDelayR = SubDelayR;
            var lFilter = LeftFilter;
            var rFilter = RightFilter;

            if (lFilter == null || rFilter == null) return;

            for (int i = 0; i < sampleCount - 1; i += 2)
            {
                float left = input[i];
                float right = input[i + 1];

                headphoneSamples[i] = hpDelayL.Process(left);
                headphoneSamples[i + 1] = hpDelayR.Process(right);

                float bassL = lFilter.Transform(left);
                float bassR = rFilter.Transform(right);

                subSamples[i] = subDelayL.Process(bassL);
                subSamples[i + 1] = subDelayR.Process(bassR);
            }

            byte[] hpBytes = new byte[sampleCount * 4];
            byte[] subBytes = new byte[sampleCount * 4];

            Buffer.BlockCopy(headphoneSamples, 0, hpBytes, 0, hpBytes.Length);
            Buffer.BlockCopy(subSamples, 0, subBytes, 0, subBytes.Length);

            HeadphoneBuffer?.AddSamples(hpBytes, 0, hpBytes.Length);
            SubBuffer?.AddSamples(subBytes, 0, subBytes.Length);
        }
        catch (Exception ex)
        {
            // Device may have been disconnected mid-processing
            Task.Run(() => HandleError($"Audio processing error: {ex.Message}"));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Task.Run(() => HandleError($"Recording stopped: {e.Exception.Message}"));
        }
    }

    private void HandleError(string message)
    {
        bool wasRunning;
        lock (_Lock)
        {
            wasRunning = _IsRunning;
            if (_IsRunning)
            {
                _IsRunning = false;
                CleanupResources();
            }
        }

        if (wasRunning)
        {
            Error?.Invoke(this, message);
            Stopped?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CleanupResources()
    {
        try { Capture?.StopRecording(); } catch { }

        if (Capture != null)
        {
            Capture.DataAvailable -= OnAudioDataAvailable;
            Capture.RecordingStopped -= OnRecordingStopped;
        }

        try { HeadphoneOut?.Stop(); } catch { }
        try { SubOut?.Stop(); } catch { }

        try { Capture?.Dispose(); } catch { }
        try { HeadphoneOut?.Dispose(); } catch { }
        try { SubOut?.Dispose(); } catch { }

        Capture = null;
        HeadphoneOut = null;
        SubOut = null;
        HeadphoneBuffer = null;
        SubBuffer = null;
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;
        Stop();
    }
}
