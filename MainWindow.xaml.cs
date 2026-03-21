using BassRouter.Audio;
using SamsidParty;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace BassRouter;

public partial class MainWindow : Window
{
    private readonly AudioEngineController Controller;
    private List<AudioOutputDevice> Devices = new();
    private bool ShouldIgnoreControlEvents;
    private bool AllowClose;

    private static readonly SolidColorBrush StartBrush = new(MediaColor.FromRgb(10, 122, 62));
    private static readonly SolidColorBrush StopBrush = new(MediaColor.FromRgb(180, 40, 40));
    private static readonly SolidColorBrush RunningBrush = new(MediaColor.FromRgb(80, 220, 80));
    private static readonly SolidColorBrush ErrorBrush = new(MediaColor.FromRgb(255, 100, 100));
    private static readonly SolidColorBrush DimBrush = new(MediaColor.FromRgb(144, 144, 168));


    public MainWindow(AudioEngineController controller)
    {
        Controller = controller;

        InitializeComponent();

        SourceInitialized += (s, e) =>
        {
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            WindowHelpers.FixWindows11Corners(handle);
            WindowHelpers.SetWindowBackgroundMode(handle, WindowHelpers.WindowBackgroundMode.BlurBehind);
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

        ButtonStartStop.Click += OnStartStopClicked;

        StateChanged += OnWindowStateChanged;
        Closing += OnWindowClosing;
    }

    // ── Device list management ──────────────────────────────

    private void RefreshDeviceList()
    {
        Devices = Controller.GetOutputDevices().ToList();
        AudioEngineState state = Controller.GetState();

        ComboPrimaryDevice.Items.Clear();
        ComboSubDevice.Items.Clear();

        foreach (var device in Devices)
        {
            ComboPrimaryDevice.Items.Add(device.Name);
            ComboSubDevice.Items.Add(device.Name);
        }

        SelectDevice(ComboPrimaryDevice, state.HeadphoneDeviceId, "Headphones");
        SelectDevice(ComboSubDevice, state.SubDeviceId, "Speaker", "Subwoofer");
        ApplyEngineState(state);
    }

    private void SelectDevice(WpfComboBox combo, string? previousId, params string[] defaultHints)
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

    private string? GetSelectedDeviceId(WpfComboBox combo)
    {
        int idx = combo.SelectedIndex;
        return idx >= 0 && idx < Devices.Count ? Devices[idx].Id : null;
    }

    // ── Event handlers ──────────────────────────────────────

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshDeviceList);
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

    private void OnPrimaryLatencyChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)SliderPrimaryLatency.Value;
        TextPrimaryLatencyValue.Text = $"{ms} ms";
        if (!ShouldIgnoreControlEvents)
            Controller.SetHeadphoneDelayMs(ms);
    }

    private void OnPrimaryVolumeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int pct = (int)SliderPrimaryVolume.Value;
        TextPrimaryVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreControlEvents)
            Controller.SetHeadphoneVolume(pct / 100f);
    }

    private void OnSubLatencyChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)SliderSubLatency.Value;
        TextSubLatencyValue.Text = $"{ms} ms";
        if (!ShouldIgnoreControlEvents)
            Controller.SetSubDelayMs(ms);
    }

    private void OnSubVolumeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int pct = (int)SliderSubVolume.Value;
        TextSubVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreControlEvents)
            Controller.SetSubVolume(pct / 100f);
    }

    private void OnLowPassChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
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
        ComboPrimaryDevice.IsEnabled = enabled;
        ComboSubDevice.IsEnabled = enabled;

        // Reduce opacity when disabled
        ComboPrimaryDevice.Opacity = enabled ? 1 : 0.4;
        ComboSubDevice.Opacity = enabled ? 1 : 0.4;
    }

    // ── Engine events (come from background thread) ─────────

    private void OnEngineStateChanged(object? sender, AudioEngineStateChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() => ApplyEngineState(e.State));
    }

    private void OnControllerWarning(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TextStatus.Text = $"Error: {message}";
            TextStatus.Foreground = ErrorBrush;
        });
    }

    // ── Cleanup ─────────────────────────────────────────────

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

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            HideToTray();
        }
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

    private void SelectDeviceById(WpfComboBox combo, string? deviceId)
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

    private static void ApplySliderState(Slider slider, TextBlock textBlock, int value, string suffix)
    {
        if (!slider.IsMouseCaptureWithin)
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
