using System.Windows;
using System.Threading;
using System.IO;
using System.Reflection;

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
		var currentVersion = GetCurrentInstallerVersion();
		var hasChangelogArgument = e.Args.Any(arg => string.Equals(arg, "--show-changelog", StringComparison.OrdinalIgnoreCase));
		var hasChangelogSignal = ConsumeChangelogStartupSignal();
		var isFirstLaunchForVersion = ConsumeFirstLaunchForVersion(currentVersion);
		var showChangelogOnStartup = hasChangelogArgument || hasChangelogSignal || isFirstLaunchForVersion;
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

	private static bool ConsumeFirstLaunchForVersion(string currentVersion)
	{
		if (string.IsNullOrWhiteSpace(currentVersion) || string.Equals(currentVersion, "unknown", StringComparison.OrdinalIgnoreCase)) return false;

		try
		{
			var appDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TAPythonInstaller");
			var versionPath = Path.Combine(appDataDirectory, "last-changelog-version.txt");
			var lastVersion = File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : string.Empty;
			if (string.Equals(lastVersion, currentVersion, StringComparison.OrdinalIgnoreCase)) return false;

			Directory.CreateDirectory(appDataDirectory);
			File.WriteAllText(versionPath, currentVersion);
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static string GetCurrentInstallerVersion()
	{
		var assembly = Assembly.GetExecutingAssembly();
		var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		return string.IsNullOrWhiteSpace(informationalVersion)
			? assembly.GetName().Version?.ToString() ?? "unknown"
			: informationalVersion.Split('+')[0];
	}

	protected override void OnExit(ExitEventArgs e)
	{
		if (hasSingleInstanceLock) singleInstanceMutex?.ReleaseMutex();
		singleInstanceMutex?.Dispose();
		base.OnExit(e);
	}
}
