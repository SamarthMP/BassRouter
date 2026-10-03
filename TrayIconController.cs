using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using BassRouter.Audio;

namespace BassRouter;

public sealed class TrayIconController : IDisposable
{
    private readonly AudioEngineController Controller;
    private readonly Action ShowWindow;
    private readonly Action ExitApplication;
    private readonly Func<nint> GetWindowHandle;
    private readonly Application Application;
    private readonly TrayIcon TrayIcon;
    private readonly NativeMenu Menu;
    private bool Disposed;

    public TrayIconController(
        Application application,
        AudioEngineController controller,
        Action showWindow,
        Action exitApplication,
        Func<nint> getWindowHandle)
    {
        Application = application;
        Controller = controller;
        ShowWindow = showWindow;
        ExitApplication = exitApplication;
        GetWindowHandle = getWindowHandle;

        Menu = new NativeMenu();

        TrayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://BassRouter/Resources/Icon.png"))),
            ToolTipText = "BassRouter",
            Menu = Menu,
            IsVisible = true
        };

        TrayIcon.Clicked += OnTrayIconClicked;
        TrayIcon.SetIcons(Application, [TrayIcon]);

        Controller.StateChanged += OnControllerStateChanged;
        Controller.DevicesChanged += OnControllerDevicesChanged;
        Controller.Warning += OnControllerWarning;

        RebuildMenu();
        UpdateTrayText(Controller.GetState());
    }

    public void Dispose()
    {
        if (Disposed)
            return;

        Disposed = true;

        Controller.StateChanged -= OnControllerStateChanged;
        Controller.DevicesChanged -= OnControllerDevicesChanged;
        Controller.Warning -= OnControllerWarning;

        TrayIcon.Clicked -= OnTrayIconClicked;
        TrayIcon.IsVisible = false;
        TrayIcon.SetIcons(Application, []);
        TrayIcon.Dispose();
        DesktopIntegration.Cleanup();
    }

    /// <summary>
    /// The menu can't be rebuilt lazily when it opens on every platform, so it's kept up to date instead.
    /// </summary>
    private void RebuildMenu()
    {
        if (Disposed)
            return;

        AudioEngineState state = Controller.GetState();
        IReadOnlyList<AudioOutputDevice> devices = Controller.GetOutputDevices();

        Menu.Items.Clear();
        Menu.Items.Add(CreateStatusItem(state));
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(CreateMenuItem("Open", ShowWindow));
        Menu.Items.Add(CreateMenuItem(state.IsRunning ? "Stop" : "Start", ToggleRunning));
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(CreateDeviceMenu("Primary Output", devices, state.HeadphoneDeviceId, state.IsRunning, Controller.SetHeadphoneDeviceById));
        Menu.Items.Add(CreateDeviceMenu("Subwoofer Output", devices, state.SubDeviceId, state.IsRunning, Controller.SetSubDeviceById));
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(CreateMenuItem("Exit", ExitApplication));
    }

    private static NativeMenuItem CreateStatusItem(AudioEngineState state)
    {
        string label = state.LastErrorMessage != null
            ? "Status: Error"
            : state.IsRunning
                ? "Status: Running"
                : "Status: Stopped";

        return new NativeMenuItem(label)
        {
            IsEnabled = false
        };
    }

    private static NativeMenuItem CreateDeviceMenu(
        string title,
        IReadOnlyList<AudioOutputDevice> devices,
        string? selectedDeviceId,
        bool isRunning,
        Action<string?> onSelected)
    {
        var submenu = new NativeMenu();
        var menuItem = new NativeMenuItem(title)
        {
            IsEnabled = !isRunning,
            Menu = submenu
        };

        if (devices.Count == 0)
        {
            submenu.Items.Add(new NativeMenuItem("No active devices") { IsEnabled = false });
            return menuItem;
        }

        foreach (var device in devices)
        {
            var deviceItem = new NativeMenuItem(device.Name)
            {
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = device.Id == selectedDeviceId
            };

            deviceItem.Click += (_, _) => onSelected(device.Id);
            submenu.Items.Add(deviceItem);
        }

        return menuItem;
    }

    private static NativeMenuItem CreateMenuItem(string label, Action onClick)
    {
        var menuItem = new NativeMenuItem(label);
        menuItem.Click += (_, _) => onClick();
        return menuItem;
    }

    private void ToggleRunning()
    {
        Controller.ToggleRunning();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowWindow();
    }

    private void OnControllerStateChanged(object? sender, AudioEngineStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTrayText(e.State);
            RebuildMenu();
        });
    }

    private void OnControllerDevicesChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(RebuildMenu);
    }

    private void OnControllerWarning(object? sender, string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DesktopIntegration.ShowNotification(message, GetWindowHandle());
            RebuildMenu();
        });
    }

    private void UpdateTrayText(AudioEngineState state)
    {
        if (Disposed)
            return;

        string status = state.LastErrorMessage != null
            ? "Error"
            : state.IsRunning
                ? "Running"
                : "Stopped";

        TrayIcon.ToolTipText = $"BassRouter ({status})";
    }
}
