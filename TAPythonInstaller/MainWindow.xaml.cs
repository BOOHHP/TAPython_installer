using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace TAPythonInstaller;

public partial class MainWindow : Window
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string DefaultSourceEngineRoot = @"D:\AntLibs\WS";
    private const int MaxVisibleLogEntries = 200;

    private static readonly Brush StepPendingBackground = new SolidColorBrush(Color.FromRgb(29, 36, 53));
    private static readonly Brush StepActiveBackground = new SolidColorBrush(Color.FromRgb(245, 247, 251));
    private static readonly Brush StepDoneBackground = new SolidColorBrush(Color.FromRgb(43, 53, 81));
    private static readonly Brush StepPendingForeground = new SolidColorBrush(Color.FromRgb(121, 131, 153));
    private static readonly Brush StepActiveForeground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
    private static readonly Brush StepDoneForeground = new SolidColorBrush(Color.FromRgb(245, 247, 251));

    private readonly HttpClient httpClient = new();
    private readonly Queue<string> visibleLogEntries = new();

    private string? projectDirectory;
    private string? uprojectPath;
    private string? detectedEngineVersion;
    private string? localZipPath;

    private enum StepVisualState
    {
        Pending,
        Active,
        Done
    }

    public MainWindow()
    {
        InitializeComponent();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TAPythonInstaller/1.0");
        releaseCombo.SelectionChanged += (_, _) => UpdateReadinessState();
        projectInstallRadio.Checked += (_, _) => UpdateReadinessState();
        projectInstallRadio.Unchecked += (_, _) => UpdateReadinessState();
        engineInstallRadio.Checked += (_, _) => UpdateReadinessState();
        engineInstallRadio.Unchecked += (_, _) => UpdateReadinessState();
        fixBuildIdBox.Checked += (_, _) => UpdateReadinessState();
        fixBuildIdBox.Unchecked += (_, _) => UpdateReadinessState();
        ScanEngines();
        UpdateReadinessState();
    }

    // ─── Event handlers ───────────────────────────────────────

    private void BrowseProject_Click(object sender, RoutedEventArgs e) => BrowseProject();

    private void BrowseEngine_Click(object sender, RoutedEventArgs e) => BrowseEngine();

    private void ScanEngines_Click(object sender, RoutedEventArgs e) => ScanEngines();

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => SelectEngineFromCombo();

    private async void RefreshReleases_Click(object sender, RoutedEventArgs e)
        => await RefreshReleasesAsync();

    private void SelectLocalZip_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "TAPython ZIP (*.zip)|*.zip|All files (*.*)|*.*" };
        if (dialog.ShowDialog() == true)
        {
            localZipPath = dialog.FileName;
            localZipLabel.Text = localZipPath;
            UpdateReadinessState();
            Log($"已选择本地 ZIP：{localZipPath}");
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
        => await InstallAsync();

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProject();

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    // ─── UI actions ───────────────────────────────────────────

    private void BrowseProject()
    {
        var dialog = new OpenFileDialog { Filter = "Unreal Project (*.uproject)|*.uproject" };
        if (dialog.ShowDialog() != true) return;
        LoadProject(dialog.FileName);
    }

    private void LoadProject(string path)
    {
        uprojectPath = path;
        projectDirectory = Path.GetDirectoryName(path);
        projectPathBox.Text = path;

        var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        detectedEngineVersion = json?["EngineAssociation"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(detectedEngineVersion))
        {
            detectedEngineVersion = null;
            projectStatusLabel.Text = "未读取到 EngineAssociation，请手动选择引擎目录";
        }
        else
        {
            projectStatusLabel.Text = $"检测到 EngineAssociation: {detectedEngineVersion}";
        }

        var tapythonPlugin = Path.Combine(projectDirectory!, "Plugins", "TAPython", "TAPython.uplugin");
        installedStatusLabel.Text = File.Exists(tapythonPlugin)
            ? $"当前安装状态：已安装（{TryReadPluginVersion(tapythonPlugin)}）"
            : "当前安装状态：未在项目 Plugins 中发现 TAPython";

        SelectBestEngineForProject();
        UpdateReadinessState();
        _ = RefreshReleasesAsync();
    }

    private void BrowseEngine()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 UE 引擎根目录，例如 C:\\Program Files\\Epic Games\\UE_5.5 或 D:\\AntLibs\\WS\\..."
        };
        if (dialog.ShowDialog() == true)
        {
            enginePathBox.Text = dialog.FolderName;
            UpdateEngineStatus(dialog.FolderName);
            UpdateReadinessState();
        }
    }

    private void ScanEngines()
    {
        engineCombo.Items.Clear();
        var engines = DiscoverEngines()
            .GroupBy(e => e.Root, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(e => e.Version)
            .ToList();

        foreach (var engine in engines)
            engineCombo.Items.Add(engine);

        Log($"扫描到 {engines.Count} 个 UE 引擎（含 Launcher / 注册表 / {DefaultSourceEngineRoot}）。");
        SelectBestEngineForProject();
        UpdateReadinessState();
    }

    private List<EngineInfo> DiscoverEngines()
    {
        var result = new List<EngineInfo>();
        AddLauncherEngines(result);
        AddRegisteredSourceBuilds(result);
        AddSourceWorkspaceEngines(result, DefaultSourceEngineRoot);
        return result;
    }

    private static void AddLauncherEngines(List<EngineInfo> result)
    {
        foreach (var rootPath in new[] { @"SOFTWARE\EpicGames\Unreal Engine", @"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine" })
        {
            using var root = Registry.LocalMachine.OpenSubKey(rootPath);
            if (root == null) continue;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(name);
                var dir = key?.GetValue("InstalledDirectory") as string;
                AddEngineIfValid(result, dir, "Epic Launcher", name);
            }
        }

        var epicDir = @"C:\Program Files\Epic Games";
        if (Directory.Exists(epicDir))
        {
            foreach (var dir in Directory.GetDirectories(epicDir, "UE_*"))
                AddEngineIfValid(result, dir, "Epic Launcher", Path.GetFileName(dir).Replace("UE_", string.Empty));
        }
    }

    private static void AddRegisteredSourceBuilds(List<EngineInfo> result)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
        if (key == null) return;
        foreach (var valueName in key.GetValueNames())
            AddEngineIfValid(result, key.GetValue(valueName) as string, $"Source Build ({valueName})", valueName);
    }

    private static void AddSourceWorkspaceEngines(List<EngineInfo> result, string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot)) return;
        var candidates = Directory.EnumerateDirectories(workspaceRoot)
            .Concat(Directory.EnumerateDirectories(workspaceRoot).SelectMany(d => SafeEnumerateDirectories(d)))
            .Take(500);

        foreach (var dir in candidates)
            AddEngineIfValid(result, dir, "AntLibs Source", null);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static void AddEngineIfValid(List<EngineInfo> result, string? root, string source, string? association)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var editorExe = Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
        if (!File.Exists(editorExe)) return;
        result.Add(new EngineInfo(GetEngineVersion(root), root, source, association, GetEngineBuildId(root)));
    }

    private static string GetEngineVersion(string root)
    {
        var buildVersionPath = Path.Combine(root, "Engine", "Build", "Build.version");
        if (File.Exists(buildVersionPath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(buildVersionPath));
                var major = node?["MajorVersion"]?.GetValue<int>();
                var minor = node?["MinorVersion"]?.GetValue<int>();
                if (major.HasValue && minor.HasValue) return $"{major}.{minor}";
            }
            catch { }
        }

        var folderName = Path.GetFileName(root);
        var match = System.Text.RegularExpressions.Regex.Match(folderName, @"(\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : "Unknown";
    }

    private static string? GetEngineBuildId(string root)
    {
        var modulesPath = Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor.modules");
        if (!File.Exists(modulesPath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(modulesPath))?["BuildId"]?.GetValue<string>();
        }
        catch { return null; }
    }

    private void SelectBestEngineForProject()
    {
        if (engineCombo.Items.Count == 0) return;

        EngineInfo? best = null;
        if (!string.IsNullOrWhiteSpace(detectedEngineVersion))
        {
            foreach (EngineInfo engine in engineCombo.Items)
            {
                if (MatchesEngineAssociation(engine, detectedEngineVersion))
                {
                    best = engine;
                    break;
                }
            }

            if (best == null)
            {
                foreach (EngineInfo engine in engineCombo.Items)
                {
                    if (engine.Version == detectedEngineVersion ||
                        detectedEngineVersion.Contains(engine.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        best = engine;
                        break;
                    }
                }
            }
        }

        best ??= engineCombo.Items[0] as EngineInfo;
        engineCombo.SelectedItem = best;
    }

    private static bool MatchesEngineAssociation(EngineInfo engine, string engineAssociation)
    {
        var normalizedAssociation = NormalizeEngineAssociation(engineAssociation);
        if (string.IsNullOrWhiteSpace(normalizedAssociation)) return false;

        return string.Equals(NormalizeEngineAssociation(engine.Association), normalizedAssociation, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(engine.Version, normalizedAssociation, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(engine.Version.Replace('.', '_'), normalizedAssociation, StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeEngineAssociation(string? association)
    {
        if (string.IsNullOrWhiteSpace(association)) return null;
        return association.Trim().Trim('{', '}').Replace("UE_", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private void SelectEngineFromCombo()
    {
        if (engineCombo.SelectedItem is not EngineInfo engine) return;
        enginePathBox.Text = engine.Root;
        UpdateEngineStatus(engine.Root);
        UpdateReadinessState();
    }

    private void UpdateEngineStatus(string engineRoot)
    {
        engineStatusLabel.Text = Directory.Exists(engineRoot)
            ? $"引擎版本: {GetEngineVersion(engineRoot)} · BuildId: {GetEngineBuildId(engineRoot) ?? "未找到"}"
            : "引擎目录不存在";
    }

    private async Task RefreshReleasesAsync()
    {
        try
        {
            releaseCombo.Items.Clear();
            Log("正在查询 TAPython GitHub Release 列表...");
            var json = await httpClient.GetStringAsync(ReleaseApiUrl);
            var releases = JsonNode.Parse(json)?.AsArray() ?? [];

            foreach (var release in releases)
            {
                var tag = release?["tag_name"]?.GetValue<string>() ?? "unknown";
                var name = release?["name"]?.GetValue<string>() ?? tag;
                var assets = release?["assets"]?.AsArray();
                if (assets == null) continue;

                foreach (var asset in assets)
                {
                    var assetName = asset?["name"]?.GetValue<string>() ?? "";
                    var downloadUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";
                    if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!IsCompatibleRelease(tag, assetName)) continue;
                    releaseCombo.Items.Add(new ReleaseInfo(tag, name, assetName, downloadUrl));
                }
            }

            if (releaseCombo.Items.Count > 0) releaseCombo.SelectedIndex = 0;
            UpdateReadinessState();
            Log($"版本列表刷新完成：{releaseCombo.Items.Count} 个候选 ZIP。{(detectedEngineVersion == null ? "未检测项目版本，显示全部。" : $"已按 UE {detectedEngineVersion} 过滤。")}");
        }
        catch (Exception ex)
        {
            UpdateReadinessState();
            Log($"刷新版本失败：{ex.Message}");
        }
    }

    private bool IsCompatibleRelease(string tag, string assetName)
    {
        if (string.IsNullOrWhiteSpace(detectedEngineVersion)) return true;
        var version = detectedEngineVersion.Trim();
        if (version.StartsWith("{", StringComparison.Ordinal)) return true;
        return tag.Contains(version, StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains(version, StringComparison.OrdinalIgnoreCase) ||
               tag.Contains(version.Replace(".", "_"), StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains(version.Replace(".", "_"), StringComparison.OrdinalIgnoreCase);
    }

    private async Task InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(uprojectPath))
        {
            MessageBox.Show("请先选择 .uproject 文件。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(enginePathBox.Text) && (fixBuildIdBox.IsChecked == true || engineInstallRadio.IsChecked == true))
        {
            MessageBox.Show("请先选择引擎目录。", "缺少引擎", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(localZipPath) && releaseCombo.SelectedItem is not ReleaseInfo)
        {
            MessageBox.Show("请刷新并选择 TAPython 版本，或选择本地 ZIP。", "缺少安装源", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            installButton.IsEnabled = false;
            SetInstallProgress(0, "准备安装");
            Log("开始安装 TAPython...");

            var zipPath = await ResolveZipAsync();
            var installRoot = projectInstallRadio.IsChecked == true
                ? Path.Combine(projectDirectory, "Plugins")
                : Path.Combine(enginePathBox.Text, "Engine", "Plugins", "Marketplace");

            Directory.CreateDirectory(installRoot);
            var targetPluginDir = Path.Combine(installRoot, "TAPython");
            if (Directory.Exists(targetPluginDir))
            {
                if (backupBox.IsChecked == true)
                {
                    var backupDir = targetPluginDir + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    Directory.Move(targetPluginDir, backupDir);
                    Log($"已备份旧版本：{backupDir}");
                }
                else
                {
                    Directory.Delete(targetPluginDir, true);
                    Log("已删除旧版本 TAPython。");
                }
            }

            var extractRoot = Path.Combine(Path.GetTempPath(), "TAPythonInstaller", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);
            var sourcePluginDir = FindTapythonPluginDirectory(extractRoot);
            if (sourcePluginDir == null)
                throw new InvalidOperationException("ZIP 中未找到 TAPython.uplugin。请确认选择的是 TAPython 插件 ZIP。");

            CopyDirectory(sourcePluginDir, targetPluginDir);
            Log($"插件已安装到：{targetPluginDir}");

            InstallDefaultPythonSource(targetPluginDir);

            if (autoEnablePythonBox.IsChecked == true || autoEnableTapythonBox.IsChecked == true)
                EnablePluginsInUProject();
            if (configurePythonPathBox.IsChecked == true)
                ConfigurePythonPath();
            if (fixBuildIdBox.IsChecked == true)
                FixBuildId(targetPluginDir, enginePathBox.Text);

            ValidateInstall(targetPluginDir);
            SetInstallProgress(100, "安装完成");
            openProjectButton.IsEnabled = true;
            MessageBox.Show("TAPython 安装配置完成。建议重启 UE 编辑器后打开 Chameleon Gallery 检查 Python Path Ready。",
                            "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            installStateText.Text = "安装失败";
            pipelineStatusText.Text = "安装失败，请查看日志";
            Log($"安装失败：{ex}");
            MessageBox.Show(ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            installButton.IsEnabled = true;
        }
    }

    private async Task<string> ResolveZipAsync()
    {
        if (!string.IsNullOrWhiteSpace(localZipPath))
        {
            Log($"使用本地 ZIP：{localZipPath}");
            return localZipPath;
        }

        var release = (ReleaseInfo)releaseCombo.SelectedItem!;
        var zipPath = Path.Combine(Path.GetTempPath(), "TAPythonInstaller", release.AssetName);
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        Log($"正在下载：{release.AssetName}");

        using var response = await httpClient.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(zipPath);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (total.HasValue)
            {
                var percent = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
                SetInstallProgress(percent, $"下载中 {percent}%");
            }
        }

        Log($"下载完成：{zipPath}");
        return zipPath;
    }

    private static string? FindTapythonPluginDirectory(string root)
    {
        var candidates = Directory.GetFiles(root, "TAPython.uplugin", SearchOption.AllDirectories);
        return candidates.Length == 0 ? null : Path.GetDirectoryName(candidates[0]);
    }

    private void EnablePluginsInUProject()
    {
        var root = JsonNode.Parse(File.ReadAllText(uprojectPath!))!.AsObject();
        var plugins = root["Plugins"] as JsonArray;
        if (plugins == null)
        {
            plugins = [];
            root["Plugins"] = plugins;
        }

        if (autoEnablePythonBox.IsChecked == true) UpsertPlugin(plugins, "PythonScriptPlugin");
        if (autoEnableTapythonBox.IsChecked == true) UpsertPlugin(plugins, "TAPython");
        File.WriteAllText(uprojectPath!, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log(".uproject 已更新插件启用状态。");
    }

    private static void UpsertPlugin(JsonArray plugins, string name)
    {
        foreach (var item in plugins)
        {
            if (item is JsonObject obj && obj["Name"]?.GetValue<string>() == name)
            {
                obj["Enabled"] = true;
                return;
            }
        }
        plugins.Add(new JsonObject { ["Name"] = name, ["Enabled"] = true });
    }

    private void ConfigurePythonPath()
    {
        var configDir = Path.Combine(projectDirectory!, "Config");
        Directory.CreateDirectory(configDir);
        var iniPath = Path.Combine(configDir, "DefaultEngine.ini");
        if (!File.Exists(iniPath)) File.WriteAllText(iniPath, string.Empty);

        var pythonPath = Path.Combine(projectDirectory!, "TA", "TAPython", "Python");
        Directory.CreateDirectory(pythonPath);
        var unrealPythonPath = GetUnrealPythonPath(pythonPath);

        var content = File.ReadAllText(iniPath);
        var section = "[/Script/PythonScriptPlugin.PythonScriptPluginSettings]";
        var devMode = "bDeveloperMode=True";
        var pathLine = $"+AdditionalPaths=(Path=\"{unrealPythonPath}\")";

        if (!content.Contains(section, StringComparison.OrdinalIgnoreCase))
        {
            content = content.TrimEnd() + Environment.NewLine + Environment.NewLine + section + Environment.NewLine + devMode + Environment.NewLine + pathLine + Environment.NewLine;
        }
        else
        {
            if (!content.Contains(devMode, StringComparison.OrdinalIgnoreCase)) content = InsertAfterSection(content, section, devMode);
            content = UpsertPythonAdditionalPath(content, section, pathLine);
        }

        File.WriteAllText(iniPath, content);
        Log($"DefaultEngine.ini 已配置 Python 附加路径：{unrealPythonPath}");
    }

    private static string UpsertPythonAdditionalPath(string content, string section, string pathLine)
    {
        if (content.Contains(pathLine, StringComparison.OrdinalIgnoreCase)) return content;

        const string oldPathPattern = "(?im)^[ \\t]*\\+AdditionalPaths=\\(Path=\"[^\"]*(?:TA/TAPython/Python|TA\\\\TAPython\\\\Python|TAPythonInstaller/PythonPathLinks|TAPythonInstaller\\\\PythonPathLinks|TAPythonInstaller/ProjectLinks|TAPythonInstaller\\\\ProjectLinks)[^\"]*\"\\)[ \\t]*\\r?$";
        var updated = System.Text.RegularExpressions.Regex.Replace(content, oldPathPattern, pathLine);
        return updated == content ? InsertAfterSection(content, section, pathLine) : updated;
    }

    private void InstallDefaultPythonSource(string targetPluginDir)
    {
        var sourceRoot = Path.Combine(targetPluginDir, "Resources", "DefaultPythonSource", "TA", "TAPython");
        if (!Directory.Exists(sourceRoot))
        {
            Log("未找到 TAPython 默认 Python 源文件，跳过复制。");
            return;
        }

        var projectTapythonDir = Path.Combine(projectDirectory!, "TA", "TAPython");
        CopyDirectory(sourceRoot, projectTapythonDir);
        Log($"已复制 TAPython 默认资源到：{projectTapythonDir}");

        var defaultConfig = Path.Combine(sourceRoot, "Config", "config.ini");
        if (File.Exists(defaultConfig))
        {
            var pluginConfigDir = Path.Combine(targetPluginDir, "Config");
            Directory.CreateDirectory(pluginConfigDir);
            File.Copy(defaultConfig, Path.Combine(pluginConfigDir, "Plugin_Config.ini"), true);
            Log("已写入 TAPython 插件配置：Config/Plugin_Config.ini");
        }
    }

    private string GetUnrealPythonPath(string pythonPath)
    {
        if (!ContainsNonAscii(projectDirectory!)) return NormalizeUnrealPath(pythonPath);

        var projectLink = CreateAsciiProjectJunction(projectDirectory!);
        var linkedPythonPath = Path.Combine(projectLink, "TA", "TAPython", "Python");
        Log($"检测到项目路径包含非 ASCII 字符，Python 附加路径将使用 ASCII 项目联接：{linkedPythonPath}");
        return NormalizeUnrealPath(linkedPythonPath);
    }

    private static bool ContainsNonAscii(string value)
    {
        foreach (var ch in value)
            if (ch > 127) return true;
        return false;
    }

    private static string NormalizeUnrealPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

    private static string CreateAsciiProjectJunction(string targetProjectDir)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetProjectDir)))[..16];
        var linkParent = Path.Combine(localAppData, "TAPythonInstaller", "ProjectLinks", hash);
        var linkPath = Path.Combine(linkParent, "Project");

        Directory.CreateDirectory(linkParent);
        if (Directory.Exists(linkPath)) RemoveDirectoryJunction(linkPath);

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{linkPath}\" \"{targetProjectDir}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        process!.WaitForExit();
        if (process.ExitCode != 0)
        {
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"创建项目路径联接失败：{output}{error}");
        }

        return linkPath;
    }

    private static void RemoveDirectoryJunction(string linkPath)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c rmdir \"{linkPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        });

        process!.WaitForExit();
        if (process.ExitCode != 0) Directory.Delete(linkPath);
    }

    private static string InsertAfterSection(string content, string section, string line)
    {
        var index = content.IndexOf(section, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return content + Environment.NewLine + section + Environment.NewLine + line + Environment.NewLine;
        var insertAt = content.IndexOf(Environment.NewLine, index, StringComparison.Ordinal);
        if (insertAt < 0) insertAt = content.Length;
        return content.Insert(insertAt + Environment.NewLine.Length, line + Environment.NewLine);
    }

    private void FixBuildId(string targetPluginDir, string engineRoot)
    {
        var buildId = GetEngineBuildId(engineRoot);
        if (string.IsNullOrWhiteSpace(buildId))
        {
            Log("未找到引擎 BuildId，跳过 BuildId 修复。");
            return;
        }

        var pluginModules = Path.Combine(targetPluginDir, "Binaries", "Win64", "UnrealEditor.modules");
        if (!File.Exists(pluginModules))
        {
            Log("未找到 TAPython 的 UnrealEditor.modules，跳过 BuildId 修复。");
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(pluginModules))!.AsObject();
        node["BuildId"] = buildId;
        File.WriteAllText(pluginModules, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log($"BuildId 已修复为：{buildId}");
    }

    private void ValidateInstall(string targetPluginDir)
    {
        var expectedPythonPath = GetUnrealPythonPath(Path.Combine(projectDirectory!, "TA", "TAPython", "Python"));
        var checks = new Dictionary<string, bool>
        {
            ["TAPython.uplugin"]          = File.Exists(Path.Combine(targetPluginDir, "TAPython.uplugin")),
            ["Binaries/Win64"]            = Directory.Exists(Path.Combine(targetPluginDir, "Binaries", "Win64")),
            ["DefaultEngine.ini Python Path"] = File.ReadAllText(Path.Combine(projectDirectory!, "Config", "DefaultEngine.ini"))
                                               .Contains(expectedPythonPath, StringComparison.OrdinalIgnoreCase)
        };

        foreach (var check in checks)
            Log($"校验 {(check.Value ? "通过" : "失败")}：{check.Key}");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }

    private string TryReadPluginVersion(string upluginPath)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(upluginPath));
            return node?["VersionName"]?.GetValue<string>() ?? "版本未知";
        }
        catch { return "版本未知"; }
    }

    private void OpenProject()
    {
        if (File.Exists(uprojectPath))
            Process.Start(new ProcessStartInfo(uprojectPath) { UseShellExecute = true });
    }

    private void UpdateReadinessState()
    {
        var hasProject = !string.IsNullOrWhiteSpace(projectDirectory) && File.Exists(uprojectPath);
        var hasEngine = !string.IsNullOrWhiteSpace(enginePathBox.Text) && Directory.Exists(enginePathBox.Text);
        var hasSource = !string.IsNullOrWhiteSpace(localZipPath) || releaseCombo.SelectedItem is ReleaseInfo;
        var target = projectInstallRadio.IsChecked == true ? "项目 Plugins" : "引擎 Marketplace";

        projectCheckText.Text = hasProject ? "已选择" : "未选择";
        engineCheckText.Text = hasEngine ? "已选择" : "未选择";
        sourceCheckText.Text = hasSource ? (!string.IsNullOrWhiteSpace(localZipPath) ? "本地 ZIP" : "远程 Release") : "未选择";
        targetCheckText.Text = target;
        heroTargetText.Text = target;

        projectHeroChip.Text = hasProject ? "项目已就绪" : "项目待选择";
        sourceHeroChip.Text = hasSource ? "安装源已就绪" : "安装源待选择";

        if (!hasProject)
        {
            UpdateStepRail(activeStep: 1, completedSteps: 0, badgeText: "1");
            topStatusLabel.Text = "等待选择项目";
            heroCurrentStatusText.Text = "等待选择项目";
            heroNextActionText.Text = "选择 .uproject 文件";
            pipelineStatusText.Text = "请选择 .uproject 文件开始";
            return;
        }

        if (!hasEngine && (fixBuildIdBox.IsChecked == true || engineInstallRadio.IsChecked == true))
        {
            UpdateStepRail(activeStep: 1, completedSteps: 0, badgeText: "1");
            topStatusLabel.Text = "需要引擎目录";
            heroCurrentStatusText.Text = "需要引擎目录";
            heroNextActionText.Text = "选择 UE 引擎根目录";
            pipelineStatusText.Text = "选择 UE 引擎目录后可继续";
            return;
        }

        if (!hasSource)
        {
            UpdateStepRail(activeStep: 2, completedSteps: 1, badgeText: "2");
            topStatusLabel.Text = "等待安装源";
            heroCurrentStatusText.Text = "等待安装源";
            heroNextActionText.Text = "刷新版本或选择 ZIP";
            pipelineStatusText.Text = "刷新远程版本或选择本地 ZIP";
            return;
        }

        UpdateStepRail(activeStep: 3, completedSteps: 2, badgeText: "3");
        topStatusLabel.Text = "准备安装";
        heroCurrentStatusText.Text = "准备安装";
        heroNextActionText.Text = "点击一键安装";
        pipelineStatusText.Text = $"目标：{target}";
    }

    private void SetInstallProgress(int percent, string state)
    {
        var clampedPercent = Math.Clamp(percent, 0, 100);
        progressBar.Value = clampedPercent;
        installProgressText.Text = $"{clampedPercent}%";
        installStateText.Text = state;
        heroCurrentStatusText.Text = state;
        heroNextActionText.Text = clampedPercent >= 100 ? "打开项目检查插件" : "等待流程完成";
        pipelineStatusText.Text = state;
        UpdateStepRail(
            activeStep: clampedPercent >= 100 ? 0 : 4,
            completedSteps: clampedPercent >= 100 ? 4 : 3,
            badgeText: clampedPercent >= 100 ? "✓" : "4");
    }

    private void UpdateStepRail(int activeStep, int completedSteps, string badgeText)
    {
        SetStepState(projectStepCard, projectStepNumber, projectStepLabel, GetStepState(1, activeStep, completedSteps));
        SetStepState(versionStepCard, versionStepNumber, versionStepLabel, GetStepState(2, activeStep, completedSteps));
        SetStepState(optionsStepCard, optionsStepNumber, optionsStepLabel, GetStepState(3, activeStep, completedSteps));
        SetStepState(installStepCard, installStepNumber, installStepLabel, GetStepState(4, activeStep, completedSteps));

        railStatusText.Text = badgeText;
        railStatusBadge.Background = activeStep == 0 ? StepDoneBackground : StepPendingBackground;
        railStatusText.Foreground = activeStep == 0 ? StepDoneForeground : StepPendingForeground;
    }

    private static StepVisualState GetStepState(int step, int activeStep, int completedSteps)
    {
        if (step <= completedSteps) return StepVisualState.Done;
        return step == activeStep ? StepVisualState.Active : StepVisualState.Pending;
    }

    private static void SetStepState(Border card, TextBlock number, TextBlock label, StepVisualState state)
    {
        var background = state switch
        {
            StepVisualState.Active => StepActiveBackground,
            StepVisualState.Done => StepDoneBackground,
            _ => StepPendingBackground
        };
        var foreground = state switch
        {
            StepVisualState.Active => StepActiveForeground,
            StepVisualState.Done => StepDoneForeground,
            _ => StepPendingForeground
        };

        card.Background = background;
        number.Foreground = foreground;
        label.Foreground = foreground;
    }

    private void Log(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Log(message));
            return;
        }

        visibleLogEntries.Enqueue($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (visibleLogEntries.Count > MaxVisibleLogEntries)
            visibleLogEntries.Dequeue();

        logBox.Text = string.Join(Environment.NewLine, visibleLogEntries);
        logBox.ScrollToEnd();
    }

    private sealed record EngineInfo(string Version, string Root, string Source, string? Association, string? BuildId)
    {
        public override string ToString() => $"UE {Version} · {Source} · {Root}";
    }

    private sealed record ReleaseInfo(string Tag, string Name, string AssetName, string DownloadUrl)
    {
        public override string ToString() => $"{Tag} · {AssetName}";
    }
}
