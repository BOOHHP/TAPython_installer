using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TAPythonInstaller;

public partial class Form1 : Form
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/cgerchenhp/UE_TAPython_Plugin_Release/releases";
    private const string DefaultSourceEngineRoot = @"D:\AntLibs\WS";

    private readonly HttpClient httpClient = new();

    private TextBox projectPathBox = null!;
    private TextBox enginePathBox = null!;
    private ComboBox engineCombo = null!;
    private ComboBox releaseCombo = null!;
    private Label projectStatusLabel = null!;
    private Label installedStatusLabel = null!;
    private Label engineStatusLabel = null!;
    private CheckBox autoEnablePythonBox = null!;
    private CheckBox autoEnableTapythonBox = null!;
    private CheckBox configurePythonPathBox = null!;
    private CheckBox fixBuildIdBox = null!;
    private CheckBox backupBox = null!;
    private RadioButton projectInstallRadio = null!;
    private RadioButton engineInstallRadio = null!;
    private ProgressBar progressBar = null!;
    private TextBox logBox = null!;
    private Button installButton = null!;
    private Button openProjectButton = null!;

    private string? projectDirectory;
    private string? uprojectPath;
    private string? detectedEngineVersion;
    private string? localZipPath;

    public Form1()
    {
        InitializeComponent();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TAPythonInstaller/1.0");
        BuildUi();
        ScanEngines();
    }

    private void BuildUi()
    {
        Text = "TAPython 快速安装器";
        Width = 1080;
        Height = 760;
        MinimumSize = new Size(760, 620);
        StartPosition = FormStartPosition.CenterScreen;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 7,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(root);

        var title = new Label
        {
            Text = "TAPython 快速安装器",
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 0, 10)
        };
        root.Controls.Add(title);

        root.Controls.Add(BuildProjectGroup());
        root.Controls.Add(BuildVersionGroup());
        root.Controls.Add(BuildOptionsGroup());
        root.Controls.Add(BuildLogGroup());

        progressBar = new ProgressBar { Dock = DockStyle.Top, Height = 18, Minimum = 0, Maximum = 100 };
        root.Controls.Add(progressBar);

        var actionPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 54,
            ColumnCount = 3,
            RowCount = 1,
            Margin = new Padding(0)
        };
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        installButton = new Button
        {
            Text = "一键安装 TAPython",
            Font = new Font(Font.FontFamily, 13, FontStyle.Bold),
            Width = 240,
            Height = 42,
            Anchor = AnchorStyles.Right | AnchorStyles.Top
        };
        installButton.Click += async (_, _) => await InstallAsync();
        openProjectButton = new Button { Text = "打开项目", Width = 120, Height = 42, Enabled = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        openProjectButton.Click += (_, _) => OpenProject();
        actionPanel.Controls.Add(openProjectButton, 1, 0);
        actionPanel.Controls.Add(installButton, 2, 0);
        root.Controls.Add(actionPanel);
    }

    private Control BuildProjectGroup()
    {
        var group = NewGroup("1. 项目与引擎");
        var grid = NewGrid(6);
        group.Controls.Add(grid);

        grid.Controls.Add(NewLabel("项目 .uproject"), 0, 0);
        projectPathBox = new TextBox { Dock = DockStyle.Fill };
        grid.Controls.Add(projectPathBox, 1, 0);
        var projectButton = new Button { Text = "浏览...", Width = 90 };
        projectButton.Click += (_, _) => BrowseProject();
        grid.Controls.Add(projectButton, 2, 0);

        projectStatusLabel = NewStatusLabel("请选择 .uproject 文件");
        grid.Controls.Add(projectStatusLabel, 1, 1);
        installedStatusLabel = NewStatusLabel("当前安装状态：未知");
        grid.Controls.Add(installedStatusLabel, 1, 2);

        grid.Controls.Add(NewLabel("引擎目录"), 0, 3);
        enginePathBox = new TextBox { Dock = DockStyle.Fill };
        grid.Controls.Add(enginePathBox, 1, 3);
        var engineButton = new Button { Text = "浏览...", Width = 90 };
        engineButton.Click += (_, _) => BrowseEngine();
        grid.Controls.Add(engineButton, 2, 3);

        grid.Controls.Add(NewLabel("已发现引擎"), 0, 4);
        engineCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        engineCombo.SelectedIndexChanged += (_, _) => SelectEngineFromCombo();
        grid.Controls.Add(engineCombo, 1, 4);
        var scanButton = new Button { Text = "扫描", Width = 90 };
        scanButton.Click += (_, _) => ScanEngines();
        grid.Controls.Add(scanButton, 2, 4);

        engineStatusLabel = NewStatusLabel("会扫描 Epic Launcher 引擎、注册表 Source Builds，以及 D:\\AntLibs\\WS");
        grid.Controls.Add(engineStatusLabel, 1, 5);
        return group;
    }

    private Control BuildVersionGroup()
    {
        var group = NewGroup("2. TAPython 版本");
        var grid = NewGrid(2);
        group.Controls.Add(grid);

        grid.Controls.Add(NewLabel("远程版本"), 0, 0);
        releaseCombo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
        grid.Controls.Add(releaseCombo, 1, 0);
        var refreshButton = new Button { Text = "刷新列表", Width = 90 };
        refreshButton.Click += async (_, _) => await RefreshReleasesAsync();
        grid.Controls.Add(refreshButton, 2, 0);

        grid.Controls.Add(NewLabel("离线安装"), 0, 1);
        var localZipLabel = NewStatusLabel("未选择本地 ZIP；选择后将优先使用本地 ZIP");
        grid.Controls.Add(localZipLabel, 1, 1);
        var localZipButton = new Button { Text = "选择 ZIP", Width = 90 };
        localZipButton.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "TAPython ZIP (*.zip)|*.zip|All files (*.*)|*.*" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                localZipPath = dialog.FileName;
                localZipLabel.Text = localZipPath;
                Log($"已选择本地 ZIP：{localZipPath}");
            }
        };
        grid.Controls.Add(localZipButton, 2, 1);
        return group;
    }

    private Control BuildOptionsGroup()
    {
        var group = NewGroup("3. 安装选项");
        group.AutoSize = false;
        group.Height = 96;
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        group.Controls.Add(panel);

        autoEnablePythonBox = NewCheckBox("启用 PythonScriptPlugin", true);
        autoEnableTapythonBox = NewCheckBox("启用 TAPython 插件", true);
        configurePythonPathBox = NewCheckBox("配置 Python 附加路径", true);
        fixBuildIdBox = NewCheckBox("自动修复 BuildId", true);
        backupBox = NewCheckBox("覆盖前备份旧版本", true);
        projectInstallRadio = new RadioButton { Text = "安装到项目 Plugins/", AutoSize = true, Checked = true, Margin = new Padding(8) };
        engineInstallRadio = new RadioButton { Text = "安装到引擎 Plugins/Marketplace/", AutoSize = true, Margin = new Padding(8) };

        panel.Controls.Add(autoEnablePythonBox);
        panel.Controls.Add(autoEnableTapythonBox);
        panel.Controls.Add(configurePythonPathBox);
        panel.Controls.Add(fixBuildIdBox);
        panel.Controls.Add(backupBox);
        panel.Controls.Add(projectInstallRadio);
        panel.Controls.Add(engineInstallRadio);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = NewGroup("4. 安装日志");
        group.AutoSize = false;
        group.Dock = DockStyle.Fill;
        logBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            ReadOnly = true,
            Font = new Font("Consolas", 9),
            BackColor = Color.FromArgb(25, 25, 25),
            ForeColor = Color.Gainsboro
        };
        group.Controls.Add(logBox);
        return group;
    }

    private static GroupBox NewGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Top,
        AutoSize = true,
        Padding = new Padding(10),
        Margin = new Padding(0, 0, 0, 10)
    };

    private static TableLayoutPanel NewGrid(int rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = rows,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static Label NewLabel(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 12, 7) };
    private static Label NewStatusLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Height = 24,
        ForeColor = Color.DimGray,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(0, 5, 0, 5)
    };
    private static CheckBox NewCheckBox(string text, bool isChecked) => new() { Text = text, AutoSize = true, Checked = isChecked, Margin = new Padding(8) };

    private void BrowseProject()
    {
        using var dialog = new OpenFileDialog { Filter = "Unreal Project (*.uproject)|*.uproject" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
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
        _ = RefreshReleasesAsync();
    }

    private void BrowseEngine()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择 UE 引擎根目录，例如 C:\\Program Files\\Epic Games\\UE_5.5 或 D:\\AntLibs\\WS\\..." };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            enginePathBox.Text = dialog.SelectedPath;
            UpdateEngineStatus(dialog.SelectedPath);
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
        {
            engineCombo.Items.Add(engine);
        }

        Log($"扫描到 {engines.Count} 个 UE 引擎（含 Launcher / 注册表 / {DefaultSourceEngineRoot}）。");
        SelectBestEngineForProject();
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
                AddEngineIfValid(result, dir, "Epic Launcher");
            }
        }

        var epicDir = @"C:\Program Files\Epic Games";
        if (Directory.Exists(epicDir))
        {
            foreach (var dir in Directory.GetDirectories(epicDir, "UE_*"))
            {
                AddEngineIfValid(result, dir, "Epic Launcher");
            }
        }
    }

    private static void AddRegisteredSourceBuilds(List<EngineInfo> result)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
        if (key == null) return;
        foreach (var valueName in key.GetValueNames())
        {
            AddEngineIfValid(result, key.GetValue(valueName) as string, $"Source Build ({valueName})");
        }
    }

    private static void AddSourceWorkspaceEngines(List<EngineInfo> result, string workspaceRoot)
    {
        if (!Directory.Exists(workspaceRoot)) return;
        var candidates = Directory.EnumerateDirectories(workspaceRoot)
            .Concat(Directory.EnumerateDirectories(workspaceRoot).SelectMany(d => SafeEnumerateDirectories(d)))
            .Take(500);

        foreach (var dir in candidates)
        {
            AddEngineIfValid(result, dir, "AntLibs Source");
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    private static void AddEngineIfValid(List<EngineInfo> result, string? root, string source)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var editorExe = Path.Combine(root, "Engine", "Binaries", "Win64", "UnrealEditor.exe");
        if (!File.Exists(editorExe)) return;
        result.Add(new EngineInfo(GetEngineVersion(root), root, source, GetEngineBuildId(root)));
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
        foreach (EngineInfo engine in engineCombo.Items)
        {
            if (!string.IsNullOrWhiteSpace(detectedEngineVersion) &&
                (engine.Version == detectedEngineVersion || detectedEngineVersion.Contains(engine.Version, StringComparison.OrdinalIgnoreCase)))
            {
                best = engine;
                break;
            }
        }

        best ??= engineCombo.Items[0] as EngineInfo;
        engineCombo.SelectedItem = best;
    }

    private void SelectEngineFromCombo()
    {
        if (engineCombo.SelectedItem is not EngineInfo engine) return;
        enginePathBox.Text = engine.Root;
        UpdateEngineStatus(engine.Root);
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
            Log($"版本列表刷新完成：{releaseCombo.Items.Count} 个候选 ZIP。{(detectedEngineVersion == null ? "未检测项目版本，显示全部。" : $"已按 UE {detectedEngineVersion} 过滤。")}");
        }
        catch (Exception ex)
        {
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
            MessageBox.Show(this, "请先选择 .uproject 文件。", "缺少项目", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(enginePathBox.Text) && (fixBuildIdBox.Checked || engineInstallRadio.Checked))
        {
            MessageBox.Show(this, "请先选择引擎目录。", "缺少引擎", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(localZipPath) && releaseCombo.SelectedItem is not ReleaseInfo)
        {
            MessageBox.Show(this, "请刷新并选择 TAPython 版本，或选择本地 ZIP。", "缺少安装源", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            installButton.Enabled = false;
            progressBar.Value = 0;
            Log("开始安装 TAPython...");

            var zipPath = await ResolveZipAsync();
            var installRoot = projectInstallRadio.Checked
                ? Path.Combine(projectDirectory, "Plugins")
                : Path.Combine(enginePathBox.Text, "Engine", "Plugins", "Marketplace");

            Directory.CreateDirectory(installRoot);
            var targetPluginDir = Path.Combine(installRoot, "TAPython");
            if (Directory.Exists(targetPluginDir))
            {
                if (backupBox.Checked)
                {
                    var backupDir = targetPluginDir + "_backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    Directory.Move(targetPluginDir, backupDir);
                    Log($"已备份旧版本：{backupDir}");
                }
                else
                {
                    Directory.Delete(targetPluginDir, true);
                    Log("已删除旧版本 TAPython。 ");
                }
            }

            var extractRoot = Path.Combine(Path.GetTempPath(), "TAPythonInstaller", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);
            var sourcePluginDir = FindTapythonPluginDirectory(extractRoot);
            if (sourcePluginDir == null) throw new InvalidOperationException("ZIP 中未找到 TAPython.uplugin。请确认选择的是 TAPython 插件 ZIP。 ");

            CopyDirectory(sourcePluginDir, targetPluginDir);
            Log($"插件已安装到：{targetPluginDir}");

            if (autoEnablePythonBox.Checked || autoEnableTapythonBox.Checked) EnablePluginsInUProject();
            if (configurePythonPathBox.Checked) ConfigurePythonPath();
            if (fixBuildIdBox.Checked) FixBuildId(targetPluginDir, enginePathBox.Text);

            ValidateInstall(targetPluginDir);
            progressBar.Value = 100;
            openProjectButton.Enabled = true;
            MessageBox.Show(this, "TAPython 安装配置完成。建议重启 UE 编辑器后打开 Chameleon Gallery 检查 Python Path Ready。", "安装完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log($"安装失败：{ex}");
            MessageBox.Show(this, ex.Message, "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            installButton.Enabled = true;
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
                progressBar.Value = percent;
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

        if (autoEnablePythonBox.Checked) UpsertPlugin(plugins, "PythonScriptPlugin");
        if (autoEnableTapythonBox.Checked) UpsertPlugin(plugins, "TAPython");
        File.WriteAllText(uprojectPath!, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log(".uproject 已更新插件启用状态。 ");
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

        var content = File.ReadAllText(iniPath);
        var section = "[/Script/PythonScriptPlugin.PythonScriptPluginSettings]";
        var devMode = "bDeveloperMode=True";
        var pathLine = "+AdditionalPaths=(Path=\"TA/TAPython/Python\")";

        if (!content.Contains(section, StringComparison.OrdinalIgnoreCase))
        {
            content = content.TrimEnd() + Environment.NewLine + Environment.NewLine + section + Environment.NewLine + devMode + Environment.NewLine + pathLine + Environment.NewLine;
        }
        else
        {
            if (!content.Contains(devMode, StringComparison.OrdinalIgnoreCase)) content = InsertAfterSection(content, section, devMode);
            if (!content.Contains(pathLine, StringComparison.OrdinalIgnoreCase)) content = InsertAfterSection(content, section, pathLine);
        }

        File.WriteAllText(iniPath, content);
        Directory.CreateDirectory(Path.Combine(projectDirectory!, "TA", "TAPython", "Python"));
        Log("DefaultEngine.ini 已配置 Python 附加路径：TA/TAPython/Python");
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
            Log("未找到引擎 BuildId，跳过 BuildId 修复。 ");
            return;
        }

        var pluginModules = Path.Combine(targetPluginDir, "Binaries", "Win64", "UnrealEditor.modules");
        if (!File.Exists(pluginModules))
        {
            Log("未找到 TAPython 的 UnrealEditor.modules，跳过 BuildId 修复。 ");
            return;
        }

        var node = JsonNode.Parse(File.ReadAllText(pluginModules))!.AsObject();
        node["BuildId"] = buildId;
        File.WriteAllText(pluginModules, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        Log($"BuildId 已修复为：{buildId}");
    }

    private void ValidateInstall(string targetPluginDir)
    {
        var checks = new Dictionary<string, bool>
        {
            ["TAPython.uplugin"] = File.Exists(Path.Combine(targetPluginDir, "TAPython.uplugin")),
            ["Binaries/Win64"] = Directory.Exists(Path.Combine(targetPluginDir, "Binaries", "Win64")),
            ["DefaultEngine.ini Python Path"] = File.ReadAllText(Path.Combine(projectDirectory!, "Config", "DefaultEngine.ini")).Contains("TA/TAPython/Python", StringComparison.OrdinalIgnoreCase)
        };

        foreach (var check in checks)
        {
            Log($"校验 {(check.Value ? "通过" : "失败")}：{check.Key}");
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
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
        if (File.Exists(uprojectPath)) Process.Start(new ProcessStartInfo(uprojectPath) { UseShellExecute = true });
    }

    private void Log(string message)
    {
        if (InvokeRequired)
        {
            Invoke(() => Log(message));
            return;
        }
        logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private sealed record EngineInfo(string Version, string Root, string Source, string? BuildId)
    {
        public override string ToString() => $"UE {Version} · {Source} · {Root}";
    }

    private sealed record ReleaseInfo(string Tag, string Name, string AssetName, string DownloadUrl)
    {
        public override string ToString() => $"{Tag} · {AssetName}";
    }
}
