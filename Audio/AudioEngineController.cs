using BassRouter.Audio.Latency;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace BassRouter.Audio;

public sealed class AudioEngineController : IDisposable
{
    private readonly IAudioBackend Backend;
    private readonly AudioEngineConfigStore ConfigStore;
    private readonly BlockingCollection<Action> WorkQueue = new();
    private readonly Task WorkerTask;
    private readonly object AdjustmentLock = new();
    private readonly object StateLock = new();
    private readonly object DevicesLock = new();
    private readonly object ConfigLock = new();
    private readonly System.Threading.Timer AdjustmentTimer;
    private readonly System.Threading.Timer SaveTimer;
    private readonly CancellationTokenSource DisposeCancellation = new();
    private AudioEngineState CurrentState;
    private AudioOutputDevice[] CachedDevices = [];
    private AudioInputDevice[] CachedInputDevices = [];
    private AudioEngineConfig CurrentConfig;
    private int PendingHeadphoneDelayMs;
    private int PendingSubDelayMs;
    private float PendingLowPassFrequency;
    private float PendingHeadphoneVolume;
    private float PendingSubVolume;
    private bool PendingHeadphoneDelay;
    private bool PendingSubDelay;
    private bool PendingLowPassFrequencyChange;
    private bool PendingHeadphoneVolumeChange;
    private bool PendingSubVolumeChange;
    private int WorkerThreadId;
    private bool Disposed;

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Warning;

    public AudioEngineController()
    {
        Backend = AudioBackend.Create();
        ConfigStore = new AudioEngineConfigStore();
        AdjustmentTimer = new System.Threading.Timer(_ => FlushPendingAdjustments(), null, Timeout.Infinite, Timeout.Infinite);
        SaveTimer = new System.Threading.Timer(_ => PersistConfig(), null, Timeout.Infinite, Timeout.Infinite);

        WorkerTask = Task.Factory.StartNew(
            ProcessWorkQueue,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        RefreshCachedDevices();
        CurrentConfig = ConfigStore.Load();
        InvokeOnWorker(() => ApplyConfig(CurrentConfig));
        CurrentState = InvokeOnWorker(() => Backend.GetState());

        Backend.DevicesChanged += OnDevicesChanged;
        Backend.StopRequested += OnStopRequested;
        Backend.StateChanged += OnEngineStateChanged;
        Backend.Error += OnEngineError;
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        lock (DevicesLock)
        {
            return CachedDevices;
        }
    }

    public IReadOnlyList<AudioInputDevice> GetInputDevices()
    {
        lock (DevicesLock)
        {
            return CachedInputDevices;
        }
    }

    public AudioEngineState GetState()
    {
        lock (StateLock)
        {
            return CurrentState;
        }
    }

    public void SetHeadphoneDeviceById(string? deviceId)
    {
        Enqueue(() => Backend.SetHeadphoneDevice(deviceId));
    }

    public void SetSubDeviceById(string? deviceId)
    {
        Enqueue(() => Backend.SetSubDevice(deviceId));
    }

    public void SetHeadphoneDelayMs(int milliseconds)
    {
        ScheduleAdjustment(
            markDirty: () =>
            {
                PendingHeadphoneDelay = true;
                PendingHeadphoneDelayMs = milliseconds;
            });
    }

    public void SetSubDelayMs(int milliseconds)
    {
        ScheduleAdjustment(
            markDirty: () =>
            {
                PendingSubDelay = true;
                PendingSubDelayMs = milliseconds;
            });
    }

    public void SetLowPassFrequency(float frequency)
    {
        ScheduleAdjustment(
            markDirty: () =>
            {
                PendingLowPassFrequencyChange = true;
                PendingLowPassFrequency = frequency;
            });
    }

    public void SetHeadphoneVolume(float volume)
    {
        ScheduleAdjustment(
            markDirty: () =>
            {
                PendingHeadphoneVolumeChange = true;
                PendingHeadphoneVolume = volume;
            });
    }

    public void SetSubVolume(float volume)
    {
        ScheduleAdjustment(
            markDirty: () =>
            {
                PendingSubVolumeChange = true;
                PendingSubVolume = volume;
            });
    }

    public void Start()
    {
        FlushPendingAdjustments();
        Enqueue(StartCore);
    }

    public void Stop()
    {
        Enqueue(() => Backend.Stop());
    }

    public void ToggleRunning()
    {
        FlushPendingAdjustments();
        Enqueue(() =>
        {
            if (Backend.IsRunning)
                Backend.Stop();
            else
                StartCore();
        });
    }

    /// <summary>
    /// Measures how much later one output plays than the other using a microphone, then sets the artificial latency
    /// so they play in sync. Starts routing if it isn't running, since the test plays through it.
    /// Fails with <see cref="LatencyDetectionException"/> when the measurement didn't work out.
    /// </summary>
    public Task<LatencyDetectionResult> DetectLatencyAsync(string microphoneId, CancellationToken cancellationToken)
    {
        FlushPendingAdjustments();

        var completion = new TaskCompletionSource<LatencyDetectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = Enqueue(() =>
        {
            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, DisposeCancellation.Token);
                completion.TrySetResult(DetectLatencyCore(microphoneId, cancellation.Token));
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        });

        if (!queued)
            completion.TrySetCanceled();

        return completion.Task;
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;
        DisposeCancellation.Cancel();

        Backend.DevicesChanged -= OnDevicesChanged;
        Backend.StopRequested -= OnStopRequested;
        Backend.StateChanged -= OnEngineStateChanged;
        Backend.Error -= OnEngineError;

        WorkQueue.CompleteAdding();
        WorkerTask.Wait(TimeSpan.FromSeconds(2));
        AdjustmentTimer.Change(Timeout.Infinite, Timeout.Infinite);
        AdjustmentTimer.Dispose();
        SaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        PersistConfig();
        SaveTimer.Dispose();
        WorkQueue.Dispose();
        DisposeCancellation.Dispose();

        Backend.Dispose();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        RefreshCachedDevices();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopRequested(object? sender, string message)
    {
        Enqueue(() =>
        {
            if (!Backend.IsRunning)
                return;

            Backend.Stop();
            Warning?.Invoke(this, message);
        });
    }

    private void OnEngineStateChanged(object? sender, AudioEngineStateChangedEventArgs e)
    {
        bool changed;
        lock (StateLock)
        {
            changed = !Equals(CurrentState, e.State);
            CurrentState = e.State;
        }

        if (!changed)
            return;

        UpdateConfig(e.State);
        StateChanged?.Invoke(this, e);
    }

    private void OnEngineError(object? sender, string message)
    {
        Warning?.Invoke(this, message);
    }

    private bool Enqueue(Action action)
    {
        if (Disposed || WorkQueue.IsAddingCompleted)
            return false;

        try
        {
            WorkQueue.Add(action);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ProcessWorkQueue()
    {
        WorkerThreadId = Environment.CurrentManagedThreadId;

        foreach (Action action in WorkQueue.GetConsumingEnumerable())
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Warning?.Invoke(this, ex.Message);
            }
        }
    }

    private void StartCore()
    {
        Backend.Start();
    }

    private LatencyDetectionResult DetectLatencyCore(string microphoneId, CancellationToken cancellationToken)
    {
        LatencyTestSignal signal = LatencyTestSignal.Default;

        if (!Backend.IsRunning)
            StartCore();

        LatencyTestRecording recording = Backend.RecordLatencyTest(microphoneId, signal, cancellationToken);
        double offset = LatencyAnalyzer.MeasureOffset(signal, recording);

        LatencyDetectionResult result = LatencyDetectionResult.FromOffset(offset);
        Backend.SetHeadphoneDelayMs(result.HeadphoneDelayMs);
        Backend.SetSubDelayMs(result.SubDelayMs);
        return result;
    }

    private void ScheduleAdjustment(Action markDirty)
    {
        lock (AdjustmentLock)
        {
            markDirty();
            AdjustmentTimer.Change(50, Timeout.Infinite);
        }
    }

    private void FlushPendingAdjustments()
    {
        int headphoneDelayMs = 0;
        int subDelayMs = 0;
        float lowPassFrequency = 0;
        float headphoneVolume = 0;
        float subVolume = 0;
        bool applyHeadphoneDelay = false;
        bool applySubDelay = false;
        bool applyLowPassFrequency = false;
        bool applyHeadphoneVolume = false;
        bool applySubVolume = false;

        lock (AdjustmentLock)
        {
            if (!PendingHeadphoneDelay &&
                !PendingSubDelay &&
                !PendingLowPassFrequencyChange &&
                !PendingHeadphoneVolumeChange &&
                !PendingSubVolumeChange)
            {
                return;
            }

            applyHeadphoneDelay = PendingHeadphoneDelay;
            applySubDelay = PendingSubDelay;
            applyLowPassFrequency = PendingLowPassFrequencyChange;
            applyHeadphoneVolume = PendingHeadphoneVolumeChange;
            applySubVolume = PendingSubVolumeChange;

            headphoneDelayMs = PendingHeadphoneDelayMs;
            subDelayMs = PendingSubDelayMs;
            lowPassFrequency = PendingLowPassFrequency;
            headphoneVolume = PendingHeadphoneVolume;
            subVolume = PendingSubVolume;

            PendingHeadphoneDelay = false;
            PendingSubDelay = false;
            PendingLowPassFrequencyChange = false;
            PendingHeadphoneVolumeChange = false;
            PendingSubVolumeChange = false;
        }

        Enqueue(() =>
        {
            if (applyHeadphoneDelay)
                Backend.SetHeadphoneDelayMs(headphoneDelayMs);

            if (applySubDelay)
                Backend.SetSubDelayMs(subDelayMs);

            if (applyLowPassFrequency)
                Backend.SetLowPassFrequency(lowPassFrequency);

            if (applyHeadphoneVolume)
                Backend.SetHeadphoneVolume(headphoneVolume);

            if (applySubVolume)
                Backend.SetSubVolume(subVolume);
        });
    }

    private void RefreshCachedDevices()
    {
        AudioOutputDevice[] devices = Backend.GetOutputDevices().ToArray();
        AudioInputDevice[] inputDevices = Backend.GetInputDevices().ToArray();

        lock (DevicesLock)
        {
            CachedDevices = devices;
            CachedInputDevices = inputDevices;
        }
    }

    private void ApplyConfig(AudioEngineConfig config)
    {
        Backend.SetHeadphoneDevice(config.HeadphoneDeviceId);
        Backend.SetSubDevice(config.SubDeviceId);
        Backend.SetHeadphoneDelayMs(config.HeadphoneDelayMs);
        Backend.SetSubDelayMs(config.SubDelayMs);
        Backend.SetLowPassFrequency(config.LowPassFrequency);
        Backend.SetHeadphoneVolume(config.HeadphoneVolume);
        Backend.SetSubVolume(config.SubVolume);
    }

    private void UpdateConfig(AudioEngineState state)
    {
        lock (ConfigLock)
        {
            CurrentConfig = CurrentConfig with
            {
                HeadphoneDeviceId = state.HeadphoneDeviceId,
                SubDeviceId = state.SubDeviceId,
                HeadphoneDelayMs = state.HeadphoneDelayMs,
                SubDelayMs = state.SubDelayMs,
                LowPassFrequency = state.LowPassFrequency,
                HeadphoneVolume = state.HeadphoneVolume >= 0 ? state.HeadphoneVolume : CurrentConfig.HeadphoneVolume,
                SubVolume = state.SubVolume >= 0 ? state.SubVolume : CurrentConfig.SubVolume
            };
        }

        SaveTimer.Change(250, Timeout.Infinite);
    }

    private void PersistConfig()
    {
        AudioEngineConfig config;
        lock (ConfigLock)
        {
            config = CurrentConfig;
        }

        ConfigStore.Save(config);
    }

    private void InvokeOnWorker(Action action)
    {
        InvokeOnWorker(() =>
        {
            action();
            return true;
        });
    }

    private T InvokeOnWorker<T>(Func<T> func)
    {
        if (Environment.CurrentManagedThreadId == WorkerThreadId)
            return func();

        Exception? exception = null;
        T? result = default;
        using var completed = new ManualResetEventSlim(false);

        Enqueue(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
            finally
            {
                completed.Set();
            }
        });

        completed.Wait();

        if (exception != null)
            ExceptionDispatchInfo.Capture(exception).Throw();

        return result!;
    }
}