using BassRouter.Audio;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace BassRouter;

public partial class MainWindow : Window
{
    private readonly AudioDeviceManager DeviceManager;
    private readonly AudioEngine Engine;
    private List<MMDevice> Devices = new();
    private bool ShouldIgnoreVolumeEvents;

    private static readonly SolidColorBrush StartBrush = new(Color.FromRgb(10, 122, 62));
    private static readonly SolidColorBrush StopBrush = new(Color.FromRgb(180, 40, 40));
    private static readonly SolidColorBrush RunningBrush = new(Color.FromRgb(80, 220, 80));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(255, 100, 100));
    private static readonly SolidColorBrush DimBrush = new(Color.FromRgb(144, 144, 168));

    #region Window Management Native Code

    [DllImport("DwmApi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWA_WINDOW_CORNER_PREFERENCE_UNDEFINED = 0,
        DWMWA_WINDOW_CORNER_PREFERENCE_DONOTROUND = 1,
        DWMWA_WINDOW_CORNER_PREFERENCE_ROUND = 2,
        DWMWA_WINDOW_CORNER_PREFERENCE_ROUNDSMALL = 3
    }

    #endregion

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (s, e) =>
        {
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            var preference = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWA_WINDOW_CORNER_PREFERENCE_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        };

        DeviceManager = new AudioDeviceManager();
        Engine = new AudioEngine();

        WireEvents();
        RefreshDeviceList();
    }

    private void WireEvents()
    {
        DeviceManager.DevicesChanged += OnDevicesChanged;

        Engine.Error += OnEngineError;
        Engine.Stopped += OnEngineStopped;

        ComboPrimaryDevice.SelectionChanged += OnPrimaryDeviceChanged;
        ComboSubDevice.SelectionChanged += OnSubDeviceChanged;

        SliderPrimaryLatency.ValueChanged += OnPrimaryLatencyChanged;
        SliderPrimaryVolume.ValueChanged += OnPrimaryVolumeChanged;

        SliderSubLatency.ValueChanged += OnSubLatencyChanged;
        SliderSubVolume.ValueChanged += OnSubVolumeChanged;
        SliderLowPass.ValueChanged += OnLowPassChanged;

        ButtonStartStop.Click += OnStartStopClicked;

        Closing += OnWindowClosing;
    }

    // ── Device list management ──────────────────────────────

    private void RefreshDeviceList()
    {
        Devices = DeviceManager.GetOutputDevices();

        string? previousPrimaryId = GetSelectedDeviceId(ComboPrimaryDevice);
        string? previousSubId = GetSelectedDeviceId(ComboSubDevice);

        ComboPrimaryDevice.Items.Clear();
        ComboSubDevice.Items.Clear();

        foreach (var device in Devices)
        {
            ComboPrimaryDevice.Items.Add(device.FriendlyName);
            ComboSubDevice.Items.Add(device.FriendlyName);
        }

        SelectDevice(ComboPrimaryDevice, previousPrimaryId, "Headphones");
        SelectDevice(ComboSubDevice, previousSubId, "Speaker", "Subwoofer");

        SyncDeviceToEngine();
        SyncVolumeSlidersFromDevices();
    }

    private void SelectDevice(ComboBox combo, string? previousId, params string[] defaultHints)
    {
        if (previousId != null)
        {
            int idx = Devices.FindIndex(d => d.ID == previousId);
            if (idx >= 0) { combo.SelectedIndex = idx; return; }
        }

        foreach (string hint in defaultHints)
        {
            int idx = Devices.FindIndex(d =>
                d.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) { combo.SelectedIndex = idx; return; }
        }

        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private string? GetSelectedDeviceId(ComboBox combo)
    {
        int idx = combo.SelectedIndex;
        return idx >= 0 && idx < Devices.Count ? Devices[idx].ID : null;
    }

    private MMDevice? GetSelectedDevice(ComboBox combo)
    {
        int idx = combo.SelectedIndex;
        return idx >= 0 && idx < Devices.Count ? Devices[idx] : null;
    }

    private void SyncDeviceToEngine()
    {
        Engine.SetHeadphoneDevice(GetSelectedDevice(ComboPrimaryDevice));
        Engine.SetSubDevice(GetSelectedDevice(ComboSubDevice));
    }

    private void SyncVolumeSlidersFromDevices()
    {
        ShouldIgnoreVolumeEvents = true;
        try
        {
            float hpVol = Engine.GetHeadphoneVolume();
            if (hpVol >= 0)
            {
                SliderPrimaryVolume.Value = (int)(hpVol * 100);
                TextPrimaryVolumeValue.Text = $"{(int)SliderPrimaryVolume.Value}%";
            }

            float subVol = Engine.GetSubVolume();
            if (subVol >= 0)
            {
                SliderSubVolume.Value = (int)(subVol * 100);
                TextSubVolumeValue.Text = $"{(int)SliderSubVolume.Value}%";
            }
        }
        finally
        {
            ShouldIgnoreVolumeEvents = false;
        }
    }

    // ── Event handlers ──────────────────────────────────────

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshDeviceList);
    }

    private void OnPrimaryDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        Engine.SetHeadphoneDevice(GetSelectedDevice(ComboPrimaryDevice));
        SyncVolumeSlidersFromDevices();
    }

    private void OnSubDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        Engine.SetSubDevice(GetSelectedDevice(ComboSubDevice));
        SyncVolumeSlidersFromDevices();
    }

    private void OnPrimaryLatencyChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)SliderPrimaryLatency.Value;
        TextPrimaryLatencyValue.Text = $"{ms} ms";
        Engine.SetHeadphoneDelayMs(ms);
    }

    private void OnPrimaryVolumeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int pct = (int)SliderPrimaryVolume.Value;
        TextPrimaryVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreVolumeEvents)
            Engine.SetHeadphoneVolume(pct / 100f);
    }

    private void OnSubLatencyChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int ms = (int)SliderSubLatency.Value;
        TextSubLatencyValue.Text = $"{ms} ms";
        Engine.SetSubDelayMs(ms);
    }

    private void OnSubVolumeChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int pct = (int)SliderSubVolume.Value;
        TextSubVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreVolumeEvents)
            Engine.SetSubVolume(pct / 100f);
    }

    private void OnLowPassChanged(object? sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int freq = (int)SliderLowPass.Value;
        TextLowPassValue.Text = $"{freq} Hz";
        Engine.SetLowPassFrequency(freq);
    }

    private void OnStartStopClicked(object? sender, RoutedEventArgs e)
    {
        if (Engine.IsRunning)
            StopEngine();
        else
            StartEngine();
    }

    // ── Start / Stop ────────────────────────────────────────

    private void StartEngine()
    {
        try
        {
            Engine.SetHeadphoneDevice(GetSelectedDevice(ComboPrimaryDevice));
            Engine.SetSubDevice(GetSelectedDevice(ComboSubDevice));
            Engine.SetHeadphoneDelayMs((int)SliderPrimaryLatency.Value);
            Engine.SetSubDelayMs((int)SliderSubLatency.Value);
            Engine.SetLowPassFrequency((float)SliderLowPass.Value);

            Engine.Start();

            ButtonStartStop.Content = "Stop";
            ButtonStartStop.Background = StopBrush;
            TextStatus.Text = "Running";
            TextStatus.Foreground = RunningBrush;

            SetControlsEnabled(false);
        }
        catch (Exception ex)
        {
            TextStatus.Text = $"Error: {ex.Message}";
            TextStatus.Foreground = ErrorBrush;
        }
    }

    private void StopEngine()
    {
        Engine.Stop();

        ButtonStartStop.Content = "Start";
        ButtonStartStop.Background = StartBrush;
        TextStatus.Text = "Stopped";
        TextStatus.Foreground = DimBrush;

        SetControlsEnabled(true);
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

    private void OnEngineError(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            TextStatus.Text = $"Error: {message}";
            TextStatus.Foreground = ErrorBrush;
        });
    }

    private void OnEngineStopped(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            ButtonStartStop.Content = "Start";
            ButtonStartStop.Background = StartBrush;

            if (TextStatus.Foreground is SolidColorBrush brush &&
                brush.Color != ErrorBrush.Color)
            {
                TextStatus.Text = "Stopped";
                TextStatus.Foreground = DimBrush;
            }

            SetControlsEnabled(true);
        });
    }

    // ── Cleanup ─────────────────────────────────────────────

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Engine.Dispose();
        DeviceManager.Dispose();
    }
}
