namespace BassRouter.Audio.Latency;

/// <summary>
/// Mono microphone audio recorded while the test sound played.
/// </summary>
public sealed record LatencyTestRecording(float[] Samples, int SampleRate);
