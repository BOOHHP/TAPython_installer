using System.Windows;
using System.Threading;
using System.IO;

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
		var showChangelogOnStartup = e.Args.Any(arg => string.Equals(arg, "--show-changelog", StringComparison.OrdinalIgnoreCase)) || ConsumeChangelogStartupSignal();
		var mainWindow = new MainWindow(showChangelogOnStartup);
		MainWindow = mainWindow;
		mainWindow.Show();
	}

	private static bool ConsumeChangelogStartupSignal()
	{
		try
		{
			var markerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAPythonInstaller", "show-changelog.flag");
			if (!File.Exists(markerPath)) return false;
			File.Delete(markerPath);
			return true;
		}
		catch
		{
			return false;
		}
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (hasSingleInstanceLock) singleInstanceMutex?.ReleaseMutex();
		singleInstanceMutex?.Dispose();
		base.OnExit(e);
	}
}
