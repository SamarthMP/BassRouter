namespace BassRouter.Audio;

public sealed record AudioEngineConfig(
    string? HeadphoneDeviceId,
    string? SubDeviceId,
    int HeadphoneDelayMs,
    int SubDelayMs,
    float LowPassFrequency,
    float HeadphoneVolume,
    float SubVolume)
{
    public static AudioEngineConfig Default { get; } = new(
        HeadphoneDeviceId: null,
        SubDeviceId: null,
        HeadphoneDelayMs: 0,
        SubDelayMs: 0,
        LowPassFrequency: 120f,
        HeadphoneVolume: 1f,
        SubVolume: 1f);
}