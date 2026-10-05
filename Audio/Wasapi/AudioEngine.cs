using BassRouter.Audio.Latency;
using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace BassRouter.Audio.Wasapi;

/// <summary>
/// Captures system audio via WASAPI loopback and routes it to a primary output and a lowpassed subwoofer output.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioEngine : IDisposable
{
    private static readonly TimeSpan LatencyTestStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LatencyTestStallTimeout = TimeSpan.FromSeconds(3);

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

    // Replaces the routed audio while latency detection runs
    private volatile LatencyTestPlayback? LatencyTest;

    // Config
    private MMDevice? CaptureDevice;
    private MMDevice? HeadphoneDevice;
    private MMDevice? SubDevice;
    private int HeadphoneDelayMs;
    private int SubDelayMs;
    private float LowPassFrequency = 120f;
    private string? LastErrorMessage;

    public bool IsRunning
    {
        get { lock (_Lock) return _IsRunning; }
    }

    /// <summary>
    /// Raised whenever the engine state or configuration changes.
    /// </summary>
    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when the engine stops unexpectedly (e.g. device disconnected).
    /// The string argument contains the error message.
    /// </summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Raised when the engine stops (either normally or due to error).
    /// </summary>
    public event EventHandler? Stopped;

    public void SetHeadphoneDevice(MMDevice? device)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            HeadphoneDevice = device;
            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    public void SetSubDevice(MMDevice? device)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            SubDevice = device;
            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    public void SetHeadphoneDelayMs(int ms)
    {
        AudioEngineState state;
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

            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    public void SetSubDelayMs(int ms)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            SubDelayMs = Math.Clamp(ms, 0, 5000);
            if (_IsRunning && Capture != null)
            {
                int samples = Capture.WaveFormat.SampleRate * SubDelayMs / 1000;
                SubDelayL.Resize(samples);
                SubDelayR.Resize(samples);
            }

            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    public void SetLowPassFrequency(float frequency)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            LowPassFrequency = Math.Clamp(frequency, 10f, 300f);
            if (_IsRunning && Capture != null)
            {
                int sr = Capture.WaveFormat.SampleRate;
                LeftFilter = BiQuadFilter.LowPassFilter(sr, LowPassFrequency, 0.7f);
                RightFilter = BiQuadFilter.LowPassFilter(sr, LowPassFrequency, 0.7f);
            }

            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    /// <summary>
    /// Sets the device volume (0.0 to 1.0) for the headphone output device.
    /// This controls the system endpoint volume, not the stream volume.
    /// </summary>
    public void SetHeadphoneVolume(float volume)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            try
            {
                if (HeadphoneDevice?.State == DeviceState.Active)
                    SetDeviceVolume_NoLock(HeadphoneDevice, volume);
            }
            catch { }

            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    /// <summary>
    /// Sets the device volume (0.0 to 1.0) for the subwoofer output device.
    /// </summary>
    public void SetSubVolume(float volume)
    {
        AudioEngineState state;
        lock (_Lock)
        {
            try
            {
                if (SubDevice?.State == DeviceState.Active)
                    SetDeviceVolume_NoLock(SubDevice, volume);
            }
            catch { }

            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    /// <summary>
    /// Gets the current device volume for the headphone device, or -1 if unavailable.
    /// </summary>
    public float GetHeadphoneVolume()
    {
        lock (_Lock)
        {
            return GetDeviceVolume_NoLock(HeadphoneDevice);
        }
    }

    /// <summary>
    /// Gets the current device volume for the sub device, or -1 if unavailable.
    /// </summary>
    public float GetSubVolume()
    {
        lock (_Lock)
        {
            return GetDeviceVolume_NoLock(SubDevice);
        }
    }

    public AudioEngineState GetState()
    {
        lock (_Lock)
        {
            return CreateState_NoLock();
        }
    }

    /// <summary>
    /// Starts capturing from the default render device and routing audio.
    /// Throws if required devices are not set.
    /// </summary>
    public void Start()
    {
        AudioEngineState? state = null;
        Exception? exception = null;

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
                using var enumerator = new MMDeviceEnumerator();
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
                LastErrorMessage = null;
                state = CreateState_NoLock();
            }
            catch (Exception ex)
            {
                CleanupResources();
                LastErrorMessage = ex.Message;
                state = CreateState_NoLock();
                exception = ex;
            }
        }

        if (state != null)
            PublishState(state);

        if (exception != null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    /// <summary>
    /// Plays the latency test sound through the running routing paths while recording from a microphone.
    /// See <see cref="IAudioBackend.RecordLatencyTest"/>.
    /// </summary>
    public LatencyTestRecording RecordLatencyTest(MMDevice microphone, LatencyTestSignal signal, CancellationToken cancellationToken)
    {
        WaveFormat format;
        MMDevice captureDevice;

        lock (_Lock)
        {
            if (!_IsRunning || Capture == null || CaptureDevice == null)
                throw new InvalidOperationException("Audio routing isn't running.");

            format = Capture.WaveFormat;
            captureDevice = CaptureDevice;
        }

        // The test sound is fed in as loopback audio arrives, so it shares a timeline across both outputs.
        // Loopback capture only delivers audio while something is playing on the device though, so keep it
        // playing silence for the duration of the test.
        using var keepAlive = new WasapiOut(captureDevice, AudioClientShareMode.Shared, false, 50);
        keepAlive.Init(new SilenceProvider(format));
        keepAlive.Play();

        TimeSpan maxRecording = TimeSpan.FromSeconds(signal.Duration + signal.RecordingTail + 2 * LatencyTestStartTimeout.TotalSeconds);
        using var recorder = new MicrophoneRecorder(microphone, maxRecording);
        recorder.Start();

        // Start recording before playing anything, so the recording catches all of the test sound
        WaitForLatencyTest(
            () => recorder.RecordedFrames > 0,
            "The microphone didn't record anything. Make sure it's connected and apps are allowed to use it.",
            recorder,
            cancellationToken);

        // Anything recorded before the test sound starts playing can't contain it
        int recordingStart = recorder.RecordedFrames;
        var playback = new LatencyTestPlayback(signal.Render(format.SampleRate));
        LatencyTest = playback;

        try
        {
            WaitForLatencyTest(
                () => playback.IsStarted,
                "Couldn't play the test sound through BassRouter.",
                recorder,
                cancellationToken);

            int recordingEnd = -1;
            int lastRecordedFrames = recorder.RecordedFrames;
            long lastRecordedAt = Environment.TickCount64;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfLatencyTestBroken(recorder);

                int recordedFrames = recorder.RecordedFrames;
                if (recordingEnd < 0 && playback.IsFinished)
                    recordingEnd = recordedFrames + (int)(signal.RecordingTail * recorder.SampleRate);

                if ((recordingEnd >= 0 && recordedFrames >= recordingEnd) || recorder.IsFull)
                    break;

                long now = Environment.TickCount64;
                if (recordedFrames != lastRecordedFrames)
                {
                    lastRecordedFrames = recordedFrames;
                    lastRecordedAt = now;
                }
                else if (now - lastRecordedAt > LatencyTestStallTimeout.TotalMilliseconds)
                {
                    throw new LatencyDetectionException("The microphone stopped recording during the test.");
                }

                cancellationToken.WaitHandle.WaitOne(10);
            }
        }
        finally
        {
            LatencyTest = null;
        }

        return new LatencyTestRecording(recorder.Stop(recordingStart), recorder.SampleRate);
    }

    private void WaitForLatencyTest(Func<bool> condition, string timeoutMessage, MicrophoneRecorder recorder, CancellationToken cancellationToken)
    {
        long deadline = Environment.TickCount64 + (long)LatencyTestStartTimeout.TotalMilliseconds;

        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLatencyTestBroken(recorder);

            if (Environment.TickCount64 > deadline)
                throw new LatencyDetectionException(timeoutMessage);

            cancellationToken.WaitHandle.WaitOne(10);
        }
    }

    private void ThrowIfLatencyTestBroken(MicrophoneRecorder recorder)
    {
        if (!IsRunning)
            throw new LatencyDetectionException("Audio routing stopped during the test.");

        if (recorder.Error != null)
            throw new LatencyDetectionException($"The microphone couldn't be used: {recorder.Error.Message}");
    }

    /// <summary>
    /// Stops routing and releases audio resources.
    /// </summary>
    public void Stop()
    {
        AudioEngineState? state = null;

        lock (_Lock)
        {
            if (!_IsRunning)
                return;

            _IsRunning = false;
            LastErrorMessage = null;
            CleanupResources();
            state = CreateState_NoLock();
        }

        if (state != null)
            PublishState(state);

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

            LatencyTestPlayback? latencyTest = LatencyTest;

            for (int i = 0; i < sampleCount - 1; i += 2)
            {
                if (latencyTest != null)
                {
                    // Play the test sound instead, through the same low pass but without the artificial latency
                    latencyTest.Read(out float headphoneSample, out float subSample);
                    headphoneSamples[i] = headphoneSample;
                    headphoneSamples[i + 1] = headphoneSample;
                    subSamples[i] = lFilter.Transform(subSample);
                    subSamples[i + 1] = rFilter.Transform(subSample);
                    continue;
                }

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
        AudioEngineState state;
        lock (_Lock)
        {
            wasRunning = _IsRunning;
            if (_IsRunning)
            {
                _IsRunning = false;
                CleanupResources();
            }

            LastErrorMessage = message;
            state = CreateState_NoLock();
        }

        PublishState(state);

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

    private AudioEngineState CreateState_NoLock()
    {
        return new AudioEngineState(
            IsRunning: _IsRunning,
            HeadphoneDeviceId: HeadphoneDevice?.ID,
            HeadphoneDeviceName: HeadphoneDevice?.FriendlyName,
            SubDeviceId: SubDevice?.ID,
            SubDeviceName: SubDevice?.FriendlyName,
            HeadphoneDelayMs: HeadphoneDelayMs,
            SubDelayMs: SubDelayMs,
            LowPassFrequency: LowPassFrequency,
            HeadphoneVolume: GetHeadphoneVolume_NoLock(),
            SubVolume: GetSubVolume_NoLock(),
            LastErrorMessage: LastErrorMessage);
    }

    private float GetHeadphoneVolume_NoLock()
    {
        return GetDeviceVolume_NoLock(HeadphoneDevice);
    }

    private float GetSubVolume_NoLock()
    {
        return GetDeviceVolume_NoLock(SubDevice);
    }

    private static void SetDeviceVolume_NoLock(MMDevice device, float volume)
    {
        float clampedVolume = Math.Clamp(volume, 0f, 1f);
        var endpointVolume = device.AudioEndpointVolume;

        if (clampedVolume <= 0f)
        {
            endpointVolume.Mute = true;
            return;
        }

        endpointVolume.MasterVolumeLevelScalar = clampedVolume;
        endpointVolume.Mute = false;
    }

    private static float GetDeviceVolume_NoLock(MMDevice? device)
    {
        try
        {
            if (device?.State != DeviceState.Active)
                return -1f;

            var endpointVolume = device.AudioEndpointVolume;
            return endpointVolume.Mute ? 0f : endpointVolume.MasterVolumeLevelScalar;
        }
        catch
        {
            return -1f;
        }
    }

    private void PublishState(AudioEngineState state)
    {
        StateChanged?.Invoke(this, new AudioEngineStateChangedEventArgs(state));
    }

    /// <summary>
    /// Steps through the latency test tracks on the capture thread, as loopback audio comes in.
    /// </summary>
    private sealed class LatencyTestPlayback(LatencyTestTracks tracks)
    {
        private int Position;

        public bool IsStarted => Volatile.Read(ref Position) > 0;

        public bool IsFinished => Volatile.Read(ref Position) >= tracks.Length;

        public void Read(out float headphoneSample, out float subSample)
        {
            int position = Position;
            if (position >= tracks.Length)
            {
                headphoneSample = 0f;
                subSample = 0f;
                return;
            }

            headphoneSample = tracks.Headphones[position];
            subSample = tracks.Subwoofer[position];
            Volatile.Write(ref Position, position + 1);
        }
    }
}
