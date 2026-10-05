namespace BassRouter.Audio.Latency;

/// <summary>
/// Latency detection failed for a reason the user can do something about. The message is shown to them as is.
/// </summary>
public sealed class LatencyDetectionException(string message) : Exception(message);
