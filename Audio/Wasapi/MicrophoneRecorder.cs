using BassRouter.Audio.Latency;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.Versioning;

namespace BassRouter.Audio.Wasapi;

/// <summary>
/// Records the first channel of a microphone into memory.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MicrophoneRecorder : IDisposable
{
    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly WasapiCapture Capture;
    private readonly float[] Samples;
    private readonly bool IsFloat;
    private readonly int BlockAlign;
    private int _RecordedFrames;
    private volatile Exception? _Error;
    private bool Disposed;

    public MicrophoneRecorder(MMDevice device, TimeSpan maxDuration)
    {
        Capture = new WasapiCapture(device);

        WaveFormat format = Capture.WaveFormat;
        Guid? subFormat = (format as WaveFormatExtensible)?.SubFormat;
        IsFloat = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat || subFormat == FloatSubFormat);
        bool isPcm16 = format.BitsPerSample == 16 && (format.Encoding == WaveFormatEncoding.Pcm || subFormat == PcmSubFormat);

        if (!IsFloat && !isPcm16)
        {
            Capture.Dispose();
            throw new LatencyDetectionException("The microphone uses an audio format BassRouter can't record.");
        }

        SampleRate = format.SampleRate;
        BlockAlign = format.BlockAlign;
        Samples = new float[(int)(format.SampleRate * maxDuration.TotalSeconds)];
        Capture.DataAvailable += OnDataAvailable;
        Capture.RecordingStopped += OnRecordingStopped;
    }

    public int SampleRate { get; }

    public int RecordedFrames => Volatile.Read(ref _RecordedFrames);

    public bool IsFull => RecordedFrames >= Samples.Length;

    /// <summary>
    /// Set if recording stopped because of an error, e.g. the microphone was unplugged.
    /// </summary>
    public Exception? Error => _Error;

    public void Start()
    {
        Capture.StartRecording();
    }

    /// <summary>
    /// Stops recording and returns what was recorded from the given frame on.
    /// </summary>
    public float[] Stop(int fromFrame)
    {
        Dispose();
        return Samples[Math.Min(fromFrame, _RecordedFrames).._RecordedFrames];
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;

        // Waits for the capture thread to finish, so nothing gets recorded afterwards
        try { Capture.StopRecording(); } catch { }
        Capture.Dispose();
        Capture.DataAvailable -= OnDataAvailable;
        Capture.RecordingStopped -= OnRecordingStopped;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        int frames = Math.Min(e.BytesRecorded / BlockAlign, Samples.Length - _RecordedFrames);

        for (int i = 0; i < frames; i++)
        {
            int offset = i * BlockAlign;
            Samples[_RecordedFrames + i] = IsFloat
                ? BitConverter.ToSingle(e.Buffer, offset)
                : BitConverter.ToInt16(e.Buffer, offset) / 32768f;
        }

        Volatile.Write(ref _RecordedFrames, _RecordedFrames + frames);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            _Error = e.Exception;
    }
}
