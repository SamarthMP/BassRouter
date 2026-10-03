using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using BassRouter.Audio;
using System.Runtime.InteropServices;

namespace BassRouter;

public partial class App : Application
{
    private AudioEngineController? Controller;
    private TrayIconController? TrayIconController;
    private MainWindow? MainWindow;
    private IClassicDesktopStyleApplicationLifetime? Desktop;
    private PosixSignalRegistration[] SignalRegistrations = [];
    private bool Exiting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnExit;

            try
            {
                Controller = new AudioEngineController();
            }
            catch (Exception ex)
            {
                // e.g. PipeWire libraries missing on Linux
                desktop.MainWindow = CreateStartupErrorWindow(ex);
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                base.OnFrameworkInitializationCompleted();
                return;
            }

            MainWindow = new MainWindow(Controller)
            {
                IsTrayAvailable = DesktopIntegration.IsTrayAvailable
            };

            // The window only really closes when there's no tray to hide into, so then the app exits
            MainWindow.Closed += (_, _) => ExitApplication();

            desktop.MainWindow = MainWindow;

            TrayIconController = new TrayIconController(
                this,
                Controller,
                showWindow: MainWindow.ShowFromTray,
                exitApplication: ExitApplication,
                getWindowHandle: () => MainWindow.TryGetPlatformHandle()?.Handle ?? 0);

            // Shut down cleanly on logout / kill / Ctrl+C so the previous default output gets restored
            if (!OperatingSystem.IsWindows())
            {
                SignalRegistrations =
                [
                    PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminationSignal),
                    PosixSignalRegistration.Create(PosixSignal.SIGINT, OnTerminationSignal),
                ];
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        foreach (PosixSignalRegistration registration in SignalRegistrations)
            registration.Dispose();

        TrayIconController?.Dispose();
        Controller?.Dispose();
    }

    private void OnTerminationSignal(PosixSignalContext context)
    {
        context.Cancel = true;
        Dispatcher.UIThread.Post(ExitApplication);
    }

    private void ExitApplication()
    {
        if (Exiting)
            return;

        Exiting = true;
        MainWindow?.RequestExit();
        Desktop?.Shutdown();
    }

    private static Window CreateStartupErrorWindow(Exception ex)
    {
        string details = OperatingSystem.IsLinux()
            ? "BassRouter needs PipeWire (with WirePlumber) to route audio on Linux."
            : "BassRouter couldn't initialize the audio system.";

        var close = new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };

        var window = new Window
        {
            Title = "BassRouter",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Children =
                {
                    new TextBlock { Text = "Failed to start", FontSize = 18, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 8) },
                    new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 8, 0, 0) },
                    close
                }
            }
        };

        close.Click += (_, _) => window.Close();
        return window;
    }
}
