using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TAPythonInstaller;

public partial class MainWindow : Window
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string ReleaseHtmlUrl = "https://github.com/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string ReleaseAtomUrl = "https://github.com/cgerchenhp/UE_TAPython_Plugin_Release/releases.atom";
    private const string DefaultSourceEngineRoot = @"D:\AntLibs\WS";
    private const string ToolHubRoot = @"D:\Claude\project\tapython-tool-hub";
    private const int MaxVisibleLogEntries = 200;
    private const int MaxHtmlReleaseTags = 40;

    public static readonly DependencyProperty NavIsCollapsedProperty = DependencyProperty.Register(
        nameof(NavIsCollapsed),
        typeof(bool),
        typeof(MainWindow),
        new PropertyMetadata(false));

    private static readonly Brush StepPendingBackground = new SolidColorBrush(Color.FromRgb(29, 36, 53));
    private static readonly Brush StepActiveBackground = new SolidColorBrush(Color.FromRgb(245, 247, 251));
    private static readonly Brush StepDoneBackground = new SolidColorBrush(Color.FromRgb(43, 53, 81));
    private static readonly Brush StepPendingForeground = new SolidColorBrush(Color.FromRgb(121, 131, 153));
    private static readonly Brush StepActiveForeground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
    private static readonly Brush StepDoneForeground = new SolidColorBrush(Color.FromRgb(245, 247, 251));

    private readonly HttpClient httpClient = new();
    private readonly Queue<string> visibleLogEntries = new();
    private readonly List<TapythonToolInfo> detectedProjectTools = new();
    private readonly ObservableCollection<NavigationItem> navigationItems = new();
    private readonly ObservableCollection<TapythonToolInfo> projectToolItems = new();
    private readonly ObservableCollection<HubToolInfo> hubToolItems = new();

    private string? projectDirectory;
    private string? uprojectPath;
    private string? detectedEngineVersion;
    private string? localZipPath;
    private Point navDragStartPoint;
    private NavigationItem? draggedNavigationItem;
    private Point toolTabDragStartPoint;
    private bool isToolTabDragging;

    public bool NavIsCollapsed
    {
        get => (bool)GetValue(NavIsCollapsedProperty);
        set => SetValue(NavIsCollapsedProperty, value);
    }

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
        InitializeNavigation();
        InitializeToolPages();
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

    private void InitializeNavigation()
    {
        navigationItems.Add(new NavigationItem("install", "01", "安装诊断", "一键安装/卸载"));
        navigationItems.Add(new NavigationItem("tools", "02", "脚本工具", "项目工具与资源库"));
        navigationItems.Add(new NavigationItem("templates", "03", "项目模板", "预留入口"));
        navigationItems.Add(new NavigationItem("versions", "04", "版本管理", "预留入口"));
        navigationItems.Add(new NavigationItem("checks", "05", "配置检查", "预留入口"));
        navigationItems.Add(new NavigationItem("cleanup", "06", "清理维护", "预留入口"));
        navigationItems.Add(new NavigationItem("logs", "07", "日志中心", "预留入口"));
        navigationItems.Add(new NavigationItem("settings", "08", "设置", "预留入口"));

        navList.ItemsSource = navigationItems;
        navList.SelectedIndex = 0;
    }

    private void InitializeToolPages()
    {
        projectToolsList.ItemsSource = projectToolItems;
        hubToolsList.ItemsSource = hubToolItems;
        ShowToolTab("project");
        RefreshToolSummaries();
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

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
        => await UninstallAsync();

    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProject();

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void ToggleMaximizeWindow_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWindow_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleNavigation_Click(object sender, RoutedEventArgs e)
    {
        NavIsCollapsed = !NavIsCollapsed;
        navigationColumn.Width = new GridLength(NavIsCollapsed ? 72 : 216);
        navBrandText.Visibility = NavIsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        navHintText.Visibility = NavIsCollapsed ? Visibility.Collapsed : Visibility.Visible;
        navCollapseButton.Content = NavIsCollapsed ? "›" : "‹";
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (navList.SelectedItem is not NavigationItem item) return;
        ShowNavigationPage(item.PageKey);
    }

    private void NavList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        navDragStartPoint = e.GetPosition(null);
        draggedNavigationItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as NavigationItem;
    }

    private void NavList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || draggedNavigationItem == null) return;
        var currentPosition = e.GetPosition(null);
        if (Math.Abs(currentPosition.Y - navDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(navList, draggedNavigationItem, DragDropEffects.Move);
    }

    private void NavList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(NavigationItem))) return;
        var droppedItem = (NavigationItem)e.Data.GetData(typeof(NavigationItem))!;
        var targetItem = FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource)?.DataContext as NavigationItem;
        if (targetItem == null || ReferenceEquals(droppedItem, targetItem)) return;

        var oldIndex = navigationItems.IndexOf(droppedItem);
        var newIndex = navigationItems.IndexOf(targetItem);
        if (oldIndex < 0 || newIndex < 0) return;
        navigationItems.Move(oldIndex, newIndex);
        navList.SelectedItem = droppedItem;
    }

    private void ProjectToolsTab_Click(object sender, RoutedEventArgs e) => ShowToolTab("project");

    private void HubToolsTab_Click(object sender, RoutedEventArgs e) => ShowToolTab("hub");

    private void ToolTabsHost_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        isToolTabDragging = true;
        toolTabDragStartPoint = e.GetPosition(toolTabsHost);
        toolTabsHost.CaptureMouse();
    }

    private void ToolTabsHost_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!isToolTabDragging) return;
        isToolTabDragging = false;
        toolTabsHost.ReleaseMouseCapture();
        var deltaX = e.GetPosition(toolTabsHost).X - toolTabDragStartPoint.X;
        if (Math.Abs(deltaX) < 60) return;
        ShowToolTab(deltaX < 0 ? "hub" : "project");
    }

    private void RefreshProjectTools_Click(object sender, RoutedEventArgs e) => RefreshProjectTools();

    private void RefreshHubTools_Click(object sender, RoutedEventArgs e) => RefreshHubTools();

    private void MergeHubTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HubToolInfo hubTool)
            MergeHubTool(hubTool);
    }

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

        RefreshInstalledStatus();

        SelectBestEngineForProject();
        RefreshProjectTools();
        UpdateReadinessState();
        _ = RefreshReleasesAsync();
    }

    private void ShowNavigationPage(string pageKey)
    {
        installPageGrid.Visibility = pageKey == "install" ? Visibility.Visible : Visibility.Collapsed;
        toolsPageGrid.Visibility = pageKey == "tools" ? Visibility.Visible : Visibility.Collapsed;

        if (pageKey == "tools")
        {
            RefreshProjectTools();
            if (hubToolItems.Count == 0) RefreshHubTools();
        }
        else if (pageKey != "install")
        {
            installPageGrid.Visibility = Visibility.Visible;
            toolsPageGrid.Visibility = Visibility.Collapsed;
            Log($"导航入口“{navigationItems.FirstOrDefault(item => item.PageKey == pageKey)?.Title}”已预留，后续可接入功能页。");
        }
    }

    private void ShowToolTab(string tabKey)
    {
        var showProjectTools = tabKey == "project";
        projectToolsPanel.Visibility = showProjectTools ? Visibility.Visible : Visibility.Collapsed;
        hubToolsPanel.Visibility = showProjectTools ? Visibility.Collapsed : Visibility.Visible;
        projectToolsTabButton.Background = showProjectTools ? StepActiveBackground : StepPendingBackground;
        projectToolsTabButton.Foreground = showProjectTools ? StepActiveForeground : StepPendingForeground;
        hubToolsTabButton.Background = showProjectTools ? StepPendingBackground : StepActiveBackground;
        hubToolsTabButton.Foreground = showProjectTools ? StepPendingForeground : StepActiveForeground;
    }

    private void RefreshProjectTools()
    {
        projectToolItems.Clear();
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            RefreshToolSummaries();
            return;
        }

        var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
        foreach (var tool in DetectExistingProjectTapythonTools(projectPythonDir))
            projectToolItems.Add(tool);

        RefreshToolSummaries();
    }

    private void RefreshHubTools()
    {
        hubToolItems.Clear();
        if (!Directory.Exists(ToolHubRoot))
        {
            Log($"未找到 TAPython 工具分享网站目录：{ToolHubRoot}");
            RefreshToolSummaries();
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(ToolHubRoot, "*", SearchOption.TopDirectoryOnly)
                     .Where(directory => Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories).Any(IsTapythonToolFile))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            hubToolItems.Add(new HubToolInfo(Path.GetFileName(dir), Path.GetRelativePath(ToolHubRoot, dir), dir));
        }

        foreach (var file in Directory.EnumerateFiles(ToolHubRoot, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsTapythonToolFile)
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            hubToolItems.Add(new HubToolInfo(Path.GetFileNameWithoutExtension(file), Path.GetRelativePath(ToolHubRoot, file), file));
        }

        Log($"工具分享网站资源扫描完成：{hubToolItems.Count} 个候选工具。");
        RefreshToolSummaries();
    }

    private void MergeHubTool(HubToolInfo hubTool)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再合并工具到项目。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
        Directory.CreateDirectory(projectPythonDir);

        if (Directory.Exists(hubTool.SourcePath))
        {
            CopyDirectory(hubTool.SourcePath, Path.Combine(projectPythonDir, Path.GetFileName(hubTool.SourcePath)), overwrite: false);
        }
        else if (File.Exists(hubTool.SourcePath))
        {
            var targetFile = Path.Combine(projectPythonDir, Path.GetFileName(hubTool.SourcePath));
            if (!File.Exists(targetFile)) File.Copy(hubTool.SourcePath, targetFile);
        }

        Log($"已合并工具资源到项目：{hubTool.Name}");
        RefreshProjectTools();
    }

    private void RefreshToolSummaries()
    {
        toolsProjectSummaryText.Text = string.IsNullOrWhiteSpace(projectDirectory)
            ? "未选择"
            : $"{projectToolItems.Count} 个工具";
        projectToolsEmptyText.Visibility = projectToolItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        hubToolsEmptyText.Visibility = hubToolItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        releaseCombo.Items.Clear();
        Log("正在查询 TAPython 远程版本列表...");

        var source = "远程";
        List<ReleaseInfo> releases;
        try
        {
            releases = await FetchReleaseInfosAsync();
        }
        catch (Exception ex)
        {
            releases = ReadCachedReleaseInfos();
            source = "本地缓存";
            if (releases.Count == 0)
            {
                UpdateReadinessState();
                Log($"刷新版本失败，且未找到本地缓存：{ex.Message}。仍可使用本地 ZIP 安装。 ");
                return;
            }

            Log($"远程版本刷新失败，已自动使用本地缓存：{ex.Message}");
        }

        var compatibleReleases = releases
            .Where(release => IsCompatibleRelease(release.Tag, release.AssetName))
            .OrderByDescending(release => release.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(release => release.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var release in compatibleReleases)
            releaseCombo.Items.Add(release);

        if (releaseCombo.Items.Count > 0) releaseCombo.SelectedIndex = 0;
        UpdateReadinessState();
        Log($"版本列表刷新完成：{releaseCombo.Items.Count} 个候选 ZIP（来源：{source}）。{(detectedEngineVersion == null ? "未检测项目版本，显示全部。" : $"已按 UE {detectedEngineVersion} 过滤。")}");
    }

    private async Task<List<ReleaseInfo>> FetchReleaseInfosAsync()
    {
        var failures = new List<string>();

        try
        {
            var releases = await FetchReleaseInfosFromApiAsync();
            if (releases.Count > 0)
            {
                WriteCachedReleaseInfos(releases);
                Log("GitHub API 查询成功，已更新版本缓存。 ");
                return releases;
            }

            failures.Add("GitHub API 未返回 ZIP 资源");
        }
        catch (Exception ex)
        {
            failures.Add($"GitHub API: {ex.Message}");
            Log($"GitHub API 查询失败，尝试 Releases 页面兜底：{ex.Message}");
        }

        try
        {
            var releases = await FetchReleaseInfosFromHtmlAsync();
            if (releases.Count > 0)
            {
                WriteCachedReleaseInfos(releases);
                Log("GitHub Releases 页面解析成功，已更新版本缓存。 ");
                return releases;
            }

            failures.Add("GitHub Releases 页面未解析到 ZIP 资源");
        }
        catch (Exception ex)
        {
            failures.Add($"GitHub Releases 页面: {ex.Message}");
        }

        throw new InvalidOperationException(string.Join("；", failures));
    }

    private async Task<List<ReleaseInfo>> FetchReleaseInfosFromApiAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseApiUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var releases = JsonNode.Parse(json)?.AsArray() ?? [];
        var result = new List<ReleaseInfo>();

        foreach (var release in releases)
        {
            var tag = release?["tag_name"]?.GetValue<string>() ?? "unknown";
            var name = release?["name"]?.GetValue<string>() ?? tag;
            var assets = release?["assets"]?.AsArray();
            if (assets == null) continue;

            foreach (var asset in assets)
            {
                var assetName = asset?["name"]?.GetValue<string>() ?? string.Empty;
                var downloadUrl = asset?["browser_download_url"]?.GetValue<string>() ?? string.Empty;
                if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(downloadUrl)) continue;
                result.Add(new ReleaseInfo(tag, name, assetName, downloadUrl));
            }
        }

        return DeduplicateReleases(result);
    }

    private async Task<List<ReleaseInfo>> FetchReleaseInfosFromHtmlAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseHtmlUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync();
        var result = ExtractReleaseInfosFromAssetHtml(html);
        if (result.Count > 0) return result;

        var tags = ExtractReleaseTags(html).Take(MaxHtmlReleaseTags).ToList();
        if (tags.Count == 0)
            tags = (await FetchReleaseTagsFromAtomAsync()).Take(MaxHtmlReleaseTags).ToList();

        foreach (var tag in tags)
        {
            try
            {
                var assetHtml = await FetchReleaseAssetHtmlAsync(tag);
                result.AddRange(ExtractReleaseInfosFromAssetHtml(assetHtml, tag));
            }
            catch (Exception ex)
            {
                Log($"解析 Release 附件失败，已跳过 {tag}：{ex.Message}");
            }
        }

        return DeduplicateReleases(result);
    }

    private async Task<List<string>> FetchReleaseTagsFromAtomAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleaseAtomUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return ExtractReleaseTags(await response.Content.ReadAsStringAsync());
    }

    private async Task<string> FetchReleaseAssetHtmlAsync(string tag)
    {
        var expandedAssetsUrl = $"https://github.com/cgerchenhp/UE_TAPython_Plugin_Release/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, expandedAssetsUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync();
    }

    private static List<string> ExtractReleaseTags(string content)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            content,
            "/cgerchenhp/UE_TAPython_Plugin_Release/releases/tag/(?<tag>[^\"'<#? ]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return matches
            .Select(match => WebUtility.UrlDecode(match.Groups["tag"].Value))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ReleaseInfo> ExtractReleaseInfosFromAssetHtml(string html, string? fallbackTag = null)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html,
            "(?:href=\")?(?<href>(?:https://github\\.com)?/cgerchenhp/UE_TAPython_Plugin_Release/releases/download/(?<tag>[^/\"' >]+)/(?<asset>[^\"' >]+?\\.zip))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var result = new List<ReleaseInfo>();
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (href.StartsWith('/')) href = "https://github.com" + href;
            var tag = WebUtility.UrlDecode(match.Groups["tag"].Success ? match.Groups["tag"].Value : fallbackTag ?? "unknown");
            var assetName = WebUtility.UrlDecode(match.Groups["asset"].Value);
            result.Add(new ReleaseInfo(tag, tag, assetName, href));
        }

        return DeduplicateReleases(result);
    }

    private static List<ReleaseInfo> DeduplicateReleases(IEnumerable<ReleaseInfo> releases)
    {
        return releases
            .GroupBy(release => release.DownloadUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    private static string GetReleaseCachePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "TAPythonInstaller", "release-cache.json");
    }

    private static List<ReleaseInfo> ReadCachedReleaseInfos()
    {
        var cachePath = GetReleaseCachePath();
        if (!File.Exists(cachePath)) return [];

        try
        {
            var cache = JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(cachePath));
            return DeduplicateReleases(cache?.Releases ?? []);
        }
        catch
        {
            return [];
        }
    }

    private static void WriteCachedReleaseInfos(List<ReleaseInfo> releases)
    {
        var cachePath = GetReleaseCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var cache = new ReleaseCache(DateTimeOffset.UtcNow, DeduplicateReleases(releases));
        File.WriteAllText(cachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool IsCompatibleRelease(string tag, string assetName)
    {
        if (!IsWindowsZipAsset(assetName)) return false;
        if (string.IsNullOrWhiteSpace(detectedEngineVersion)) return true;

        var version = detectedEngineVersion.Trim();
        if (version.StartsWith("{", StringComparison.Ordinal)) return true;
        var underscoreVersion = version.Replace(".", "_");
        return assetName.Contains(version, StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains(underscoreVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsZipAsset(string assetName)
    {
        if (!assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return false;
        if (assetName.Contains("mac", StringComparison.OrdinalIgnoreCase) ||
            assetName.Contains("linux", StringComparison.OrdinalIgnoreCase)) return false;
        return assetName.Contains("win", StringComparison.OrdinalIgnoreCase) ||
               !System.Text.RegularExpressions.Regex.IsMatch(assetName, "(?:mac|linux|android|ios)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            var installRoot = ResolveInstallRoot();

            Directory.CreateDirectory(installRoot);
            var targetPluginDir = Path.Combine(installRoot, "TAPython");
            if (Directory.Exists(targetPluginDir))
            {
                if (backupBox.IsChecked == true)
                {
                    var backupDir = MoveTapythonPluginToExternalBackup(targetPluginDir, "backup", ResolveExternalBackupRoot());
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
            UpdateReadinessState();
        }
    }

    private async Task UninstallAsync()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(uprojectPath))
        {
            MessageBox.Show("请先选择 .uproject 文件。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (engineInstallRadio.IsChecked == true && string.IsNullOrWhiteSpace(enginePathBox.Text))
        {
            MessageBox.Show("卸载引擎 Marketplace 中的 TAPython 前，请先选择引擎目录。", "缺少引擎", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targetPluginDir = ResolveTargetPluginDirectory();
        var hasTargetPlugin = IsTapythonPluginDirectory(targetPluginDir);
        var staleBackupDirs = FindTapythonBackupDirectoriesInCurrentScanPath().ToList();
        if (!hasTargetPlugin && staleBackupDirs.Count == 0)
        {
            MessageBox.Show("当前安装位置未发现 TAPython 插件。", "无需卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            UpdateReadinessState();
            return;
        }

        var targetLabel = projectInstallRadio.IsChecked == true ? "项目 Plugins" : "引擎 Marketplace";
        var legacyBackupText = staleBackupDirs.Count > 0
            ? $"\n- 删除 {staleBackupDirs.Count} 个仍位于插件扫描路径中的历史备份目录"
            : string.Empty;
        var confirm = MessageBox.Show(
            $"将从 {targetLabel} 卸载 TAPython。\n\n插件目录：\n{targetPluginDir}\n\n将执行：\n- 直接删除 TAPython 插件目录\n- 从 .uproject 移除 TAPython 启用项\n- 从 DefaultEngine.ini 移除安装器写入的 TAPython Python 路径{legacyBackupText}\n\n项目 TA/TAPython/Python 用户脚本会保留。\n卸载不会备份插件目录，以确保 UE 不再扫描到 TAPython。\n\n确认继续？",
            "确认卸载 TAPython",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            installButton.IsEnabled = false;
            uninstallButton.IsEnabled = false;
            SetInstallProgress(0, "准备卸载");
            Log("开始卸载 TAPython...");

            await Task.Run(() =>
            {
                Dispatcher.Invoke(() => SetInstallProgress(20, "验证插件目录"));
                if (!hasTargetPlugin && staleBackupDirs.Count == 0)
                    throw new InvalidOperationException("目标目录不是有效的 TAPython 插件目录，已中止卸载。");

                Dispatcher.Invoke(() => SetInstallProgress(45, "移除插件文件"));
                if (hasTargetPlugin)
                    RemoveTapythonPluginDirectory(targetPluginDir);
                DeleteStaleTapythonBackups(staleBackupDirs);

                Dispatcher.Invoke(() => SetInstallProgress(70, "更新项目配置"));
                RemoveTapythonFromUProject();

                Dispatcher.Invoke(() => SetInstallProgress(88, "清理 Python 路径"));
                RemoveTapythonPythonPathConfig();
            });

            SetInstallProgress(100, "卸载完成");
            openProjectButton.IsEnabled = true;
            RefreshInstalledStatus();
            Log("卸载完成。项目 Python 脚本目录已保留。建议重启 UE 编辑器后确认插件状态。");
            MessageBox.Show("TAPython 已卸载。项目 TA/TAPython/Python 用户脚本已保留。", "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            installStateText.Text = "卸载失败";
            pipelineStatusText.Text = "卸载失败，请查看日志";
            Log($"卸载失败：{ex}");
            MessageBox.Show(ex.Message, "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            installButton.IsEnabled = true;
            UpdateReadinessState();
        }
    }

    private string ResolveTargetPluginDirectory()
    {
        return Path.Combine(ResolveInstallRoot(), "TAPython");
    }

    private string ResolveInstallRoot()
    {
        return projectInstallRadio.IsChecked == true
            ? Path.Combine(projectDirectory!, "Plugins")
            : Path.Combine(enginePathBox.Text, "Engine", "Plugins", "Marketplace");
    }

    private static bool IsTapythonPluginDirectory(string pluginDir)
        => File.Exists(Path.Combine(pluginDir, "TAPython.uplugin"));

    private void RemoveTapythonPluginDirectory(string targetPluginDir)
    {
        Directory.Delete(targetPluginDir, true);
        Log($"插件目录已删除：{targetPluginDir}");
    }

    private string MoveTapythonPluginToExternalBackup(string sourceDir, string reason, string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);
        var backupDir = Path.Combine(backupRoot, $"TAPython_{reason}_{DateTime.Now:yyyyMMdd_HHmmss}");
        var suffix = 1;
        while (Directory.Exists(backupDir))
        {
            backupDir = Path.Combine(backupRoot, $"TAPython_{reason}_{DateTime.Now:yyyyMMdd_HHmmss}_{suffix++}");
        }

        Directory.Move(sourceDir, backupDir);
        return backupDir;
    }

    private string ResolveExternalBackupRoot()
    {
        var root = projectInstallRadio.IsChecked == true
            ? projectDirectory!
            : enginePathBox.Text;
        return Path.Combine(root, "TAPythonInstallerBackups");
    }

    private IEnumerable<string> FindTapythonBackupDirectoriesInCurrentScanPath()
    {
        var installRoot = ResolveInstallRoot();
        if (!Directory.Exists(installRoot)) return [];

        return Directory.EnumerateDirectories(installRoot, "TAPython_*", SearchOption.TopDirectoryOnly)
            .Where(IsTapythonPluginDirectory)
            .ToList();
    }

    private void DeleteStaleTapythonBackups(IEnumerable<string> staleBackupDirs)
    {
        foreach (var backupDir in staleBackupDirs)
        {
            if (!Directory.Exists(backupDir)) continue;
            Directory.Delete(backupDir, true);
            Log($"已删除插件扫描路径中的历史备份：{backupDir}");
        }
    }

    private void RemoveTapythonFromUProject()
    {
        var root = JsonNode.Parse(File.ReadAllText(uprojectPath!))!.AsObject();
        if (root["Plugins"] is not JsonArray plugins)
        {
            Log(".uproject 未包含 Plugins 数组，跳过 TAPython 启用项清理。");
            return;
        }

        var removed = false;
        for (var index = plugins.Count - 1; index >= 0; index--)
        {
            if (plugins[index] is JsonObject obj &&
                string.Equals(obj["Name"]?.GetValue<string>(), "TAPython", StringComparison.OrdinalIgnoreCase))
            {
                plugins.RemoveAt(index);
                removed = true;
            }
        }

        if (removed)
        {
            File.WriteAllText(uprojectPath!, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Log(".uproject 已移除 TAPython 启用项。PythonScriptPlugin 已保留。 ");
        }
        else
        {
            Log(".uproject 中未找到 TAPython 启用项，跳过。 ");
        }
    }

    private void RemoveTapythonPythonPathConfig()
    {
        var iniPath = Path.Combine(projectDirectory!, "Config", "DefaultEngine.ini");
        if (!File.Exists(iniPath))
        {
            Log("未找到 DefaultEngine.ini，跳过 Python 路径清理。");
            return;
        }

        var content = File.ReadAllText(iniPath);
        const string pathPattern = "(?im)^[ \\t]*\\+AdditionalPaths=\\(Path=\"[^\"]*(?:TA/TAPython/Python|TA\\\\TAPython\\\\Python|TAPythonInstaller/ProjectLinks|TAPythonInstaller\\\\ProjectLinks)[^\"]*\"\\)[ \\t]*\\r?\\n?";
        var updated = System.Text.RegularExpressions.Regex.Replace(content, pathPattern, string.Empty);
        if (updated == content)
        {
            Log("DefaultEngine.ini 未发现安装器写入的 TAPython Python 路径，跳过。 ");
            return;
        }

        File.WriteAllText(iniPath, updated);
        Log("DefaultEngine.ini 已移除 TAPython Python 附加路径。 ");
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
        var projectPythonDir = Path.Combine(projectTapythonDir, "Python");
        detectedProjectTools.Clear();
        detectedProjectTools.AddRange(DetectExistingProjectTapythonTools(projectPythonDir));

        if (detectedProjectTools.Count == 0)
        {
            CopyDirectory(sourceRoot, projectTapythonDir);
            Log($"未检测到已有 TA Python 脚本，已正常复制 TAPython 默认资源到：{projectTapythonDir}");
        }
        else
        {
            CopyDirectory(sourceRoot, projectTapythonDir, overwrite: false);
            Log($"检测到已有 TA Python 脚本/工具 {detectedProjectTools.Count} 个，已保留现有文件并仅补齐缺失的默认资源。");
            foreach (var tool in detectedProjectTools.Take(20))
                Log($"已有工具：{tool.Name} · {tool.Kind} · {tool.RelativePath}");
            if (detectedProjectTools.Count > 20)
                Log($"已有工具数量较多，日志仅显示前 20 个；完整展示将在后续工具列表区域中呈现。");
        }

        var defaultConfig = Path.Combine(sourceRoot, "Config", "config.ini");
        if (File.Exists(defaultConfig))
        {
            var pluginConfigDir = Path.Combine(targetPluginDir, "Config");
            Directory.CreateDirectory(pluginConfigDir);
            File.Copy(defaultConfig, Path.Combine(pluginConfigDir, "Plugin_Config.ini"), true);
            Log("已写入 TAPython 插件配置：Config/Plugin_Config.ini");
        }
    }

    private List<TapythonToolInfo> DetectExistingProjectTapythonTools(string projectPythonDir)
    {
        if (!Directory.Exists(projectPythonDir)) return [];

        var tools = new List<TapythonToolInfo>();
        foreach (var dir in Directory.EnumerateDirectories(projectPythonDir, "*", SearchOption.TopDirectoryOnly)
                     .Where(d => !string.Equals(Path.GetFileName(d), "__pycache__", StringComparison.OrdinalIgnoreCase)))
        {
            if (Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Any(IsTapythonToolFile))
            {
                tools.Add(new TapythonToolInfo(Path.GetFileName(dir), "目录", Path.GetRelativePath(projectPythonDir, dir)));
            }
        }

        foreach (var file in Directory.EnumerateFiles(projectPythonDir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsTapythonToolFile))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "__init__.py", StringComparison.OrdinalIgnoreCase)) continue;
            tools.Add(new TapythonToolInfo(Path.GetFileNameWithoutExtension(file), "文件", Path.GetRelativePath(projectPythonDir, file)));
        }

        return tools
            .OrderBy(t => t.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsTapythonToolFile(string file)
    {
        var extension = Path.GetExtension(file);
        return extension.Equals(".py", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ini", StringComparison.OrdinalIgnoreCase);
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

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite = true)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFile = Path.Combine(targetDir, Path.GetFileName(file));
            if (overwrite || !File.Exists(targetFile))
                File.Copy(file, targetFile, overwrite);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)), overwrite);
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

    private bool HasTapythonInCurrentTarget()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(uprojectPath)) return false;
        if (engineInstallRadio.IsChecked == true && string.IsNullOrWhiteSpace(enginePathBox.Text)) return false;
        return IsTapythonPluginDirectory(ResolveTargetPluginDirectory()) ||
               FindTapythonBackupDirectoriesInCurrentScanPath().Any();
    }

    private void RefreshInstalledStatus()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || string.IsNullOrWhiteSpace(uprojectPath))
        {
            installedStatusLabel.Text = "当前安装状态：未知";
            return;
        }

        if (projectInstallRadio.IsChecked == true)
        {
            var projectPlugin = Path.Combine(projectDirectory!, "Plugins", "TAPython", "TAPython.uplugin");
            if (File.Exists(projectPlugin))
            {
                installedStatusLabel.Text = $"当前安装状态：项目已安装（{TryReadPluginVersion(projectPlugin)}）";
            }
            else
            {
                var staleCount = FindTapythonBackupDirectoriesInCurrentScanPath().Count();
                installedStatusLabel.Text = staleCount > 0
                    ? $"当前安装状态：发现 {staleCount} 个历史备份仍在项目 Plugins 扫描路径中"
                    : "当前安装状态：未在项目 Plugins 中发现 TAPython";
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(enginePathBox.Text))
        {
            installedStatusLabel.Text = "当前安装状态：请选择引擎目录以检测 Marketplace";
            return;
        }

        var enginePlugin = Path.Combine(enginePathBox.Text, "Engine", "Plugins", "Marketplace", "TAPython", "TAPython.uplugin");
        if (File.Exists(enginePlugin))
        {
            installedStatusLabel.Text = $"当前安装状态：引擎已安装（{TryReadPluginVersion(enginePlugin)}）";
        }
        else
        {
            var staleCount = FindTapythonBackupDirectoriesInCurrentScanPath().Count();
            installedStatusLabel.Text = staleCount > 0
                ? $"当前安装状态：发现 {staleCount} 个历史备份仍在引擎 Marketplace 扫描路径中"
                : "当前安装状态：未在引擎 Marketplace 中发现 TAPython";
        }
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
        var hasInstalledTarget = hasProject && HasTapythonInCurrentTarget();

        RefreshInstalledStatus();
        projectCheckText.Text = hasProject ? "已选择" : "未选择";
        engineCheckText.Text = hasEngine ? "已选择" : "未选择";
        sourceCheckText.Text = hasSource ? (!string.IsNullOrWhiteSpace(localZipPath) ? "本地 ZIP" : "远程 Release") : "未选择";
        targetCheckText.Text = target;
        heroTargetText.Text = target;
        uninstallButton.IsEnabled = hasInstalledTarget;

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

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private sealed record EngineInfo(string Version, string Root, string Source, string? Association, string? BuildId)
    {
        public override string ToString() => $"UE {Version} · {Source} · {Root}";
    }

    private sealed record ReleaseInfo(string Tag, string Name, string AssetName, string DownloadUrl)
    {
        public override string ToString() => $"{Tag} · {AssetName}";
    }

    private sealed record ReleaseCache(DateTimeOffset UpdatedAt, List<ReleaseInfo> Releases);

    private sealed record TapythonToolInfo(string Name, string Kind, string RelativePath);

    private sealed record HubToolInfo(string Name, string RelativePath, string SourcePath);

    private sealed record NavigationItem(string PageKey, string Icon, string Title, string Subtitle);
}
