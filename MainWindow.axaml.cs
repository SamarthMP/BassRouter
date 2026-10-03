using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using BassRouter.Audio;
using SamsidParty;

namespace BassRouter;

public partial class MainWindow : Window
{
    private readonly AudioEngineController Controller;
    private readonly HashSet<Slider> DraggingSliders = new();
    private List<AudioOutputDevice> Devices = new();
    private bool ShouldIgnoreControlEvents;
    private bool AllowClose;

    private static readonly IBrush StartBrush = new SolidColorBrush(Color.FromRgb(10, 122, 62));
    private static readonly IBrush StopBrush = new SolidColorBrush(Color.FromRgb(180, 40, 40));
    private static readonly IBrush RunningBrush = new SolidColorBrush(Color.FromRgb(80, 220, 80));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(255, 100, 100));
    private static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(144, 144, 168));
    private static readonly IBrush TitleBarBlurBrush = new SolidColorBrush(Color.FromArgb(0x3C, 0x46, 0x46, 0x46));
    private static readonly IBrush TitleBarOpaqueBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2B, 0x2E));

    /// <summary>
    /// Whether a system tray is available. When there isn't one, minimizing and closing behave like a normal window.
    /// Checked each time since the tray may only show up after the app started (e.g. when autostarted at login).
    /// </summary>
    public Func<bool> IsTrayAvailable { get; init; } = () => true;

    public MainWindow(AudioEngineController controller)
    {
        Controller = controller;

        InitializeComponent();

        // Blur behind the title bar, like the WPF version. Falls back to an opaque title bar if unsupported.
        TransparencyLevelHint = [WindowTransparencyLevel.Blur, WindowTransparencyLevel.AcrylicBlur];
        UpdateTitleBarBackground();

        Opened += (s, e) =>
        {
            if (OperatingSystem.IsWindows() && TryGetPlatformHandle()?.Handle is nint handle)
                WindowHelpers.FixWindows11Corners(handle);
        };

        WireEvents();
        RefreshDeviceList();
        ApplyEngineState(Controller.GetState());
    }

    private void WireEvents()
    {
        Controller.DevicesChanged += OnDevicesChanged;
        Controller.StateChanged += OnEngineStateChanged;
        Controller.Warning += OnControllerWarning;

        ComboPrimaryDevice.SelectionChanged += OnPrimaryDeviceChanged;
        ComboSubDevice.SelectionChanged += OnSubDeviceChanged;

        SliderPrimaryLatency.ValueChanged += OnPrimaryLatencyChanged;
        SliderPrimaryVolume.ValueChanged += OnPrimaryVolumeChanged;

        SliderSubLatency.ValueChanged += OnSubLatencyChanged;
        SliderSubVolume.ValueChanged += OnSubVolumeChanged;
        SliderLowPass.ValueChanged += OnLowPassChanged;

        foreach (Slider slider in new[] { SliderPrimaryLatency, SliderPrimaryVolume, SliderSubLatency, SliderSubVolume, SliderLowPass })
            TrackDragging(slider);

        ButtonStartStop.Click += OnStartStopClicked;
        ButtonMinimize.Click += OnMinimizeClicked;
        ButtonClose.Click += OnCloseClicked;
        TitleBar.PointerPressed += OnTitleBarPointerPressed;

        Closing += OnWindowClosing;
    }

    private void TrackDragging(Slider slider)
    {
        // The slider handles these events itself, so listen for handled ones too
        slider.AddHandler(PointerPressedEvent, (_, _) => DraggingSliders.Add(slider), RoutingStrategies.Tunnel, handledEventsToo: true);
        slider.AddHandler(PointerReleasedEvent, (_, _) => DraggingSliders.Remove(slider), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        slider.AddHandler(PointerCaptureLostEvent, (_, _) => DraggingSliders.Remove(slider), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
    }

    // ── Device list management ──────────────────────────────

    private void RefreshDeviceList()
    {
        Devices = Controller.GetOutputDevices().ToList();
        AudioEngineState state = Controller.GetState();

        // Clearing the lists briefly deselects everything, which shouldn't be sent to the engine
        ShouldIgnoreControlEvents = true;
        try
        {
            ComboPrimaryDevice.Items.Clear();
            ComboSubDevice.Items.Clear();

            foreach (var device in Devices)
            {
                ComboPrimaryDevice.Items.Add(device.Name);
                ComboSubDevice.Items.Add(device.Name);
            }
        }
        finally
        {
            ShouldIgnoreControlEvents = false;
        }

        SelectDevice(ComboPrimaryDevice, state.HeadphoneDeviceId, "Headphones", "Headset");
        SelectDevice(ComboSubDevice, state.SubDeviceId, "Speaker", "Subwoofer");
        ApplyEngineState(state);
    }

    private void SelectDevice(ComboBox combo, string? previousId, params string[] defaultHints)
    {
        if (previousId != null)
        {
            int idx = Devices.FindIndex(d => d.Id == previousId);
            if (idx >= 0) { combo.SelectedIndex = idx; return; }
        }

        foreach (string hint in defaultHints)
        {
            int idx = Devices.FindIndex(d =>
                d.Name.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { combo.SelectedIndex = idx; return; }
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private string? GetSelectedDeviceId(ComboBox combo)
    {
        int idx = combo.SelectedIndex;
        return idx >= 0 && idx < Devices.Count ? Devices[idx].Id : null;
    }

    // ── Event handlers ──────────────────────────────────────

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RefreshDeviceList);
    }

    private void OnPrimaryDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreControlEvents)
            return;

        Controller.SetHeadphoneDeviceById(GetSelectedDeviceId(ComboPrimaryDevice));
    }

    private void OnSubDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ShouldIgnoreControlEvents)
            return;

        Controller.SetSubDeviceById(GetSelectedDeviceId(ComboSubDevice));
    }

    private void OnPrimaryLatencyChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int ms = (int)SliderPrimaryLatency.Value;
        TextPrimaryLatencyValue.Text = $"{ms} ms";
        if (!ShouldIgnoreControlEvents)
            Controller.SetHeadphoneDelayMs(ms);
    }

    private void OnPrimaryVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int pct = (int)SliderPrimaryVolume.Value;
        TextPrimaryVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreControlEvents)
            Controller.SetHeadphoneVolume(pct / 100f);
    }

    private void OnSubLatencyChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int ms = (int)SliderSubLatency.Value;
        TextSubLatencyValue.Text = $"{ms} ms";
        if (!ShouldIgnoreControlEvents)
            Controller.SetSubDelayMs(ms);
    }

    private void OnSubVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int pct = (int)SliderSubVolume.Value;
        TextSubVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreControlEvents)
            Controller.SetSubVolume(pct / 100f);
    }

    private void OnLowPassChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        int freq = (int)SliderLowPass.Value;
        TextLowPassValue.Text = $"{freq} Hz";
        if (!ShouldIgnoreControlEvents)
            Controller.SetLowPassFrequency(freq);
    }

    private void OnStartStopClicked(object? sender, RoutedEventArgs e)
    {
        if (Controller.GetState().IsRunning)
            StopEngine();
        else
            StartEngine();
    }

    private void OnMinimizeClicked(object? sender, RoutedEventArgs e)
    {
        if (IsTrayAvailable())
            HideToTray();
        else
            WindowState = WindowState.Minimized;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    // ── Start / Stop ────────────────────────────────────────

    private void StartEngine()
    {
        if (Controller.GetState().IsRunning)
            return;

        Controller.Start();
    }

    private void StopEngine()
    {
        if (!Controller.GetState().IsRunning)
            return;

        Controller.Stop();
    }

    private void SetControlsEnabled(bool enabled)
    {
        // Reduced opacity when disabled is handled by the ComboBox:disabled style
        ComboPrimaryDevice.IsEnabled = enabled;
        ComboSubDevice.IsEnabled = enabled;
    }

    // ── Engine events (come from background thread) ─────────

    private void OnEngineStateChanged(object? sender, AudioEngineStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => ApplyEngineState(e.State));
    }

    private void OnControllerWarning(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            TextStatus.Text = $"Error: {message}";
            TextStatus.Foreground = ErrorBrush;
        });
    }

    // ── Window management ───────────────────────────────────

    public void ShowFromTray()
    {
        ShowInTaskbar = true;

        if (!IsVisible)
            Show();

        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void RequestExit()
    {
        AllowClose = true;
        Close();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == WindowStateProperty && WindowState == WindowState.Minimized && IsTrayAvailable())
            HideToTray();
        else if (change.Property == ActualTransparencyLevelProperty)
            UpdateTitleBarBackground();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (!AllowClose && IsTrayAvailable())
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void UpdateTitleBarBackground()
    {
        bool hasBlur = ActualTransparencyLevel == WindowTransparencyLevel.Blur ||
                       ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur ||
                       ActualTransparencyLevel == WindowTransparencyLevel.Mica;

        TitleBar.Background = hasBlur ? TitleBarBlurBrush : TitleBarOpaqueBrush;
    }

    private void ApplyEngineState(AudioEngineState state)
    {
        ShouldIgnoreControlEvents = true;
        try
        {
            SelectDeviceById(ComboPrimaryDevice, state.HeadphoneDeviceId);
            SelectDeviceById(ComboSubDevice, state.SubDeviceId);

            ApplySliderState(SliderPrimaryLatency, TextPrimaryLatencyValue, state.HeadphoneDelayMs, " ms");

            int primaryVolume = ToPercent(state.HeadphoneVolume, (int)SliderPrimaryVolume.Value);
            ApplySliderState(SliderPrimaryVolume, TextPrimaryVolumeValue, primaryVolume, "%");

            ApplySliderState(SliderSubLatency, TextSubLatencyValue, state.SubDelayMs, " ms");

            int subVolume = ToPercent(state.SubVolume, (int)SliderSubVolume.Value);
            ApplySliderState(SliderSubVolume, TextSubVolumeValue, subVolume, "%");

            int lowPass = (int)Math.Round(state.LowPassFrequency);
            ApplySliderState(SliderLowPass, TextLowPassValue, lowPass, " Hz");

            ButtonStartStop.Content = state.IsRunning ? "Stop" : "Start";
            ButtonStartStop.Background = state.IsRunning ? StopBrush : StartBrush;

            if (!string.IsNullOrWhiteSpace(state.LastErrorMessage))
            {
                TextStatus.Text = $"Error: {state.LastErrorMessage}";
                TextStatus.Foreground = ErrorBrush;
            }
            else if (state.IsRunning)
            {
                TextStatus.Text = "Running";
                TextStatus.Foreground = RunningBrush;
            }
            else
            {
                TextStatus.Text = "Stopped";
                TextStatus.Foreground = DimBrush;
            }

            SetControlsEnabled(!state.IsRunning);
        }
        finally
        {
            ShouldIgnoreControlEvents = false;
        }
    }

    private void SelectDeviceById(ComboBox combo, string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        int idx = Devices.FindIndex(device => device.Id == deviceId);
        if (idx >= 0 && combo.SelectedIndex != idx)
            combo.SelectedIndex = idx;
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
    }

    private void ApplySliderState(Slider slider, TextBlock textBlock, int value, string suffix)
    {
        // Don't fight the user while they're dragging
        if (!DraggingSliders.Contains(slider))
            slider.Value = value;

        textBlock.Text = $"{value}{suffix}";
    }

    private static int ToPercent(float scalar, int fallback)
    {
        if (scalar < 0)
            return fallback;

        return (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f);
    }
}
