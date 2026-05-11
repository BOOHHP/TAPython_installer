using System.Windows;
using System.Threading;

namespace TAPythonInstaller;

public partial class App : Application
{
	private const string SingleInstanceMutexName = @"Local\BOOHHP.TAPythonInstaller";
	private Mutex? singleInstanceMutex;
	private bool hasSingleInstanceLock;

	public App()
	{
		singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out hasSingleInstanceLock);
	}

	protected override void OnStartup(StartupEventArgs e)
	{
		if (!hasSingleInstanceLock)
		{
			Shutdown();
			return;
		}

		base.OnStartup(e);
		var mainWindow = new MainWindow();
		MainWindow = mainWindow;
		mainWindow.Show();
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (hasSingleInstanceLock) singleInstanceMutex?.ReleaseMutex();
		singleInstanceMutex?.Dispose();
		base.OnExit(e);
	}
}
