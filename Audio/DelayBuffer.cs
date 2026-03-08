namespace BassRouter.Audio;

/// <summary>
/// System for delaying audio playback by offsetting a buffer
/// </summary>
public sealed class DelayBuffer
{
    private float[] Buffer;
    private int Index;
    private readonly object _Lock = new();

    public int DelaySamples { get; private set; }

    public DelayBuffer(int delaySamples)
    {
        DelaySamples = Math.Max(0, delaySamples);
        Buffer = new float[Math.Max(1, DelaySamples)];
        Index = 0;
    }

    /// <summary>
    /// Resizes the delay buffer and resets internal state.
    /// </summary>
    public void Resize(int delaySamples)
    {
        lock (_Lock)
        {
            DelaySamples = Math.Max(0, delaySamples);
            Buffer = new float[Math.Max(1, DelaySamples)];
            Index = 0;
        }
    }

    /// <summary>
    /// Processes a single sample through the delay line.
    /// Returns the original sample immediately if delay is zero.
    /// </summary>
    public float Process(float sample)
    {
        if (DelaySamples == 0)
            return sample;

        lock (_Lock)
        {
            if (DelaySamples == 0)
                return sample;

            float output = Buffer[Index];
            Buffer[Index] = sample;
            Index = (Index + 1) % Buffer.Length;
            return output;
        }
    }

    /// <summary>
    /// Clears all buffered samples without changing the size.
    /// </summary>
    public void Clear()
    {
        lock (_Lock)
        {
            Array.Clear(Buffer);
            Index = 0;
        }
    }
}
