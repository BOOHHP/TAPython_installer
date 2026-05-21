using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TAPythonInstaller;

public partial class MainWindow : Window
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string ReleaseHtmlUrl = "https://github.com/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string ReleaseAtomUrl = "https://github.com/cgerchenhp/UE_TAPython_Plugin_Release/releases.atom";
    private const string InstallerReleaseApiUrl = "https://api.github.com/repos/BOOHHP/TAPython_installer/releases/latest";
    private const string InstallerReleaseHtmlUrl = "https://github.com/BOOHHP/TAPython_installer/releases/latest";
    private const string ToolHubBaseUrl = "http://10.67.8.194:8787";
    private const string ToolHubSubmitPath = "#submit";
    private const string ToolPackageExtension = ".tapython-tool.zip";
    private const string DefaultSourceEngineRoot = @"D:\AntLibs\WS";
    private const string CopilotSkillsRelativePath = @".copilot\skills";
    private const int MaxVisibleLogEntries = 200;
    private const int MaxHtmlReleaseTags = 40;
    private const double ExpandedNavigationWidth = 216;
    private const double CollapsedNavigationWidth = 72;

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
    private static readonly HashSet<string> BuiltInTapythonToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ChameleonGallery",
        "ChameleonSketch",
        "Example",
        "ImageCompareTools",
        "QueryTools",
        "ShelfTools",
        "Utilities"
    };

    private readonly HttpClient httpClient = new();
    private readonly Queue<string> visibleLogEntries = new();
    private readonly List<TapythonToolInfo> detectedProjectTools = new();
    private readonly ObservableCollection<NavigationItem> navigationItems = new();
    private readonly ObservableCollection<TapythonToolInfo> projectToolItems = new();
    private readonly ObservableCollection<HubToolInfo> hubToolItems = new();
    private readonly List<HubToolInfo> allHubTools = new();
    private readonly ObservableCollection<string> hubCategoryFilters = new();
    private readonly ObservableCollection<string> hubRiskFilters = new();
    private readonly ObservableCollection<string> hubStatusFilters = new();
    private readonly DispatcherTimer projectRuntimeTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private string? projectDirectory;
    private string? uprojectPath;
    private string? detectedEngineVersion;
    private string? inferredProjectEngineRoot;
    private string? localZipPath;
    private FrameworkElement? currentNavigationPage;
    private string installerCurrentVersion = "unknown";
    private string installerLatestVersion = "unknown";
    private string installerLatestReleaseUrl = InstallerReleaseHtmlUrl;
    private string installerLatestDownloadUrl = string.Empty;
    private bool installerUpdateAvailable;
    private int currentNavigationIndex;
    private Point toolTabDragStartPoint;
    private bool isToolTabDragging;
    private readonly bool showChangelogOnStartup;
    private bool hubToolsLoaded;
    private bool currentProjectIsRunning;
    private bool suppressEngineReleaseRefresh;
    private string? lastHubInstallTargetRoot;
    private string? lastHubInstallBackupRoot;

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

    private enum ToolPackageSourceKind
    {
        LegacyProjectTool,
        HubLegacy,
        ToolPackageV2
    }

    public MainWindow(bool showChangelogOnStartup = false)
    {
        this.showChangelogOnStartup = showChangelogOnStartup;
        InitializeComponent();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"TAPythonInstaller/{GetCurrentInstallerVersion()}");
        InitializeNavigation();
        InitializeToolPages();
        InitializeInstallerVersionPanel();
        releaseCombo.SelectionChanged += (_, _) => UpdateReadinessState();
        projectInstallRadio.Checked += (_, _) => UpdateReadinessState();
        projectInstallRadio.Unchecked += (_, _) => UpdateReadinessState();
        engineInstallRadio.Checked += (_, _) => UpdateReadinessState();
        engineInstallRadio.Unchecked += (_, _) => UpdateReadinessState();
        fixBuildIdBox.Checked += (_, _) => UpdateReadinessState();
        fixBuildIdBox.Unchecked += (_, _) => UpdateReadinessState();
        projectRuntimeTimer.Tick += (_, _) => RefreshProjectRuntimeState();
        projectRuntimeTimer.Start();
        ScanEngines();
        UpdateReadinessState();
        _ = RefreshInstallerUpdateAsync(showLog: false);
        if (this.showChangelogOnStartup)
            Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(ShowUpdateLogDialog), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    protected override void OnClosed(EventArgs e)
    {
        projectRuntimeTimer.Stop();
        base.OnClosed(e);
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
        hubCategoryFilter.ItemsSource = hubCategoryFilters;
        hubRiskFilter.ItemsSource = hubRiskFilters;
        hubStatusFilter.ItemsSource = hubStatusFilters;
        hubRiskFilters.Add("所有风险");
        hubRiskFilters.Add("低风险");
        hubRiskFilters.Add("中风险");
        hubRiskFilters.Add("高风险");
        hubStatusFilters.Add("已审核");
        hubStatusFilters.Add("所有状态");
        hubStatusFilters.Add("待审核");
        hubStatusFilters.Add("草稿");
        hubStatusFilters.Add("已归档");
        hubCategoryFilters.Add("所有分类");
        hubCategoryFilter.SelectedIndex = 0;
        hubRiskFilter.SelectedIndex = 0;
        hubStatusFilter.SelectedIndex = 0;
        hubSearchBox.TextChanged += (_, _) =>
        {
            UpdateHubSearchPlaceholder();
            ApplyHubFilters();
        };
        hubCategoryFilter.SelectionChanged += (_, _) => ApplyHubFilters();
        hubRiskFilter.SelectionChanged += (_, _) => ApplyHubFilters();
        hubStatusFilter.SelectionChanged += (_, _) => ApplyHubFilters();
        hubCompatibleOnlyBox.Checked += (_, _) => ApplyHubFilters();
        hubCompatibleOnlyBox.Unchecked += (_, _) => ApplyHubFilters();
        hubToolsList.SelectionChanged += HubToolsList_SelectionChanged;
        ShowToolTab("project");
        UpdateHubSearchPlaceholder();
        RefreshToolSummaries();
    }

    private void InitializeInstallerVersionPanel()
    {
        installerCurrentVersion = GetCurrentInstallerVersion();
        installerCurrentVersionText.Text = FormatInstallerVersion(installerCurrentVersion);
        installerLatestVersionText.Text = "未检查";
        installerUpdateStatusText.Text = "未检查";
        installerCheckButton.Content = "检查";
        installerCheckButton.IsEnabled = true;
        installerUpdateButton.Content = "更新";
        installerUpdateButton.IsEnabled = false;
    }

    // ─── Event handlers ───────────────────────────────────────

    private void BrowseProject_Click(object sender, RoutedEventArgs e) => BrowseProject();

    private void BrowseEngine_Click(object sender, RoutedEventArgs e) => BrowseEngine();

    private void ScanEngines_Click(object sender, RoutedEventArgs e) => ScanEngines();

    private void EngineCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => SelectEngineFromCombo();

    private async void RefreshReleases_Click(object sender, RoutedEventArgs e)
        => await RefreshReleasesAsync();

    private async void InstallerUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (installerUpdateAvailable) await UpdateInstallerAsync();
    }

    private void ShowUpdateLog_Click(object sender, RoutedEventArgs e) => ShowUpdateLogDialog();

    private void CloseUpdateLog_Click(object sender, RoutedEventArgs e) => HideUpdateLogDialog();

    private void UpdateLogOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == updateLogOverlay) HideUpdateLogDialog();
    }

    private async void InstallerCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshInstallerUpdateAsync(showLog: true);
    }

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
        SetNavigationCollapsed(!NavIsCollapsed);
    }

    private void SetNavigationCollapsed(bool collapsed)
    {
        if (NavIsCollapsed == collapsed) return;

        NavIsCollapsed = collapsed;
        navCollapseButton.Content = collapsed ? "›" : "‹";
        navCollapseButton.ToolTip = collapsed ? "展开导航" : "收起导航";
        navLogoMark.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(navCollapseButton, collapsed ? 0 : 2);
        Grid.SetColumnSpan(navCollapseButton, collapsed ? 3 : 1);
        navCollapseButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        if (!collapsed)
        {
            navBrandText.Visibility = Visibility.Visible;
            installerUpdatePanel.Visibility = Visibility.Visible;
        }

        AnimateElementOpacity(navBrandText, collapsed ? 0 : 1, 140);
        AnimateElementOpacity(installerUpdatePanel, collapsed ? 0 : 1, 140);

        var widthAnimation = new GridLengthAnimation
        {
            From = navigationColumn.Width,
            To = new GridLength(collapsed ? CollapsedNavigationWidth : ExpandedNavigationWidth),
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        widthAnimation.Completed += (_, _) =>
        {
            navigationColumn.Width = new GridLength(collapsed ? CollapsedNavigationWidth : ExpandedNavigationWidth);
            if (collapsed)
            {
                navBrandText.Visibility = Visibility.Collapsed;
                installerUpdatePanel.Visibility = Visibility.Collapsed;
            }
        };

        navigationColumn.BeginAnimation(ColumnDefinition.WidthProperty, widthAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void AnimateElementOpacity(UIElement element, double to, int milliseconds)
    {
        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (navList.SelectedItem is not NavigationItem item) return;
        var nextIndex = Math.Max(0, navList.SelectedIndex);
        ShowNavigationPage(item, nextIndex >= currentNavigationIndex);
        currentNavigationIndex = nextIndex;
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

    private void ToolPanel_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (IsInsideListBoxItem(source) || IsInsideButton(source) || IsInsideElement(source, hubDetailPanel)) return;
        ClearToolListSelection();
    }

    private void ToolList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideListBoxItem(e.OriginalSource as DependencyObject)) return;
        ClearToolListSelection();
    }

    private void ClearToolListSelection()
    {
        projectToolsList.SelectedItem = null;
        hubToolsList.SelectedItem = null;
        Keyboard.ClearFocus();
    }

    private static bool IsInsideListBoxItem(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ListBoxItem) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsInsideButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private static bool IsInsideElement(DependencyObject? source, DependencyObject target)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, target)) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RefreshProjectTools_Click(object sender, RoutedEventArgs e) => RefreshProjectTools();

    private void ImportProjectTool_Click(object sender, RoutedEventArgs e) => ImportProjectToolPackage();

    private void ProjectToolsPanel_DragOver(object sender, DragEventArgs e)
    {
        var canAcceptDrop = CanAcceptProjectToolDrop(e);
        e.Effects = canAcceptDrop ? DragDropEffects.Copy : DragDropEffects.None;
        projectToolDropOverlay.Visibility = canAcceptDrop ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void ProjectToolsPanel_DragLeave(object sender, DragEventArgs e)
    {
        projectToolDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void ProjectToolsPanel_Drop(object sender, DragEventArgs e)
    {
        projectToolDropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        var packagePath = GetDroppedProjectToolPackagePath(e);
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            MessageBox.Show("请拖入一个 .zip 或 .tapython-tool.zip 工具包。", "不支持的文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ImportProjectToolPackage(packagePath, "拖拽导入");
    }

    private void ExportProjectTool_Click(object sender, RoutedEventArgs e)
    {
        if (projectToolsList.SelectedItem is not TapythonToolInfo tool)
        {
            MessageBox.Show("请先在当前项目工具列表中选择一个要导出的工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ExportProjectTool(tool);
    }

    private void UploadProjectTool_Click(object sender, RoutedEventArgs e)
    {
        OpenToolHubSubmitPage();
    }

    private void DeleteProjectTool_Click(object sender, RoutedEventArgs e)
    {
        if (projectToolsList.SelectedItem is not TapythonToolInfo tool)
        {
            MessageBox.Show("请先在当前项目工具列表中选择一个要删除的工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DeleteProjectTool(tool);
    }

    private void RefreshHubTools_Click(object sender, RoutedEventArgs e) => _ = RefreshHubToolsAsync(force: true);

    private void HubToolsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var tool = hubToolsList.SelectedItem as HubToolInfo;
        RestoreHubInstallFolders(tool);
        UpdateHubToolDetails(tool);
    }

    private void HubPreviewInstall_Click(object sender, RoutedEventArgs e)
    {
        if (hubToolsList.SelectedItem is not HubToolInfo hubTool)
        {
            MessageBox.Show("请先在工具分享网站列表中选择一个工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ = PreviewHubToolInstallAsync(hubTool);
    }

    private void HubRedownloadPreview_Click(object sender, RoutedEventArgs e)
    {
        if (hubToolsList.SelectedItem is not HubToolInfo hubTool)
        {
            MessageBox.Show("请先在工具分享网站列表中选择一个工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ = PreviewHubToolInstallAsync(hubTool, forceRedownload: true);
    }

    private void HubInstallToProject_Click(object sender, RoutedEventArgs e)
    {
        if (hubToolsList.SelectedItem is not HubToolInfo hubTool)
        {
            MessageBox.Show("请先在工具分享网站列表中选择一个工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _ = InstallHubToolToProjectAsync(hubTool);
    }

    private void HubUninstallTool_Click(object sender, RoutedEventArgs e)
    {
        if (hubToolsList.SelectedItem is not HubToolInfo hubTool)
        {
            MessageBox.Show("请先在工具分享网站列表中选择一个已安装工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!hubTool.IsManagedHubInstalled)
        {
            MessageBox.Show("当前工具没有 Tool Hub 安装记录。若它是普通项目工具，请在“当前项目工具”页使用“删除”。", "无法卸载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UninstallHubTool(hubTool);
    }

    private void HubRepairInstall_Click(object sender, RoutedEventArgs e)
    {
        if (hubToolsList.SelectedItem is not HubToolInfo hubTool)
        {
            MessageBox.Show("请先在工具分享网站列表中选择一个已安装工具。", "未选择工具", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!hubTool.IsInstalled)
        {
            MessageBox.Show("当前工具尚未安装，无法执行安装修复。", "未安装", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RepairHubToolInstall(hubTool);
    }

    private void OpenHubInstallFolder_Click(object sender, RoutedEventArgs e)
        => OpenDirectoryOrWarn(lastHubInstallTargetRoot, "还没有可打开的 Tool Hub 安装目录。");

    private void OpenHubBackupFolder_Click(object sender, RoutedEventArgs e)
        => OpenDirectoryOrWarn(lastHubInstallBackupRoot, "还没有可打开的 Tool Hub 备份目录。");

    private void OpenDirectoryOrWarn(string? path, string emptyMessage)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show(this, emptyMessage, "没有可打开的目录", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Directory.Exists(path))
        {
            MessageBox.Show(this, $"目录不存在：\n{path}", "无法打开目录", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开目录：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateHubInstallFolderButtons()
    {
        openHubInstallFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(lastHubInstallTargetRoot) && Directory.Exists(lastHubInstallTargetRoot);
        openHubBackupFolderButton.IsEnabled = !string.IsNullOrWhiteSpace(lastHubInstallBackupRoot) && Directory.Exists(lastHubInstallBackupRoot);
    }

    private void OpenToolHubWebsite_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ToolHubBaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开工具分享网站：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenToolHubSubmitPage()
    {
        var targetUrl = $"{ToolHubBaseUrl}/{ToolHubSubmitPath}";
        try
        {
            Process.Start(new ProcessStartInfo(targetUrl) { UseShellExecute = true });
            Log($"已打开 ToolHub 提交或发布工具页面：{targetUrl}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法打开 ToolHub 提交或发布工具页面：{ex.Message}", "打开失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        inferredProjectEngineRoot = null;
        detectedEngineVersion = json?["EngineAssociation"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(detectedEngineVersion))
        {
            inferredProjectEngineRoot = TryInferProjectEngineRoot(projectDirectory);
            detectedEngineVersion = string.IsNullOrWhiteSpace(inferredProjectEngineRoot) ? null : GetEngineVersion(inferredProjectEngineRoot);
            projectStatusLabel.Text = string.IsNullOrWhiteSpace(inferredProjectEngineRoot)
                ? "未读取到 EngineAssociation，请手动选择引擎目录"
                : $"已从项目历史记录推断引擎：{inferredProjectEngineRoot}";
        }
        else
        {
            projectStatusLabel.Text = $"检测到 EngineAssociation: {detectedEngineVersion}";
        }

        RefreshInstalledStatus();
        currentProjectIsRunning = IsCurrentProjectRunning();

        SelectBestEngineForProject();
        RefreshProjectTools();
        UpdateHubInstallButtonState(hubToolsList.SelectedItem as HubToolInfo);
        UpdateReadinessState();
        _ = RefreshReleasesAsync();
    }

    private void ShowNavigationPage(NavigationItem item, bool forward)
    {
        var targetPage = item.PageKey switch
        {
            "install" => installPageGrid,
            "tools" => toolsPageGrid,
            _ => placeholderPageGrid
        };

        if (item.PageKey == "tools")
        {
            RefreshProjectTools();
            if (!hubToolsLoaded) _ = RefreshHubToolsAsync(force: false);
        }
        else if (item.PageKey != "install")
        {
            placeholderTitleText.Text = item.Title;
            placeholderSubtitleText.Text = $"{item.Subtitle} · 已预留，后续可接入新的 TAPython 工作流。";
        }

        AnimateNavigationPage(targetPage, forward);
    }

    private void AnimateNavigationPage(FrameworkElement targetPage, bool forward)
    {
        if (ReferenceEquals(currentNavigationPage, targetPage)) return;

        var previousPage = currentNavigationPage;
        currentNavigationPage = targetPage;

        if (previousPage == null)
        {
            installPageGrid.Visibility = ReferenceEquals(targetPage, installPageGrid) ? Visibility.Visible : Visibility.Collapsed;
            toolsPageGrid.Visibility = ReferenceEquals(targetPage, toolsPageGrid) ? Visibility.Visible : Visibility.Collapsed;
            placeholderPageGrid.Visibility = ReferenceEquals(targetPage, placeholderPageGrid) ? Visibility.Visible : Visibility.Collapsed;
            targetPage.Opacity = 1;
            SetPageOffset(targetPage, 0);
            return;
        }

        var distance = forward ? 24 : -24;
        targetPage.Visibility = Visibility.Visible;
        targetPage.Opacity = 0;
        SetPageOffset(targetPage, distance);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing };
        var slideIn = new DoubleAnimation(distance, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = easing };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = easing };
        var slideOut = new DoubleAnimation(0, -distance * 0.45, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing };

        fadeOut.Completed += (_, _) =>
        {
            previousPage.Visibility = Visibility.Collapsed;
            previousPage.Opacity = 1;
            SetPageOffset(previousPage, 0);
        };

        previousPage.BeginAnimation(OpacityProperty, fadeOut);
        GetPageTranslate(previousPage).BeginAnimation(TranslateTransform.XProperty, slideOut);
        targetPage.BeginAnimation(OpacityProperty, fadeIn);
        GetPageTranslate(targetPage).BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private static void SetPageOffset(FrameworkElement page, double offset)
    {
        GetPageTranslate(page).X = offset;
    }

    private static TranslateTransform GetPageTranslate(FrameworkElement page)
    {
        if (page.RenderTransform is TranslateTransform translate) return translate;
        translate = new TranslateTransform();
        page.RenderTransform = translate;
        return translate;
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

    private void ShowUpdateLogDialog()
    {
        updateLogOverlay.Visibility = Visibility.Visible;
        updateLogOverlay.Opacity = 0;
        AnimateElementOpacity(updateLogOverlay, 1, 160);
    }

    private void HideUpdateLogDialog()
    {
        updateLogOverlay.Visibility = Visibility.Collapsed;
    }

    private void RefreshProjectTools()
    {
        projectToolItems.Clear();
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            UpdateHubInstalledStates();
            RefreshToolSummaries();
            return;
        }

        var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
        RepairLegacyHubInstallLayouts();
        foreach (var tool in DetectExistingProjectTapythonTools(projectPythonDir))
            projectToolItems.Add(EnrichProjectToolDescriptionFromHub(tool));

        UpdateHubInstalledStates();
        RefreshToolSummaries();
    }

    private TapythonToolInfo EnrichProjectToolDescriptionFromHub(TapythonToolInfo tool)
    {
        var needsDescription = string.Equals(tool.Description, "未提供工具说明", StringComparison.OrdinalIgnoreCase);
        var needsVersion = string.IsNullOrWhiteSpace(tool.Version);
        if (!needsDescription && !needsVersion) return tool;

        var normalizedToolName = NormalizeToolIdentity(tool.Name);
        var hubTool = allHubTools.FirstOrDefault(candidate =>
            NormalizeToolIdentity(candidate.Slug) == normalizedToolName ||
            NormalizeToolIdentity(candidate.Name) == normalizedToolName ||
            NormalizeToolIdentity(candidate.DisplayName) == normalizedToolName);

        if (hubTool == null) return tool;

        var description = needsDescription && !string.IsNullOrWhiteSpace(hubTool.Description)
            ? hubTool.Description
            : tool.Description;
        var version = needsVersion && !string.IsNullOrWhiteSpace(hubTool.InstalledVersion)
            ? hubTool.InstalledVersion
            : needsVersion && !string.IsNullOrWhiteSpace(hubTool.LatestVersion)
                ? hubTool.LatestVersion
                : tool.Version;

        return tool with { Description = description, Version = version };
    }

    private void UpdateHubInstalledStates()
    {
        var installedMetadataRecords = ReadInstalledHubToolMetadataRecords();
        var installedIdentities = ReadInstalledHubToolIdentities(installedMetadataRecords);
        foreach (var projectTool in projectToolItems)
            installedIdentities.Add(NormalizeToolIdentity(projectTool.Name));

        foreach (var tool in allHubTools)
        {
            var metadata = FindInstalledHubToolMetadata(tool, installedMetadataRecords);
            tool.InstalledVersion = metadata?.Version ?? string.Empty;
            tool.InstalledTargetRoot = metadata?.TargetRoot ?? string.Empty;
            tool.InstalledBackupRoot = metadata?.BackupRoot ?? string.Empty;
            tool.InstalledPackageSha256 = metadata?.PackageSha256 ?? string.Empty;
            tool.IsInstalled = metadata != null || HubToolMatchesInstalledIdentities(tool, installedIdentities);
        }

        if (hubToolsLoaded)
            ApplyHubFilters();
        else
            UpdateHubToolDetails(hubToolsList.SelectedItem as HubToolInfo);
    }

    private async Task RefreshHubToolsAsync(bool force)
    {
        if (hubToolsLoaded && !force)
        {
            ApplyHubFilters();
            return;
        }

        hubRefreshButton.IsEnabled = false;
        hubStatusText.Text = "正在连接 Tool Hub API...";
        hubToolsEmptyText.Text = "正在加载公司内网 Tool Hub 工具...";
        hubToolsEmptyText.Visibility = Visibility.Visible;

        try
        {
            using var response = await httpClient.GetAsync($"{ToolHubBaseUrl}/api/tools");
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStringAsync();
            var root = JsonNode.Parse(content)?.AsObject() ?? throw new InvalidOperationException("Tool Hub API 返回为空。 ");
            var tools = root["tools"]?.AsArray() ?? throw new InvalidOperationException("Tool Hub API 缺少 tools 列表。 ");

            allHubTools.Clear();
            foreach (var item in tools.OfType<JsonObject>())
            {
                allHubTools.Add(ParseHubToolInfo(item));
            }

            hubToolsLoaded = true;
            UpdateHubInstalledStates();
            RefreshHubCategoryFilters();
            ApplyHubFilters();
            hubStatusText.Text = $"已连接 {ToolHubBaseUrl}";
            Log($"Tool Hub API 加载完成：{allHubTools.Count} 个工具。 ");
        }
        catch (Exception ex)
        {
            hubToolsLoaded = false;
            allHubTools.Clear();
            hubToolItems.Clear();
            hubToolsCountText.Text = "加载失败";
            hubToolsEmptyText.Text = "无法加载 Tool Hub。请确认公司网络和服务地址可访问。";
            hubToolsEmptyText.Visibility = Visibility.Visible;
            hubStatusText.Text = "Tool Hub API 连接失败";
            Log($"Tool Hub API 加载失败：{ex.Message}");
        }
        finally
        {
            hubRefreshButton.IsEnabled = true;
            RefreshToolSummaries();
        }
    }

    private void MergeHubTool(HubToolInfo hubTool)
    {
        MessageBox.Show($"{hubTool.DisplayName} 已进入 Tool Hub API 浏览页。实际安装会在下一阶段接入 hash 校验、备份和 MenuConfig 合并。", "安装尚未开放", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private HubToolInfo ParseHubToolInfo(JsonObject item)
    {
        var downloads = item["downloads"] as JsonObject;
        var compatibility = item["compatibility"] as JsonObject;
        var ueVersions = ReadStringArray(compatibility?["unrealEngine"] as JsonArray);
        var tags = ReadStringArray(item["tags"] as JsonArray);
        var packageSize = downloads?["latestPackageSize"]?.GetValue<long?>();
        var apiUrl = BuildToolHubUrl(item["apiUrl"]?.GetValue<string>() ?? string.Empty);
        var packageUrl = BuildToolHubUrl(downloads?["latestPackage"]?.GetValue<string>() ?? string.Empty);
        var manifestUrl = BuildToolHubUrl(downloads?["latestManifest"]?.GetValue<string>() ?? string.Empty);
        var version = item["latestVersion"]?.GetValue<string>() ?? "unknown";

        return new HubToolInfo
        {
            Slug = item["slug"]?.GetValue<string>() ?? string.Empty,
            Name = item["name"]?.GetValue<string>() ?? string.Empty,
            DisplayName = item["displayName"]?.GetValue<string>() ?? item["name"]?.GetValue<string>() ?? "未知工具",
            Description = item["description"]?.GetValue<string>() ?? "未提供说明",
            Category = item["category"]?.GetValue<string>() ?? "uncategorized",
            Author = item["author"]?.GetValue<string>() ?? "unknown",
            OwnerTeam = item["ownerTeam"]?.GetValue<string>() ?? "unknown",
            Status = item["status"]?.GetValue<string>() ?? "unknown",
            RiskLevel = item["riskLevel"]?.GetValue<string>() ?? "unknown",
            LatestVersion = version.StartsWith('v') ? version : $"v{version}",
            ApiUrl = apiUrl,
            PackageUrl = packageUrl,
            ManifestUrl = manifestUrl,
            PackageSha256 = downloads?["latestPackageSha256"]?.GetValue<string>() ?? string.Empty,
            PackageSize = packageSize,
            PackageAvailable = downloads?["latestPackageAvailable"]?.GetValue<bool?>() ?? !string.IsNullOrWhiteSpace(packageUrl),
            UnrealVersions = ueVersions,
            Tags = tags,
            SourcePath = packageUrl
        };
    }

    private void RefreshHubCategoryFilters()
    {
        var selected = hubCategoryFilter.SelectedItem as string ?? "所有分类";
        hubCategoryFilters.Clear();
        hubCategoryFilters.Add("所有分类");
        foreach (var category in allHubTools.Select(tool => tool.Category).Where(category => !string.IsNullOrWhiteSpace(category)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(category => category, StringComparer.OrdinalIgnoreCase))
            hubCategoryFilters.Add(category);

        hubCategoryFilter.SelectedItem = hubCategoryFilters.Contains(selected) ? selected : "所有分类";
    }

    private void UpdateHubSearchPlaceholder()
    {
        var hasSearchText = !string.IsNullOrEmpty(hubSearchBox.Text);
        hubSearchPlaceholderText.Visibility = string.IsNullOrWhiteSpace(hubSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        hubSearchClearButton.Visibility = hasSearchText
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClearHubSearch_Click(object sender, RoutedEventArgs e)
    {
        hubSearchBox.Clear();
        hubSearchBox.Focus();
    }

    private void ApplyHubFilters()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ApplyHubFilters);
            return;
        }

        var query = hubSearchBox?.Text?.Trim() ?? string.Empty;
        var category = hubCategoryFilter?.SelectedItem as string ?? "所有分类";
        var risk = hubRiskFilter?.SelectedItem as string ?? "所有风险";
        var status = hubStatusFilter?.SelectedItem as string ?? "已审核";
        var compatibleOnly = hubCompatibleOnlyBox?.IsChecked == true;

        var filtered = allHubTools.Where(tool => MatchesHubQuery(tool, query)
                                                && MatchesHubCategory(tool, category)
                                                && MatchesHubRisk(tool, risk)
                                                && MatchesHubStatus(tool, status)
                                                && (!compatibleOnly || IsHubToolCompatibleWithCurrentProject(tool)))
                                  .OrderByDescending(tool => string.Equals(tool.Status, "approved", StringComparison.OrdinalIgnoreCase))
                                  .ThenBy(tool => tool.DisplayName, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        var selectedSlug = (hubToolsList?.SelectedItem as HubToolInfo)?.Slug;
        hubToolItems.Clear();
        foreach (var tool in filtered)
            hubToolItems.Add(tool);

        if (!string.IsNullOrWhiteSpace(selectedSlug) && hubToolsList != null)
            hubToolsList.SelectedItem = hubToolItems.FirstOrDefault(tool => string.Equals(tool.Slug, selectedSlug, StringComparison.OrdinalIgnoreCase));

        hubToolsCountText.Text = allHubTools.Count == 0 ? "等待加载" : $"{hubToolItems.Count} / {allHubTools.Count} 个工具";
        hubToolsEmptyText.Text = allHubTools.Count == 0 ? "点击刷新加载公司内网 TAPython Tool Hub 工具。" : "当前筛选条件下没有工具。";
        hubToolsEmptyText.Visibility = hubToolItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshToolSummaries();
    }

    private void UpdateHubToolDetails(HubToolInfo? tool)
    {
        if (tool == null)
        {
            hubDetailTitleText.Text = "选择一个工具";
            hubDetailMetaText.Text = "工具详情、风险与安装预览会显示在这里";
            SetHubDetailStatus("未选择", installed: false);
            hubDetailDescriptionText.Text = "从左侧工具库选择工具，或点击刷新从 Tool Hub API 获取最新列表。";
            hubDetailCompatibilityText.Text = "-";
            hubDetailPackageText.Text = "-";
            hubDetailTagsText.Text = "-";
            hubInstallPreviewText.Text = "点击“安装预览”后，会读取 Tool Hub 安装计划模板并结合当前项目生成预览；确认无误后可安装到当前项目。";
            SetHubPreviewStatus("未预览", "#1A2031", "#3A435B", "#8B95AA");
            hubPreviewButton.Content = "安装预览";
            hubRedownloadPreviewButton.IsEnabled = false;
            hubUninstallButton.IsEnabled = false;
            hubRepairInstallButton.IsEnabled = false;
            hubInstallButton.IsEnabled = false;
            hubInstallButton.Content = "安装到项目";
            return;
        }

        hubDetailTitleText.Text = tool.DisplayName;
        hubDetailMetaText.Text = $"{tool.Author} · {tool.OwnerTeam} · {tool.Category} · {tool.LatestVersion}";
        SetHubDetailStatus(tool.DetailStatusText, tool.IsInstalled);
        hubDetailDescriptionText.Text = tool.Description;
        hubDetailCompatibilityText.Text = tool.CompatibilitySummary;
        hubDetailPackageText.Text = tool.PackageAvailable
            ? $"{tool.PackageSizeText}\nSHA256 {tool.PackageSha256Short}"
            : "当前版本没有可下载 ZIP 包";
        hubDetailTagsText.Text = string.IsNullOrWhiteSpace(tool.TagsText) ? "-" : tool.TagsText;
        hubInstallPreviewText.Text = tool.IsInstalled
            ? FormatHubInstalledHealthSummary(tool, RunHubInstallHealthCheck(tool))
            : "点击“安装预览”读取 Tool Hub 安装计划并执行包校验；确认无误后可安装到当前项目。";
        if (tool.IsInstalled)
            SetHubPreviewStatus("已安装", "#14312B", "#2E8F72", "#86EFAC");
        else
            SetHubPreviewStatus("未预览", "#1A2031", "#3A435B", "#8B95AA");
        hubPreviewButton.Content = "安装预览";
        hubRedownloadPreviewButton.IsEnabled = tool.PackageAvailable && !tool.IsInstalled;
        UpdateHubInstallButtonState(tool);
    }

    private void UpdateHubInstallButtonState(HubToolInfo? tool)
    {
        var hasProject = !string.IsNullOrWhiteSpace(projectDirectory);
        var isInstalled = tool?.IsInstalled == true;
        hubInstallButton.Content = tool == null
            ? "安装到项目"
            : tool.IsUpdateAvailable
                ? "更新工具"
                : isInstalled
                    ? "重新安装"
                    : "安装到项目";
        hubPreviewButton.IsEnabled = tool?.PackageAvailable == true;
        hubRedownloadPreviewButton.IsEnabled = tool?.PackageAvailable == true;
        hubUninstallButton.IsEnabled = tool?.IsManagedHubInstalled == true && hasProject;
        hubRepairInstallButton.IsEnabled = tool?.IsManagedHubInstalled == true && hasProject;
        hubInstallButton.IsEnabled = tool?.PackageAvailable == true && hasProject;
    }

    private void SetHubDetailStatus(string text, bool installed)
    {
        hubDetailStatusText.Text = text;
        hubDetailStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#14312B" : "#171E2E"));
        hubDetailStatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#2E8F72" : "#313B54"));
        hubDetailStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(installed ? "#86EFAC" : "#5DE2FF"));
    }

    private async Task<HubInstallPlan> FetchHubInstallPlanAsync(HubToolInfo tool)
    {
        var version = tool.LatestVersion.TrimStart('v');
        var url = $"{ToolHubBaseUrl}/api/tools/{tool.Slug}/versions/{version}/install-plan-template";
        using var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        var plan = JsonNode.Parse(content)?.AsObject() ?? throw new InvalidOperationException("安装计划模板为空。 ");
        var manifest = plan["manifest"] as JsonObject;
        var files = manifest?["files"] as JsonArray;
        var menuItems = manifest?["menuConfigMerge"]?["itemsToAdd"] as JsonArray;
        var installPath = manifest?["installPath"]?.GetValue<string>() ?? plan["installPath"]?.GetValue<string>() ?? "<Project>/TA/TAPython/Python";
        var resolvedPath = string.IsNullOrWhiteSpace(projectDirectory)
            ? installPath
            : installPath.Replace("<Project>", projectDirectory, StringComparison.OrdinalIgnoreCase);

        return new HubInstallPlan(
            installPath,
            resolvedPath,
            files?.Count ?? 0,
            menuItems,
            ReadStringArray(plan["riskNotes"] as JsonArray),
            ReadStringArray(plan["preInstallChecks"] as JsonArray),
            ReadStringArray(plan["postInstallSteps"] as JsonArray));
    }

    private async Task PreviewHubToolInstallAsync(HubToolInfo tool, bool forceRedownload = false)
    {
        if (string.IsNullOrWhiteSpace(tool.Slug)) return;

        SetHubPreviewWorking(forceRedownload ? "重新下载中" : "校验中");
        hubPreviewButton.IsEnabled = false;
        hubRedownloadPreviewButton.IsEnabled = false;
        hubInstallButton.IsEnabled = false;
        hubPreviewButton.Content = forceRedownload ? "重新下载中..." : "校验中...";
        hubInstallPreviewText.Text = "正在读取安装计划模板...";

        try
        {
            var plan = await FetchHubInstallPlanAsync(tool);

            var packageValidation = await DownloadAndValidateHubPackageAsync(tool, forceRedownload);
            var packageDescriptor = ReadHubPackageDescriptor(packageValidation.PackagePath, plan, tool);
            var packageMenuItems = packageDescriptor.MenuEntries.Count > 0 ? packageDescriptor.MenuEntries : plan.MenuItems;
            var packageFileCount = packageDescriptor.Files.Count > 0 ? packageDescriptor.Files.Count : plan.FileCount;
            var resolvedTargetRoot = string.IsNullOrWhiteSpace(projectDirectory)
                ? plan.ResolvedPath
                : ResolveToolPackageTargetRoot(packageDescriptor, plan);
            var layout = string.IsNullOrWhiteSpace(projectDirectory)
                ? new HubInstallLayout(resolvedTargetRoot, resolvedTargetRoot, string.Empty)
                : ResolveHubInstallLayout(resolvedTargetRoot, packageValidation.PackagePath);
            var impact = AnalyzeHubPackageImpact(packageValidation.PackagePath, string.IsNullOrWhiteSpace(projectDirectory) ? null : layout.ExtractionRoot, layout.PackageRootPrefix);
            var preflight = EvaluateHubProjectPreflight(layout.InstallDirectory);
            var previewState = ResolveHubPreviewState(tool, packageValidation, impact, preflight);

            var previewLines = new List<string>
            {
                $"【状态】{previewState.Title}",
                previewState.Description,
                "",
                "【基本信息】",
                $"工具：{tool.DisplayName} {tool.LatestVersion}",
                $"目标：{layout.InstallDirectory}",
                $"展开根目录：{layout.ExtractionRoot}",
                $"安装计划：文件 {packageFileCount} 个 · MenuConfig 新增项 {packageMenuItems?.Count ?? 0} 个",
                "",
                "【包校验】",
                $"结果：{packageValidation.StatusText}",
                $"SHA256：{packageValidation.Sha256}",
                $"大小：{FormatFileSize(packageValidation.PackageSize)}",
                $"缓存：{packageValidation.PackagePath}",
                "",
                "【项目预检】"
            };
            AddCheckLines(previewLines, preflight.Blockers, "阻止");
            AddCheckLines(previewLines, preflight.Warnings, "警告");
            AddCheckLines(previewLines, preflight.Passed, "通过");
            previewLines.AddRange([
                "",
                "【ZIP 检查】",
                $"manifest：{impact.ManifestText}",
                $"文件：可预览 {impact.SafeFileCount} 个 · 跳过 {impact.SkippedEntryCount} 个 · 不安全路径 {impact.UnsafePathCount} 个",
                "",
                "【文件影响】",
            ]);
            previewLines.Add(impact.ProjectSelected
                ? $"新增 {impact.NewFileCount} 个 · 覆盖 {impact.OverwriteFileCount} 个 · 不安全路径 {impact.UnsafePathCount} 个"
                : "未选择项目，仅完成包校验和 ZIP 结构检查");
            if (impact.UnsafePathCount > 0)
                previewLines.Add("- 检测到不安全 ZIP 路径，后续安装会被阻止");
            foreach (var sample in impact.ImpactSamples)
                previewLines.Add($"  · {sample}");
            previewLines.Add("");
            previewLines.Add("【安装前检查】");
            previewLines.AddRange(plan.PreChecks.Count == 0 ? ["- Tool Hub 未提供检查项"] : plan.PreChecks.Select(check => $"- {check}"));
            if (plan.RiskNotes.Count > 0)
            {
                previewLines.Add("");
                previewLines.Add("【风险提示】");
                previewLines.AddRange(plan.RiskNotes.Select(note => $"- {note}"));
            }
            if (plan.PostSteps.Count > 0)
            {
                previewLines.Add("");
                previewLines.Add("【安装后操作】");
                previewLines.AddRange(plan.PostSteps.Select(step => $"- {step}"));
            }
            previewLines.Add("");
            previewLines.Add(string.IsNullOrWhiteSpace(projectDirectory)
                ? "请选择 .uproject 后再安装到项目。"
                : "预览通过后可点击“安装到项目”写入当前项目。 ");

            hubInstallPreviewText.Text = string.Join(Environment.NewLine, previewLines);
            SetHubPreviewStatus(previewState.BadgeText, previewState.Background, previewState.Border, previewState.Foreground);
            hubPreviewButton.Content = "重新预览";
            Log($"已生成 Tool Hub 安装预览：{tool.DisplayName} {tool.LatestVersion}");
        }
        catch (Exception ex)
        {
            hubInstallPreviewText.Text = FormatHubPreviewError(ex);
            SetHubPreviewStatus("预览失败", "#3A1D25", "#8F4250", "#FCA5A5");
            hubPreviewButton.Content = "重试预览";
            Log($"Tool Hub 安装预览失败：{ex.Message}");
        }
        finally
        {
            hubPreviewButton.IsEnabled = true;
            UpdateHubInstallButtonState(tool);
        }
    }

    private async Task InstallHubToolToProjectAsync(HubToolInfo tool)
    {
        var operation = ResolveHubInstallOperation(tool);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再安装 Tool Hub 工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!tool.PackageAvailable || string.IsNullOrWhiteSpace(tool.PackageUrl))
        {
            MessageBox.Show("当前工具版本没有可下载 ZIP 包，无法安装到项目。", "缺少工具包", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetHubPreviewWorking(operation.WorkingText);
        hubPreviewButton.IsEnabled = false;
        hubRedownloadPreviewButton.IsEnabled = false;
        hubUninstallButton.IsEnabled = false;
        hubInstallButton.IsEnabled = false;
        hubInstallButton.Content = operation.ProgressButtonText;
        hubInstallPreviewText.Text = $"正在执行{operation.ActionName}前校验...";

        string installStage = "开始安装";
        string? targetRoot = null;
        string? extractionRoot = null;
        string? backupRoot = null;

        try
        {
            installStage = "读取安装计划";
            var plan = await FetchHubInstallPlanAsync(tool);
            targetRoot = Path.GetFullPath(plan.ResolvedPath);
            if (!IsPathInsideDirectory(targetRoot, projectDirectory))
                throw new InvalidOperationException("安装目标路径超出当前项目目录，已取消安装。 ");

            installStage = "下载并校验工具包";
            var packageValidation = await DownloadAndValidateHubPackageAsync(tool, forceRedownload: false);
            var packageDescriptor = ReadHubPackageDescriptor(packageValidation.PackagePath, plan, tool);
            var packageMenuItems = packageDescriptor.MenuEntries.Count > 0 ? packageDescriptor.MenuEntries : plan.MenuItems;
            targetRoot = ResolveToolPackageTargetRoot(packageDescriptor, plan);
            if (!IsPathInsideDirectory(targetRoot, projectDirectory))
                throw new InvalidOperationException("安装目标路径超出当前项目目录，已取消安装。 ");

            var layout = ResolveHubInstallLayout(targetRoot, packageValidation.PackagePath);
            targetRoot = layout.InstallDirectory;
            extractionRoot = layout.ExtractionRoot;
            if (!IsPathInsideDirectory(extractionRoot, projectDirectory))
                throw new InvalidOperationException("工具包展开目录超出当前项目目录，已取消安装。 ");

            var preflight = EvaluateHubProjectPreflight(targetRoot);
            if (preflight.BlockerCount > 0)
                throw new InvalidOperationException("安装前项目预检未通过：" + string.Join("；", preflight.Blockers));

            installStage = "分析文件影响";
            var impact = AnalyzeHubPackageImpact(packageValidation.PackagePath, extractionRoot, layout.PackageRootPrefix);
            if (impact.UnsafePathCount > 0)
                throw new InvalidOperationException("工具包包含不安全路径或越界目标，已取消安装。 ");
            if (impact.SafeFileCount == 0)
                throw new InvalidOperationException("工具包内没有可安装的文件。 ");

            var confirmMessage = BuildHubInstallConfirmMessage(tool, plan, packageValidation, impact, targetRoot, operation);
            var confirmIcon = impact.OverwriteFileCount > 0 || !impact.HasManifest || string.IsNullOrWhiteSpace(tool.PackageSha256)
                ? MessageBoxImage.Warning
                : MessageBoxImage.Question;
            var confirm = MessageBox.Show(this, confirmMessage, $"确认{operation.ActionName} Tool Hub 工具", MessageBoxButton.YesNo, confirmIcon);
            if (confirm != MessageBoxResult.Yes)
            {
                SetHubPreviewStatus("已取消", "#1A2031", "#3A435B", "#8B95AA");
                hubInstallPreviewText.Text = $"{operation.ActionName}已取消，未写入项目文件。";
                return;
            }

            installStage = "创建备份";
            backupRoot = CreateToolOperationBackupRoot(operation.BackupOperation, tool.DisplayName);
            BackupTapythonUiConfigFiles(backupRoot);

            installStage = "写入工具文件";
            var installResult = InstallHubPackageEntries(packageValidation.PackagePath, extractionRoot, backupRoot, layout.PackageRootPrefix);

            installStage = "合并菜单配置";
            var addedMenuItems = MergeHubMenuEntries(packageMenuItems);

            installStage = "写入安装记录";
            UpsertInstalledHubToolMetadata(tool, targetRoot, backupRoot, packageValidation.Sha256);

            lastHubInstallTargetRoot = targetRoot;
            lastHubInstallBackupRoot = backupRoot;
            UpdateHubInstallFolderButtons();
            RefreshProjectTools();
            var healthCheck = RunHubInstallHealthCheck(tool, targetRoot);
            SetHubPreviewStatus("已安装", "#14312B", "#2E8F72", "#86EFAC");
            hubInstallPreviewText.Text = FormatHubInstallSuccess(tool, targetRoot, backupRoot, installResult, addedMenuItems, healthCheck, operation);

            Log($"已{operation.ActionName} Tool Hub 工具：{tool.DisplayName} {tool.LatestVersion}；写入 {installResult.WrittenFileCount} 个文件，覆盖 {installResult.OverwrittenPathCount} 个，合并菜单 {addedMenuItems} 个；备份位置：{backupRoot}");
            MessageBox.Show(this, $"工具已{operation.ActionName}：{tool.DisplayName}\n\n写入文件：{installResult.WrittenFileCount} 个\n覆盖文件：{installResult.OverwrittenPathCount} 个\n合并菜单：{addedMenuItems} 个\n\n安装目录：{targetRoot}\n备份目录：{backupRoot}", $"{operation.ActionName}完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(targetRoot)) lastHubInstallTargetRoot = targetRoot;
            if (!string.IsNullOrWhiteSpace(backupRoot)) lastHubInstallBackupRoot = backupRoot;
            UpdateHubInstallFolderButtons();

            hubInstallPreviewText.Text = FormatHubInstallError(ex, installStage, targetRoot, backupRoot, extractionRoot);
            SetHubPreviewStatus("安装失败", "#3A1D25", "#8F4250", "#FCA5A5");
            Log($"Tool Hub 工具{operation.ActionName}失败：{tool.DisplayName}；阶段：{installStage}；错误：{ex.Message}");
            MessageBox.Show(this, $"{operation.ActionName}失败：{tool.DisplayName}\n\n失败阶段：{installStage}\n错误：{ex.Message}\n\n详情已写入安装预览区域。", $"{operation.ActionName}失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            hubPreviewButton.IsEnabled = true;
            UpdateHubInstallButtonState(tool);
        }
    }

    private void RepairHubToolInstall(HubToolInfo tool)
    {
        var metadata = FindInstalledHubToolMetadata(tool);
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.TargetRoot))
        {
            MessageBox.Show(this, "缺少 ToolHubInstalled.json 安装记录，暂时无法定位工具目录。", "无法修复", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var beforeHealth = RunHubInstallHealthCheck(tool, metadata.TargetRoot);
        var repairResult = RepairLegacyHubInstallLayout(metadata);
        if (repairResult.Changed && !string.IsNullOrWhiteSpace(repairResult.BackupRoot))
        {
            UpsertInstalledHubToolMetadata(tool, metadata.TargetRoot, repairResult.BackupRoot, metadata.PackageSha256);
            lastHubInstallBackupRoot = repairResult.BackupRoot;
        }

        lastHubInstallTargetRoot = metadata.TargetRoot;
        if (string.IsNullOrWhiteSpace(lastHubInstallBackupRoot))
            lastHubInstallBackupRoot = metadata.BackupRoot;
        UpdateHubInstallFolderButtons();
        RefreshProjectTools();

        var afterHealth = RunHubInstallHealthCheck(tool, metadata.TargetRoot);
        hubInstallPreviewText.Text = FormatHubRepairResult(tool, beforeHealth, repairResult, afterHealth);
        SetHubPreviewStatus(afterHealth.HasIssues ? "需检查" : "已修复", afterHealth.HasIssues ? "#332A17" : "#14312B", afterHealth.HasIssues ? "#8A6A2E" : "#2E8F72", afterHealth.HasIssues ? "#FACC15" : "#86EFAC");
        Log($"Tool Hub 安装修复：{tool.DisplayName}；{repairResult.Message}");
        MessageBox.Show(this, repairResult.Changed ? $"修复完成：{tool.DisplayName}\n\n备份目录：{repairResult.BackupRoot}" : $"检查完成：{tool.DisplayName}\n\n{repairResult.Message}", "修复安装", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UninstallHubTool(HubToolInfo tool)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再卸载 Tool Hub 工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var metadata = FindInstalledHubToolMetadata(tool);
        if (metadata == null || string.IsNullOrWhiteSpace(metadata.TargetRoot))
        {
            MessageBox.Show("缺少 ToolHubInstalled.json 安装记录。若它是普通项目工具，请在“当前项目工具”页使用“删除”。", "无法卸载", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var targetRoot = Path.GetFullPath(metadata.TargetRoot);
        var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
        if (!IsPathInsideDirectory(targetRoot, projectPythonDir))
        {
            MessageBox.Show($"安装目录不在当前项目 Python 工具目录内，已取消卸载。\n\n{targetRoot}", "路径异常", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"将卸载 Tool Hub 工具：{tool.DisplayName}\n\n会先备份工具目录、MenuConfig.json 和 HotkeyConfig.json，然后移除相关菜单/快捷键引用，并删除 ToolHubInstalled.json 中的安装记录。\n\n安装目录：\n{targetRoot}\n\n这与“当前项目工具”的删除逻辑保持一致，只是额外清理 Hub 安装记录。是否继续？",
            "确认卸载 Tool Hub 工具",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            SetHubPreviewWorking("卸载中");
            hubUninstallButton.IsEnabled = false;
            hubRepairInstallButton.IsEnabled = false;
            hubInstallButton.IsEnabled = false;
            hubPreviewButton.IsEnabled = false;
            hubRedownloadPreviewButton.IsEnabled = false;

            var backupRoot = CreateToolOperationBackupRoot("toolhub-uninstall", tool.DisplayName);
            var relativePath = Path.GetRelativePath(projectPythonDir, targetRoot);
            BackupPathIfExists(targetRoot, Path.Combine(backupRoot, "Python", relativePath));
            BackupTapythonUiConfigFiles(backupRoot);

            var removalNames = GetHubToolRemovalNames(tool, metadata).ToList();
            var removedMenuEntries = 0;
            var removedHotkeyEntries = 0;
            foreach (var removalName in removalNames)
            {
                removedMenuEntries += RemoveToolReferencesFromMenuConfig(removalName);
                removedHotkeyEntries += RemoveToolReferencesFromHotkeyConfig(removalName);
            }

            var deletedPathCount = Directory.Exists(targetRoot)
                ? Directory.EnumerateFileSystemEntries(targetRoot, "*", SearchOption.AllDirectories).Count() + 1
                : File.Exists(targetRoot) ? 1 : 0;
            if (Directory.Exists(targetRoot)) Directory.Delete(targetRoot, recursive: true);
            else if (File.Exists(targetRoot)) File.Delete(targetRoot);

            RemoveInstalledHubToolMetadata(tool);

            lastHubInstallTargetRoot = null;
            lastHubInstallBackupRoot = backupRoot;
            UpdateHubInstallFolderButtons();
            RefreshProjectTools();
            UpdateHubToolDetails(tool);

            hubInstallPreviewText.Text = FormatHubUninstallSuccess(tool, targetRoot, backupRoot, deletedPathCount, removedMenuEntries, removedHotkeyEntries);
            SetHubPreviewStatus("已卸载", "#1A2031", "#3A435B", "#8B95AA");
            Log($"已卸载 Tool Hub 工具：{tool.DisplayName}；删除路径 {deletedPathCount} 个，移除菜单项 {removedMenuEntries} 个，快捷键项 {removedHotkeyEntries} 个；备份位置：{backupRoot}");
            MessageBox.Show(this, $"工具已卸载：{tool.DisplayName}\n\n删除路径：{deletedPathCount} 个\n移除菜单项：{removedMenuEntries} 个\n移除快捷键：{removedHotkeyEntries} 个\n\n备份目录：{backupRoot}", "卸载完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            hubInstallPreviewText.Text = string.Join(Environment.NewLine, [
                "【状态】卸载失败",
                $"工具：{tool.DisplayName}",
                "",
                "【发生了什么】",
                ex.Message.Trim()
            ]);
            SetHubPreviewStatus("卸载失败", "#3A1D25", "#8F4250", "#FCA5A5");
            Log($"Tool Hub 工具卸载失败：{tool.DisplayName}；{ex.Message}");
            MessageBox.Show(this, $"卸载失败：{tool.DisplayName}\n\n{ex.Message}", "卸载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            UpdateHubInstallButtonState(tool);
        }
    }

    private static HubInstallOperation ResolveHubInstallOperation(HubToolInfo tool)
    {
        if (tool.IsUpdateAvailable)
            return new HubInstallOperation("更新", "Tool Hub 最新版本会覆盖当前已安装版本，覆盖前会自动备份。", "更新检查中", "更新中...", "toolhub-update");

        return tool.IsInstalled
            ? new HubInstallOperation("重新安装", "当前项目已安装该工具，本次会重新写入同版本工具文件并先备份覆盖内容。", "重装检查中", "重装中...", "toolhub-reinstall")
            : new HubInstallOperation("安装", "将 Tool Hub 工具安装到当前项目。", "安装检查中", "安装中...", "toolhub-install");
    }

    private static string FormatHubInstallSuccess(HubToolInfo tool, string targetRoot, string backupRoot, HubPackageInstallResult result, int addedMenuItems, HubInstallHealthCheckResult healthCheck, HubInstallOperation operation)
    {
        var lines = new List<string>
        {
            $"【状态】{operation.ActionName}完成",
            $"工具：{tool.DisplayName} {tool.LatestVersion}",
            $"目标目录：{targetRoot}",
            $"备份目录：{backupRoot}",
            "",
            "【结果摘要】",
            $"操作类型：{operation.ActionName}",
            $"写入文件：{result.WrittenFileCount} 个",
            $"覆盖文件：{result.OverwrittenPathCount} 个",
            $"跳过条目：{result.SkippedEntryCount} 个",
            $"合并菜单：{addedMenuItems} 个",
            "",
            "【写入文件示例】"
        };

        AddPathPreview(lines, result.WrittenFiles);
        lines.Add("");
        lines.Add("【覆盖文件示例】");
        AddPathPreview(lines, result.OverwrittenPaths);
        lines.Add("");
        lines.Add("【安装健康检查】");
        AddHealthCheckLines(lines, healthCheck);
        lines.Add("");
        lines.Add("当前项目工具列表已刷新，可用下方按钮打开安装目录或备份目录。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatHubInstalledHealthSummary(HubToolInfo tool, HubInstallHealthCheckResult healthCheck)
    {
        var lines = new List<string>
        {
            "【状态】当前项目已安装该 Tool Hub 工具",
            $"工具：{tool.DisplayName} {tool.LatestVersion}",
            string.IsNullOrWhiteSpace(tool.InstalledVersion) ? "安装版本：未知" : $"安装版本：{tool.InstalledVersion}",
            tool.IsUpdateAvailable ? "版本状态：Hub 有新版本，可点击“更新工具”。" : "版本状态：当前为最新或未提供可比较版本。",
            "",
            "【安装健康检查】"
        };
        AddHealthCheckLines(lines, healthCheck);
        lines.Add("");
        lines.Add("如检测到旧版双层目录或入口文件缺失，可点击“修复安装”；需要移除 Hub 安装包时点击“卸载工具”。");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatHubRepairResult(HubToolInfo tool, HubInstallHealthCheckResult beforeHealth, HubLayoutRepairResult repairResult, HubInstallHealthCheckResult afterHealth)
    {
        var lines = new List<string>
        {
            repairResult.Changed ? "【状态】修复完成" : "【状态】检查完成",
            $"工具：{tool.DisplayName} {tool.LatestVersion}",
            repairResult.Message,
        };

        if (!string.IsNullOrWhiteSpace(repairResult.BackupRoot))
            lines.Add($"备份目录：{repairResult.BackupRoot}");

        lines.AddRange([
            "",
            "【修复前健康检查】"
        ]);
        AddHealthCheckLines(lines, beforeHealth);
        lines.AddRange([
            "",
            "【修复后健康检查】"
        ]);
        AddHealthCheckLines(lines, afterHealth);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatHubUninstallSuccess(HubToolInfo tool, string targetRoot, string backupRoot, int deletedPathCount, int removedMenuEntries, int removedHotkeyEntries)
    {
        return string.Join(Environment.NewLine, [
            "【状态】卸载完成",
            $"工具：{tool.DisplayName} {tool.InstalledVersion}",
            $"目标目录：{targetRoot}",
            $"备份目录：{backupRoot}",
            "",
            "【结果摘要】",
            "操作类型：卸载",
            $"删除路径：{deletedPathCount} 个",
            $"移除菜单项：{removedMenuEntries} 个",
            $"移除快捷键：{removedHotkeyEntries} 个",
            "移除安装记录：1 条",
            "",
            "当前项目工具列表与 Tool Hub 安装状态已刷新。备份方式与“当前项目工具”的删除功能保持一致。"
        ]);
    }

    private static void AddHealthCheckLines(List<string> lines, HubInstallHealthCheckResult healthCheck)
    {
        AddCheckLines(lines, healthCheck.Missing, "缺失");
        AddCheckLines(lines, healthCheck.Warnings, "警告");
        AddCheckLines(lines, healthCheck.Passed, "通过");
    }

    private static void AddCheckLines(List<string> lines, IReadOnlyList<string> items, string label)
    {
        if (items.Count == 0 && label is "缺失" or "阻止")
            return;

        foreach (var item in items)
            lines.Add($"- {label}：{item}");
    }

    private static void AddPathPreview(List<string> lines, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            lines.Add("- 无");
            return;
        }

        foreach (var path in paths.Take(12))
            lines.Add($"- {path}");

        if (paths.Count > 12)
            lines.Add($"- ... 另有 {paths.Count - 12} 个文件");
    }

    private static string BuildHubInstallConfirmMessage(HubToolInfo tool, HubInstallPlan plan, HubPackageValidationResult packageValidation, HubPackageImpact impact, string targetRoot, HubInstallOperation operation)
    {
        var lines = new List<string>
        {
            $"将{operation.ActionName} Tool Hub 工具：{tool.DisplayName} {tool.LatestVersion}",
            operation.Description,
            "",
            $"目标目录：{targetRoot}",
            $"ZIP 校验：{packageValidation.StatusText}",
            $"新增文件：{impact.NewFileCount} 个",
            $"覆盖文件：{impact.OverwriteFileCount} 个",
            $"MenuConfig 新增项：{plan.MenuItems?.Count ?? 0} 个"
        };

        if (impact.OverwriteFileCount > 0)
            lines.Add("覆盖前会自动备份已有文件。 ");
        if (!impact.HasManifest)
            lines.Add("警告：ZIP 缺少 manifest.json。 ");
        if (string.IsNullOrWhiteSpace(tool.PackageSha256))
            lines.Add("警告：Tool Hub 未提供 SHA256，无法确认包内容是否与索引一致。 ");
        if (plan.RiskNotes.Count > 0)
        {
            lines.Add("");
            lines.Add("风险提示：");
            lines.AddRange(plan.RiskNotes.Select(note => $"- {note}"));
        }

        lines.Add("");
        lines.Add("是否继续安装？");
        return string.Join(Environment.NewLine, lines);
    }

    private static HubPackageInstallResult InstallHubPackageEntries(string packagePath, string targetRoot, string backupRoot, string packageRootPrefix)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var normalizedTargetRoot = Path.GetFullPath(targetRoot);
        Directory.CreateDirectory(normalizedTargetRoot);
        var writtenFileCount = 0;
        var overwrittenPathCount = 0;
        var skippedEntryCount = 0;
        var writtenFiles = new List<string>();
        var overwrittenPaths = new List<string>();

        foreach (var entry in archive.Entries)
        {
            var normalizedEntry = NormalizePackagePath(entry.FullName).TrimStart('/');
            if (ShouldSkipHubPackageEntry(entry, normalizedEntry))
            {
                skippedEntryCount++;
                continue;
            }

            var relativePath = MapHubPackageRelativePath(normalizedEntry, packageRootPrefix);
            if (!IsSafeRelativePath(relativePath))
                throw new InvalidOperationException($"工具包中存在不安全路径：{entry.FullName}");

            var targetPath = Path.GetFullPath(Path.Combine(normalizedTargetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInsideDirectory(targetPath, normalizedTargetRoot))
                throw new InvalidOperationException($"工具包中存在越界路径：{entry.FullName}");

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                skippedEntryCount++;
                continue;
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                overwrittenPathCount++;
                overwrittenPaths.Add(relativePath);
                var backupPath = Path.Combine(backupRoot, "Files", relativePath.Replace('/', Path.DirectorySeparatorChar));
                BackupPathIfExists(targetPath, backupPath);
                if (Directory.Exists(targetPath)) Directory.Delete(targetPath, recursive: true);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
            writtenFileCount++;
            writtenFiles.Add(relativePath);
        }

        return new HubPackageInstallResult(writtenFileCount, overwrittenPathCount, skippedEntryCount, writtenFiles, overwrittenPaths);
    }

    private int MergeHubMenuEntries(JsonArray? menuItems)
    {
        if (menuItems == null || menuItems.Count == 0) return 0;

        var menuConfigPath = GetProjectMenuConfigPath();
        var root = ReadJsonObjectOrCreate(menuConfigPath);
        var toolbar = GetOrCreateJsonObject(root, "OnToolBarChameleon");
        var items = GetOrCreateJsonArray(toolbar, "items");
        var addedCount = 0;

        foreach (var entry in menuItems.OfType<JsonObject>())
        {
            if (JsonArrayContainsEquivalentObject(items, entry)) continue;
            items.Add(entry.DeepClone());
            addedCount++;
        }

        if (addedCount > 0)
            WriteJsonObject(menuConfigPath, root);

        return addedCount;
    }

    private void UpsertInstalledHubToolMetadata(HubToolInfo tool, string targetRoot, string backupRoot, string packageSha256)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return;

        var metadataPath = GetInstalledHubToolsMetadataPath(projectDirectory);
        var root = ReadJsonObjectOrCreate(metadataPath);
        var tools = GetOrCreateJsonArray(root, "tools");
        var normalizedSlug = NormalizeToolIdentity(tool.Slug);
        var normalizedName = NormalizeToolIdentity(tool.Name);
        var normalizedDisplayName = NormalizeToolIdentity(tool.DisplayName);

        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is not JsonObject existing) continue;
            var existingSlug = NormalizeToolIdentity(TryGetStringProperty(existing, "slug") ?? string.Empty);
            var existingName = NormalizeToolIdentity(TryGetStringProperty(existing, "name") ?? string.Empty);
            var existingDisplayName = NormalizeToolIdentity(TryGetStringProperty(existing, "displayName") ?? string.Empty);
            if (existingSlug == normalizedSlug || existingName == normalizedName || existingDisplayName == normalizedDisplayName)
                tools.RemoveAt(index);
        }

        tools.Add(new JsonObject
        {
            ["slug"] = tool.Slug,
            ["name"] = tool.Name,
            ["displayName"] = tool.DisplayName,
            ["description"] = tool.Description,
            ["version"] = tool.LatestVersion,
            ["targetRoot"] = targetRoot,
            ["backupRoot"] = backupRoot,
            ["packageSha256"] = string.IsNullOrWhiteSpace(packageSha256) ? tool.PackageSha256 : packageSha256,
            ["installedAt"] = DateTimeOffset.Now.ToString("O")
        });

        WriteJsonObject(metadataPath, root);
    }

    private void RemoveInstalledHubToolMetadata(HubToolInfo tool)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return;

        var metadataPath = GetInstalledHubToolsMetadataPath(projectDirectory);
        if (!File.Exists(metadataPath)) return;

        var root = ReadJsonObjectOrCreate(metadataPath);
        if (root["tools"] is not JsonArray tools) return;

        var normalizedSlug = NormalizeToolIdentity(tool.Slug);
        var normalizedName = NormalizeToolIdentity(tool.Name);
        var normalizedDisplayName = NormalizeToolIdentity(tool.DisplayName);
        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is not JsonObject existing) continue;
            var existingSlug = NormalizeToolIdentity(TryGetStringProperty(existing, "slug") ?? string.Empty);
            var existingName = NormalizeToolIdentity(TryGetStringProperty(existing, "name") ?? string.Empty);
            var existingDisplayName = NormalizeToolIdentity(TryGetStringProperty(existing, "displayName") ?? string.Empty);
            if (existingSlug == normalizedSlug || existingName == normalizedName || existingDisplayName == normalizedDisplayName)
                tools.RemoveAt(index);
        }

        WriteJsonObject(metadataPath, root);
    }

    private int RemoveInstalledHubToolMetadataForProjectTool(TapythonToolInfo tool, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return 0;

        var metadataPath = GetInstalledHubToolsMetadataPath(projectDirectory);
        if (!File.Exists(metadataPath)) return 0;

        var root = ReadJsonObjectOrCreate(metadataPath);
        if (root["tools"] is not JsonArray tools) return 0;

        var normalizedToolName = NormalizeToolIdentity(tool.Name);
        var normalizedSourcePath = NormalizeFullPathForCompare(sourcePath);
        var removedCount = 0;

        for (var index = tools.Count - 1; index >= 0; index--)
        {
            if (tools[index] is not JsonObject existing) continue;

            var targetRoot = TryGetStringProperty(existing, "targetRoot") ?? string.Empty;
            var pathMatches = !string.IsNullOrWhiteSpace(targetRoot) &&
                              string.Equals(NormalizeFullPathForCompare(targetRoot), normalizedSourcePath, StringComparison.OrdinalIgnoreCase);
            var identityMatches = normalizedToolName == NormalizeToolIdentity(TryGetStringProperty(existing, "slug") ?? string.Empty) ||
                                  normalizedToolName == NormalizeToolIdentity(TryGetStringProperty(existing, "name") ?? string.Empty) ||
                                  normalizedToolName == NormalizeToolIdentity(TryGetStringProperty(existing, "displayName") ?? string.Empty);

            if (!pathMatches && !identityMatches) continue;

            tools.RemoveAt(index);
            removedCount++;
        }

        if (removedCount > 0) WriteJsonObject(metadataPath, root);
        return removedCount;
    }

    private static string NormalizeFullPathForCompare(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static IEnumerable<string> GetHubToolRemovalNames(HubToolInfo tool, HubInstalledToolMetadata metadata)
    {
        var values = new[] { tool.Slug, tool.Name, tool.DisplayName, metadata.Slug, metadata.Name, metadata.DisplayName };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (seen.Add(value.Trim())) yield return value.Trim();
        }
    }

    private HashSet<string> ReadInstalledHubToolIdentities(List<HubInstalledToolMetadata>? installedTools = null)
    {
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var installedTool in installedTools ?? ReadInstalledHubToolMetadataRecords())
        {
            AddNormalizedIdentity(identities, installedTool.Slug);
            AddNormalizedIdentity(identities, installedTool.Name);
            AddNormalizedIdentity(identities, installedTool.DisplayName);
        }

        return identities;
    }

    private HubProjectPreflightResult EvaluateHubProjectPreflight(string targetRoot)
    {
        var passed = new List<string>();
        var warnings = new List<string>();
        var blockers = new List<string>();

        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            blockers.Add("未选择 .uproject 文件");
            return new HubProjectPreflightResult(passed, warnings, blockers);
        }

        if (string.IsNullOrWhiteSpace(uprojectPath) || !File.Exists(uprojectPath))
            blockers.Add("当前项目文件不存在或尚未加载");
        else
            passed.Add($"项目文件：{uprojectPath}");

        if (!string.IsNullOrWhiteSpace(targetRoot) && IsPathInsideDirectory(targetRoot, projectDirectory))
            passed.Add("目标目录位于当前项目内");
        else
            blockers.Add("目标目录超出当前项目目录");

        var tapythonRoot = Path.Combine(projectDirectory, "TA", "TAPython");
        var pythonRoot = Path.Combine(tapythonRoot, "Python");
        var menuConfigPath = Path.Combine(tapythonRoot, "UI", "MenuConfig.json");

        if (Directory.Exists(tapythonRoot)) passed.Add("TA/TAPython 目录已存在");
        else warnings.Add("TA/TAPython 目录尚不存在，安装会按需创建工具和 UI 配置目录");

        if (Directory.Exists(pythonRoot)) passed.Add("Python 工具目录已存在");
        else warnings.Add("Python 工具目录尚不存在，工具文件写入时会创建目标目录");

        if (File.Exists(menuConfigPath)) passed.Add("MenuConfig.json 已存在，可合并菜单项");
        else warnings.Add("MenuConfig.json 尚不存在，合并菜单时会创建新的配置文件");

        return new HubProjectPreflightResult(passed, warnings, blockers);
    }

    private HubInstallHealthCheckResult RunHubInstallHealthCheck(HubToolInfo tool, string? targetRootOverride = null)
    {
        var passed = new List<string>();
        var warnings = new List<string>();
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            missing.Add("未选择 .uproject 文件，无法检查项目安装状态");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        var metadata = FindInstalledHubToolMetadata(tool);
        var targetRoot = string.IsNullOrWhiteSpace(targetRootOverride) ? metadata?.TargetRoot : targetRootOverride;
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            missing.Add("ToolHubInstalled.json 中缺少安装目录记录");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        targetRoot = Path.GetFullPath(targetRoot);
        if (!Directory.Exists(targetRoot))
        {
            missing.Add($"安装目录不存在：{targetRoot}");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        passed.Add($"安装目录存在：{targetRoot}");
        var installLeaf = Path.GetFileName(targetRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var expectedEntry = string.IsNullOrWhiteSpace(installLeaf) ? string.Empty : Path.Combine(targetRoot, installLeaf + ".json");
        var nestedEntry = string.IsNullOrWhiteSpace(installLeaf) ? string.Empty : Path.Combine(targetRoot, installLeaf, installLeaf + ".json");

        if (!string.IsNullOrWhiteSpace(expectedEntry) && File.Exists(expectedEntry))
        {
            passed.Add($"入口 JSON 存在：{expectedEntry}");
        }
        else
        {
            var firstJson = Directory.EnumerateFiles(targetRoot, "*.json", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstJson))
                warnings.Add($"未找到默认入口 JSON，但发现同级 JSON：{firstJson}");
            else
                missing.Add(string.IsNullOrWhiteSpace(expectedEntry) ? "未找到入口 JSON" : $"入口 JSON 不存在：{expectedEntry}");
        }

        if (!string.IsNullOrWhiteSpace(nestedEntry) && File.Exists(nestedEntry) && !File.Exists(expectedEntry))
            warnings.Add($"检测到旧版双层目录入口：{nestedEntry}");

        var menuConfigPath = GetProjectMenuConfigPath();
        if (!File.Exists(menuConfigPath))
        {
            warnings.Add($"MenuConfig.json 不存在：{menuConfigPath}");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        JsonNode? menuRoot;
        try
        {
            menuRoot = JsonNode.Parse(File.ReadAllText(menuConfigPath));
        }
        catch (Exception ex)
        {
            missing.Add($"MenuConfig.json 无法解析：{ex.Message}");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        var menuConfigDir = Path.GetDirectoryName(menuConfigPath)!;
        var tapythonRoot = Directory.GetParent(menuConfigDir)?.FullName ?? menuConfigDir;
        var menuToolPaths = FindMatchingMenuToolPaths(menuRoot, tool);
        if (menuToolPaths.Count == 0)
        {
            warnings.Add("MenuConfig.json 中未找到该工具的 ChameleonTools 引用");
            return new HubInstallHealthCheckResult(passed, warnings, missing);
        }

        foreach (var menuToolPath in menuToolPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolvedPath = ResolveChameleonToolsPath(menuToolPath, menuConfigDir, tapythonRoot);
            if (File.Exists(resolvedPath))
                passed.Add($"MenuConfig 引用可解析：{menuToolPath}");
            else
                missing.Add($"MenuConfig 引用目标不存在：{menuToolPath} -> {resolvedPath}");
        }

        return new HubInstallHealthCheckResult(passed, warnings, missing);
    }

    private static List<string> FindMatchingMenuToolPaths(JsonNode? node, HubToolInfo tool)
    {
        var paths = new List<string>();
        CollectMatchingMenuToolPaths(node, tool, paths);
        return paths;
    }

    private static void CollectMatchingMenuToolPaths(JsonNode? node, HubToolInfo tool, List<string> paths)
    {
        if (node is JsonObject jsonObject)
        {
            var chameleonToolPath = TryGetStringProperty(jsonObject, "ChameleonTools");
            if (!string.IsNullOrWhiteSpace(chameleonToolPath))
            {
                var referencedToolName = GetToolNameFromChameleonPath(chameleonToolPath);
                if (HubToolIdentityMatches(tool, referencedToolName))
                    paths.Add(chameleonToolPath.Trim());
            }

            foreach (var property in jsonObject)
                CollectMatchingMenuToolPaths(property.Value, tool, paths);
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                CollectMatchingMenuToolPaths(item, tool, paths);
        }
    }

    private static bool HubToolIdentityMatches(HubToolInfo tool, string? value)
    {
        var normalizedValue = NormalizeToolIdentity(value ?? string.Empty);
        return !string.IsNullOrWhiteSpace(normalizedValue) &&
               (normalizedValue == NormalizeToolIdentity(tool.Slug) ||
                normalizedValue == NormalizeToolIdentity(tool.Name) ||
                normalizedValue == NormalizeToolIdentity(tool.DisplayName));
    }

    private static string ResolveChameleonToolsPath(string chameleonToolPath, string menuConfigDir, string tapythonRoot)
    {
        var trimmedPath = chameleonToolPath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(trimmedPath)) return Path.GetFullPath(trimmedPath);

        var normalized = NormalizePackagePath(chameleonToolPath).TrimStart('/');
        var baseDir = normalized.StartsWith("Python/", StringComparison.OrdinalIgnoreCase)
            ? tapythonRoot
            : menuConfigDir;
        return Path.GetFullPath(Path.Combine(baseDir, trimmedPath));
    }

    private void RestoreHubInstallFolders(HubToolInfo? tool)
    {
        if (tool == null || !tool.IsInstalled)
        {
            lastHubInstallTargetRoot = null;
            lastHubInstallBackupRoot = null;
            UpdateHubInstallFolderButtons();
            return;
        }

        var metadata = FindInstalledHubToolMetadata(tool);
        lastHubInstallTargetRoot = metadata?.TargetRoot;
        lastHubInstallBackupRoot = metadata?.BackupRoot;

        if (string.IsNullOrWhiteSpace(lastHubInstallBackupRoot))
            lastHubInstallBackupRoot = FindLatestHubToolBackupRoot(tool.DisplayName);

        UpdateHubInstallFolderButtons();
    }

    private HubInstalledToolMetadata? FindInstalledHubToolMetadata(HubToolInfo tool)
    {
        var normalizedSlug = NormalizeToolIdentity(tool.Slug);
        var normalizedName = NormalizeToolIdentity(tool.Name);
        var normalizedDisplayName = NormalizeToolIdentity(tool.DisplayName);

        return FindInstalledHubToolMetadata(tool, ReadInstalledHubToolMetadataRecords());
    }

    private static HubInstalledToolMetadata? FindInstalledHubToolMetadata(HubToolInfo tool, IEnumerable<HubInstalledToolMetadata> installedTools)
    {
        var normalizedSlug = NormalizeToolIdentity(tool.Slug);
        var normalizedName = NormalizeToolIdentity(tool.Name);
        var normalizedDisplayName = NormalizeToolIdentity(tool.DisplayName);

        return installedTools.FirstOrDefault(installedTool =>
            NormalizeToolIdentity(installedTool.Slug) == normalizedSlug ||
            NormalizeToolIdentity(installedTool.Name) == normalizedName ||
            NormalizeToolIdentity(installedTool.DisplayName) == normalizedDisplayName);
    }

    private List<HubInstalledToolMetadata> ReadInstalledHubToolMetadataRecords()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return [];

        var metadataPath = GetInstalledHubToolsMetadataPath(projectDirectory);
        if (!File.Exists(metadataPath)) return [];

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            if (root?["tools"] is not JsonArray tools) return [];

            return tools.OfType<JsonObject>()
                .Select(toolNode => new HubInstalledToolMetadata(
                    TryGetStringProperty(toolNode, "slug") ?? string.Empty,
                    TryGetStringProperty(toolNode, "name") ?? string.Empty,
                    TryGetStringProperty(toolNode, "displayName") ?? string.Empty,
                    TryGetStringProperty(toolNode, "version") ?? string.Empty,
                    TryGetStringProperty(toolNode, "targetRoot") ?? string.Empty,
                    TryGetStringProperty(toolNode, "backupRoot") ?? string.Empty,
                    TryGetStringProperty(toolNode, "packageSha256") ?? string.Empty,
                    TryGetStringProperty(toolNode, "installedAt") ?? string.Empty))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string? FindLatestHubToolBackupRoot(string toolName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var backupRoot = Path.Combine(localAppData, "TAPythonInstaller", "ToolBackups");
        if (!Directory.Exists(backupRoot)) return null;

        var suffix = $"-toolhub-install-{SanitizeFileName(toolName)}";
        return Directory.EnumerateDirectories(backupRoot)
            .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void RepairLegacyHubInstallLayouts()
    {
        foreach (var installedTool in ReadInstalledHubToolMetadataRecords())
        {
            var repairResult = RepairLegacyHubInstallLayout(installedTool);
            if (repairResult.Changed)
                Log($"已修复旧版 Tool Hub 双层目录：{installedTool.DisplayName}；备份位置：{repairResult.BackupRoot}");
            else if (repairResult.Message.StartsWith("修复失败", StringComparison.OrdinalIgnoreCase))
                Log($"修复旧版 Tool Hub 目录失败：{installedTool.DisplayName}；{repairResult.Message}");
        }
    }

    private HubLayoutRepairResult RepairLegacyHubInstallLayout(HubInstalledToolMetadata installedTool)
    {
        if (string.IsNullOrWhiteSpace(installedTool.TargetRoot) || !Directory.Exists(installedTool.TargetRoot))
            return new HubLayoutRepairResult(false, string.Empty, "工具目录不存在，未执行目录整理。");

        var installDirectory = Path.GetFullPath(installedTool.TargetRoot);
        var installLeaf = Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(installLeaf))
            return new HubLayoutRepairResult(false, string.Empty, "安装目录名称为空，未执行目录整理。");

        var nestedRoot = Path.Combine(installDirectory, installLeaf);
        if (!Directory.Exists(nestedRoot))
            return new HubLayoutRepairResult(false, string.Empty, "未检测到旧版双层目录布局。");

        var expectedEntry = Path.Combine(installDirectory, installLeaf + ".json");
        var nestedEntry = Path.Combine(nestedRoot, installLeaf + ".json");
        if (File.Exists(expectedEntry))
            return new HubLayoutRepairResult(false, string.Empty, "入口 JSON 已在正确位置，无需整理目录。");
        if (!File.Exists(nestedEntry))
            return new HubLayoutRepairResult(false, string.Empty, "检测到嵌套目录，但未找到可提升的入口 JSON。");

        try
        {
            var backupRoot = CreateToolOperationBackupRoot("toolhub-layout-repair", string.IsNullOrWhiteSpace(installedTool.DisplayName) ? installLeaf : installedTool.DisplayName);
            BackupPathIfExists(installDirectory, Path.Combine(backupRoot, "Before", installLeaf));

            CopyDirectory(nestedRoot, installDirectory, overwrite: false);
            if (!File.Exists(expectedEntry))
                return new HubLayoutRepairResult(false, backupRoot, "目录整理已尝试，但入口 JSON 仍未出现在目标位置。");

            Directory.Delete(nestedRoot, recursive: true);
            return new HubLayoutRepairResult(true, backupRoot, "已将旧版双层目录中的工具文件提升到正确位置。");
        }
        catch (Exception ex)
        {
            return new HubLayoutRepairResult(false, string.Empty, $"修复失败：{ex.Message}");
        }
    }

    private static bool HubToolMatchesInstalledIdentities(HubToolInfo tool, HashSet<string> installedIdentities)
        => installedIdentities.Contains(NormalizeToolIdentity(tool.Slug)) ||
           installedIdentities.Contains(NormalizeToolIdentity(tool.Name)) ||
           installedIdentities.Contains(NormalizeToolIdentity(tool.DisplayName));

    private static void AddNormalizedIdentity(HashSet<string> identities, string? value)
    {
        var normalized = NormalizeToolIdentity(value ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalized)) identities.Add(normalized);
    }

    private static string NormalizeToolIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool HubVersionIsNewer(string latestVersion, string installedVersion)
    {
        var latest = ExtractVersionNumbers(latestVersion);
        var installed = ExtractVersionNumbers(installedVersion);
        if (latest.Count == 0 || installed.Count == 0) return false;

        var length = Math.Max(latest.Count, installed.Count);
        for (var index = 0; index < length; index++)
        {
            var latestPart = index < latest.Count ? latest[index] : 0;
            var installedPart = index < installed.Count ? installed[index] : 0;
            if (latestPart > installedPart) return true;
            if (latestPart < installedPart) return false;
        }

        return false;
    }

    private static List<int> ExtractVersionNumbers(string version)
        => Regex.Matches(version ?? string.Empty, "\\d+")
            .Select(match => int.TryParse(match.Value, out var number) ? number : 0)
            .ToList();

    private static string GetInstalledHubToolsMetadataPath(string projectDirectory)
        => Path.Combine(projectDirectory, "TA", "TAPython", "ToolHubInstalled.json");

    private static string FormatHubInstallError(Exception ex, string installStage, string? targetRoot, string? backupRoot, string? extractionRoot)
    {
        var lines = new List<string>
        {
            "【状态】安装失败",
            "安装流程未确认完成。若错误发生在写入阶段，请查看日志、安装目录和备份目录。",
            "",
            "【失败阶段】",
            installStage,
            "",
            "【发生了什么】",
            ex.Message.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(targetRoot) || !string.IsNullOrWhiteSpace(backupRoot) || !string.IsNullOrWhiteSpace(extractionRoot))
        {
            lines.Add("");
            lines.Add("【恢复线索】");
            if (!string.IsNullOrWhiteSpace(targetRoot)) lines.Add($"目标目录：{targetRoot}");
            if (!string.IsNullOrWhiteSpace(extractionRoot)) lines.Add($"展开根目录：{extractionRoot}");
            if (!string.IsNullOrWhiteSpace(backupRoot)) lines.Add($"备份目录：{backupRoot}");
        }

        lines.AddRange([
            "",
            "【建议动作】",
            "先查看上方失败阶段；如果已创建备份，可打开备份目录确认覆盖前文件。",
            "重新生成安装预览后再试一次；如果是路径、ZIP 或 SHA256 问题，请联系工具发布者修正 Tool Hub 包。"
        ]);

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<HubPackageValidationResult> DownloadAndValidateHubPackageAsync(HubToolInfo tool, bool forceRedownload)
    {
        if (!tool.PackageAvailable || string.IsNullOrWhiteSpace(tool.PackageUrl))
            throw new InvalidOperationException("当前工具版本没有可下载 ZIP 包，无法进行安装前校验。 ");

        var packagePath = GetHubPackageCachePath(tool);
        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
        if (forceRedownload && File.Exists(packagePath))
            File.Delete(packagePath);

        if (File.Exists(packagePath))
        {
            var cachedSha256 = ComputeFileSha256(packagePath);
            if (HubSha256Matches(tool.PackageSha256, cachedSha256))
                return new HubPackageValidationResult(packagePath, new FileInfo(packagePath).Length, cachedSha256, "通过 SHA256（使用本地缓存）");

            File.Delete(packagePath);
        }

        hubInstallPreviewText.Text = "正在下载工具包并计算 SHA256...";
        var tempPath = packagePath + ".download";
        if (File.Exists(tempPath)) File.Delete(tempPath);

        using (var response = await httpClient.GetAsync(tool.PackageUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(tempPath);
            await input.CopyToAsync(output);
        }

        File.Move(tempPath, packagePath, overwrite: true);
        var fileInfo = new FileInfo(packagePath);
        var sha256 = ComputeFileSha256(packagePath);
        if (!HubSha256Matches(tool.PackageSha256, sha256))
        {
            File.Delete(packagePath);
            throw new InvalidOperationException($"ZIP sha256 校验失败。期望 {tool.PackageSha256Short}，实际 {(sha256.Length <= 12 ? sha256 : sha256[..12])}。 ");
        }

        var sizeText = tool.PackageSize.HasValue && tool.PackageSize.Value != fileInfo.Length
            ? $"通过 SHA256（大小 {FormatFileSize(fileInfo.Length)}，索引记录 {FormatFileSize(tool.PackageSize)}）"
            : $"通过 SHA256（{FormatFileSize(fileInfo.Length)}）";
        return new HubPackageValidationResult(packagePath, fileInfo.Length, sha256, string.IsNullOrWhiteSpace(tool.PackageSha256) ? $"未提供 SHA256（{FormatFileSize(fileInfo.Length)}）" : sizeText);
    }

    private void SetHubPreviewWorking(string text)
    {
        SetHubPreviewStatus(text, "#1B2A3A", "#2F78A8", "#7DD3FC");
    }

    private void SetHubPreviewStatus(string text, string background, string border, string foreground)
    {
        hubPreviewStatusText.Text = text;
        hubPreviewStatusBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        hubPreviewStatusBadge.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(border));
        hubPreviewStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
    }

    private static HubPreviewState ResolveHubPreviewState(HubToolInfo tool, HubPackageValidationResult packageValidation, HubPackageImpact impact, HubProjectPreflightResult preflight)
    {
        if (preflight.BlockerCount > 0)
            return new HubPreviewState("阻止：项目预检未通过", "当前项目或目标目录不满足安装条件，后续安装会被阻止。", "已阻止", "#3A1D25", "#8F4250", "#FCA5A5");

        if (impact.UnsafePathCount > 0)
        {
            return new HubPreviewState("阻止：检测到不安全路径", "发现 ZIP 路径越界或包含不安全相对路径，后续安装会被阻止。", "已阻止", "#3A1D25", "#8F4250", "#FCA5A5");
        }

        if (preflight.WarningCount > 0 || !impact.HasManifest || impact.OverwriteFileCount > 0 || string.IsNullOrWhiteSpace(tool.PackageSha256))
        {
            var reason = preflight.WarningCount > 0
                ? "项目预检存在提示项；多数情况下可继续安装，但建议确认目标项目结构。"
                : !impact.HasManifest
                ? "ZIP 缺少 manifest.json，请联系工具发布者补齐包元数据。"
                : impact.OverwriteFileCount > 0
                    ? "目标项目中存在同名文件，后续安装前会要求确认并先备份。"
                    : "Tool Hub 未提供 SHA256，当前只能完成下载和 ZIP 结构检查。";
            return new HubPreviewState("警告：需要确认", reason, "有警告", "#332A17", "#8A6A2E", "#FACC15");
        }

        return new HubPreviewState("可安装预览通过", "包校验、ZIP 结构和目标路径预览均未发现阻止项。", "预览通过", "#14312B", "#2E8F72", "#86EFAC");
    }

    private static string FormatHubPreviewError(Exception ex)
    {
        var message = ex.Message.Trim();
        var probableReason = ex switch
        {
            HttpRequestException => "公司网络、Tool Hub 服务或下载地址暂时不可访问。",
            InvalidDataException => "下载到的 ZIP 包可能损坏，或不是有效 ZIP 格式。",
            InvalidOperationException when message.Contains("sha256", StringComparison.OrdinalIgnoreCase) => "工具包内容与 Tool Hub 索引记录不一致，可能是包被替换但索引未刷新。",
            InvalidOperationException when message.Contains("manifest", StringComparison.OrdinalIgnoreCase) => "Tool Hub 返回的安装计划或工具包缺少必要元数据。",
            _ => "安装预览流程遇到未预期的问题。"
        };

        var suggestion = ex switch
        {
            HttpRequestException => "建议：确认公司网络可访问后重试；如果网站可打开但仍失败，请联系 Tool Hub 维护者检查下载地址。",
            InvalidDataException => "建议：点击“重新下载”；如果仍失败，请联系工具发布者重新上传 ZIP 包。",
            InvalidOperationException when message.Contains("sha256", StringComparison.OrdinalIgnoreCase) => "建议：点击“重新下载”；如果仍失败，请联系工具发布者重新生成索引或重新上传包。",
            InvalidOperationException when message.Contains("manifest", StringComparison.OrdinalIgnoreCase) => "建议：联系工具发布者检查 install-plan-template 和 manifest.json。",
            _ => "建议：点击“重试预览”；如果问题持续，请把日志中的错误信息发给维护者。"
        };

        return string.Join(Environment.NewLine, [
            "【状态】预览失败",
            "安装预览没有写入项目文件。",
            "",
            "【发生了什么】",
            message,
            "",
            "【可能原因】",
            probableReason,
            "",
            "【建议动作】",
            suggestion
        ]);
    }

    private string GetHubPackageCachePath(HubToolInfo tool)
    {
        var version = SanitizeFileName(tool.LatestVersion.TrimStart('v'));
        var fileName = string.Empty;
        if (Uri.TryCreate(tool.PackageUrl, UriKind.Absolute, out var uri))
            fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{SanitizeFileName(tool.Slug)}-{version}.zip";

        return Path.Combine(Path.GetTempPath(), "TAPythonInstaller", "ToolHub", SanitizeFileName(tool.Slug), version, fileName);
    }

    private static HubInstallLayout ResolveHubInstallLayout(HubInstallPlan plan, string packagePath)
        => ResolveHubInstallLayout(plan.ResolvedPath, packagePath);

    private static HubInstallLayout ResolveHubInstallLayout(string resolvedInstallPath, string packagePath)
    {
        var installDirectory = Path.GetFullPath(resolvedInstallPath);
        var extractionRoot = installDirectory;
        var installLeaf = Path.GetFileName(installDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(installLeaf) || !File.Exists(packagePath))
            return new HubInstallLayout(installDirectory, extractionRoot, string.Empty);

        using var archive = ZipFile.OpenRead(packagePath);
        var relativePaths = archive.Entries
            .Select(entry => new { Entry = entry, Normalized = NormalizePackagePath(entry.FullName).TrimStart('/') })
            .Where(item => !ShouldSkipHubPackageEntry(item.Entry, item.Normalized))
            .Select(item => MapHubPackageRelativePath(item.Normalized, string.Empty))
            .Where(relativePath => !string.IsNullOrWhiteSpace(relativePath))
            .ToList();

        var packageRootPrefix = relativePaths.Any(relativePath => relativePath.StartsWith(installLeaf + "/", StringComparison.OrdinalIgnoreCase))
            ? installLeaf
            : string.Empty;

        return new HubInstallLayout(installDirectory, extractionRoot, packageRootPrefix);
    }

    private static bool ShouldSkipHubPackageEntry(ZipArchiveEntry entry, string normalizedEntry)
    {
        if (string.IsNullOrWhiteSpace(normalizedEntry) || string.IsNullOrWhiteSpace(entry.Name)) return true;
        if (string.Equals(normalizedEntry, "manifest.json", StringComparison.OrdinalIgnoreCase)) return true;
        if (normalizedEntry.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)) return true;

        var fileName = normalizedEntry.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return string.Equals(fileName, "MenuConfig.snippet.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".MenuConfig.snippet.json", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapHubPackageRelativePath(string normalizedEntry, string packageRootPrefix)
    {
        var relativePath = normalizedEntry.StartsWith("Python/", StringComparison.OrdinalIgnoreCase)
            ? normalizedEntry["Python/".Length..]
            : normalizedEntry;

        return !string.IsNullOrWhiteSpace(packageRootPrefix) && relativePath.StartsWith(packageRootPrefix + "/", StringComparison.OrdinalIgnoreCase)
            ? relativePath[(packageRootPrefix.Length + 1)..]
            : relativePath;
    }

    private static string ComputeFileSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool HubSha256Matches(string expectedSha256, string actualSha256)
        => string.IsNullOrWhiteSpace(expectedSha256) || string.Equals(expectedSha256.Trim(), actualSha256, StringComparison.OrdinalIgnoreCase);

    private static HubPackageImpact AnalyzeHubPackageImpact(string packagePath, string? targetRoot, string packageRootPrefix = "")
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var projectSelected = !string.IsNullOrWhiteSpace(targetRoot);
        var normalizedTargetRoot = projectSelected ? Path.GetFullPath(targetRoot!) : string.Empty;
        var safeFileCount = 0;
        var unsafePathCount = 0;
        var newFileCount = 0;
        var overwriteFileCount = 0;
        var skippedEntryCount = 0;
        var samples = new List<string>();

        foreach (var entry in archive.Entries)
        {
            var normalizedEntry = NormalizePackagePath(entry.FullName).TrimStart('/');
            if (ShouldSkipHubPackageEntry(entry, normalizedEntry))
            {
                skippedEntryCount++;
                continue;
            }

            var relativePath = MapHubPackageRelativePath(normalizedEntry, packageRootPrefix);

            if (!IsSafeRelativePath(relativePath))
            {
                unsafePathCount++;
                AddImpactSample(samples, $"不安全路径：{normalizedEntry}");
                continue;
            }

            safeFileCount++;
            if (!projectSelected) continue;

            var targetPath = Path.GetFullPath(Path.Combine(normalizedTargetRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsPathInsideDirectory(targetPath, normalizedTargetRoot))
            {
                unsafePathCount++;
                AddImpactSample(samples, $"越界目标：{normalizedEntry}");
                continue;
            }

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                overwriteFileCount++;
                AddImpactSample(samples, $"覆盖：{relativePath}");
            }
            else
            {
                newFileCount++;
                AddImpactSample(samples, $"新增：{relativePath}");
            }
        }

        return new HubPackageImpact(archive.GetEntry("manifest.json") != null, projectSelected, safeFileCount, unsafePathCount, newFileCount, overwriteFileCount, skippedEntryCount, samples);
    }

    private static void AddImpactSample(List<string> samples, string text)
    {
        if (samples.Count < 8) samples.Add(text);
    }

    private static bool MatchesHubQuery(HubToolInfo tool, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return tool.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.Author.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.OwnerTeam.Contains(query, StringComparison.OrdinalIgnoreCase)
            || tool.TagsText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesHubCategory(HubToolInfo tool, string category)
        => string.Equals(category, "所有分类", StringComparison.OrdinalIgnoreCase)
           || string.Equals(tool.Category, category, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesHubRisk(HubToolInfo tool, string risk)
        => risk switch
        {
            "低风险" => string.Equals(tool.RiskLevel, "low", StringComparison.OrdinalIgnoreCase),
            "中风险" => string.Equals(tool.RiskLevel, "medium", StringComparison.OrdinalIgnoreCase),
            "高风险" => string.Equals(tool.RiskLevel, "high", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    private static bool MatchesHubStatus(HubToolInfo tool, string status)
        => status switch
        {
            "已审核" => string.Equals(tool.Status, "approved", StringComparison.OrdinalIgnoreCase),
            "待审核" => string.Equals(tool.Status, "pending", StringComparison.OrdinalIgnoreCase),
            "草稿" => string.Equals(tool.Status, "draft", StringComparison.OrdinalIgnoreCase),
            "已归档" => string.Equals(tool.Status, "archived", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    private bool IsHubToolCompatibleWithCurrentProject(HubToolInfo tool)
    {
        if (string.IsNullOrWhiteSpace(detectedEngineVersion)) return true;
        var version = detectedEngineVersion.Trim();
        return tool.UnrealVersions.Count == 0 || tool.UnrealVersions.Any(ue => version.StartsWith(ue, StringComparison.OrdinalIgnoreCase) || ue.StartsWith(version, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadStringArray(JsonArray? array)
        => array?.Select(item => item?.GetValue<string>()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToList() ?? [];

    private static string BuildToolHubUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        if (Uri.TryCreate(path, UriKind.Absolute, out var absolute)) return absolute.ToString();
        return $"{ToolHubBaseUrl}/{path.TrimStart('/')}";
    }

    private static string FormatFileSize(long? bytes)
    {
        if (!bytes.HasValue || bytes.Value <= 0) return "无包体积";
        if (bytes.Value >= 1024 * 1024) return $"{bytes.Value / 1024d / 1024d:0.0} MB";
        return $"{bytes.Value / 1024d:0.0} KB";
    }

    private void ExportProjectTool(TapythonToolInfo tool)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再导出项目工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
        var sourcePath = Path.GetFullPath(Path.Combine(projectPythonDir, tool.RelativePath));
        if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
        {
            MessageBox.Show("选中的工具文件已不存在，请重新扫描项目工具。", "工具不存在", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshProjectTools();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出 TAPython 项目工具",
            Filter = "TAPython Tool Package (*.tapython-tool.zip)|*.tapython-tool.zip|ZIP (*.zip)|*.zip",
            FileName = BuildToolPackageFileName(tool),
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            CreateProjectToolPackage(tool, projectPythonDir, sourcePath, dialog.FileName);

            Log($"已导出项目工具：{tool.Name} -> {dialog.FileName}");
            MessageBox.Show($"工具已导出：\n{dialog.FileName}", "导出完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"导出工具失败：{ex.Message}");
            MessageBox.Show($"导出工具失败：\n{ex.Message}", "导出失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CreateProjectToolPackage(TapythonToolInfo tool, string projectPythonDir, string sourcePath, string packagePath)
    {
        if (File.Exists(packagePath)) File.Delete(packagePath);
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        var packageFiles = CollectProjectToolPackageFiles(projectPythonDir, sourcePath);

        var manifest = CreateToolPackageManifest(tool, projectPythonDir, sourcePath, packageFiles);
        AddTextEntry(archive, "manifest.json", manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AddProjectToolFilesToArchive(archive, projectPythonDir, sourcePath);
    }

    private JsonObject CreateToolPackageManifest(TapythonToolInfo tool, string projectPythonDir, string sourcePath, IReadOnlyList<ToolPackageFileDescriptor> packageFiles)
    {
        var tapythonRoot = Directory.GetParent(projectPythonDir)?.FullName ?? string.Empty;
        var menuConfigPath = Path.Combine(tapythonRoot, "UI", "MenuConfig.json");
        var hotkeyConfigPath = Path.Combine(tapythonRoot, "UI", "HotkeyConfig.json");
        var now = DateTimeOffset.Now.ToString("O");
        var releasedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd");
        var projectName = Path.GetFileName(projectDirectory?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty);
        var normalizedRelativePath = NormalizePackagePath(tool.RelativePath);
        var version = NormalizePackageVersion(tool.Version);

        return new JsonObject
        {
            ["formatVersion"] = 2,
            ["packageType"] = "TAPythonToolPackage",
            ["schemaVersion"] = "2.0.0",
            ["slug"] = NormalizePackageSlug(tool.Name),
            ["name"] = tool.Name,
            ["displayName"] = tool.Name,
            ["version"] = version,
            ["releasedAt"] = releasedAt,
            ["author"] = "Local Project",
            ["ownerTeam"] = string.IsNullOrWhiteSpace(projectName) ? "Local Project" : projectName,
            ["description"] = tool.Description,
            ["category"] = tool.Kind,
            ["riskLevel"] = "medium",
            ["tags"] = new JsonArray("local-export"),
            ["compatibility"] = new JsonObject
            {
                ["unrealEngine"] = new JsonArray(),
                ["tapython"] = new JsonArray(),
                ["plugins"] = new JsonArray()
            },
            ["dependencies"] = new JsonArray(),
            ["install"] = new JsonObject
            {
                ["pythonRoot"] = $"Python/{normalizedRelativePath}",
                ["targetPath"] = $"<Project>/TA/TAPython/Python/{normalizedRelativePath}",
                ["entryJson"] = InferEntryJsonFromLegacyRelativePath(normalizedRelativePath),
                ["mountPoint"] = "OnToolBarChameleon"
            },
            ["files"] = BuildPackageFilesArray(packageFiles),
            ["menuEntries"] = CollectConfigEntriesForTool(menuConfigPath, tool.Name),
            ["hotkeyEntries"] = CollectHotkeyEntriesForTool(hotkeyConfigPath, tool.Name),
            ["externalJson"] = CollectExternalJsonReferences(sourcePath),
            ["summary"] = new JsonObject
            {
                ["features"] = new JsonArray(),
                ["unrealApis"] = new JsonArray(),
                ["widgetAkas"] = new JsonArray(),
                ["riskNotes"] = new JsonArray("本包由本地项目导出，上传前建议人工复核菜单项、快捷键和外部 JSON 引用。")
            },
            ["preInstallChecks"] = new JsonArray("确认当前项目已安装 TAPython 插件。"),
            ["postInstallSteps"] = new JsonArray("在 Unreal Editor 中重新加载 TAPython 菜单或重启编辑器。"),
            ["uninstallSteps"] = new JsonArray("使用 TAPython Installer 的项目工具删除功能移除工具并备份配置。"),
            ["createdAt"] = now,
            ["updatedAt"] = now
        };
    }

    private void ImportProjectToolPackage()
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再导入项目工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入 TAPython 项目工具",
            Filter = "TAPython Tool Package (*.tapython-tool.zip)|*.tapython-tool.zip|ZIP (*.zip)|*.zip"
        };
        if (dialog.ShowDialog() != true) return;

        ImportProjectToolPackage(dialog.FileName, "导入");
    }

    private void ImportProjectToolPackage(string packagePath, string sourceLabel)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(projectDirectory))
            {
                MessageBox.Show("请先选择 .uproject 文件，再导入项目工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!File.Exists(packagePath))
                throw new FileNotFoundException("工具包文件不存在。", packagePath);
            if (!IsProjectToolPackagePath(packagePath))
                throw new InvalidOperationException("请导入 .zip 或 .tapython-tool.zip 工具包。");

            using var archive = ZipFile.OpenRead(packagePath);
            var packageDescriptor = ReadToolPackageDescriptor(archive);
            ValidateToolPackageEntries(archive, packageDescriptor);
            var toolName = packageDescriptor.Name;
            var toolRelativePath = GetToolPackagePythonRelativePath(packageDescriptor);

            if (IsBuiltInTapythonTool(toolName))
                throw new InvalidOperationException($"导入包指向 TAPython 内置工具：{toolName}。为避免覆盖内置资源，已取消导入。");
            if (!IsSafeRelativePath(toolRelativePath))
                throw new InvalidOperationException("工具包中的工具路径不安全，已取消导入。");

            var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
            Directory.CreateDirectory(projectPythonDir);
            var targetPath = Path.GetFullPath(Path.Combine(projectPythonDir, toolRelativePath));
            if (!IsPathInsideDirectory(targetPath, projectPythonDir))
                throw new InvalidOperationException("工具包中的目标路径超出项目 Python 目录，已取消导入。");

            var pythonEntries = archive.Entries
                .Where(entry => entry.FullName.StartsWith("Python/", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Name))
                .ToList();
            if (pythonEntries.Count == 0)
                throw new InvalidOperationException("工具包缺少 Python 工具文件。");

            var overwriteExisting = File.Exists(targetPath) || Directory.Exists(targetPath);
            if (overwriteExisting)
            {
                var result = MessageBox.Show($"项目中已存在同名工具：{toolName}\n是否备份后覆盖？", "工具已存在", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
            }

            var backupRoot = CreateToolOperationBackupRoot("import", toolName);
            BackupPathIfExists(targetPath, Path.Combine(backupRoot, "Python", toolRelativePath));
            BackupTapythonUiConfigFiles(backupRoot);

            if (overwriteExisting)
            {
                if (Directory.Exists(targetPath)) Directory.Delete(targetPath, recursive: true);
                else if (File.Exists(targetPath)) File.Delete(targetPath);
            }

            ExtractPythonEntries(archive, projectPythonDir);
            MergeImportedMenuEntries(packageDescriptor.MenuEntries);
            MergeImportedHotkeyEntries(packageDescriptor.HotkeyEntries);

            Log($"已{sourceLabel}项目工具：{toolName}；来源：{packagePath}；备份位置：{backupRoot}");
            RefreshProjectTools();
            MessageBox.Show($"工具已导入：{toolName}", $"{sourceLabel}完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"{sourceLabel}工具失败：{ex.Message}");
            MessageBox.Show($"导入工具失败：\n{ex.Message}", $"{sourceLabel}失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private bool CanAcceptProjectToolDrop(DragEventArgs e)
        => !string.IsNullOrWhiteSpace(projectDirectory) && !string.IsNullOrWhiteSpace(GetDroppedProjectToolPackagePath(e));

    private static string? GetDroppedProjectToolPackagePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(IsProjectToolPackagePath);
    }

    private static bool IsProjectToolPackagePath(string? path)
        => !string.IsNullOrWhiteSpace(path) &&
           string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase) &&
           File.Exists(path);

    private void DeleteProjectTool(TapythonToolInfo tool)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            MessageBox.Show("请先选择 .uproject 文件，再删除项目工具。", "缺少项目", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show($"将删除项目工具：{tool.Name}\n\n会先备份工具目录、MenuConfig.json 和 HotkeyConfig.json，然后移除相关菜单/快捷键引用。是否继续？", "确认删除工具", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        try
        {
            var projectPythonDir = Path.Combine(projectDirectory, "TA", "TAPython", "Python");
            var sourcePath = Path.GetFullPath(Path.Combine(projectPythonDir, tool.RelativePath));
            if (!IsPathInsideDirectory(sourcePath, projectPythonDir))
                throw new InvalidOperationException("工具路径超出项目 Python 目录，已取消删除。");

            var backupRoot = CreateToolOperationBackupRoot("delete", tool.Name);
            BackupPathIfExists(sourcePath, Path.Combine(backupRoot, "Python", tool.RelativePath));
            BackupTapythonUiConfigFiles(backupRoot);

            var removedMenuEntries = RemoveToolReferencesFromMenuConfig(tool.Name);
            var removedHotkeyEntries = RemoveToolReferencesFromHotkeyConfig(tool.Name);

            if (Directory.Exists(sourcePath)) Directory.Delete(sourcePath, recursive: true);
            else if (File.Exists(sourcePath)) File.Delete(sourcePath);

            var removedHubRecords = RemoveInstalledHubToolMetadataForProjectTool(tool, sourcePath);

            Log($"已删除项目工具：{tool.Name}；移除菜单项 {removedMenuEntries} 个，快捷键项 {removedHotkeyEntries} 个，Hub 安装记录 {removedHubRecords} 条；备份位置：{backupRoot}");
            RefreshProjectTools();
            MessageBox.Show($"工具已删除：{tool.Name}\n备份位置：{backupRoot}{(removedHubRecords > 0 ? "\n已同步刷新 Tool Hub 安装状态。" : string.Empty)}", "删除完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log($"删除工具失败：{ex.Message}");
            MessageBox.Show($"删除工具失败：\n{ex.Message}", "删除失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private int RemoveToolReferencesFromMenuConfig(string toolName)
    {
        var menuConfigPath = GetProjectMenuConfigPath();
        if (!File.Exists(menuConfigPath)) return 0;

        var root = JsonNode.Parse(File.ReadAllText(menuConfigPath)) as JsonObject;
        if (root == null) return 0;

        var removedCount = RemoveToolReferencesFromJsonArrays(root, toolName);
        if (removedCount > 0) WriteJsonObject(menuConfigPath, root);
        return removedCount;
    }

    private int RemoveToolReferencesFromHotkeyConfig(string toolName)
    {
        var hotkeyConfigPath = GetProjectHotkeyConfigPath();
        if (!File.Exists(hotkeyConfigPath)) return 0;

        var root = JsonNode.Parse(File.ReadAllText(hotkeyConfigPath)) as JsonObject;
        if (root?["Hotkeys"] is not JsonObject hotkeys) return 0;

        var keysToRemove = hotkeys
            .Where(hotkey => hotkey.Value is JsonObject hotkeyObject && JsonObjectReferencesTool(hotkeyObject, toolName))
            .Select(hotkey => hotkey.Key)
            .ToList();

        foreach (var key in keysToRemove)
            hotkeys.Remove(key);

        if (keysToRemove.Count > 0) WriteJsonObject(hotkeyConfigPath, root);
        return keysToRemove.Count;
    }

    private static int RemoveToolReferencesFromJsonArrays(JsonNode? node, string toolName)
    {
        var removedCount = 0;
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
                removedCount += RemoveToolReferencesFromJsonArrays(property.Value, toolName);
        }
        else if (node is JsonArray jsonArray)
        {
            for (var index = jsonArray.Count - 1; index >= 0; index--)
            {
                if (jsonArray[index] is JsonObject itemObject && JsonObjectReferencesTool(itemObject, toolName))
                {
                    jsonArray.RemoveAt(index);
                    removedCount++;
                }
                else
                {
                    removedCount += RemoveToolReferencesFromJsonArrays(jsonArray[index], toolName);
                }
            }
        }

        return removedCount;
    }

    private JsonObject ReadToolPackageManifest(ZipArchive archive)
    {
        var manifest = ReadToolPackageManifestObject(archive);

        var packageType = TryGetStringProperty(manifest, "packageType");
        if (!string.Equals(packageType, "TAPythonProjectTool", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不是有效的 TAPython 项目工具包。");

        return manifest;
    }

    private static JsonObject ReadToolPackageManifestObject(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidOperationException("工具包缺少 manifest.json。");
        using var stream = manifestEntry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var manifest = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        if (manifest == null) throw new InvalidOperationException("manifest.json 格式无效。");
        return manifest;
    }

    private static ToolPackageDescriptor ReadToolPackageDescriptor(ZipArchive archive)
    {
        var manifest = ReadToolPackageManifestObject(archive);
        var packageType = TryGetStringProperty(manifest, "packageType") ?? string.Empty;
        if (string.Equals(packageType, "TAPythonToolPackage", StringComparison.OrdinalIgnoreCase))
            return ReadToolPackageV2Descriptor(manifest);
        if (string.Equals(packageType, "TAPythonProjectTool", StringComparison.OrdinalIgnoreCase))
            return ReadLegacyProjectToolPackageDescriptor(manifest);

        throw new InvalidOperationException($"不支持的 TAPython 工具包类型：{packageType}");
    }

    private static ToolPackageDescriptor ReadHubPackageDescriptor(string packagePath, HubInstallPlan plan, HubToolInfo tool)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        try
        {
            var manifest = ReadToolPackageManifestObject(archive);
            var packageType = TryGetStringProperty(manifest, "packageType") ?? string.Empty;
            if (string.Equals(packageType, "TAPythonToolPackage", StringComparison.OrdinalIgnoreCase))
            {
                var descriptor = ReadToolPackageV2Descriptor(manifest);
                ValidateToolPackageEntries(archive, descriptor);
                return descriptor;
            }
        }
        catch
        {
            // Legacy Tool Hub packages may not contain a v2 manifest yet.
        }

        return new ToolPackageDescriptor(
            ToolPackageSourceKind.HubLegacy,
            tool.Slug,
            tool.Name,
            tool.DisplayName,
            tool.LatestVersion,
            tool.Description,
            plan.InstallPath,
            BuildPackagePythonRootFromInstallPath(plan.InstallPath),
            string.Empty,
            "OnToolBarChameleon",
            [],
            CloneJsonArray(plan.MenuItems),
            [],
            []);
    }

    private static ToolPackageDescriptor ReadToolPackageV2Descriptor(JsonObject manifest)
    {
        var formatVersion = manifest["formatVersion"]?.GetValue<int?>() ?? 0;
        if (formatVersion != 2)
            throw new InvalidOperationException($"不支持的 TAPythonToolPackage formatVersion：{formatVersion}");

        var install = manifest["install"] as JsonObject ?? throw new InvalidOperationException("manifest.json 缺少 install 节点。");
        return new ToolPackageDescriptor(
            ToolPackageSourceKind.ToolPackageV2,
            ReadRequiredManifestString(manifest, "slug"),
            ReadRequiredManifestString(manifest, "name"),
            ReadRequiredManifestString(manifest, "displayName"),
            ReadRequiredManifestString(manifest, "version"),
            TryGetStringProperty(manifest, "description") ?? string.Empty,
            ReadRequiredManifestString(install, "targetPath"),
            ReadRequiredManifestString(install, "pythonRoot"),
            ReadRequiredManifestString(install, "entryJson"),
            ReadRequiredManifestString(install, "mountPoint"),
            ReadPackageFileDescriptors(manifest["files"] as JsonArray),
            CloneJsonArray(manifest["menuEntries"] as JsonArray),
            CloneJsonObject(manifest["hotkeyEntries"] as JsonObject),
            ReadStringArray(manifest["externalJson"] as JsonArray));
    }

    private static ToolPackageDescriptor ReadLegacyProjectToolPackageDescriptor(JsonObject manifest)
    {
        var tool = manifest["tool"] as JsonObject ?? throw new InvalidOperationException("manifest.json 缺少 tool 节点。");
        var relativePath = ReadRequiredManifestString(tool, "relativePath");
        var toolName = ReadRequiredManifestString(tool, "name");
        return new ToolPackageDescriptor(
            ToolPackageSourceKind.LegacyProjectTool,
            NormalizePackageSlug(toolName),
            toolName,
            toolName,
            TryGetStringProperty(tool, "version") ?? string.Empty,
            TryGetStringProperty(tool, "description") ?? string.Empty,
            $"<Project>/TA/TAPython/Python/{NormalizePackagePath(relativePath)}",
            $"Python/{NormalizePackagePath(relativePath)}",
            InferEntryJsonFromLegacyRelativePath(relativePath),
            "OnToolBarChameleon",
            [],
            CloneJsonArray(manifest["menuEntries"] as JsonArray),
            CloneJsonObject(manifest["hotkeyEntries"] as JsonObject),
            ReadStringArray(manifest["externalJson"] as JsonArray));
    }

    private static string ReadRequiredManifestString(JsonObject jsonObject, string propertyName)
        => TryGetStringProperty(jsonObject, propertyName) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"manifest.json 缺少 {propertyName}。");

    private static List<ToolPackageFileDescriptor> ReadPackageFileDescriptors(JsonArray? files)
        => files?.OfType<JsonObject>()
            .Select(file => new ToolPackageFileDescriptor(
                ReadRequiredManifestString(file, "path"),
                ReadRequiredManifestString(file, "sha256"),
                file["size"]?.GetValue<long?>() ?? 0,
                TryGetStringProperty(file, "role") ?? string.Empty))
            .ToList() ?? [];

    private static JsonArray CloneJsonArray(JsonArray? array)
        => array == null ? [] : JsonNode.Parse(array.ToJsonString())?.AsArray() ?? [];

    private static JsonObject CloneJsonObject(JsonObject? jsonObject)
        => jsonObject == null ? [] : JsonNode.Parse(jsonObject.ToJsonString())?.AsObject() ?? [];

    private static string NormalizePackageSlug(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                previousDash = false;
            }
            else if (!previousDash)
            {
                builder.Append('-');
                previousDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string InferEntryJsonFromLegacyRelativePath(string relativePath)
    {
        var normalizedPath = NormalizePackagePath(relativePath).Trim('/');
        if (normalizedPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return normalizedPath;

        var leafName = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? normalizedPath;
        return string.IsNullOrWhiteSpace(leafName) ? normalizedPath : $"{normalizedPath}/{leafName}.json";
    }

    private static string GetToolPackagePythonRelativePath(ToolPackageDescriptor packageDescriptor)
    {
        foreach (var candidate in new[] { packageDescriptor.PythonRoot, packageDescriptor.TargetPathTemplate })
        {
            var relativePath = TryGetPythonRelativePath(candidate);
            if (!string.IsNullOrWhiteSpace(relativePath)) return relativePath;
        }

        throw new InvalidOperationException("工具包缺少可解析的 Python 安装目录。");
    }

    private static string? TryGetPythonRelativePath(string path)
    {
        var normalizedPath = NormalizePackagePath(path).Trim('/');
        const string packagePythonPrefix = "Python/";
        const string projectPythonPrefix = "TA/TAPython/Python/";

        if (normalizedPath.StartsWith(packagePythonPrefix, StringComparison.OrdinalIgnoreCase))
            return normalizedPath[packagePythonPrefix.Length..];
        if (normalizedPath.StartsWith(projectPythonPrefix, StringComparison.OrdinalIgnoreCase))
            return normalizedPath[projectPythonPrefix.Length..];

        var marker = "/TA/TAPython/Python/";
        var markerIndex = normalizedPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0 ? normalizedPath[(markerIndex + marker.Length)..] : null;
    }

    private static string BuildPackagePythonRootFromInstallPath(string installPath)
    {
        var relativePath = TryGetPythonRelativePath(installPath);
        return string.IsNullOrWhiteSpace(relativePath) ? string.Empty : $"Python/{relativePath}";
    }

    private string ResolveToolPackageTargetRoot(ToolPackageDescriptor packageDescriptor, HubInstallPlan fallbackPlan)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory)) return fallbackPlan.ResolvedPath;

        var targetPath = packageDescriptor.TargetPathTemplate;
        if (string.IsNullOrWhiteSpace(targetPath)) return fallbackPlan.ResolvedPath;

        if (targetPath.Contains("<Project>", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(targetPath.Replace("<Project>", projectDirectory, StringComparison.OrdinalIgnoreCase));

        var pythonRelativePath = TryGetPythonRelativePath(targetPath) ?? TryGetPythonRelativePath(packageDescriptor.PythonRoot);
        if (!string.IsNullOrWhiteSpace(pythonRelativePath))
            return Path.GetFullPath(Path.Combine(projectDirectory, "TA", "TAPython", "Python", pythonRelativePath.Replace('/', Path.DirectorySeparatorChar)));

        return Path.IsPathRooted(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetFullPath(Path.Combine(projectDirectory, targetPath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string ReadManifestString(JsonObject manifest, string sectionName, string propertyName)
    {
        if (manifest[sectionName] is not JsonObject section)
            throw new InvalidOperationException($"manifest.json 缺少 {sectionName} 节点。");

        var value = TryGetStringProperty(section, propertyName);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"manifest.json 缺少 {sectionName}.{propertyName}。 ");

        return value;
    }

    private void ExtractPythonEntries(ZipArchive archive, string projectPythonDir)
    {
        foreach (var entry in archive.Entries.Where(entry => entry.FullName.StartsWith("Python/", StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = entry.FullName["Python/".Length..].Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(relativePath)) continue;
            if (!IsSafeRelativePath(relativePath)) throw new InvalidOperationException($"工具包中存在不安全路径：{entry.FullName}");

            var targetPath = Path.GetFullPath(Path.Combine(projectPythonDir, relativePath));
            if (!IsPathInsideDirectory(targetPath, projectPythonDir)) throw new InvalidOperationException($"工具包中存在越界路径：{entry.FullName}");
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                Directory.CreateDirectory(targetPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private void MergeImportedMenuEntries(JsonArray menuEntries)
    {
        if (menuEntries.Count == 0) return;

        var menuConfigPath = GetProjectMenuConfigPath();
        var root = ReadJsonObjectOrCreate(menuConfigPath);
        var toolbar = GetOrCreateJsonObject(root, "OnToolBarChameleon");
        var items = GetOrCreateJsonArray(toolbar, "items");
        var addedCount = 0;

        foreach (var entry in menuEntries.OfType<JsonObject>())
        {
            if (JsonArrayContainsEquivalentObject(items, entry)) continue;
            items.Add(entry.DeepClone());
            addedCount++;
        }

        if (addedCount > 0)
        {
            WriteJsonObject(menuConfigPath, root);
            Log($"已合并菜单项：{addedCount} 个。");
        }
    }

    private void MergeImportedHotkeyEntries(JsonObject hotkeyEntries)
    {
        if (hotkeyEntries.Count == 0) return;

        var hotkeyConfigPath = GetProjectHotkeyConfigPath();
        var root = ReadJsonObjectOrCreate(hotkeyConfigPath);
        var hotkeys = GetOrCreateJsonObject(root, "Hotkeys");
        var addedCount = 0;

        foreach (var hotkeyEntry in hotkeyEntries)
        {
            if (hotkeys.ContainsKey(hotkeyEntry.Key))
            {
                Log($"快捷键槽位已存在，跳过导入：{hotkeyEntry.Key}");
                continue;
            }

            hotkeys[hotkeyEntry.Key] = hotkeyEntry.Value?.DeepClone();
            addedCount++;
        }

        if (addedCount > 0)
        {
            WriteJsonObject(hotkeyConfigPath, root);
            Log($"已合并快捷键项：{addedCount} 个。");
        }
    }

    private static JsonArray CollectConfigEntriesForTool(string configPath, string toolName)
    {
        var entries = new JsonArray();
        if (!File.Exists(configPath)) return entries;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath));
            CollectConfigEntriesForTool(root, toolName, entries);
        }
        catch
        {
            return entries;
        }

        return entries;
    }

    private static JsonObject CollectHotkeyEntriesForTool(string configPath, string toolName)
    {
        var entries = new JsonObject();
        if (!File.Exists(configPath)) return entries;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject;
            if (root?["Hotkeys"] is not JsonObject hotkeys) return entries;
            foreach (var hotkey in hotkeys)
            {
                if (hotkey.Value is JsonObject hotkeyObject && JsonObjectReferencesTool(hotkeyObject, toolName))
                    entries[hotkey.Key] = hotkeyObject.DeepClone();
            }
        }
        catch
        {
            return entries;
        }

        return entries;
    }

    private static void CollectConfigEntriesForTool(JsonNode? node, string toolName, JsonArray entries)
    {
        if (node is JsonObject jsonObject)
        {
            if (JsonObjectReferencesTool(jsonObject, toolName))
                entries.Add(jsonObject.DeepClone());

            foreach (var property in jsonObject)
                CollectConfigEntriesForTool(property.Value, toolName, entries);
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                CollectConfigEntriesForTool(item, toolName, entries);
        }
    }

    private static bool JsonObjectReferencesTool(JsonObject jsonObject, string toolName)
    {
        foreach (var referencedToolName in GetReferencedToolNames(jsonObject))
        {
            if (string.Equals(referencedToolName, toolName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static JsonArray CollectExternalJsonReferences(string sourcePath)
    {
        var references = new JsonArray();
        var toolRoot = Directory.Exists(sourcePath) ? sourcePath : Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(toolRoot) || !Directory.Exists(toolRoot)) return references;

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var jsonFile in Directory.EnumerateFiles(toolRoot, "*.json", SearchOption.AllDirectories)
                     .Where(path => !ShouldSkipToolPackagePath(path)))
            CollectExternalJsonReferences(jsonFile, toolRoot, visited, references);

        return references;
    }

    private static void CollectExternalJsonReferences(string jsonFile, string toolRoot, HashSet<string> visited, JsonArray references)
    {
        jsonFile = Path.GetFullPath(jsonFile);
        if (!visited.Add(jsonFile)) return;
        if (!File.Exists(jsonFile)) return;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(jsonFile));
        }
        catch
        {
            return;
        }

        foreach (var externalJson in FindExternalJsonValues(root))
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(jsonFile)!, externalJson));
            if (!IsPathInsideDirectory(resolvedPath, toolRoot) || !File.Exists(resolvedPath)) continue;

            var relativePath = NormalizePackagePath(Path.GetRelativePath(toolRoot, resolvedPath));
            if (!references.Any(item => string.Equals(item?.GetValue<string>(), relativePath, StringComparison.OrdinalIgnoreCase)))
                references.Add(relativePath);

            CollectExternalJsonReferences(resolvedPath, toolRoot, visited, references);
        }
    }

    private static IEnumerable<string> FindExternalJsonValues(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                if (string.Equals(property.Key, "ExternalJson", StringComparison.OrdinalIgnoreCase) &&
                    property.Value is JsonValue value && value.TryGetValue<string>(out var externalJson) &&
                    !string.IsNullOrWhiteSpace(externalJson))
                {
                    yield return externalJson.Trim();
                }

                foreach (var nested in FindExternalJsonValues(property.Value))
                    yield return nested;
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            foreach (var nested in FindExternalJsonValues(item))
                yield return nested;
        }
    }

    private static JsonArray BuildPackageFilesArray(IReadOnlyList<ToolPackageFileDescriptor> packageFiles)
    {
        var files = new JsonArray();
        foreach (var file in packageFiles)
        {
            files.Add(new JsonObject
            {
                ["path"] = file.Path,
                ["sha256"] = file.Sha256,
                ["size"] = file.Size,
                ["role"] = file.Role
            });
        }

        return files;
    }

    private static string BuildToolPackageFileName(TapythonToolInfo tool)
    {
        var slug = NormalizePackageSlug(tool.Name);
        var version = NormalizePackageVersion(tool.Version);
        return $"{SanitizeFileName(slug)}-{SanitizeFileName(version)}{ToolPackageExtension}";
    }

    private static string NormalizePackageVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0.0";
        var trimmed = version.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
    }

    private static void ValidateToolPackageEntries(ZipArchive archive, ToolPackageDescriptor packageDescriptor)
    {
        if (packageDescriptor.SourceKind != ToolPackageSourceKind.ToolPackageV2 || packageDescriptor.Files.Count == 0)
            return;

        foreach (var file in packageDescriptor.Files)
        {
            var entryPath = NormalizePackagePath(file.Path).TrimStart('/');
            if (!IsSafeRelativePath(entryPath))
                throw new InvalidOperationException($"manifest.json 中存在不安全文件路径：{file.Path}");

            var entry = archive.GetEntry(entryPath) ?? throw new InvalidOperationException($"manifest.json 声明的文件不存在于 ZIP：{file.Path}");
            if (string.IsNullOrWhiteSpace(entry.Name))
                throw new InvalidOperationException($"manifest.json 声明了目录路径而非文件：{file.Path}");
            if (file.Size >= 0 && entry.Length != file.Size)
                throw new InvalidOperationException($"manifest.json 文件大小不匹配：{file.Path}，期望 {file.Size}，实际 {entry.Length}");

            using var stream = entry.Open();
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"manifest.json 文件 SHA256 不匹配：{file.Path}");
        }
    }

    private static List<ToolPackageFileDescriptor> CollectProjectToolPackageFiles(string projectPythonDir, string sourcePath)
    {
        var packageFiles = new List<ToolPackageFileDescriptor>();
        if (File.Exists(sourcePath))
        {
            packageFiles.Add(CreateToolPackageFileDescriptor(projectPythonDir, sourcePath));
            return packageFiles;
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                     .Where(path => !ShouldSkipToolPackagePath(path)))
            packageFiles.Add(CreateToolPackageFileDescriptor(projectPythonDir, file));

        return packageFiles;
    }

    private static void AddProjectToolFilesToArchive(ZipArchive archive, string projectPythonDir, string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            AddFileToToolArchive(archive, projectPythonDir, sourcePath);
            return;
        }

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                     .Where(path => !ShouldSkipToolPackagePath(path)))
            AddFileToToolArchive(archive, projectPythonDir, file);
    }

    private static ToolPackageFileDescriptor CreateToolPackageFileDescriptor(string projectPythonDir, string filePath)
    {
        var relativePath = Path.GetRelativePath(projectPythonDir, filePath);
        var entryName = $"Python/{NormalizePackagePath(relativePath)}";
        var fileInfo = new FileInfo(filePath);
        var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
        return new ToolPackageFileDescriptor(entryName, sha256, fileInfo.Length, InferPackageFileRole(entryName));
    }

    private static void AddFileToToolArchive(ZipArchive archive, string projectPythonDir, string filePath)
    {
        var relativePath = Path.GetRelativePath(projectPythonDir, filePath);
        var entryName = $"Python/{NormalizePackagePath(relativePath)}";
        archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.NoCompression);
    }

    private static string InferPackageFileRole(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)) return "python";
        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) return "chameleon-ui";
        return "asset";
    }

    private static bool ShouldSkipToolPackagePath(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(segment => string.Equals(segment, "__pycache__", StringComparison.OrdinalIgnoreCase))) return true;

        var extension = Path.GetExtension(path);
        return extension.Equals(".pyc", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pyo", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static bool IsPathInsideDirectory(string path, string directory)
    {
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePackagePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalidChar, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "TAPythonTool" : fileName;
    }

    private string GetProjectMenuConfigPath()
        => Path.Combine(projectDirectory!, "TA", "TAPython", "UI", "MenuConfig.json");

    private string GetProjectHotkeyConfigPath()
        => Path.Combine(projectDirectory!, "TA", "TAPython", "UI", "HotkeyConfig.json");

    private string CreateToolOperationBackupRoot(string operation, string toolName)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupRoot = Path.Combine(localAppData, "TAPythonInstaller", "ToolBackups", $"{timestamp}-{operation}-{SanitizeFileName(toolName)}");
        Directory.CreateDirectory(backupRoot);
        return backupRoot;
    }

    private void BackupTapythonUiConfigFiles(string backupRoot)
    {
        BackupPathIfExists(GetProjectMenuConfigPath(), Path.Combine(backupRoot, "UI", "MenuConfig.json"));
        BackupPathIfExists(GetProjectHotkeyConfigPath(), Path.Combine(backupRoot, "UI", "HotkeyConfig.json"));
    }

    private static void BackupPathIfExists(string sourcePath, string backupPath)
    {
        if (Directory.Exists(sourcePath))
        {
            CopyDirectory(sourcePath, backupPath);
            return;
        }

        if (!File.Exists(sourcePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(sourcePath, backupPath, overwrite: true);
    }

    private static JsonObject ReadJsonObjectOrCreate(string path)
    {
        if (!File.Exists(path)) return [];

        try
        {
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static JsonObject GetOrCreateJsonObject(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonObject jsonObject) return jsonObject;
        jsonObject = [];
        parent[propertyName] = jsonObject;
        return jsonObject;
    }

    private static JsonArray GetOrCreateJsonArray(JsonObject parent, string propertyName)
    {
        if (parent[propertyName] is JsonArray jsonArray) return jsonArray;
        jsonArray = [];
        parent[propertyName] = jsonArray;
        return jsonArray;
    }

    private static void WriteJsonObject(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool JsonArrayContainsEquivalentObject(JsonArray array, JsonObject candidate)
    {
        var candidateJson = candidate.ToJsonString();
        return array.OfType<JsonObject>().Any(item => string.Equals(item.ToJsonString(), candidateJson, StringComparison.Ordinal));
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        if (Path.IsPathRooted(relativePath)) return false;

        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(segment => segment != "." && segment != "..");
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
        AddEngineIfValid(result, inferredProjectEngineRoot, "Project History", null);
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
        if (!IsValidUnrealEngineRoot(root)) return;
        result.Add(new EngineInfo(GetEngineVersion(root), root, source, association, GetEngineBuildId(root)));
    }

    private static bool IsValidUnrealEngineRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        var editorExe = Path.Combine(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), "Engine", "Binaries", "Win64", "UnrealEditor.exe");
        return File.Exists(editorExe);
    }

    private static string? TryInferProjectEngineRoot(string? projectDirectory)
    {
        if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory)) return null;

        foreach (var candidate in EnumerateProjectEngineRootCandidates(projectDirectory))
        {
            var normalizedCandidate = NormalizePotentialEngineRoot(candidate);
            if (IsValidUnrealEngineRoot(normalizedCandidate)) return normalizedCandidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateProjectEngineRootCandidates(string projectDirectory)
    {
        var savedDir = Path.Combine(projectDirectory, "Saved");
        var explicitConfig = Path.Combine(savedDir, "Config", "WindowsEditor", "EditorPerProjectUserSettings.ini");
        if (File.Exists(explicitConfig))
        {
            foreach (var candidate in ReadEngineRootCandidatesFromTextFile(explicitConfig))
                yield return candidate;
        }

        var crashesDir = Path.Combine(savedDir, "Crashes");
        if (!Directory.Exists(crashesDir)) yield break;

        foreach (var crashContext in Directory.EnumerateFiles(crashesDir, "CrashContext.runtime-xml", SearchOption.AllDirectories).Take(20))
        {
            foreach (var candidate in ReadEngineRootCandidatesFromTextFile(crashContext))
                yield return candidate;
        }
    }

    private static IEnumerable<string> ReadEngineRootCandidatesFromTextFile(string filePath)
    {
        string content;
        try { content = File.ReadAllText(filePath); }
        catch { yield break; }

        foreach (Match match in Regex.Matches(content, @"(?im)^\s*(?:Project|RootDir)\s*=\s*(.+?)\s*$"))
            yield return match.Groups[1].Value;

        foreach (Match match in Regex.Matches(content, @"(?is)<RootDir>\s*(.+?)\s*</RootDir>"))
            yield return match.Groups[1].Value;

        foreach (Match match in Regex.Matches(content, @"(?im)^\s*BaseDir\s*=\s*(.+?)\s*$"))
            yield return ConvertBaseDirToEngineRoot(match.Groups[1].Value);

        foreach (Match match in Regex.Matches(content, @"(?is)<BaseDir>\s*(.+?)\s*</BaseDir>"))
            yield return ConvertBaseDirToEngineRoot(match.Groups[1].Value);
    }

    private static string NormalizePotentialEngineRoot(string value)
    {
        var normalized = value.Trim().Trim('"', '\'').Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.GetFullPath(normalized);
    }

    private static string ConvertBaseDirToEngineRoot(string value)
    {
        var normalized = NormalizePotentialEngineRoot(value);
        var win64 = new DirectoryInfo(normalized);
        var binaries = win64.Parent;
        var engine = binaries?.Parent;
        return engine?.Parent?.FullName ?? normalized;
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
                var patch = node?["PatchVersion"]?.GetValue<int>();
                if (major.HasValue && minor.HasValue)
                    return patch.HasValue ? $"{major}.{minor}.{patch}" : $"{major}.{minor}";
            }
            catch { }
        }

        var folderName = Path.GetFileName(root);
        var match = System.Text.RegularExpressions.Regex.Match(folderName, @"(\d+[\._]\d+(?:[\._]\d+)?)");
        return match.Success ? match.Groups[1].Value.Replace('_', '.') : "Unknown";
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
        if (!string.IsNullOrWhiteSpace(inferredProjectEngineRoot))
        {
            var normalizedInferredRoot = NormalizeFullPathForCompare(inferredProjectEngineRoot);
            best = engineCombo.Items.OfType<EngineInfo>()
                .FirstOrDefault(engine => string.Equals(NormalizeFullPathForCompare(engine.Root), normalizedInferredRoot, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(detectedEngineVersion))
        {
            foreach (EngineInfo engine in engineCombo.Items)
            {
                if (best != null) break;
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
        suppressEngineReleaseRefresh = true;
        engineCombo.SelectedItem = best;
        suppressEngineReleaseRefresh = false;
        if (best != null)
        {
            enginePathBox.Text = best.Root;
            UpdateEngineStatus(best.Root);
        }
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
        if (!suppressEngineReleaseRefresh)
            _ = RefreshReleasesAsync();
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

        var releaseEngineVersion = GetReleaseEngineVersion();
        var compatibleReleases = releases
            .Where(release => IsCompatibleRelease(release.Tag, release.AssetName))
            .OrderByDescending(release => release.Tag, StringComparer.OrdinalIgnoreCase)
            .ThenBy(release => release.AssetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var release in compatibleReleases)
            releaseCombo.Items.Add(release);

        if (releaseCombo.Items.Count > 0) releaseCombo.SelectedIndex = 0;
        UpdateReadinessState();
        Log($"版本列表刷新完成：{releaseCombo.Items.Count} 个候选 ZIP（来源：{source}）。{(releaseEngineVersion == null ? "未检测引擎版本，显示全部。" : $"已按 UE {releaseEngineVersion} 过滤。")}");
    }

    private async Task RefreshInstallerUpdateAsync(bool showLog)
    {
        installerUpdateAvailable = false;
        installerLatestDownloadUrl = string.Empty;
        installerCheckButton.IsEnabled = false;
        installerCheckButton.Content = "检查中";
        installerUpdateButton.IsEnabled = false;
        installerUpdateButton.Content = "更新";
        installerUpdateStatusText.Text = "检查中";
        installerLatestVersionText.Text = "获取中";

        try
        {
            var release = await FetchLatestInstallerReleaseAsync();
            installerLatestVersion = release.Tag;
            installerLatestReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? InstallerReleaseHtmlUrl : release.HtmlUrl;
            installerLatestDownloadUrl = release.DownloadUrl;
            installerLatestVersionText.Text = FormatInstallerVersion(installerLatestVersion);

            var comparison = CompareInstallerVersions(installerLatestVersion, installerCurrentVersion);
            if (comparison > 0)
            {
                installerUpdateAvailable = true;
                installerUpdateStatusText.Text = "有新版";
                installerUpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(93, 226, 255));
                installerUpdateButton.Content = "更新";
                installerUpdateButton.IsEnabled = true;
                installerCheckButton.Content = "检查";
                installerCheckButton.IsEnabled = true;
                if (showLog) Log($"发现 TAPythonInstaller 新版本：{installerLatestVersion}（当前：{installerCurrentVersion}）。");
            }
            else if (comparison == 0)
            {
                installerUpdateStatusText.Text = "已最新";
                installerUpdateStatusText.Foreground = StepPendingForeground;
                installerUpdateButton.IsEnabled = false;
                installerCheckButton.Content = "检查";
                installerCheckButton.IsEnabled = true;
                if (showLog) Log($"TAPythonInstaller 已是最新版本：{installerCurrentVersion}。");
            }
            else
            {
                installerUpdateStatusText.Text = "本地较新";
                installerUpdateStatusText.Foreground = StepPendingForeground;
                installerUpdateButton.IsEnabled = false;
                installerCheckButton.Content = "检查";
                installerCheckButton.IsEnabled = true;
                if (showLog) Log($"当前 TAPythonInstaller 版本高于远端最新版本：{installerCurrentVersion} > {installerLatestVersion}。");
            }
        }
        catch (Exception ex)
        {
            installerLatestVersion = "unknown";
            installerLatestDownloadUrl = string.Empty;
            installerLatestVersionText.Text = "检查失败";
            installerUpdateStatusText.Text = "失败";
            installerUpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 154, 94));
            installerCheckButton.Content = "重试";
            installerCheckButton.IsEnabled = true;
            installerUpdateButton.Content = "更新";
            installerUpdateButton.IsEnabled = false;
            if (showLog) Log($"检查 TAPythonInstaller 更新失败：{ex.Message}");
        }
    }

    private async Task<InstallerReleaseInfo> FetchLatestInstallerReleaseAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, InstallerReleaseApiUrl);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("远端 Release 响应为空");
        var tag = release["tag_name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidOperationException("远端 Release 缺少 tag_name");
        var htmlUrl = release["html_url"]?.GetValue<string>() ?? InstallerReleaseHtmlUrl;
        var assets = release["assets"]?.AsArray();
        var downloadUrl = assets?
            .Select(asset => new
            {
                Name = asset?["name"]?.GetValue<string>() ?? string.Empty,
                Url = asset?["browser_download_url"]?.GetValue<string>() ?? string.Empty
            })
            .FirstOrDefault(asset => string.Equals(asset.Name, "TAPythonInstaller.exe", StringComparison.OrdinalIgnoreCase))
            ?.Url;
        if (string.IsNullOrWhiteSpace(downloadUrl)) throw new InvalidOperationException("远端 Release 缺少 TAPythonInstaller.exe 资产");
        return new InstallerReleaseInfo(tag, htmlUrl, downloadUrl);
    }

    private async Task UpdateInstallerAsync()
    {
        if (string.IsNullOrWhiteSpace(installerLatestDownloadUrl))
        {
            MessageBox.Show(this, "远端发行版缺少可下载的 TAPythonInstaller.exe，请稍后重试。", "无法更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currentExePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExePath) || !File.Exists(currentExePath))
        {
            MessageBox.Show(this, "无法定位当前运行的 TAPythonInstaller.exe。", "无法更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var currentExeDirectory = Path.GetDirectoryName(currentExePath);
        if (string.IsNullOrWhiteSpace(currentExeDirectory) || !CanWriteToDirectory(currentExeDirectory))
        {
            MessageBox.Show(this, "当前程序所在目录不可写，请将 TAPythonInstaller.exe 放到可写目录后再更新。", "无法更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        installerUpdateButton.IsEnabled = false;
        installerCheckButton.IsEnabled = false;
        installerUpdateButton.Content = "下载中";
        installerUpdateStatusText.Text = "下载中";

        try
        {
            Log($"开始下载 TAPythonInstaller {installerLatestVersion} 更新包...");
            var updateExePath = await DownloadInstallerUpdateAsync(installerLatestDownloadUrl, installerLatestVersion);
            Log($"更新包下载完成：{updateExePath}");
            installerUpdateStatusText.Text = "准备重启";
            installerUpdateButton.Content = "重启中";
            LaunchInstallerUpdater(updateExePath, currentExePath);
            Log("已启动自更新替换流程，程序即将退出并重启。 ");
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            installerUpdateStatusText.Text = "更新失败";
            installerUpdateStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 154, 94));
            installerUpdateButton.Content = "更新";
            installerUpdateButton.IsEnabled = true;
            installerCheckButton.Content = "检查";
            installerCheckButton.IsEnabled = true;
            Log($"TAPythonInstaller 自更新失败：{ex.Message}");
            MessageBox.Show(this, $"自更新失败：{ex.Message}", "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task<string> DownloadInstallerUpdateAsync(string downloadUrl, string version)
    {
        var updateDirectory = Path.Combine(Path.GetTempPath(), "TAPythonInstaller", "SelfUpdate", NormalizeVersionText(version));
        Directory.CreateDirectory(updateDirectory);
        var updateExePath = Path.Combine(updateDirectory, "TAPythonInstaller.exe");
        if (File.Exists(updateExePath)) File.Delete(updateExePath);

        using var response = await httpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync();
        await using var output = File.Create(updateExePath);
        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            readTotal += read;
            if (!total.HasValue) continue;
            var percent = (int)Math.Clamp(readTotal * 100 / total.Value, 0, 100);
            installerUpdateStatusText.Text = $"下载 {percent}%";
        }

        return updateExePath;
    }

    private static void LaunchInstallerUpdater(string updateExePath, string targetExePath)
    {
        var updaterDirectory = Path.Combine(Path.GetTempPath(), "TAPythonInstaller", "SelfUpdate");
        Directory.CreateDirectory(updaterDirectory);
        var scriptPath = Path.Combine(updaterDirectory, $"update-{Guid.NewGuid():N}.ps1");
        var script = "param([int]$ProcessId,[string]$SourcePath,[string]$TargetPath,[string]$RelaunchPath,[string]$ScriptPath)\r\n" +
                     "$ErrorActionPreference='Stop'\r\n" +
                     "try { Wait-Process -Id $ProcessId -Timeout 60 } catch {}\r\n" +
                     "$updated=$false\r\n" +
                     "for ($i=0; $i -lt 80; $i++) {\r\n" +
                     "  try { Copy-Item -LiteralPath $SourcePath -Destination $TargetPath -Force; $updated=$true; break }\r\n" +
                     "  catch { Start-Sleep -Milliseconds 500 }\r\n" +
                     "}\r\n" +
                     "if (-not $updated) { exit 1 }\r\n" +
                     "$markerDirectory = Join-Path $env:LOCALAPPDATA 'TAPythonInstaller'\r\n" +
                     "New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null\r\n" +
                     "Set-Content -LiteralPath (Join-Path $markerDirectory 'show-changelog.flag') -Value '1' -Encoding ASCII\r\n" +
                     "Start-Process -FilePath $RelaunchPath -ArgumentList @('--show-changelog')\r\n" +
                     "Start-Sleep -Seconds 2\r\n" +
                     "Remove-Item -LiteralPath $SourcePath -Force -ErrorAction SilentlyContinue\r\n" +
                     "Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue\r\n";
        File.WriteAllText(scriptPath, script, Encoding.ASCII);

        var process = Process.GetCurrentProcess();
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(process.Id.ToString());
        startInfo.ArgumentList.Add(updateExePath);
        startInfo.ArgumentList.Add(targetExePath);
        startInfo.ArgumentList.Add(targetExePath);
        startInfo.ArgumentList.Add(scriptPath);
        Process.Start(startInfo);
    }

    private static bool CanWriteToDirectory(string directory)
    {
        try
        {
            var probePath = Path.Combine(directory, $".tapython-update-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, string.Empty);
            File.Delete(probePath);
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

    private static string FormatInstallerVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "unknown", StringComparison.OrdinalIgnoreCase)) return "v--";
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    private static int CompareInstallerVersions(string latestVersion, string currentVersion)
    {
        var latest = TryParseVersion(latestVersion);
        var current = TryParseVersion(currentVersion);
        if (latest == null || current == null)
            return string.Compare(NormalizeVersionText(latestVersion), NormalizeVersionText(currentVersion), StringComparison.OrdinalIgnoreCase);
        return latest.CompareTo(current);
    }

    private static Version? TryParseVersion(string version)
    {
        var match = Regex.Match(version, @"\d+(?:\.\d+){0,3}");
        if (!match.Success) return null;
        var parts = match.Value.Split('.').ToList();
        while (parts.Count < 3) parts.Add("0");
        return Version.TryParse(string.Join('.', parts), out var parsed) ? parsed : null;
    }

    private static string NormalizeVersionText(string version)
        => version.Trim().TrimStart('v', 'V');

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
        var engineVersion = GetReleaseEngineVersion();
        if (string.IsNullOrWhiteSpace(engineVersion)) return true;

        var version = engineVersion.Trim();
        if (version.StartsWith("{", StringComparison.Ordinal)) return true;
        var underscoreVersion = version.Replace(".", "_");
        return assetName.Contains(version, StringComparison.OrdinalIgnoreCase) ||
               assetName.Contains(underscoreVersion, StringComparison.OrdinalIgnoreCase);
    }

    private string? GetReleaseEngineVersion()
    {
        if (engineCombo.SelectedItem is EngineInfo selectedEngine && !string.IsNullOrWhiteSpace(selectedEngine.Version))
            return selectedEngine.Version;

        var engineRoot = enginePathBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(engineRoot) && Directory.Exists(engineRoot))
            return GetEngineVersion(engineRoot);

        if (string.IsNullOrWhiteSpace(detectedEngineVersion)) return null;
        return detectedEngineVersion.StartsWith("{", StringComparison.Ordinal) ? null : detectedEngineVersion.Trim();
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
            InstallSelectedBundledAgentSkills();

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

        if (IsCurrentProjectRunning())
        {
            currentProjectIsRunning = true;
            UpdateReadinessState();
            MessageBox.Show("当前项目正在 Unreal Editor 中运行。请先关闭项目，再卸载 TAPython。", "项目正在运行", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        var projectToolDescriptions = ReadProjectToolDescriptions(projectPythonDir);
        var projectToolVersions = ReadProjectToolVersions(projectPythonDir);
        foreach (var dir in Directory.EnumerateDirectories(projectPythonDir, "*", SearchOption.TopDirectoryOnly)
                     .Where(d => !string.Equals(Path.GetFileName(d), "__pycache__", StringComparison.OrdinalIgnoreCase)))
        {
            var toolName = Path.GetFileName(dir);
            if (IsBuiltInTapythonTool(toolName)) continue;

            if (Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                .Any(IsTapythonToolFile))
            {
                var relativePath = Path.GetRelativePath(projectPythonDir, dir);
                tools.Add(new TapythonToolInfo(toolName, "目录", relativePath, ResolveToolDescription(toolName, projectToolDescriptions, ReadToolDescriptionFromMenuConfig(dir)), ResolveToolVersion(toolName, projectToolVersions, ReadToolVersionFromManifest(dir))));
            }
        }

        foreach (var file in Directory.EnumerateFiles(projectPythonDir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsTapythonToolFile))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, "__init__.py", StringComparison.OrdinalIgnoreCase)) continue;
            var toolName = Path.GetFileNameWithoutExtension(file);
            if (IsBuiltInTapythonTool(toolName)) continue;

            var relativePath = Path.GetRelativePath(projectPythonDir, file);
            tools.Add(new TapythonToolInfo(toolName, "文件", relativePath, ResolveToolDescription(toolName, projectToolDescriptions, "未提供工具说明"), ResolveToolVersion(toolName, projectToolVersions, ReadToolVersionFromManifest(file))));
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

    private static bool IsBuiltInTapythonTool(string toolName)
        => BuiltInTapythonToolNames.Contains(toolName);

    private static string ResolveToolDescription(string toolName, IReadOnlyDictionary<string, string> projectToolDescriptions, string fallback)
        => (projectToolDescriptions.TryGetValue(toolName, out var description) || projectToolDescriptions.TryGetValue(NormalizeToolIdentity(toolName), out description)) && !string.IsNullOrWhiteSpace(description)
            ? description
            : fallback;

    private static string ResolveToolVersion(string toolName, IReadOnlyDictionary<string, string> projectToolVersions, string fallback)
        => (projectToolVersions.TryGetValue(toolName, out var version) || projectToolVersions.TryGetValue(NormalizeToolIdentity(toolName), out version)) && !string.IsNullOrWhiteSpace(version)
            ? version
            : fallback;

    private static Dictionary<string, string> ReadProjectToolDescriptions(string projectPythonDir)
    {
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tapythonRoot = Directory.GetParent(projectPythonDir)?.FullName;
        if (string.IsNullOrWhiteSpace(tapythonRoot)) return descriptions;

        ReadInstalledHubToolDescriptions(tapythonRoot, descriptions);

        var menuConfigPath = Path.Combine(tapythonRoot, "UI", "MenuConfig.json");
        if (!File.Exists(menuConfigPath)) return descriptions;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(menuConfigPath));
            CollectToolDescriptionsFromMenuConfig(root, descriptions);
        }
        catch
        {
            return descriptions;
        }

        return descriptions;
    }

    private static Dictionary<string, string> ReadProjectToolVersions(string projectPythonDir)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tapythonRoot = Directory.GetParent(projectPythonDir)?.FullName;
        if (string.IsNullOrWhiteSpace(tapythonRoot)) return versions;

        ReadInstalledHubToolVersions(tapythonRoot, versions);
        return versions;
    }

    private static void ReadInstalledHubToolDescriptions(string tapythonRoot, Dictionary<string, string> descriptions)
    {
        var metadataPath = Path.Combine(tapythonRoot, "ToolHubInstalled.json");
        if (!File.Exists(metadataPath)) return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            if (root?["tools"] is not JsonArray tools) return;

            foreach (var toolNode in tools.OfType<JsonObject>())
            {
                var description = TryGetStringProperty(toolNode, "description");
                if (string.IsNullOrWhiteSpace(description)) continue;
                AddToolDescriptionAlias(descriptions, TryGetStringProperty(toolNode, "slug"), description);
                AddToolDescriptionAlias(descriptions, TryGetStringProperty(toolNode, "name"), description);
                AddToolDescriptionAlias(descriptions, TryGetStringProperty(toolNode, "displayName"), description);
            }
        }
        catch
        {
            return;
        }
    }

    private static void ReadInstalledHubToolVersions(string tapythonRoot, Dictionary<string, string> versions)
    {
        var metadataPath = Path.Combine(tapythonRoot, "ToolHubInstalled.json");
        if (!File.Exists(metadataPath)) return;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(metadataPath)) as JsonObject;
            if (root?["tools"] is not JsonArray tools) return;

            foreach (var toolNode in tools.OfType<JsonObject>())
            {
                var version = TryGetStringProperty(toolNode, "version");
                if (string.IsNullOrWhiteSpace(version)) continue;
                AddToolVersionAlias(versions, TryGetStringProperty(toolNode, "slug"), version);
                AddToolVersionAlias(versions, TryGetStringProperty(toolNode, "name"), version);
                AddToolVersionAlias(versions, TryGetStringProperty(toolNode, "displayName"), version);
            }
        }
        catch
        {
            return;
        }
    }

    private static void AddToolDescriptionAlias(Dictionary<string, string> descriptions, string? toolName, string description)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(description)) return;
        descriptions.TryAdd(toolName, description.Trim());

        var normalized = NormalizeToolIdentity(toolName);
        if (!string.IsNullOrWhiteSpace(normalized))
            descriptions.TryAdd(normalized, description.Trim());
    }

    private static void AddToolVersionAlias(Dictionary<string, string> versions, string? toolName, string version)
    {
        if (string.IsNullOrWhiteSpace(toolName) || string.IsNullOrWhiteSpace(version)) return;
        versions.TryAdd(toolName, NormalizeDisplayVersion(version));

        var normalized = NormalizeToolIdentity(toolName);
        if (!string.IsNullOrWhiteSpace(normalized))
            versions.TryAdd(normalized, NormalizeDisplayVersion(version));
    }

    private static string ReadToolVersionFromManifest(string toolPath)
    {
        var manifestPath = Directory.Exists(toolPath)
            ? Path.Combine(toolPath, "manifest.json")
            : Path.ChangeExtension(toolPath, ".manifest.json");
        if (!File.Exists(manifestPath)) return string.Empty;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject;
            if (root == null) return string.Empty;

            var toolNode = root["tool"] as JsonObject;
            var version = TryGetStringProperty(root, "version")
                          ?? (toolNode == null ? null : TryGetStringProperty(toolNode, "version"))
                          ?? TryGetStringProperty(root, "latestVersion");
            return NormalizeDisplayVersion(version);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeDisplayVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return string.Empty;
        var trimmed = version.Trim();
        return trimmed.StartsWith('v') || trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"v{trimmed}";
    }

    private static void CollectToolDescriptionsFromMenuConfig(JsonNode? node, Dictionary<string, string> descriptions)
    {
        if (node is JsonObject jsonObject)
        {
            var tooltip = TryGetStringProperty(jsonObject, "tooltip");
            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                foreach (var toolName in GetReferencedToolNames(jsonObject))
                {
                    if (!descriptions.ContainsKey(toolName))
                        descriptions[toolName] = tooltip.Trim();
                }
            }

            foreach (var property in jsonObject)
                CollectToolDescriptionsFromMenuConfig(property.Value, descriptions);
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
                CollectToolDescriptionsFromMenuConfig(item, descriptions);
        }
    }

    private static IEnumerable<string> GetReferencedToolNames(JsonObject jsonObject)
    {
        var chameleonToolPath = TryGetStringProperty(jsonObject, "ChameleonTools");
        if (!string.IsNullOrWhiteSpace(chameleonToolPath))
        {
            var toolName = GetToolNameFromChameleonPath(chameleonToolPath);
            if (!string.IsNullOrWhiteSpace(toolName)) yield return toolName;
        }

        var command = TryGetStringProperty(jsonObject, "command");
        if (string.IsNullOrWhiteSpace(command)) yield break;

        foreach (Match match in Regex.Matches(command, @"(?:from|import)\s+([A-Za-z_]\w*)"))
        {
            var moduleName = match.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(moduleName)) yield return moduleName;
        }
    }

    private static string? GetToolNameFromChameleonPath(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (string.Equals(segments[index], "Python", StringComparison.OrdinalIgnoreCase))
                return segments[index + 1];
        }

        return segments.Length >= 2
            ? segments[^2]
            : Path.GetFileNameWithoutExtension(path);
    }

    private static string? TryGetStringProperty(JsonObject jsonObject, string propertyName)
    {
        foreach (var property in jsonObject)
        {
            if (!string.Equals(property.Key, propertyName, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value is JsonValue value && value.TryGetValue<string>(out var text)
                ? text
                : property.Value?.ToJsonString().Trim('"');
        }

        return null;
    }

    private static string ReadToolDescriptionFromMenuConfig(string toolDirectory)
    {
        var menuConfigPath = Directory.EnumerateFiles(toolDirectory, "MenuConfig.json", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(toolDirectory, path).Count(ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (menuConfigPath == null) return "未提供工具说明";

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(menuConfigPath));
            return FindTooltipValue(root) ?? "未提供工具说明";
        }
        catch
        {
            return "未提供工具说明";
        }
    }

    private static string? FindTooltipValue(JsonNode? node)
    {
        if (node is JsonObject jsonObject)
        {
            foreach (var property in jsonObject)
            {
                if (string.Equals(property.Key, "tooltip", StringComparison.OrdinalIgnoreCase))
                {
                    var tooltip = property.Value is JsonValue value && value.TryGetValue<string>(out var text)
                        ? text
                        : property.Value?.ToJsonString().Trim('"');
                    if (!string.IsNullOrWhiteSpace(tooltip)) return tooltip.Trim();
                }

                var nestedTooltip = FindTooltipValue(property.Value);
                if (!string.IsNullOrWhiteSpace(nestedTooltip)) return nestedTooltip;
            }
        }
        else if (node is JsonArray jsonArray)
        {
            foreach (var item in jsonArray)
            {
                var nestedTooltip = FindTooltipValue(item);
                if (!string.IsNullOrWhiteSpace(nestedTooltip)) return nestedTooltip;
            }
        }

        return null;
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

    private void InstallSelectedBundledAgentSkills()
    {
        var installed = 0;
        if (installTapythonGeneratorSkillBox.IsChecked == true)
        {
            InstallBundledAgentSkill("tapython-generator", "Skills.tapython-generator.zip");
            installed++;
        }

        if (installUeApiNavigatorSkillBox.IsChecked == true)
        {
            InstallBundledAgentSkill("ue-api-navigator", "Skills.ue-api-navigator.zip");
            installed++;
        }

        if (installed == 0)
            Log("未选择 Agent Skills，跳过 Skill 部署。 ");
    }

    private void InstallBundledAgentSkill(string skillName, string resourceName)
    {
        var skillsRoot = GetUserCopilotSkillsRoot();
        var targetSkillDir = Path.Combine(skillsRoot, skillName);

        if (Directory.Exists(targetSkillDir))
        {
            Log($"用户级 Skill 已存在，保持不变：{skillName} -> {targetSkillDir}");
            return;
        }

        Directory.CreateDirectory(targetSkillDir);
        using var resourceStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"安装器缺少内置 Skill 资源：{resourceName}");

        using var archive = new ZipArchive(resourceStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) continue;

            var safeRelativePath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (!IsSafeRelativePath(safeRelativePath))
                throw new InvalidOperationException($"Skill 包含不安全路径：{entry.FullName}");

            var targetPath = Path.GetFullPath(Path.Combine(targetSkillDir, safeRelativePath));
            if (!IsPathInsideDirectory(targetPath, targetSkillDir))
                throw new InvalidOperationException($"Skill 解压路径超出目标目录：{entry.FullName}");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }

        var skillFile = Path.Combine(targetSkillDir, "SKILL.md");
        if (!File.Exists(skillFile))
            throw new InvalidOperationException($"Skill 部署后未找到 SKILL.md：{targetSkillDir}");

        Log($"已部署用户级 Skill：{skillName} -> {targetSkillDir}");
    }

    private static string GetUserCopilotSkillsRoot()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, CopilotSkillsRelativePath);
    }

    private void ValidateInstall(string targetPluginDir)
    {
        var expectedPythonPath = GetUnrealPythonPath(Path.Combine(projectDirectory!, "TA", "TAPython", "Python"));
        var checks = new Dictionary<string, bool>
        {
            ["TAPython.uplugin"]          = File.Exists(Path.Combine(targetPluginDir, "TAPython.uplugin")),
            ["Binaries/Win64"]            = Directory.Exists(Path.Combine(targetPluginDir, "Binaries", "Win64")),
            ["DefaultEngine.ini Python Path"] = File.ReadAllText(Path.Combine(projectDirectory!, "Config", "DefaultEngine.ini"))
                                               .Contains(expectedPythonPath, StringComparison.OrdinalIgnoreCase),
            ["Skill tapython-generator"]  = installTapythonGeneratorSkillBox.IsChecked != true || File.Exists(Path.Combine(GetUserCopilotSkillsRoot(), "tapython-generator", "SKILL.md")),
            ["Skill ue-api-navigator"]    = installUeApiNavigatorSkillBox.IsChecked != true || File.Exists(Path.Combine(GetUserCopilotSkillsRoot(), "ue-api-navigator", "SKILL.md"))
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
        if (IsCurrentProjectRunning())
        {
            currentProjectIsRunning = true;
            UpdateReadinessState();
            MessageBox.Show(this, "当前项目已经在 Unreal Editor 中运行。请先关闭项目，再重新打开。", "项目正在运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (File.Exists(uprojectPath))
            Process.Start(new ProcessStartInfo(uprojectPath) { UseShellExecute = true });
    }

    private void RefreshProjectRuntimeState()
    {
        var isRunning = IsCurrentProjectRunning();
        if (currentProjectIsRunning == isRunning) return;

        currentProjectIsRunning = isRunning;
        UpdateReadinessState();
    }

    private bool IsCurrentProjectRunning()
    {
        if (string.IsNullOrWhiteSpace(uprojectPath) || !File.Exists(uprojectPath)) return false;

        var projectName = Path.GetFileNameWithoutExtension(uprojectPath);
        if (string.IsNullOrWhiteSpace(projectName)) return false;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!process.ProcessName.StartsWith("UnrealEditor", StringComparison.OrdinalIgnoreCase)) continue;
                var title = process.MainWindowTitle;
                if (!string.IsNullOrWhiteSpace(title) && title.Contains(projectName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Ignore processes that exit or deny access while the UI status refreshes.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private void UpdateReadinessState()
    {
        var hasProject = !string.IsNullOrWhiteSpace(projectDirectory) && File.Exists(uprojectPath);
        var hasEngine = !string.IsNullOrWhiteSpace(enginePathBox.Text) && Directory.Exists(enginePathBox.Text);
        var hasSource = !string.IsNullOrWhiteSpace(localZipPath) || releaseCombo.SelectedItem is ReleaseInfo;
        var target = projectInstallRadio.IsChecked == true ? "项目 Plugins" : "引擎 Marketplace";
        var hasInstalledTarget = hasProject && HasTapythonInCurrentTarget();
        var projectRunning = hasProject && currentProjectIsRunning;

        RefreshInstalledStatus();
        projectCheckText.Text = hasProject ? (projectRunning ? "运行中" : "已选择") : "未选择";
        engineCheckText.Text = hasEngine ? "已选择" : "未选择";
        sourceCheckText.Text = hasSource ? (!string.IsNullOrWhiteSpace(localZipPath) ? "本地 ZIP" : "远程 Release") : "未选择";
        targetCheckText.Text = target;
        heroTargetText.Text = target;
        openProjectButton.Content = projectRunning ? "正在运行" : "打开项目";
        openProjectButton.IsEnabled = hasProject && !projectRunning;
        openProjectButton.ToolTip = projectRunning ? "当前项目正在 Unreal Editor 中运行，关闭后可重新打开" : null;
        uninstallButton.IsEnabled = hasInstalledTarget && !projectRunning;
        uninstallButton.ToolTip = projectRunning ? "当前项目正在 Unreal Editor 中运行，关闭后才可卸载 TAPython" : null;

        projectHeroChip.Text = hasProject ? (projectRunning ? "项目运行中" : "项目已就绪") : "项目待选择";
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
        pipelineStatusText.Text = projectRunning ? "当前项目正在运行，退出 UE 后可打开或卸载" : $"目标：{target}";
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

    private sealed record InstallerReleaseInfo(string Tag, string HtmlUrl, string DownloadUrl);

    private sealed record ReleaseCache(DateTimeOffset UpdatedAt, List<ReleaseInfo> Releases);

    private sealed record TapythonToolInfo(string Name, string Kind, string RelativePath, string Description, string Version)
    {
        public string VersionText => string.IsNullOrWhiteSpace(Version) ? "版本未知" : NormalizeDisplayVersion(Version);
    }

    private sealed record HubPackageValidationResult(string PackagePath, long PackageSize, string Sha256, string StatusText);

    private sealed record HubInstallPlan(string InstallPath, string ResolvedPath, int FileCount, JsonArray? MenuItems, List<string> RiskNotes, List<string> PreChecks, List<string> PostSteps);

    private sealed record HubInstallLayout(string InstallDirectory, string ExtractionRoot, string PackageRootPrefix);

    private sealed record HubInstalledToolMetadata(string Slug, string Name, string DisplayName, string Version, string TargetRoot, string BackupRoot, string PackageSha256, string InstalledAt);

    private sealed record HubInstallOperation(string ActionName, string Description, string WorkingText, string ProgressButtonText, string BackupOperation);

    private sealed record HubProjectPreflightResult(List<string> Passed, List<string> Warnings, List<string> Blockers)
    {
        public int WarningCount => Warnings.Count;
        public int BlockerCount => Blockers.Count;
    }

    private sealed record HubInstallHealthCheckResult(List<string> Passed, List<string> Warnings, List<string> Missing)
    {
        public bool HasIssues => Warnings.Count > 0 || Missing.Count > 0;
    }

    private sealed record HubLayoutRepairResult(bool Changed, string BackupRoot, string Message);

    private sealed record HubPackageImpact(bool HasManifest, bool ProjectSelected, int SafeFileCount, int UnsafePathCount, int NewFileCount, int OverwriteFileCount, int SkippedEntryCount, List<string> ImpactSamples)
    {
        public string ManifestText => HasManifest ? "manifest.json 已存在" : "缺少 manifest.json";
    }

    private sealed record HubPackageInstallResult(int WrittenFileCount, int OverwrittenPathCount, int SkippedEntryCount, IReadOnlyList<string> WrittenFiles, IReadOnlyList<string> OverwrittenPaths);

    private sealed record HubPreviewState(string Title, string Description, string BadgeText, string Background, string Border, string Foreground);

    private sealed record ToolPackageFileDescriptor(string Path, string Sha256, long Size, string Role);

    private sealed record ToolPackageDescriptor(
        ToolPackageSourceKind SourceKind,
        string Slug,
        string Name,
        string DisplayName,
        string Version,
        string Description,
        string TargetPathTemplate,
        string PythonRoot,
        string EntryJson,
        string MountPoint,
        IReadOnlyList<ToolPackageFileDescriptor> Files,
        JsonArray MenuEntries,
        JsonObject HotkeyEntries,
        IReadOnlyList<string> ExternalJson);

    private sealed class HubToolInfo
    {
        public string Slug { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public string Author { get; init; } = string.Empty;
        public string OwnerTeam { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string RiskLevel { get; init; } = string.Empty;
        public string LatestVersion { get; init; } = string.Empty;
        public string ApiUrl { get; init; } = string.Empty;
        public string PackageUrl { get; init; } = string.Empty;
        public string ManifestUrl { get; init; } = string.Empty;
        public string PackageSha256 { get; init; } = string.Empty;
        public long? PackageSize { get; init; }
        public bool PackageAvailable { get; init; }
        public bool IsInstalled { get; set; }
        public string InstalledVersion { get; set; } = string.Empty;
        public string InstalledTargetRoot { get; set; } = string.Empty;
        public string InstalledBackupRoot { get; set; } = string.Empty;
        public string InstalledPackageSha256 { get; set; } = string.Empty;
        public List<string> UnrealVersions { get; init; } = [];
        public List<string> Tags { get; init; } = [];
        public string SourcePath { get; init; } = string.Empty;
        public string RelativePath => ApiUrl;
        public string StatusText => Status.ToLowerInvariant() switch
        {
            "approved" => "已审核",
            "pending" => "待审核",
            "draft" => "草稿",
            "deprecated" => "已废弃",
            "archived" => "已归档",
            _ => Status
        };
        public bool IsManagedHubInstalled => !string.IsNullOrWhiteSpace(InstalledTargetRoot);
        public bool IsUpdateAvailable => IsManagedHubInstalled && HubVersionIsNewer(LatestVersion, InstalledVersion);
        public string HubListStatusText => IsUpdateAvailable ? "可更新" : IsInstalled ? "已安装" : StatusText;
        public string DetailStatusText => IsUpdateAvailable
            ? $"可更新 / {StatusText} / {RiskText}"
            : IsInstalled ? $"已安装 / {StatusText} / {RiskText}" : $"{StatusText} / {RiskText}";
        public string RiskText => RiskLevel.ToLowerInvariant() switch
        {
            "low" => "低风险",
            "medium" => "中风险",
            "high" => "高风险",
            _ => RiskLevel
        };
        public string CompatibilitySummary => UnrealVersions.Count == 0 ? "UE 未声明" : $"UE {string.Join(", ", UnrealVersions)}";
        public string TagsText => string.Join("  ", Tags);
        public string PackageSizeText => FormatFileSize(PackageSize);
        public string PackageSha256Short => string.IsNullOrWhiteSpace(PackageSha256)
            ? "无 sha256"
            : PackageSha256.Length <= 12 ? PackageSha256 : PackageSha256[..12];
    }

    private sealed record NavigationItem(string PageKey, string Icon, string Title, string Subtitle);
}

public sealed class GridLengthAnimation : AnimationTimeline
{
    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From),
        typeof(GridLength),
        typeof(GridLengthAnimation));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To),
        typeof(GridLength),
        typeof(GridLengthAnimation));

    public GridLength From
    {
        get => (GridLength)GetValue(FromProperty);
        set => SetValue(FromProperty, value);
    }

    public GridLength To
    {
        get => (GridLength)GetValue(ToProperty);
        set => SetValue(ToProperty, value);
    }

    public IEasingFunction? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(GridLength);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
    {
        var progress = animationClock.CurrentProgress ?? 0;
        var easedProgress = EasingFunction?.Ease(progress) ?? progress;
        var fromValue = From.Value;
        var toValue = To.Value;
        return new GridLength(fromValue + ((toValue - fromValue) * easedProgress), GridUnitType.Pixel);
    }

    protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
}
