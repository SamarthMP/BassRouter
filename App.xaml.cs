using BassRouter.Audio;
using System.Windows;

namespace BassRouter;

public partial class App : System.Windows.Application
{
	private AudioEngineController? Controller;
	private TrayIconController? TrayIconController;

	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		ShutdownMode = ShutdownMode.OnExplicitShutdown;

		Controller = new AudioEngineController();

		var mainWindow = new MainWindow(Controller);
		MainWindow = mainWindow;

		TrayIconController = new TrayIconController(
			Controller,
			showWindow: mainWindow.ShowFromTray,
			exitApplication: ExitApplication);

		mainWindow.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		TrayIconController?.Dispose();
		Controller?.Dispose();

		base.OnExit(e);
	}

	private void ExitApplication()
	{
		if (MainWindow is MainWindow mainWindow)
			mainWindow.RequestExit();

		Shutdown();
	}
}
