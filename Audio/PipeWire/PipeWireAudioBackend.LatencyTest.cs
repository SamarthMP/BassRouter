using BassRouter.Audio.Latency;
using static BassRouter.Audio.PipeWire.PwNative;

namespace BassRouter.Audio.PipeWire;

/// <summary>
/// Latency detection. The test signal is played into the BassRouter sink, so it goes through both filter chains
/// and reaches the outputs exactly like everything else does. Both chains get the same signal, so whichever output
/// shouldn't be playing at the moment is muted.
/// </summary>
public sealed unsafe partial class PipeWireAudioBackend
{
    private const string LatencyTestPlaybackNodeName = "bassrouter.latency-test.playback";
    private const string LatencyTestMicrophoneNodeName = "bassrouter.latency-test.microphone";
    private const int LatencyTestSampleRate = 48000;
    private static readonly TimeSpan LatencyTestStartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LatencyTestStallTimeout = TimeSpan.FromSeconds(3);

    public LatencyTestRecording RecordLatencyTest(string microphoneId, LatencyTestSignal signal, CancellationToken cancellationToken)
    {
        // Held for the whole test so routing can't be torn down while the test streams use it.
        // If routing breaks during the test anyway, the test notices and gives up.
        lock (EngineLock)
        {
            if (!_IsRunning)
                throw new InvalidOperationException("Audio routing isn't running.");

            PwNode microphone = FindInputDevice(microphoneId)
                ?? throw new LatencyDetectionException("The selected microphone isn't available anymore.");

            LatencyTestTracks tracks = signal.Render(LatencyTestSampleRate);

            // Room for the test sound, the time after it, and the streams taking a while to start
            int maxRecordingFrames = (int)((signal.Duration + signal.RecordingTail + 2 * LatencyTestStartTimeout.TotalSeconds) * LatencyTestSampleRate);
            var session = new LatencyTestSession(tracks, maxRecordingFrames);

            PwAudioStream? microphoneStream = null;
            PwAudioStream? playbackStream = null;

            try
            {
                ApplyLatencyTestControls_NoLock(tracks.Switches[0].Output);

                pw_thread_loop_lock(Loop);
                try
                {
                    microphoneStream = new PwAudioStream(
                        Core,
                        "BassRouter Latency Test",
                        PwDirection.Input,
                        LatencyTestSampleRate,
                        [PwAudioFormatParams.SpaAudioChannelMono],
                        CreateLatencyTestStreamProperties(LatencyTestMicrophoneNodeName, "Capture", microphone.Name!),
                        session.Record);
                }
                finally
                {
                    pw_thread_loop_unlock(Loop);
                }

                // Start recording before playing anything, so the recording catches all of the test sound
                WaitForLatencyTest(
                    () => session.RecordedFrames > 0,
                    "The microphone didn't record anything. Make sure it's connected.",
                    cancellationToken,
                    microphoneStream);

                pw_thread_loop_lock(Loop);
                try
                {
                    playbackStream = new PwAudioStream(
                        Core,
                        "BassRouter Latency Test",
                        PwDirection.Output,
                        LatencyTestSampleRate,
                        [PwAudioFormatParams.SpaAudioChannelFL, PwAudioFormatParams.SpaAudioChannelFR],
                        CreateLatencyTestStreamProperties(LatencyTestPlaybackNodeName, "Playback", SinkNodeName),
                        session.Play);
                }
                finally
                {
                    pw_thread_loop_unlock(Loop);
                }

                WaitForLatencyTest(
                    () => session.PlayedFrames > 0,
                    "Couldn't play the test sound through BassRouter.",
                    cancellationToken,
                    microphoneStream,
                    playbackStream);

                PlayLatencyTest(signal, tracks, session, cancellationToken, microphoneStream, playbackStream);
            }
            finally
            {
                pw_thread_loop_lock(Loop);
                try
                {
                    playbackStream?.Dispose();
                    microphoneStream?.Dispose();
                }
                finally
                {
                    pw_thread_loop_unlock(Loop);
                }

                ApplyHeadphoneControls_NoLock();
                ApplySubControls_NoLock();
            }

            return new LatencyTestRecording(session.GetRecording(), LatencyTestSampleRate);
        }
    }

    /// <summary>
    /// Waits for the test sound to finish playing and then for the rest of the recording, switching between the
    /// outputs along the way. Must be called with EngineLock held.
    /// </summary>
    private void PlayLatencyTest(
        LatencyTestSignal signal,
        LatencyTestTracks tracks,
        LatencyTestSession session,
        CancellationToken cancellationToken,
        params PwAudioStream[] streams)
    {
        int nextSwitch = 1;
        int recordingEnd = -1;
        int lastRecordedFrames = session.RecordedFrames;
        long lastRecordedAt = Environment.TickCount64;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLatencyTestBroken(streams);

            // The sound takes a moment to get from the stream to the filter chains, but the switch points are
            // in the middle of long enough gaps that this doesn't matter
            int playedFrames = session.PlayedFrames;
            while (nextSwitch < tracks.Switches.Count && playedFrames >= tracks.Switches[nextSwitch].Frame)
                ApplyLatencyTestControls_NoLock(tracks.Switches[nextSwitch++].Output);

            int recordedFrames = session.RecordedFrames;
            if (recordingEnd < 0 && playedFrames >= tracks.Length)
                recordingEnd = recordedFrames + (int)(signal.RecordingTail * LatencyTestSampleRate);

            if ((recordingEnd >= 0 && recordedFrames >= recordingEnd) || session.IsRecordingFull)
                return;

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

    /// <summary>
    /// Mutes whichever output shouldn't be playing, and removes the artificial latency so the native difference
    /// gets measured. Must be called with EngineLock held.
    /// </summary>
    private void ApplyLatencyTestControls_NoLock(LatencyTestOutput audibleOutput)
    {
        float headphoneGain = audibleOutput == LatencyTestOutput.Headphones ? VolumeToGain(HeadphoneVolume) : 0f;
        float subGain = audibleOutput == LatencyTestOutput.Subwoofer ? VolumeToGain(SubVolume) : 0f;

        SetControls(HeadphoneControls, [
            new("delay_l:Delay (s)", 0f),
            new("delay_r:Delay (s)", 0f),
            new("gain_l:Gain 1", headphoneGain),
            new("gain_r:Gain 1", headphoneGain),
        ]);

        SetControls(SubControls, [
            new("delay_l:Delay (s)", 0f),
            new("delay_r:Delay (s)", 0f),
            new("gain_l:Gain 1", subGain),
            new("gain_r:Gain 1", subGain),
        ]);
    }

    private void WaitForLatencyTest(Func<bool> condition, string timeoutMessage, CancellationToken cancellationToken, params PwAudioStream?[] streams)
    {
        long deadline = Environment.TickCount64 + (long)LatencyTestStartTimeout.TotalMilliseconds;

        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfLatencyTestBroken(streams);

            if (Environment.TickCount64 > deadline)
                throw new LatencyDetectionException(timeoutMessage);

            cancellationToken.WaitHandle.WaitOne(10);
        }
    }

    private void ThrowIfLatencyTestBroken(params PwAudioStream?[] streams)
    {
        lock (StateLock)
        {
            if (!Connected || RoutingInterrupted)
                throw new LatencyDetectionException("Audio routing stopped during the test.");
        }

        foreach (PwAudioStream? stream in streams)
        {
            if (stream?.State != PwStreamState.Error)
                continue;

            string what = stream.Direction == PwDirection.Input ? "The microphone couldn't be used" : "The test sound couldn't be played";
            throw new LatencyDetectionException(string.IsNullOrWhiteSpace(stream.Error) ? $"{what}." : $"{what}: {stream.Error}");
        }
    }

    private static KeyValuePair<string, string>[] CreateLatencyTestStreamProperties(string nodeName, string category, string target)
    {
        // Like the filter chains, never let the session manager move these somewhere else. Recording from
        // a different microphone, or playing the test sound on the wrong device, would be worse than failing.
        return
        [
            new("media.type", "Audio"),
            new("media.category", category),
            new("node.name", nodeName),
            new("node.description", "BassRouter Latency Test"),
            new("target.object", target),
            new("node.dont-reconnect", "true"),
            new("node.dont-fallback", "true"),
            new("node.dont-move", "true"),
        ];
    }

    /// <summary>
    /// The audio going in and out of the test streams. Only touched from the PipeWire thread loop while the
    /// streams exist, apart from the frame counts.
    /// </summary>
    private sealed class LatencyTestSession(LatencyTestTracks tracks, int maxRecordingFrames)
    {
        private readonly float[] Recording = new float[maxRecordingFrames];
        private int _RecordedFrames;
        private int _PlayedFrames;
        private int RecordedFramesAtPlaybackStart = -1;

        public int RecordedFrames => Volatile.Read(ref _RecordedFrames);

        public int PlayedFrames => Volatile.Read(ref _PlayedFrames);

        public bool IsRecordingFull => RecordedFrames >= Recording.Length;

        public void Record(Span<float> samples)
        {
            int count = Math.Min(samples.Length, Recording.Length - _RecordedFrames);
            samples[..count].CopyTo(Recording.AsSpan(_RecordedFrames));
            Volatile.Write(ref _RecordedFrames, _RecordedFrames + count);
        }

        public void Play(Span<float> samples)
        {
            if (RecordedFramesAtPlaybackStart < 0)
                RecordedFramesAtPlaybackStart = _RecordedFrames;

            // The tracks never overlap, and muting decides which output plays the mix
            int position = _PlayedFrames;
            for (int i = 0; i + 1 < samples.Length; i += 2, position++)
            {
                float sample = position < tracks.Length ? tracks.Headphones[position] + tracks.Subwoofer[position] : 0f;
                samples[i] = sample;
                samples[i + 1] = sample;
            }

            Volatile.Write(ref _PlayedFrames, position);
        }

        /// <summary>
        /// Returns what was recorded since the test sound started playing. Must be called after the streams are gone.
        /// </summary>
        public float[] GetRecording()
        {
            // Anything recorded before that can't contain the test sound, and leaving it out keeps the time between
            // starting the recording and starting the playback out of the latency the analysis has to search
            int start = Math.Max(0, RecordedFramesAtPlaybackStart);
            return Recording[start.._RecordedFrames];
        }
    }
}
