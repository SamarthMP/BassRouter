namespace BassRouter.Audio.Latency;

/// <summary>
/// Finds the test sweeps in a microphone recording and works out how far apart the two outputs are.
/// <para>
/// Only the difference between when the subwoofer and the primary output sweeps arrive is used. Both arrive through
/// the same microphone in the same recording, so the microphone's own latency (and when the recording started)
/// cancels out.
/// </para>
/// </summary>
public static class LatencyAnalyzer
{
    /// <summary>
    /// A sweep counts as heard when it stands out this far above the noise.
    /// </summary>
    private const double DetectionThreshold = 8;

    /// <summary>
    /// How far each round's measurement may be from the others, in seconds.
    /// </summary>
    private const double MaxSpread = 0.004;

    /// <summary>
    /// Peaks are widened by this much for the first pass that lines up all sweeps at once, so slightly differing
    /// rounds still line up.
    /// </summary>
    private const double PeakHoldWindow = 0.005;

    /// <summary>
    /// How much of the response around the first pass is looked at on either side, in seconds.
    /// </summary>
    private const double ResponseWindow = 0.03;

    /// <summary>
    /// How far apart the same output's sweeps may be from each other after the first pass, in seconds.
    /// </summary>
    private const double MaxShift = 0.02;

    /// <summary>
    /// The direct sound is the first peak at least this high relative to the highest one.
    /// </summary>
    private const double FirstPeakLevel = 0.6;

    /// <summary>
    /// A peak has to be the highest point within this many seconds on either side.
    /// </summary>
    private const double PeakNeighborhood = 0.001;

    /// <summary>
    /// Keeps frequencies the sweep doesn't contain from blowing up when dividing by it, relative to its peak power.
    /// </summary>
    private const double Regularization = 1e-3;

    private const float SilenceLevel = 1e-5f;

    /// <summary>
    /// Returns how many seconds later the subwoofer plays than the primary output (negative if it plays earlier).
    /// Throws <see cref="LatencyDetectionException"/> if the recording isn't good enough to tell.
    /// </summary>
    public static double MeasureOffset(LatencyTestSignal signal, LatencyTestRecording recording)
    {
        float[] samples = recording.Samples;
        int sampleRate = recording.SampleRate;

        if (samples.Length == 0 || samples.All(sample => Math.Abs(sample) < SilenceLevel))
            throw new LatencyDetectionException("The microphone only recorded silence. Make sure it's connected and not muted.");

        int longestSweep = Enum.GetValues<LatencyTestOutput>()
            .Max(output => (int)Math.Ceiling(LatencyTestSignal.GetSweepDuration(output) * sampleRate));

        // Zero padded so the correlation doesn't wrap around
        int fftSize = Fft.GetSize(samples.Length + longestSweep);
        double[] real = new double[fftSize];
        double[] imaginary = new double[fftSize];
        for (int i = 0; i < samples.Length; i++)
            real[i] = samples[i];

        Fft.Transform(real, imaginary, inverse: false);

        Arrival[] headphones = FindArrivals(signal, LatencyTestOutput.Headphones, real, imaginary, samples.Length, sampleRate);
        Arrival[] sub = FindArrivals(signal, LatencyTestOutput.Subwoofer, real, imaginary, samples.Length, sampleRate);

        bool heardHeadphones = headphones.Count(arrival => arrival.Heard) >= 2;
        bool heardSub = sub.Count(arrival => arrival.Heard) >= 2;

        if (!heardHeadphones && !heardSub)
            throw new LatencyDetectionException("Couldn't hear the test sounds. Make sure the microphone works and the volume is up.");
        if (!heardHeadphones)
            throw new LatencyDetectionException("Couldn't hear the primary output. If it's headphones, hold an ear cup right against the microphone.");
        if (!heardSub)
            throw new LatencyDetectionException("Couldn't hear the subwoofer. Turn it up or move the microphone closer to it.");

        // The n-th sweep on each output form a round
        var offsets = new List<double>();
        for (int i = 0; i < Math.Min(headphones.Length, sub.Length); i++)
        {
            if (headphones[i].Heard && sub[i].Heard)
                offsets.Add(sub[i].Latency - headphones[i].Latency);
        }

        if (offsets.Count < 2)
            throw new LatencyDetectionException("Some of the test sounds were drowned out. Try again somewhere quieter.");

        double offset = Median(offsets.ToArray());
        if (offsets.Any(value => Math.Abs(value - offset) > MaxSpread))
            throw new LatencyDetectionException("The measurements didn't agree with each other. Try again somewhere quieter.");

        return offset;
    }

    /// <summary>
    /// Finds where each of an output's sweeps shows up in the recording.
    /// </summary>
    private static Arrival[] FindArrivals(
        LatencyTestSignal signal,
        LatencyTestOutput output,
        double[] recordingReal,
        double[] recordingImaginary,
        int length,
        int sampleRate)
    {
        float[] envelope = GetEnvelope(recordingReal, recordingImaginary, LatencyTestSignal.RenderSweep(output, sampleRate), length);
        double noise = Math.Max(Median(envelope), double.Epsilon);

        LatencyTestBurst[] bursts = signal.Bursts.Where(burst => burst.Output == output).ToArray();
        int[] starts = bursts.Select(burst => (int)Math.Round(burst.Start * sampleRate)).ToArray();

        int maxLatency = Math.Min((int)(signal.MaxLatency * sampleRate), length - 1 - starts.Max());
        if (maxLatency < 0)
            throw new LatencyDetectionException("The recording ended too early.");

        // First find roughly where the sweeps line up. Scoring by the median sweep means at least two of them have to
        // line up, and since the sweeps are spaced unevenly, at any other latency only one of them can (with another
        // sweep, or one from the other output).
        // Holding peaks rather than averaging keeps a sharp response from losing out to a broad smear of the same
        // energy, like the subwoofer's harmonics leaking into the primary output's band
        float[] smoothed = MovingMax(envelope, (int)(PeakHoldWindow * sampleRate));
        float[] values = new float[starts.Length];
        int roughLatency = 0;
        float bestScore = float.MinValue;

        for (int latency = 0; latency <= maxLatency; latency++)
        {
            for (int i = 0; i < starts.Length; i++)
                values[i] = smoothed[starts[i] + latency];

            Array.Sort(values);
            float score = values[(values.Length - 1) / 2];
            if (score > bestScore)
            {
                bestScore = score;
                roughLatency = latency;
            }
        }

        // Cut out each sweep's response around there, with some slack so they can be lined up exactly
        int window = (int)(ResponseWindow * sampleRate);
        int maxShift = (int)(MaxShift * sampleRate);
        float[][] responses = new float[bursts.Length][];
        bool[] heard = new bool[bursts.Length];

        for (int i = 0; i < bursts.Length; i++)
        {
            int from = starts[i] + roughLatency - window - maxShift;
            responses[i] = new float[2 * (window + maxShift) + 1];

            for (int j = 0; j < responses[i].Length; j++)
            {
                if (from + j >= 0 && from + j < length)
                    responses[i][j] = envelope[from + j];
            }

            heard[i] = responses[i].Max() / noise >= DetectionThreshold;
        }

        var arrivals = new Arrival[bursts.Length];
        int heardCount = heard.Count(value => value);
        if (heardCount == 0)
            return arrivals;

        // Averaging the sweeps that were heard gives a cleaner picture of the response to time. Bass in particular
        // comes with reflections and room resonances, so time the direct sound rather than whatever is loudest.
        float[] average = new float[2 * window + 1];
        for (int i = 0; i < bursts.Length; i++)
        {
            if (!heard[i])
                continue;

            for (int j = 0; j < average.Length; j++)
                average[j] += responses[i][j + maxShift] / heardCount;
        }

        double reference = FindFirstPeak(average, neighborhood: (int)(PeakNeighborhood * sampleRate));

        // Then line each sweep up with the average. This compares their whole shape, so a reflection being a bit
        // louder in one round doesn't make that round jump to a different peak.
        for (int i = 0; i < bursts.Length; i++)
        {
            double shift = FindShift(responses[i], average, maxShift);
            double position = starts[i] + roughLatency - window + reference + shift;
            arrivals[i] = new Arrival(Latency: position / sampleRate - bursts[i].Start, Heard: heard[i]);
        }

        return arrivals;
    }

    /// <summary>
    /// Returns the position of the first peak that's nearly as high as the highest one.
    /// </summary>
    private static double FindFirstPeak(float[] values, int neighborhood)
    {
        float max = values.Max();

        for (int i = 1; i < values.Length - 1; i++)
        {
            if (values[i] < FirstPeakLevel * max)
                continue;

            // A local maximum, and not just a ripple on the way up to one
            bool isPeak = true;
            for (int j = Math.Max(0, i - neighborhood); j <= Math.Min(values.Length - 1, i + neighborhood) && isPeak; j++)
                isPeak = values[j] <= values[i];

            if (isPeak)
                return i + InterpolatePeak(values[i - 1], values[i], values[i + 1]);
        }

        return Array.IndexOf(values, max);
    }

    /// <summary>
    /// Returns how many samples later <paramref name="template"/> best matches <paramref name="values"/>
    /// than in the middle of it. <paramref name="values"/> has <paramref name="maxShift"/> extra samples on each side.
    /// </summary>
    private static double FindShift(float[] values, float[] template, int maxShift)
    {
        // Without the mean, the correlation would mostly follow the overall level instead of the shape
        float mean = template.Average();
        double[] correlation = new double[2 * maxShift + 1];

        for (int shift = 0; shift < correlation.Length; shift++)
        {
            double sum = 0;
            for (int j = 0; j < template.Length; j++)
                sum += (template[j] - mean) * values[j + shift];

            correlation[shift] = sum;
        }

        int best = Array.IndexOf(correlation, correlation.Max());
        double offset = best > 0 && best < correlation.Length - 1
            ? InterpolatePeak(correlation[best - 1], correlation[best], correlation[best + 1])
            : 0;

        return best + offset - maxShift;
    }

    /// <summary>
    /// Deconvolves the recording with the sweep, which leaves something like the impulse response from the output
    /// to the microphone, with a peak wherever the sweep was played. Returns its envelope.
    /// </summary>
    private static float[] GetEnvelope(double[] recordingReal, double[] recordingImaginary, float[] sweep, int length)
    {
        int n = recordingReal.Length;
        double[] real = new double[n];
        double[] imaginary = new double[n];
        for (int i = 0; i < sweep.Length; i++)
            real[i] = sweep[i];

        Fft.Transform(real, imaginary, inverse: false);

        double maxPower = 0;
        for (int k = 0; k < n; k++)
            maxPower = Math.Max(maxPower, real[k] * real[k] + imaginary[k] * imaginary[k]);

        double regularization = Regularization * maxPower;

        // recording * conj(sweep) / (|sweep|^2 + regularization). Only keeping the positive frequencies (doubled)
        // makes the result an analytic signal, so its magnitude is the envelope.
        for (int k = 0; k < n; k++)
        {
            if (k == 0 || k >= n / 2)
            {
                real[k] = 0;
                imaginary[k] = 0;
                continue;
            }

            double sweepReal = real[k];
            double sweepImaginary = imaginary[k];
            double scale = 2 / (sweepReal * sweepReal + sweepImaginary * sweepImaginary + regularization);

            real[k] = (recordingReal[k] * sweepReal + recordingImaginary[k] * sweepImaginary) * scale;
            imaginary[k] = (recordingImaginary[k] * sweepReal - recordingReal[k] * sweepImaginary) * scale;
        }

        Fft.Transform(real, imaginary, inverse: true);

        float[] envelope = new float[length];
        for (int i = 0; i < length; i++)
            envelope[i] = (float)(Math.Sqrt(real[i] * real[i] + imaginary[i] * imaginary[i]) / n);

        return envelope;
    }

    /// <summary>
    /// The highest value within <paramref name="width"/> samples centered on each sample.
    /// </summary>
    private static float[] MovingMax(float[] values, int width)
    {
        int half = Math.Max(1, width) / 2;
        float[] result = new float[values.Length];

        // Monotonic queue of indices with decreasing values, so this is linear time
        var window = new LinkedList<int>();
        for (int i = 0; i < values.Length + half; i++)
        {
            if (i < values.Length)
            {
                while (window.Count > 0 && values[window.Last!.Value] <= values[i])
                    window.RemoveLast();
                window.AddLast(i);
            }

            int center = i - half;
            if (center < 0)
                continue;

            while (window.First!.Value < center - half)
                window.RemoveFirst();

            result[center] = values[window.First.Value];
        }

        return result;
    }

    /// <summary>
    /// Offset of the true peak from the middle sample, from a parabola through three samples.
    /// </summary>
    private static double InterpolatePeak(double left, double middle, double right)
    {
        double denominator = left - 2 * middle + right;
        return denominator == 0 ? 0 : Math.Clamp(0.5 * (left - right) / denominator, -0.5, 0.5);
    }

    private static double Median(float[] values)
    {
        float[] sorted = (float[])values.Clone();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static double Median(double[] values)
    {
        double[] sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <param name="Latency">Seconds from playing the sweep to it showing up in the recording.</param>
    private readonly record struct Arrival(double Latency, bool Heard);
}
