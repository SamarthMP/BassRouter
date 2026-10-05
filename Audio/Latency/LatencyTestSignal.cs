namespace BassRouter.Audio.Latency;

public enum LatencyTestOutput
{
    Headphones,
    Subwoofer
}

/// <summary>
/// A sweep played on one output, starting <see cref="Start"/> seconds after the test sound starts.
/// </summary>
public sealed record LatencyTestBurst(LatencyTestOutput Output, double Start);

/// <summary>
/// The test sound rendered at a sample rate: one track per output, plus the points where playback moves from one
/// output to the other. The tracks never overlap, so they can also be mixed into a single signal.
/// </summary>
public sealed record LatencyTestTracks(float[] Headphones, float[] Subwoofer, IReadOnlyList<LatencyTestSwitch> Switches)
{
    public int Length => Headphones.Length;
}

/// <summary>
/// From <see cref="Frame"/> on, only <see cref="Output"/> should be audible.
/// </summary>
public sealed record LatencyTestSwitch(int Frame, LatencyTestOutput Output);

/// <summary>
/// The sound played while detecting latency: a few sweeps on each output, one output at a time.
/// <para>
/// The primary output sweeps through the mids and the subwoofer through the bass, with a gap in between, so their
/// sweeps can be told apart in the recording. The sweeps are spaced unevenly, and differently on each output, so a set
/// of sweeps only lines up with the recording at one latency, even when the latency is longer than the gaps between
/// them, and never with the other output's sweeps.
/// </para>
/// </summary>
public sealed class LatencyTestSignal
{
    private const float Amplitude = 0.5f;
    private const double FadeFraction = 0.1;

    private static readonly Sweep HeadphoneSweep = new(StartFrequency: 250, EndFrequency: 4000, Duration: 0.4);
    private static readonly Sweep SubSweep = new(StartFrequency: 25, EndFrequency: 140, Duration: 0.8);

    public static LatencyTestSignal Default { get; } = new(
        bursts:
        [
            // The lead-in gives suspended devices time to wake up, and the gaps between sweeps on different outputs
            // leave room to mute one output and unmute the other. The primary output's sweeps are 2.4 s, 3.0 s and
            // 5.4 s apart and the subwoofer's 2.7 s, 2.9 s and 5.6 s, all different from each other.
            new(LatencyTestOutput.Headphones, 1.0),
            new(LatencyTestOutput.Subwoofer, 2.0),
            new(LatencyTestOutput.Headphones, 3.4),
            new(LatencyTestOutput.Subwoofer, 4.7),
            new(LatencyTestOutput.Headphones, 6.4),
            new(LatencyTestOutput.Subwoofer, 7.6),
        ],
        duration: 8.7,
        maxLatency: 3.0);

    private LatencyTestSignal(LatencyTestBurst[] bursts, double duration, double maxLatency)
    {
        Bursts = bursts;
        Duration = duration;
        MaxLatency = maxLatency;
    }

    public IReadOnlyList<LatencyTestBurst> Bursts { get; }

    /// <summary>
    /// Length of the test sound in seconds.
    /// </summary>
    public double Duration { get; }

    /// <summary>
    /// The longest time in seconds from playing a sweep to it showing up in the recording that can be detected.
    /// This includes the output and microphone latency, plus however long the recording started before playback.
    /// </summary>
    public double MaxLatency { get; }

    /// <summary>
    /// How long to keep recording after the test sound has finished playing, so the last sweep can still arrive.
    /// </summary>
    public double RecordingTail => MaxLatency - (Duration - Bursts.Max(burst => burst.Start + GetSweep(burst.Output).Duration));

    /// <summary>
    /// Length of the sweep played on an output, in seconds.
    /// </summary>
    public static double GetSweepDuration(LatencyTestOutput output) => GetSweep(output).Duration;

    public LatencyTestTracks Render(int sampleRate)
    {
        int length = (int)Math.Ceiling(Duration * sampleRate);
        float[] headphones = new float[length];
        float[] sub = new float[length];

        foreach (LatencyTestBurst burst in Bursts)
        {
            float[] sweep = RenderSweep(burst.Output, sampleRate);
            float[] track = burst.Output == LatencyTestOutput.Headphones ? headphones : sub;
            sweep.CopyTo(track, (int)Math.Round(burst.Start * sampleRate));
        }

        // Switch outputs halfway through each gap between sweeps on different outputs
        var switches = new List<LatencyTestSwitch> { new(0, Bursts[0].Output) };
        for (int i = 1; i < Bursts.Count; i++)
        {
            LatencyTestBurst previous = Bursts[i - 1];
            LatencyTestBurst current = Bursts[i];
            if (previous.Output == current.Output)
                continue;

            double previousEnd = previous.Start + GetSweep(previous.Output).Duration;
            double middle = (previousEnd + current.Start) / 2;
            switches.Add(new LatencyTestSwitch((int)Math.Round(middle * sampleRate), current.Output));
        }

        return new LatencyTestTracks(headphones, sub, switches);
    }

    /// <summary>
    /// Renders the sweep played on an output. Also used as the reference to look for in the recording.
    /// </summary>
    public static float[] RenderSweep(LatencyTestOutput output, int sampleRate)
    {
        Sweep sweep = GetSweep(output);
        int length = (int)Math.Round(sweep.Duration * sampleRate);
        int fadeLength = Math.Max(1, (int)(length * FadeFraction));
        float[] samples = new float[length];

        // Exponential sweep: spends the same time on every octave
        double rate = Math.Log(sweep.EndFrequency / sweep.StartFrequency);
        double scale = 2 * Math.PI * sweep.StartFrequency * sweep.Duration / rate;

        for (int i = 0; i < length; i++)
        {
            double t = (double)i / sampleRate;
            double phase = scale * (Math.Exp(t / sweep.Duration * rate) - 1);

            // Raised cosine fades, so the sweep doesn't click
            double fade = 1;
            if (i < fadeLength)
                fade = 0.5 - 0.5 * Math.Cos(Math.PI * i / fadeLength);
            else if (i >= length - fadeLength)
                fade = 0.5 - 0.5 * Math.Cos(Math.PI * (length - 1 - i) / fadeLength);

            samples[i] = (float)(Amplitude * fade * Math.Sin(phase));
        }

        return samples;
    }

    private static Sweep GetSweep(LatencyTestOutput output)
    {
        return output == LatencyTestOutput.Headphones ? HeadphoneSweep : SubSweep;
    }

    private sealed record Sweep(double StartFrequency, double EndFrequency, double Duration);
}
