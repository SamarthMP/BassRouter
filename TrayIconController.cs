using BassRouter.Audio;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;

namespace BassRouter;

public sealed class TrayIconController : IDisposable
{
    private readonly AudioEngineController Controller;
    private readonly Action ShowWindow;
    private readonly Action ExitApplication;
    private readonly NotifyIcon NotifyIcon;
    private readonly ContextMenuStrip Menu;
    private readonly Dispatcher Dispatcher;
    private bool Disposed;

    public TrayIconController(AudioEngineController controller, Action showWindow, Action exitApplication)
    {
        Controller = controller;
        ShowWindow = showWindow;
        ExitApplication = exitApplication;
        Dispatcher = System.Windows.Application.Current.Dispatcher;

        Menu = new ContextMenuStrip();
        Menu.Opening += OnMenuOpening;

        NotifyIcon = new NotifyIcon
        {
            Visible = true,
            Text = "BassRouter",
            Icon = ResolveTrayIcon()
        };

        NotifyIcon.DoubleClick += OnNotifyIconDoubleClick;
        NotifyIcon.MouseUp += OnNotifyIconMouseUp;

        Controller.StateChanged += OnControllerStateChanged;
        Controller.Warning += OnControllerWarning;

        UpdateTrayText(Controller.GetState());
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;

        Controller.StateChanged -= OnControllerStateChanged;
        Controller.Warning -= OnControllerWarning;

        NotifyIcon.DoubleClick -= OnNotifyIconDoubleClick;
        NotifyIcon.MouseUp -= OnNotifyIconMouseUp;
        NotifyIcon.Visible = false;
        NotifyIcon.Dispose();
        Menu.Dispose();
    }

    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        AudioEngineState state = Controller.GetState();
        IReadOnlyList<AudioOutputDevice> devices = Controller.GetOutputDevices();

        Menu.SuspendLayout();
        try
        {
            Menu.Items.Clear();
            Menu.Items.Add(CreateStatusItem(state));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(CreateMenuItem("Open", (_, _) => ShowWindow()));
            Menu.Items.Add(CreateMenuItem(state.IsRunning ? "Stop" : "Start", (_, _) => ToggleRunning()));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(CreateDeviceMenu("Primary Output", devices, state.HeadphoneDeviceId, state.IsRunning, Controller.SetHeadphoneDeviceById));
            Menu.Items.Add(CreateDeviceMenu("Subwoofer Output", devices, state.SubDeviceId, state.IsRunning, Controller.SetSubDeviceById));
            Menu.Items.Add(new ToolStripSeparator());
            Menu.Items.Add(CreateMenuItem("Exit", (_, _) => ExitApplication()));
        }
        finally
        {
            Menu.ResumeLayout();
        }
    }

    private ToolStripItem CreateStatusItem(AudioEngineState state)
    {
        string label = state.LastErrorMessage != null
            ? $"Status: Error"
            : state.IsRunning
                ? "Status: Running"
                : "Status: Stopped";

        return new ToolStripMenuItem(label)
        {
            Enabled = false
        };
    }

    private ToolStripMenuItem CreateDeviceMenu(
        string title,
        IReadOnlyList<AudioOutputDevice> devices,
        string? selectedDeviceId,
        bool isRunning,
        Action<string?> onSelected)
    {
        var menuItem = new ToolStripMenuItem(title)
        {
            Enabled = !isRunning
        };

        if (devices.Count == 0)
        {
            menuItem.DropDownItems.Add(new ToolStripMenuItem("No active devices") { Enabled = false });
            return menuItem;
        }

        foreach (var device in devices)
        {
            var deviceItem = new ToolStripMenuItem(device.Name)
            {
                Checked = device.Id == selectedDeviceId
            };

            deviceItem.Click += (_, _) => onSelected(device.Id);
            menuItem.DropDownItems.Add(deviceItem);
        }

        return menuItem;
    }

    private ToolStripMenuItem CreateMenuItem(string label, EventHandler onClick)
    {
        var menuItem = new ToolStripMenuItem(label);
        menuItem.Click += onClick;
        return menuItem;
    }

    private void ToggleRunning()
    {
        Controller.ToggleRunning();
    }

    private void OnNotifyIconDoubleClick(object? sender, EventArgs e)
    {
        ShowWindow();
    }

    private void OnNotifyIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
            return;

        if (Dispatcher.CheckAccess())
            ShowContextMenu();
        else
            Dispatcher.BeginInvoke(ShowContextMenu);
    }

    private void OnControllerStateChanged(object? sender, AudioEngineStateChangedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
            UpdateTrayText(e.State);
        else
            Dispatcher.BeginInvoke(() => UpdateTrayText(e.State));
    }

    private void OnControllerWarning(object? sender, string message)
    {
        if (Dispatcher.CheckAccess())
            ShowBalloonTip(message);
        else
            Dispatcher.BeginInvoke(() => ShowBalloonTip(message));
    }

    private void UpdateTrayText(AudioEngineState state)
    {
        string status = state.LastErrorMessage != null
            ? "Error"
            : state.IsRunning
                ? "Running"
                : "Stopped";

        NotifyIcon.Text = $"BassRouter ({status})";
    }

    private void ShowBalloonTip(string message)
    {
        NotifyIcon.BalloonTipTitle = "BassRouter";
        NotifyIcon.BalloonTipText = message;
        NotifyIcon.BalloonTipIcon = ToolTipIcon.Warning;
        NotifyIcon.ShowBalloonTip(3000);
    }

    private void ShowContextMenu()
    {
        RebuildMenu();
        Menu.Show(Cursor.Position);
    }

    private static Icon ResolveTrayIcon()
    {
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(processPath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch { }

        return SystemIcons.Application;
    }
}