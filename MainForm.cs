using BassRouter.Audio;
using NAudio.CoreAudioApi;

namespace BassRouter;

public partial class MainForm : Form
{
    private readonly AudioDeviceManager DeviceManager;
    private readonly AudioEngine Engine;
    private List<MMDevice> Devices = new();
    private bool ShouldIgnoreVolumeEvents;

    public MainForm()
    {
        InitializeComponent();

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

        ComboPrimaryDevice.SelectedIndexChanged += OnPrimaryDeviceChanged;
        ComboSubDevice.SelectedIndexChanged += OnSubDeviceChanged;

        SliderPrimaryLatency.ValueChanged += OnPrimaryLatencyChanged;
        SliderPrimaryVolume.ValueChanged += OnPrimaryVolumeChanged;

        SliderSubLatency.ValueChanged += OnSubLatencyChanged;
        SliderSubVolume.ValueChanged += OnSubVolumeChanged;
        SliderLowPass.ValueChanged += OnLowPassChanged;

        ButtonStartStop.Click += OnStartStopClicked;

        FormClosing += OnFormClosing;
    }



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

        // Restore previous selection or pick default
        SelectDevice(ComboPrimaryDevice, previousPrimaryId, "Headphones");
        SelectDevice(ComboSubDevice, previousSubId, "Speaker", "Subwoofer");

        SyncDeviceToEngine();
        SyncVolumeSlidersFromDevices();
    }

    private void SelectDevice(ComboBox combo, string? previousId, params string[] defaultHints)
    {
        // Try to re-select the previously selected device
        if (previousId != null)
        {
            int idx = Devices.FindIndex(d => d.ID == previousId);
            if (idx >= 0)
            {
                combo.SelectedIndex = idx;
                return;
            }
        }

        // Try default hints
        foreach (string hint in defaultHints)
        {
            int idx = Devices.FindIndex(d =>
                d.FriendlyName.Contains(hint, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                combo.SelectedIndex = idx;
                return;
            }
        }

        // Fall back to first device
        if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    private string? GetSelectedDeviceId(ComboBox combo)
    {
        int idx = combo.SelectedIndex;
        if (idx >= 0 && idx < Devices.Count)
            return Devices[idx].ID;
        return null;
    }

    private MMDevice? GetSelectedDevice(ComboBox combo)
    {
        int idx = combo.SelectedIndex;
        if (idx >= 0 && idx < Devices.Count)
            return Devices[idx];
        return null;
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
                LabelPrimaryVolumeValue.Text = $"{SliderPrimaryVolume.Value}%";
            }

            float subVol = Engine.GetSubVolume();
            if (subVol >= 0)
            {
                SliderSubVolume.Value = (int)(subVol * 100);
                LabelSubVolumeValue.Text = $"{SliderSubVolume.Value}%";
            }
        }
        finally
        {
            ShouldIgnoreVolumeEvents = false;
        }
    }


    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(RefreshDeviceList);
            return;
        }
        RefreshDeviceList();
    }

    private void OnPrimaryDeviceChanged(object? sender, EventArgs e)
    {
        Engine.SetHeadphoneDevice(GetSelectedDevice(ComboPrimaryDevice));
        SyncVolumeSlidersFromDevices();
    }

    private void OnSubDeviceChanged(object? sender, EventArgs e)
    {
        Engine.SetSubDevice(GetSelectedDevice(ComboSubDevice));
        SyncVolumeSlidersFromDevices();
    }

    private void OnPrimaryLatencyChanged(object? sender, EventArgs e)
    {
        int ms = SliderPrimaryLatency.Value;
        LabelPrimaryLatencyValue.Text = $"{ms} ms";
        Engine.SetHeadphoneDelayMs(ms);
    }

    private void OnPrimaryVolumeChanged(object? sender, EventArgs e)
    {
        int pct = SliderPrimaryVolume.Value;
        LabelPrimaryVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreVolumeEvents)
            Engine.SetHeadphoneVolume(pct / 100f);
    }

    private void OnSubLatencyChanged(object? sender, EventArgs e)
    {
        int ms = SliderSubLatency.Value;
        LabelSubLatencyValue.Text = $"{ms} ms";
        Engine.SetSubDelayMs(ms);
    }

    private void OnSubVolumeChanged(object? sender, EventArgs e)
    {
        int pct = SliderSubVolume.Value;
        LabelSubVolumeValue.Text = $"{pct}%";
        if (!ShouldIgnoreVolumeEvents)
            Engine.SetSubVolume(pct / 100f);
    }

    private void OnLowPassChanged(object? sender, EventArgs e)
    {
        int freq = SliderLowPass.Value;
        LabelLowPassValue.Text = $"{freq} Hz";
        Engine.SetLowPassFrequency(freq);
    }

    private void OnStartStopClicked(object? sender, EventArgs e)
    {
        if (Engine.IsRunning)
        {
            StopEngine();
        }
        else
        {
            StartEngine();
        }
    }



    private void StartEngine()
    {
        try
        {
            Engine.SetHeadphoneDevice(GetSelectedDevice(ComboPrimaryDevice));
            Engine.SetSubDevice(GetSelectedDevice(ComboSubDevice));
            Engine.SetHeadphoneDelayMs(SliderPrimaryLatency.Value);
            Engine.SetSubDelayMs(SliderSubLatency.Value);
            Engine.SetLowPassFrequency(SliderLowPass.Value);

            Engine.Start();

            ButtonStartStop.Text = "Stop";
            ButtonStartStop.BackColor = Color.FromArgb(180, 40, 40);
            LabelStatus.Text = "Running";
            LabelStatus.ForeColor = Color.FromArgb(80, 220, 80);

            SetControlsEnabled(false);
        }
        catch (Exception ex)
        {
            LabelStatus.Text = $"Error: {ex.Message}";
            LabelStatus.ForeColor = Color.FromArgb(255, 100, 100);
        }
    }

    private void StopEngine()
    {
        Engine.Stop();

        ButtonStartStop.Text = "Start";
        ButtonStartStop.BackColor = Color.FromArgb(0, 120, 60);
        LabelStatus.Text = "Stopped";
        LabelStatus.ForeColor = Color.FromArgb(180, 180, 180);

        SetControlsEnabled(true);
    }

    private void SetControlsEnabled(bool enabled)
    {
        ComboPrimaryDevice.Enabled = enabled;
        ComboSubDevice.Enabled = enabled;
    }


    private void OnEngineError(object? sender, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEngineError(sender, message));
            return;
        }

        LabelStatus.Text = $"Error: {message}";
        LabelStatus.ForeColor = Color.FromArgb(255, 100, 100);
    }

    private void OnEngineStopped(object? sender, EventArgs e)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => OnEngineStopped(sender, e));
            return;
        }

        ButtonStartStop.Text = "Start";
        ButtonStartStop.BackColor = Color.FromArgb(0, 120, 60);
        if (LabelStatus.ForeColor != Color.FromArgb(255, 100, 100))
        {
            LabelStatus.Text = "Stopped";
            LabelStatus.ForeColor = Color.FromArgb(180, 180, 180);
        }

        SetControlsEnabled(true);
    }

    // Cleanup
    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        Engine.Dispose();
        DeviceManager.Dispose();
    }
}
