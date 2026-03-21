namespace BassRouter.Audio;

public sealed record AudioEngineState(
    bool IsRunning,
    string? HeadphoneDeviceId,
    string? HeadphoneDeviceName,
    string? SubDeviceId,
    string? SubDeviceName,
    int HeadphoneDelayMs,
    int SubDelayMs,
    float LowPassFrequency,
    float HeadphoneVolume,
    float SubVolume,
    string? LastErrorMessage);

public sealed class AudioEngineStateChangedEventArgs(AudioEngineState state) : EventArgs
{
    public AudioEngineState State { get; } = state;
}