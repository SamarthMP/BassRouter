using BassRouter.Audio.Latency;
using BassRouter.Audio.PipeWire;
using BassRouter.Audio.Wasapi;

namespace BassRouter.Audio;

/// <summary>
/// Platform-specific device management and audio routing.
/// All methods are called from the <see cref="AudioEngineController"/> worker thread.
/// Events may be raised from any thread.
/// </summary>
public interface IAudioBackend : IDisposable
{
    /// <summary>
    /// Raised when audio output devices are added, removed, or changed.
    /// </summary>
    event EventHandler? DevicesChanged;

    /// <summary>
    /// Raised whenever the engine state or configuration changes.
    /// </summary>
    event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Raised when the engine stops unexpectedly (e.g. device disconnected).
    /// The string argument contains the error message.
    /// </summary>
    event EventHandler<string>? Error;

    /// <summary>
    /// Raised when something outside the app means routing should stop (e.g. the user picked a different
    /// default output device). The string argument contains the message to show the user.
    /// </summary>
    event EventHandler<string>? StopRequested;

    bool IsRunning { get; }

    /// <summary>
    /// Returns all currently active audio output devices.
    /// </summary>
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();

    AudioEngineState GetState();

    void SetHeadphoneDevice(string? deviceId);
    void SetSubDevice(string? deviceId);
    void SetHeadphoneDelayMs(int milliseconds);
    void SetSubDelayMs(int milliseconds);
    void SetLowPassFrequency(float frequency);

    /// <summary>
    /// Sets the headphone output volume (0.0 to 1.0).
    /// </summary>
    void SetHeadphoneVolume(float volume);

    /// <summary>
    /// Sets the subwoofer output volume (0.0 to 1.0).
    /// </summary>
    void SetSubVolume(float volume);

    /// <summary>
    /// Starts routing audio. Throws if routing can't be started.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops routing and releases audio resources.
    /// </summary>
    void Stop();

    /// <summary>
    /// Returns all currently active audio input devices.
    /// </summary>
    IReadOnlyList<AudioInputDevice> GetInputDevices();

    /// <summary>
    /// Plays the test signal while recording from a microphone, and returns the recording.
    /// <para>
    /// Routing must be running. Each output's track is played through its routing path, both starting at the same
    /// moment, without the artificial latency and with the other output silent. The recording starts before the test
    /// signal and continues for <see cref="LatencyTestSignal.RecordingTail"/> after it.
    /// </para>
    /// </summary>
    LatencyTestRecording RecordLatencyTest(string microphoneId, LatencyTestSignal signal, CancellationToken cancellationToken);
}

public static class AudioBackend
{
    public static IAudioBackend Create()
    {
        if (OperatingSystem.IsWindows())
            return new WasapiAudioBackend();

        if (OperatingSystem.IsLinux())
            return new PipeWireAudioBackend();

        throw new PlatformNotSupportedException("BassRouter only supports Windows and Linux.");
    }
}
