using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using static BassRouter.Audio.PipeWire.PwNative;

namespace BassRouter.Audio.PipeWire;

/// <summary>
/// Routes audio using PipeWire, similar to how EasyEffects works.
/// <para>
/// While running, a virtual "BassRouter" sink is created and made the default output.
/// Two filter-chain modules are loaded into this process, both capturing from the virtual sink's monitor:
/// one adds delay and gain and plays to the headphones, the other adds a low pass filter, delay and gain
/// and plays to the subwoofer. All DSP runs inside the PipeWire graph, and because everything is linked
/// into a single graph, PipeWire keeps both output devices in sync (adaptive resampling handles clock drift).
/// </para>
/// </summary>
[SupportedOSPlatform("linux")]
public sealed unsafe class PipeWireAudioBackend : IAudioBackend
{
    private const string SinkNodeName = "bassrouter_sink";
    private const string SinkDescription = "BassRouter";
    private const string HeadphoneInputNodeName = "bassrouter.headphones.input";
    private const string HeadphoneOutputNodeName = "bassrouter.headphones.output";
    private const string SubInputNodeName = "bassrouter.subwoofer.input";
    private const string SubOutputNodeName = "bassrouter.subwoofer.output";
    private const string FilterChainModule = "libpipewire-module-filter-chain";
    private const string DefaultSinkKey = "default.audio.sink";
    private const string ConfiguredDefaultSinkKey = "default.configured.audio.sink";
    private const float LowPassQ = 0.7f;
    private static readonly string[] OwnNodeNames = [SinkNodeName, HeadphoneInputNodeName, HeadphoneOutputNodeName, SubInputNodeName, SubOutputNodeName];
    private const int MaxDelayMs = 5000;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);

    private static readonly Lazy<bool> Initialized = new(() =>
    {
        pw_init(0, 0);
        return true;
    });

    // PipeWire objects. Only touched with the thread loop locked.
    private readonly nint Loop;
    private readonly nint Context;
    private readonly nint CoreEvents;
    private readonly nint RegistryEvents;
    private readonly nint MetadataEvents;
    private readonly nint HeadphoneModuleEvents;
    private readonly nint SubModuleEvents;
    private readonly nint CoreHook;
    private readonly nint RegistryHook;
    private readonly nint MetadataHook;
    private readonly nint HeadphoneModuleHook;
    private readonly nint SubModuleHook;
    private GCHandle SelfHandle;
    private nint Core;
    private nint Registry;
    private nint Metadata;
    private uint MetadataId = IdAny;
    private nint SinkProxy;
    private nint HeadphoneModule;
    private nint SubModule;
    private nint HeadphoneControls;
    private nint SubControls;
    private bool OwnsDefaultSink;

    // State shared with PipeWire callbacks. StateLock is a leaf lock: never lock the thread loop while holding it.
    private readonly object StateLock = new();
    private readonly Dictionary<uint, PwNode> Nodes = new();
    private readonly HashSet<int> CompletedSyncs = new();
    private bool Connected;
    private string? DefaultSinkName;
    private string? ConfiguredDefaultSink;
    private bool Routing;
    private bool RoutingInterrupted;
    private int RoutingSession;
    private bool OwnSinkWasDefault;
    private string[] RoutingTargets = [];

    // Engine state. Lock order: EngineLock -> thread loop -> StateLock.
    private readonly object EngineLock = new();
    private bool _IsRunning;
    private string? HeadphoneDeviceId;
    private string? SubDeviceId;
    private int HeadphoneDelayMs;
    private int SubDelayMs;
    private float LowPassFrequency = 120f;
    private float HeadphoneVolume = 1f;
    private float SubVolume = 1f;
    private string? LastErrorMessage;
    private string? SavedConfiguredDefaultSink;

    private readonly Timer DevicesChangedTimer;
    private readonly Timer ReconnectTimer;
    private bool Disposed;

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioEngineStateChangedEventArgs>? StateChanged;
    public event EventHandler<string>? Error;
    public event EventHandler<string>? StopRequested;

    public bool IsRunning
    {
        get { lock (EngineLock) return _IsRunning; }
    }

    public PipeWireAudioBackend()
    {
        _ = Initialized.Value;

        DevicesChangedTimer = new Timer(_ => RaiseDevicesChanged(), null, Timeout.Infinite, Timeout.Infinite);
        ReconnectTimer = new Timer(_ => TryReconnect(), null, Timeout.Infinite, Timeout.Infinite);

        SelfHandle = GCHandle.Alloc(this);

        CoreEvents = AllocEvents(0,
            0,                                                                                  // info
            (nint)(delegate* unmanaged[Cdecl]<nint, uint, int, void>)&OnCoreDone,               // done
            0,                                                                                  // ping
            (nint)(delegate* unmanaged[Cdecl]<nint, uint, int, int, nint, void>)&OnCoreError);  // error

        RegistryEvents = AllocEvents(0,
            (nint)(delegate* unmanaged[Cdecl]<nint, uint, uint, nint, uint, nint, void>)&OnRegistryGlobal,
            (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)&OnRegistryGlobalRemove);

        MetadataEvents = AllocEvents(0,
            (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, nint, nint, int>)&OnMetadataProperty);

        HeadphoneModuleEvents = AllocEvents(0, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnHeadphoneModuleDestroy);
        SubModuleEvents = AllocEvents(0, (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnSubModuleDestroy);

        CoreHook = AllocHook();
        RegistryHook = AllocHook();
        MetadataHook = AllocHook();
        HeadphoneModuleHook = AllocHook();
        SubModuleHook = AllocHook();

        Loop = pw_thread_loop_new("bassrouter", 0);
        if (Loop == 0)
            throw new InvalidOperationException("Failed to create the PipeWire thread loop.");

        Context = pw_context_new(pw_thread_loop_get_loop(Loop), 0, 0);
        if (Context == 0)
            throw new InvalidOperationException("Failed to create the PipeWire context.");

        if (pw_thread_loop_start(Loop) < 0)
            throw new InvalidOperationException("Failed to start the PipeWire thread loop.");

        lock (EngineLock)
        {
            if (!TryConnect())
                SetTimer(ReconnectTimer, ReconnectInterval, ReconnectInterval);
        }
    }

    #region Public API

    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        lock (StateLock)
        {
            return Nodes.Values
                .Where(IsOutputDevice)
                .OrderBy(node => node.Id)
                .Select(node => new AudioOutputDevice(node.Name!, node.DisplayName))
                .ToArray();
        }
    }

    public AudioEngineState GetState()
    {
        lock (EngineLock)
        {
            return CreateState_NoLock();
        }
    }

    public void SetHeadphoneDevice(string? deviceId)
    {
        UpdateAndPublish(() => HeadphoneDeviceId = ResolveDeviceId(deviceId));
    }

    public void SetSubDevice(string? deviceId)
    {
        UpdateAndPublish(() => SubDeviceId = ResolveDeviceId(deviceId));
    }

    public void SetHeadphoneDelayMs(int milliseconds)
    {
        UpdateAndPublish(() =>
        {
            HeadphoneDelayMs = Math.Clamp(milliseconds, 0, MaxDelayMs);
            ApplyHeadphoneControls_NoLock();
        });
    }

    public void SetSubDelayMs(int milliseconds)
    {
        UpdateAndPublish(() =>
        {
            SubDelayMs = Math.Clamp(milliseconds, 0, MaxDelayMs);
            ApplySubControls_NoLock();
        });
    }

    public void SetLowPassFrequency(float frequency)
    {
        UpdateAndPublish(() =>
        {
            LowPassFrequency = Math.Clamp(frequency, 10f, 300f);
            ApplySubControls_NoLock();
        });
    }

    public void SetHeadphoneVolume(float volume)
    {
        UpdateAndPublish(() =>
        {
            HeadphoneVolume = Math.Clamp(volume, 0f, 1f);
            ApplyHeadphoneControls_NoLock();
        });
    }

    public void SetSubVolume(float volume)
    {
        UpdateAndPublish(() =>
        {
            SubVolume = Math.Clamp(volume, 0f, 1f);
            ApplySubControls_NoLock();
        });
    }

    public void Start()
    {
        AudioEngineState? state = null;
        Exception? exception = null;

        lock (EngineLock)
        {
            if (_IsRunning)
                return;

            // Without a connection there are no devices either, so report the actual cause
            if (!Connected && !TryConnect())
                throw new InvalidOperationException("Couldn't connect to PipeWire. Make sure PipeWire is running.");

            if (HeadphoneDeviceId == null)
                throw new InvalidOperationException("Primary audio output device is not set.");
            if (SubDeviceId == null)
                throw new InvalidOperationException("Subwoofer audio output device is not set.");

            try
            {
                PwNode headphones = FindOutputDevice(HeadphoneDeviceId)
                    ?? throw new InvalidOperationException("Primary audio output device is not available.");
                PwNode sub = FindOutputDevice(SubDeviceId)
                    ?? throw new InvalidOperationException("Subwoofer audio output device is not available.");

                StartRouting(headphones.Name!, sub.Name!);

                _IsRunning = true;
                LastErrorMessage = null;
                state = CreateState_NoLock();
            }
            catch (Exception ex)
            {
                StopRouting();
                LastErrorMessage = ex.Message;
                state = CreateState_NoLock();
                exception = ex;
            }
        }

        if (state != null)
            PublishState(state);

        if (exception != null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    public void Stop()
    {
        AudioEngineState state;

        lock (EngineLock)
        {
            if (!_IsRunning)
                return;

            _IsRunning = false;
            LastErrorMessage = null;
            StopRouting();
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    public void Dispose()
    {
        lock (EngineLock)
        {
            if (Disposed)
                return;

            Disposed = true;
        }

        ReconnectTimer.Dispose();
        DevicesChangedTimer.Dispose();

        lock (EngineLock)
        {
            _IsRunning = false;
            StopRouting();

            // Make sure the default device restore actually reaches PipeWire before the connection goes away
            if (Connected)
                Roundtrip();

            Disconnect();
        }

        pw_thread_loop_stop(Loop);
        pw_context_destroy(Context);
        pw_thread_loop_destroy(Loop);

        FreeHook(CoreHook);
        FreeHook(RegistryHook);
        FreeHook(MetadataHook);
        FreeHook(HeadphoneModuleHook);
        FreeHook(SubModuleHook);
        NativeMemory.Free((void*)CoreEvents);
        NativeMemory.Free((void*)RegistryEvents);
        NativeMemory.Free((void*)MetadataEvents);
        NativeMemory.Free((void*)HeadphoneModuleEvents);
        NativeMemory.Free((void*)SubModuleEvents);
        SelfHandle.Free();
    }

    #endregion

    #region Connection

    /// <summary>
    /// Connects to the PipeWire daemon and waits until the initial device list and default devices are known.
    /// Must be called with EngineLock held.
    /// </summary>
    private bool TryConnect()
    {
        if (Connected)
            return true;

        pw_thread_loop_lock(Loop);
        try
        {
            nint props = CreateProperties([
                new("application.name", "BassRouter"),
                new("application.id", "com.samsidparty.bassrouter"),
                new("application.icon-name", "audio-speakers"),
            ]);

            // pw_context_connect takes ownership of props
            Core = pw_context_connect(Context, props, 0);
            if (Core == 0)
                return false;

            CoreAddListener(Core, CoreHook, CoreEvents, GCHandle.ToIntPtr(SelfHandle));

            Registry = CoreGetRegistry(Core, VersionRegistry);
            RegistryAddListener(Registry, RegistryHook, RegistryEvents, GCHandle.ToIntPtr(SelfHandle));
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        lock (StateLock)
        {
            Connected = true;
        }

        // The first roundtrip delivers all existing globals (and binds the default metadata),
        // the second delivers the metadata's current properties (default devices).
        if (!Roundtrip() || !Roundtrip())
        {
            Disconnect();
            return false;
        }

        RestoreLeftoverDefaultSink();
        return true;
    }

    private void TryReconnect()
    {
        bool reconnected;

        try
        {
            lock (EngineLock)
            {
                if (Disposed || Connected)
                {
                    SetTimer(ReconnectTimer, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    return;
                }

                reconnected = TryConnect();
            }
        }
        catch (Exception ex)
        {
            // Runs on a timer thread, where an exception would take down the process
            Console.Error.WriteLine($"BassRouter: reconnecting to PipeWire failed: {ex}");
            return;
        }

        if (reconnected)
        {
            SetTimer(ReconnectTimer, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ScheduleDevicesChanged();
        }
    }

    /// <summary>
    /// Drops the connection to the PipeWire daemon. Must be called with EngineLock held, after StopRouting().
    /// </summary>
    private void Disconnect()
    {
        pw_thread_loop_lock(Loop);
        try
        {
            if (Metadata != 0)
            {
                RemoveHook(MetadataHook);
                pw_proxy_destroy(Metadata);
                Metadata = 0;
                MetadataId = IdAny;
            }

            if (Registry != 0)
            {
                RemoveHook(RegistryHook);
                pw_proxy_destroy(Registry);
                Registry = 0;
            }

            if (Core != 0)
            {
                RemoveHook(CoreHook);
                pw_core_disconnect(Core);
                Core = 0;
            }
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        lock (StateLock)
        {
            Connected = false;
            Nodes.Clear();
            CompletedSyncs.Clear();
            DefaultSinkName = null;
            ConfiguredDefaultSink = null;
            Monitor.PulseAll(StateLock);
        }
    }

    private void HandleDisconnected(string message)
    {
        bool wasRunning;
        AudioEngineState state;

        lock (EngineLock)
        {
            if (Disposed || !Connected)
                return;

            wasRunning = _IsRunning;
            if (_IsRunning)
            {
                _IsRunning = false;
                LastErrorMessage = message;
            }

            StopRouting(waitForCleanup: false);
            Disconnect();
            state = CreateState_NoLock();
            SetTimer(ReconnectTimer, ReconnectInterval, ReconnectInterval);
        }

        PublishState(state);
        ScheduleDevicesChanged();

        if (wasRunning)
            Error?.Invoke(this, message);
    }

    /// <summary>
    /// Waits until the daemon has processed everything sent so far.
    /// </summary>
    private bool Roundtrip()
    {
        int seq;

        pw_thread_loop_lock(Loop);
        try
        {
            if (Core == 0)
                return false;

            seq = CoreSync(Core, IdCore, 0);
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        return WaitFor(() => !Connected || CompletedSyncs.Remove(seq)) && Connected;
    }

    /// <summary>
    /// Waits for a condition on the shared state, which is re-evaluated whenever PipeWire reports a change.
    /// </summary>
    private bool WaitFor(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + (long)OperationTimeout.TotalMilliseconds;

        lock (StateLock)
        {
            while (!condition())
            {
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return false;

                Monitor.Wait(StateLock, (int)remaining);
            }

            return true;
        }
    }

    #endregion

    #region Routing

    /// <summary>
    /// Creates the virtual sink and filter chains, then makes the virtual sink the default output.
    /// Must be called with EngineLock held. On failure, the caller cleans up with StopRouting().
    /// </summary>
    private void StartRouting(string headphoneTarget, string subTarget)
    {
        pw_thread_loop_lock(Loop);
        try
        {
            if (Metadata == 0)
                throw new InvalidOperationException("PipeWire has no default device metadata. Make sure a session manager such as WirePlumber is running.");

            nint props = CreateProperties([
                new("factory.name", "support.null-audio-sink"),
                new("node.name", SinkNodeName),
                new("node.description", SinkDescription),
                new("media.class", "Audio/Sink"),
                new("audio.position", "[ FL FR ]"),
                new("monitor.channel-volumes", "true"),
                new("object.linger", "false"),
            ]);

            try
            {
                SinkProxy = CoreCreateObject(Core, "adapter", TypeNode, VersionNode, props);
            }
            finally
            {
                pw_properties_free(props);
            }

            if (SinkProxy == 0)
                throw new InvalidOperationException("Failed to create the BassRouter virtual output device.");
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        if (WaitForNode(SinkNodeName) == null)
            throw new InvalidOperationException("Timed out waiting for the BassRouter virtual output device to appear.");

        pw_thread_loop_lock(Loop);
        try
        {
            // Filter chains destroy themselves when their streams get disconnected, so track that
            // instead of assuming the module pointers stay valid until StopRouting()
            HeadphoneModule = LoadModule(FilterChainModule, BuildFilterChainArgs(
                description: "BassRouter Headphones",
                inputNodeName: HeadphoneInputNodeName,
                outputNodeName: HeadphoneOutputNodeName,
                target: headphoneTarget,
                lowPass: false));
            pw_impl_module_add_listener(HeadphoneModule, HeadphoneModuleHook, HeadphoneModuleEvents, GCHandle.ToIntPtr(SelfHandle));

            SubModule = LoadModule(FilterChainModule, BuildFilterChainArgs(
                description: "BassRouter Subwoofer",
                inputNodeName: SubInputNodeName,
                outputNodeName: SubOutputNodeName,
                target: subTarget,
                lowPass: true));
            pw_impl_module_add_listener(SubModule, SubModuleHook, SubModuleEvents, GCHandle.ToIntPtr(SelfHandle));
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        PwNode headphoneInput = WaitForNode(HeadphoneInputNodeName)
            ?? throw new InvalidOperationException("Timed out waiting for the headphone filter to appear.");
        PwNode subInput = WaitForNode(SubInputNodeName)
            ?? throw new InvalidOperationException("Timed out waiting for the subwoofer filter to appear.");

        pw_thread_loop_lock(Loop);
        try
        {
            HeadphoneControls = RegistryBind(Registry, headphoneInput.Id, TypeNode, VersionNode);
            SubControls = RegistryBind(Registry, subInput.Id, TypeNode, VersionNode);

            lock (StateLock)
            {
                Routing = true;
                RoutingInterrupted = false;
                RoutingSession++;
                OwnSinkWasDefault = DefaultSinkName == SinkNodeName;
                RoutingTargets = [SinkNodeName, HeadphoneInputNodeName, HeadphoneOutputNodeName, SubInputNodeName, SubOutputNodeName, headphoneTarget, subTarget];

                // If the configured default still points at our own sink (left over from a crash or a PipeWire
                // restart), keep whatever was saved before rather than "restoring" our own sink later
                if (ParseNodeName(ConfiguredDefaultSink) != SinkNodeName)
                    SavedConfiguredDefaultSink = ConfiguredDefaultSink;
            }

            int result = MetadataSetProperty(Metadata, IdCore, ConfiguredDefaultSinkKey, "Spa:String:JSON", ToNodeNameJson(SinkNodeName));
            if (result < 0)
                throw new InvalidOperationException($"Failed to switch the default output to {SinkDescription}: {Marshal.GetPInvokeErrorMessage(-result)}");

            OwnsDefaultSink = true;
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        // The session manager picks the actual default asynchronously
        if (!WaitFor(() => !Connected || DefaultSinkName == SinkNodeName) || !Connected)
            throw new InvalidOperationException($"The session manager didn't switch the default output to {SinkDescription}.");
    }

    /// <summary>
    /// Restores the previous default output and destroys everything StartRouting() created.
    /// Must be called with EngineLock held. Safe to call when nothing is running.
    /// </summary>
    /// <param name="waitForCleanup">
    /// Wait until PipeWire has actually removed our nodes, so an immediate restart doesn't see stale ones.
    /// Pointless when the connection is already broken.
    /// </param>
    private void StopRouting(bool waitForCleanup = true)
    {
        string? configuredDefault;

        lock (StateLock)
        {
            Routing = false;
            RoutingTargets = [];
            configuredDefault = ConfiguredDefaultSink;
        }

        bool createdNodes;

        pw_thread_loop_lock(Loop);
        try
        {
            createdNodes = SinkProxy != 0 || HeadphoneModule != 0 || SubModule != 0;

            // Restore the previous default first so streams move straight to it instead of a fallback device.
            // If the user picked a different default themselves in the meantime, leave their choice alone.
            // (The configured value may also still be the saved one if our own change hasn't been echoed back yet.)
            bool defaultStillOurs = ParseNodeName(configuredDefault) == SinkNodeName || configuredDefault == SavedConfiguredDefaultSink;
            if (OwnsDefaultSink && defaultStillOurs)
                RestoreSavedDefaultSink_NoLock();

            OwnsDefaultSink = false;

            DestroyProxy(ref HeadphoneControls);
            DestroyProxy(ref SubControls);

            if (HeadphoneModule != 0)
            {
                pw_impl_module_destroy(HeadphoneModule);
                HeadphoneModule = 0;
            }

            if (SubModule != 0)
            {
                pw_impl_module_destroy(SubModule);
                SubModule = 0;
            }

            DestroyProxy(ref SinkProxy);
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }

        if (waitForCleanup && createdNodes && Connected)
        {
            WaitFor(() => !Connected ||
                (DefaultSinkName != SinkNodeName && !Nodes.Values.Any(node => node.Name != null && OwnNodeNames.Contains(node.Name))));
        }
    }

    /// <summary>
    /// Puts back the default output that was configured before BassRouter took over.
    /// Must be called with the thread loop locked.
    /// </summary>
    private void RestoreSavedDefaultSink_NoLock()
    {
        if (Metadata == 0)
            return;

        MetadataSetProperty(
            Metadata,
            IdCore,
            ConfiguredDefaultSinkKey,
            SavedConfiguredDefaultSink == null ? null : "Spa:String:JSON",
            SavedConfiguredDefaultSink);
    }

    /// <summary>
    /// If the configured default still points at our sink while nothing is routing (after a crash or a PipeWire
    /// restart), the session manager is falling back to some other device anyway. Put the original choice back,
    /// or clear the stale value if it isn't known. Must be called with EngineLock held.
    /// </summary>
    private void RestoreLeftoverDefaultSink()
    {
        if (_IsRunning)
            return;

        string? configuredDefault;
        lock (StateLock)
        {
            configuredDefault = ConfiguredDefaultSink;
        }

        if (ParseNodeName(configuredDefault) != SinkNodeName)
            return;

        pw_thread_loop_lock(Loop);
        try
        {
            RestoreSavedDefaultSink_NoLock();
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }
    }

    /// <param name="session">The routing session the error belongs to. Errors from an older session are ignored.</param>
    private void HandleError(string message, int session)
    {
        bool wasRunning;
        AudioEngineState state;

        lock (EngineLock)
        {
            if (Disposed)
                return;

            lock (StateLock)
            {
                if (session != RoutingSession)
                    return;
            }

            wasRunning = _IsRunning;
            if (_IsRunning)
            {
                _IsRunning = false;
                StopRouting();
            }

            LastErrorMessage = message;
            state = CreateState_NoLock();
        }

        PublishState(state);

        if (wasRunning)
            Error?.Invoke(this, message);
    }

    private nint LoadModule(string name, string args)
    {
        nint module = pw_context_load_module(Context, name, args, 0);
        if (module == 0)
        {
            string reason = Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());
            throw new InvalidOperationException($"Failed to load the PipeWire filter chain: {reason}");
        }

        return module;
    }

    private static void DestroyProxy(ref nint proxy)
    {
        if (proxy == 0)
            return;

        pw_proxy_destroy(proxy);
        proxy = 0;
    }

    private PwNode? WaitForNode(string nodeName)
    {
        PwNode? node = null;
        WaitFor(() => !Connected || (node = Nodes.Values.FirstOrDefault(n => n.Name == nodeName)) != null);
        return node;
    }

    /// <summary>
    /// Builds the filter-chain module arguments. Each channel gets its own chain of nodes so the controls
    /// can be addressed explicitly (lowpass_l:Freq, delay_r:Delay (s), ...) when they're changed live.
    /// </summary>
    private string BuildFilterChainArgs(string description, string inputNodeName, string outputNodeName, string target, bool lowPass)
    {
        string maxDelay = Format(MaxDelayMs / 1000f);
        string delay = Format((lowPass ? SubDelayMs : HeadphoneDelayMs) / 1000f);
        string gain = Format(VolumeToGain(lowPass ? SubVolume : HeadphoneVolume));
        string frequency = Format(LowPassFrequency);
        string q = Format(LowPassQ);

        var nodes = new StringBuilder();
        var links = new StringBuilder();

        foreach (string channel in new[] { "l", "r" })
        {
            if (lowPass)
            {
                nodes.AppendLine($$"""{ type = builtin name = lowpass_{{channel}} label = bq_lowpass control = { "Freq" = {{frequency}} "Q" = {{q}} } }""");
                links.AppendLine($$"""{ output = "lowpass_{{channel}}:Out" input = "delay_{{channel}}:In" }""");
            }

            nodes.AppendLine($$"""{ type = builtin name = delay_{{channel}} label = delay config = { "max-delay" = {{maxDelay}} } control = { "Delay (s)" = {{delay}} } }""");
            // A single input mixer is used as the gain stage since it's available in older PipeWire versions than "linear"
            nodes.AppendLine($$"""{ type = builtin name = gain_{{channel}} label = mixer control = { "Gain 1" = {{gain}} } }""");
            links.AppendLine($$"""{ output = "delay_{{channel}}:Out" input = "gain_{{channel}}:In 1" }""");
        }

        string firstNode = lowPass ? "lowpass" : "delay";

        // node.dont-* keeps WirePlumber from ever relinking these streams somewhere else (e.g. into our own sink,
        // which would create a feedback loop). If a target disappears, routing is stopped instead.
        return $$"""
            {
                node.description = {{Quote(description)}}
                media.name = {{Quote(description)}}
                audio.channels = 2
                audio.position = [ FL FR ]
                filter.graph = {
                    nodes = [
                        {{nodes}}
                    ]
                    links = [
                        {{links}}
                    ]
                    inputs = [ "{{firstNode}}_l:In" "{{firstNode}}_r:In" ]
                    outputs = [ "gain_l:Out" "gain_r:Out" ]
                }
                capture.props = {
                    node.name = {{Quote(inputNodeName)}}
                    node.description = {{Quote(description + " Input")}}
                    target.object = {{Quote(SinkNodeName)}}
                    stream.capture.sink = true
                    node.passive = true
                    node.dont-reconnect = true
                    node.dont-fallback = true
                    node.dont-move = true
                }
                playback.props = {
                    node.name = {{Quote(outputNodeName)}}
                    target.object = {{Quote(target)}}
                    node.dont-reconnect = true
                    node.dont-fallback = true
                    node.dont-move = true
                }
            }
            """;
    }

    private void ApplyHeadphoneControls_NoLock()
    {
        float delay = HeadphoneDelayMs / 1000f;
        float gain = VolumeToGain(HeadphoneVolume);

        SetControls(HeadphoneControls, [
            new("delay_l:Delay (s)", delay),
            new("delay_r:Delay (s)", delay),
            new("gain_l:Gain 1", gain),
            new("gain_r:Gain 1", gain),
        ]);
    }

    private void ApplySubControls_NoLock()
    {
        float delay = SubDelayMs / 1000f;
        float gain = VolumeToGain(SubVolume);

        SetControls(SubControls, [
            new("lowpass_l:Freq", LowPassFrequency),
            new("lowpass_r:Freq", LowPassFrequency),
            new("delay_l:Delay (s)", delay),
            new("delay_r:Delay (s)", delay),
            new("gain_l:Gain 1", gain),
            new("gain_r:Gain 1", gain),
        ]);
    }

    private void SetControls(nint node, KeyValuePair<string, float>[] controls)
    {
        if (!_IsRunning)
            return;

        byte[] pod = PwPropsParams.Build(controls);

        pw_thread_loop_lock(Loop);
        try
        {
            if (node != 0)
                NodeSetParam(node, PwPropsParams.SpaParamProps, 0, pod);
        }
        finally
        {
            pw_thread_loop_unlock(Loop);
        }
    }

    /// <summary>
    /// Maps a volume slider position to a linear gain using the same cubic curve as PipeWire/PulseAudio mixers.
    /// </summary>
    private static float VolumeToGain(float volume)
    {
        float clamped = Math.Clamp(volume, 0f, 1f);
        return clamped * clamped * clamped;
    }

    #endregion

    #region State

    private void UpdateAndPublish(Action update)
    {
        AudioEngineState state;
        lock (EngineLock)
        {
            update();
            LastErrorMessage = null;
            state = CreateState_NoLock();
        }

        PublishState(state);
    }

    private AudioEngineState CreateState_NoLock()
    {
        return new AudioEngineState(
            IsRunning: _IsRunning,
            HeadphoneDeviceId: HeadphoneDeviceId,
            HeadphoneDeviceName: FindOutputDevice(HeadphoneDeviceId)?.DisplayName,
            SubDeviceId: SubDeviceId,
            SubDeviceName: FindOutputDevice(SubDeviceId)?.DisplayName,
            HeadphoneDelayMs: HeadphoneDelayMs,
            SubDelayMs: SubDelayMs,
            LowPassFrequency: LowPassFrequency,
            HeadphoneVolume: HeadphoneVolume,
            SubVolume: SubVolume,
            LastErrorMessage: LastErrorMessage);
    }

    private void PublishState(AudioEngineState state)
    {
        StateChanged?.Invoke(this, new AudioEngineStateChangedEventArgs(state));
    }

    /// <summary>
    /// Unknown devices are cleared, like on Windows. While disconnected nothing can be checked,
    /// so the saved device is kept until PipeWire is back.
    /// </summary>
    private string? ResolveDeviceId(string? deviceId)
    {
        return Connected ? FindOutputDevice(deviceId)?.Name : deviceId;
    }

    private PwNode? FindOutputDevice(string? nodeName)
    {
        if (string.IsNullOrWhiteSpace(nodeName))
            return null;

        lock (StateLock)
        {
            return Nodes.Values.FirstOrDefault(node => node.Name == nodeName && IsOutputDevice(node));
        }
    }

    private static bool IsOutputDevice(PwNode node)
    {
        return node.MediaClass == "Audio/Sink" && node.Name != null && node.Name != SinkNodeName;
    }

    private void ScheduleDevicesChanged()
    {
        // PipeWire announces objects one at a time, so coalesce bursts into a single notification
        SetTimer(DevicesChangedTimer, TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
    }

    private void RaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: DevicesChanged handler failed: {ex}");
        }
    }

    /// <summary>
    /// Timer callbacks can still be running while the backend is disposed.
    /// </summary>
    private static void SetTimer(Timer timer, TimeSpan dueTime, TimeSpan period)
    {
        try
        {
            timer.Change(dueTime, period);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static string? ParseNodeName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("name", out JsonElement name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ToNodeNameJson(string nodeName)
    {
        return JsonSerializer.Serialize(new Dictionary<string, string> { ["name"] = nodeName });
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Format(float value)
    {
        return value.ToString("0.0#####", CultureInfo.InvariantCulture);
    }

    #endregion

    #region PipeWire callbacks (run on the thread loop with the loop locked)

    private void HandleCoreDone(uint id, int seq)
    {
        if (id != IdCore)
            return;

        lock (StateLock)
        {
            CompletedSyncs.Add(seq);
            Monitor.PulseAll(StateLock);
        }
    }

    private void HandleCoreError(uint id, int res, string? message)
    {
        // Only a broken connection (e.g. PipeWire restarted) is fatal
        if (id == IdCore && res == -EPIPE)
        {
            Task.Run(() => HandleDisconnected("Lost connection to PipeWire."));
            return;
        }

        // ENOENT happens when destroying a proxy whose object is already gone. The filter chains use their own
        // connections, so their nodes can disappear before our destroy request for the bound node arrives.
        if (res != -ENOENT)
            Console.Error.WriteLine($"BassRouter: PipeWire error on object {id}: {message} ({res})");
    }

    private void HandleModuleDestroyed(bool subwoofer)
    {
        ref nint module = ref subwoofer ? ref SubModule : ref HeadphoneModule;
        RemoveHook(subwoofer ? SubModuleHook : HeadphoneModuleHook);
        module = 0;

        bool interruptsRouting;
        int session;
        lock (StateLock)
        {
            interruptsRouting = Routing && !RoutingInterrupted;
            if (interruptsRouting)
                RoutingInterrupted = true;

            session = RoutingSession;
        }

        if (interruptsRouting)
        {
            string output = subwoofer ? "subwoofer" : "headphone";
            Task.Run(() => HandleError($"The {output} filter was disconnected by PipeWire. Audio routing has been stopped.", session));
        }
    }

    private void HandleGlobal(uint id, string? type, nint props)
    {
        if (type == TypeNode)
        {
            Dictionary<string, string> values = ReadDict(props);
            var node = new PwNode(
                id,
                values.GetValueOrDefault("node.name"),
                values.GetValueOrDefault("node.description"),
                values.GetValueOrDefault("node.nick"),
                values.GetValueOrDefault("media.class"));

            lock (StateLock)
            {
                Nodes[id] = node;
                Monitor.PulseAll(StateLock);
            }

            if (IsOutputDevice(node))
                ScheduleDevicesChanged();
        }
        else if (type == TypeMetadata && Metadata == 0)
        {
            Dictionary<string, string> values = ReadDict(props);
            if (values.GetValueOrDefault("metadata.name") != "default")
                return;

            Metadata = RegistryBind(Registry, id, TypeMetadata, VersionMetadata);
            if (Metadata == 0)
                return;

            MetadataId = id;
            MetadataAddListener(Metadata, MetadataHook, MetadataEvents, GCHandle.ToIntPtr(SelfHandle));
        }
    }

    private void HandleGlobalRemove(uint id)
    {
        if (id == MetadataId && Metadata != 0)
        {
            RemoveHook(MetadataHook);
            pw_proxy_destroy(Metadata);
            Metadata = 0;
            MetadataId = IdAny;
        }

        PwNode? node;
        bool interruptsRouting = false;
        int session;

        lock (StateLock)
        {
            if (!Nodes.Remove(id, out node))
                return;

            session = RoutingSession;

            if (Routing && !RoutingInterrupted && node.Name != null && RoutingTargets.Contains(node.Name))
            {
                RoutingInterrupted = true;
                interruptsRouting = true;
            }
        }

        if (IsOutputDevice(node))
            ScheduleDevicesChanged();

        if (interruptsRouting)
        {
            string message = IsOutputDevice(node)
                ? $"{node.DisplayName} was disconnected. Audio routing has been stopped."
                : "A BassRouter audio node was removed unexpectedly. Audio routing has been stopped.";

            Task.Run(() => HandleError(message, session));
        }
    }

    private void HandleMetadataProperty(uint subject, string? key, string? value)
    {
        if (subject != IdCore)
            return;

        bool defaultChangedAway = false;

        lock (StateLock)
        {
            if (key == null)
            {
                DefaultSinkName = null;
                ConfiguredDefaultSink = null;
            }
            else if (key == ConfiguredDefaultSinkKey)
            {
                ConfiguredDefaultSink = value;
            }
            else if (key == DefaultSinkKey)
            {
                DefaultSinkName = ParseNodeName(value);

                if (Routing && !RoutingInterrupted)
                {
                    if (DefaultSinkName == SinkNodeName)
                    {
                        OwnSinkWasDefault = true;
                    }
                    else if (OwnSinkWasDefault)
                    {
                        RoutingInterrupted = true;
                        defaultChangedAway = true;
                    }
                }
            }

            Monitor.PulseAll(StateLock);
        }

        if (defaultChangedAway)
            Task.Run(() => StopRequested?.Invoke(this, "Default output device changed. Audio routing has been stopped."));
    }

    private static PipeWireAudioBackend? FromUserData(nint data)
    {
        return data == 0 ? null : GCHandle.FromIntPtr(data).Target as PipeWireAudioBackend;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCoreDone(nint data, uint id, int seq)
    {
        try
        {
            FromUserData(data)?.HandleCoreDone(id, seq);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: core done callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCoreError(nint data, uint id, int seq, int res, nint message)
    {
        try
        {
            FromUserData(data)?.HandleCoreError(id, res, Marshal.PtrToStringUTF8(message));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: core error callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegistryGlobal(nint data, uint id, uint permissions, nint type, uint version, nint props)
    {
        try
        {
            FromUserData(data)?.HandleGlobal(id, Marshal.PtrToStringUTF8(type), props);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: registry global callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRegistryGlobalRemove(nint data, uint id)
    {
        try
        {
            FromUserData(data)?.HandleGlobalRemove(id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: registry global_remove callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnHeadphoneModuleDestroy(nint data)
    {
        try
        {
            FromUserData(data)?.HandleModuleDestroyed(subwoofer: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: module destroy callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSubModuleDestroy(nint data)
    {
        try
        {
            FromUserData(data)?.HandleModuleDestroyed(subwoofer: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: module destroy callback failed: {ex}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnMetadataProperty(nint data, uint subject, nint key, nint type, nint value)
    {
        try
        {
            FromUserData(data)?.HandleMetadataProperty(subject, Marshal.PtrToStringUTF8(key), Marshal.PtrToStringUTF8(value));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"BassRouter: metadata callback failed: {ex}");
        }

        return 0;
    }

    #endregion

    private sealed record PwNode(uint Id, string? Name, string? Description, string? Nick, string? MediaClass)
    {
        public string DisplayName => Description ?? Nick ?? Name ?? $"Node {Id}";
    }
}
