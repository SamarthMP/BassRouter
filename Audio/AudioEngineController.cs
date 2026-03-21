using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;

namespace BassRouter.Audio;

public sealed class AudioEngineController : IDisposable
{
    private readonly AudioDeviceManager DeviceManager;
    private readonly AudioEngine Engine;
    private readonly AudioEngineConfigStore ConfigStore;
    private readonly BlockingCollection<Action> WorkQueue = new();
    private readonly Task WorkerTask;
    private readonly object AdjustmentLock = new();
    private readonly object StateLock = new();
    private readonly object DevicesLock = new();
    private readonly object ConfigLock = new();
    private readonly System.Threading.Timer AdjustmentTimer;
    private readonly System.Threading.Timer SaveTimer;
    private AudioEngineState CurrentState;
    private AudioOutputDevice[] CachedDevices = [];
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
        DeviceManager = new AudioDeviceManager();
        Engine = new AudioEngine();
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
        CurrentState = InvokeOnWorker(() => Engine.GetState());

        DeviceManager.DevicesChanged += OnDevicesChanged;
        DeviceManager.DefaultDeviceBecameNonVirtual += OnDefaultDeviceBecameNonVirtual;
        Engine.StateChanged += OnEngineStateChanged;
        Engine.Error += OnEngineError;
    }

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        lock (DevicesLock)
        {
            return CachedDevices;
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
        Enqueue(() => Engine.SetHeadphoneDevice(GetDevice(deviceId)));
    }

    public void SetSubDeviceById(string? deviceId)
    {
        Enqueue(() => Engine.SetSubDevice(GetDevice(deviceId)));
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
        Enqueue(() => Engine.Stop());
    }

    public void ToggleRunning()
    {
        FlushPendingAdjustments();
        Enqueue(() =>
        {
            if (Engine.IsRunning)
                Engine.Stop();
            else
                StartCore();
        });
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;

        DeviceManager.DevicesChanged -= OnDevicesChanged;
        DeviceManager.DefaultDeviceBecameNonVirtual -= OnDefaultDeviceBecameNonVirtual;
        Engine.StateChanged -= OnEngineStateChanged;
        Engine.Error -= OnEngineError;

        WorkQueue.CompleteAdding();
        WorkerTask.Wait(TimeSpan.FromSeconds(2));
        AdjustmentTimer.Change(Timeout.Infinite, Timeout.Infinite);
        AdjustmentTimer.Dispose();
        SaveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        PersistConfig();
        SaveTimer.Dispose();
        WorkQueue.Dispose();

        Engine.Dispose();
        DeviceManager.Dispose();
    }

    private MMDevice? GetDevice(string? deviceId)
    {
        return string.IsNullOrWhiteSpace(deviceId) ? null : DeviceManager.GetDeviceById(deviceId);
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        RefreshCachedDevices();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDefaultDeviceBecameNonVirtual(object? sender, EventArgs e)
    {
        Enqueue(() =>
        {
            Engine.Stop();
            Warning?.Invoke(this, "Default output device changed. Audio routing has been stopped.");
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

    private void Enqueue(Action action)
    {
        if (Disposed || WorkQueue.IsAddingCompleted)
            return;

        try
        {
            WorkQueue.Add(action);
        }
        catch (InvalidOperationException)
        {
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
        AudioOutputDevice? virtualDevice = DeviceManager.GetFirstVirtualOutputDevice();
        if (virtualDevice == null)
        {
            Warning?.Invoke(this, "No virtual audio output device was found. Start aborted.");
            return;
        }

        try
        {
            DeviceManager.SetDefaultRenderDevice(virtualDevice.Id);
        }
        catch (Exception ex)
        {
            Warning?.Invoke(this, $"Failed to switch default output to {virtualDevice.Name}: {ex.Message}");
            return;
        }

        Engine.Start();
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
                Engine.SetHeadphoneDelayMs(headphoneDelayMs);

            if (applySubDelay)
                Engine.SetSubDelayMs(subDelayMs);

            if (applyLowPassFrequency)
                Engine.SetLowPassFrequency(lowPassFrequency);

            if (applyHeadphoneVolume)
                Engine.SetHeadphoneVolume(headphoneVolume);

            if (applySubVolume)
                Engine.SetSubVolume(subVolume);
        });
    }

    private void RefreshCachedDevices()
    {
        AudioOutputDevice[] devices = DeviceManager
            .GetOutputDevices()
            .Select(device => new AudioOutputDevice(device.ID, device.FriendlyName))
            .ToArray();

        lock (DevicesLock)
        {
            CachedDevices = devices;
        }
    }

    private void ApplyConfig(AudioEngineConfig config)
    {
        Engine.SetHeadphoneDevice(GetDevice(config.HeadphoneDeviceId));
        Engine.SetSubDevice(GetDevice(config.SubDeviceId));
        Engine.SetHeadphoneDelayMs(config.HeadphoneDelayMs);
        Engine.SetSubDelayMs(config.SubDelayMs);
        Engine.SetLowPassFrequency(config.LowPassFrequency);
        Engine.SetHeadphoneVolume(config.HeadphoneVolume);
        Engine.SetSubVolume(config.SubVolume);
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