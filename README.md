# TAPython Installer

[中文](#中文说明) | [English](#english)

---

## 中文说明

### 简介

TAPython Installer 是一款面向 **Unreal Engine 5** 用户的 Windows 桌面安装器，帮助你通过可视化界面一键完成 [TAPython](https://github.com/cgerchenhp/UE_TAPython_Plugin_Release) 插件的安装与配置，无需手动复制文件或编辑配置。

当前版本已迁移到 **WPF / .NET 8**，采用深色工作台界面：左侧流程轨道实时跟踪安装进度，主区域左侧负责项目、引擎和安装配置，右侧展示准备状态、安装进度、操作流程和可滚动安装日志，底部保留主操作按钮。应用背景采用连续科技流动动效。

> **推荐下载 `v1.10.14` 或更新版本。** `v1.5.0` 是首个支持应用内一键自更新的版本，安装过 `v1.5.0+` 后可直接在应用左下角 Installer 面板检查并更新到后续版本。新版会持续带来脚本工具管理、Agent Skills 部署、Tool Hub 浏览、v2 工具包导入/导出与安装等改进；详细变化请查看应用内更新日志或 Releases 页面。

### 功能特性

- **自动识别引擎**：扫描 Epic Games Launcher 安装的标准引擎、注册表中的源码引擎以及自定义引擎目录；当项目 `EngineAssociation` 为空时，会从项目 Saved 配置和 CrashContext 历史记录推断最近使用过的有效源码引擎
- **版本匹配**：通过 GitHub API / Releases 页面兜底 / 本地缓存获取 TAPython Release 列表，并按当前选中引擎的 UE 补丁版本自动筛选兼容版本
- **离线安装**：支持选择本地 ZIP 包，网络受限时也能正常使用
- **灵活安装位置**：支持安装到项目 `Plugins/` 或引擎 `Engine/Plugins/Marketplace/`
- **双路径安装检测**：选择 `.uproject` 后会同时检查项目 `Plugins` 与当前引擎 `Marketplace`，任一位置存在 TAPython 都会显示“已安装”，安装进度区同步显示“已安装”
- **一键卸载**：可从当前选择的项目或引擎安装位置卸载 TAPython，并保留项目中的用户 Python 脚本
- **自动配置**
  - 启用 `PythonScriptPlugin` / `TAPython` 插件
  - 配置 Python 附加路径
  - 部署 `tapython-generator` / `tapython-hub-publisher` / `ue-api-navigator` 到当前用户 `.copilot/skills`
  - 复制 TAPython 默认 Python 文件到项目 `TA/TAPython/Python`
  - 安装前检测项目中已有的 TA Python 脚本/工具；若已存在，则保留用户文件并仅补齐缺失的默认资源
  - 中文项目路径自动通过 ASCII 项目目录联接规避 UE Python 崩溃
  - 自动修复 BuildId（解决自编译引擎兼容性问题）
  - 安装前备份旧版本
- **安全卸载**
  - 卸载前展示将处理的插件目录、配置项和保留内容
  - 检测到当前项目正在 Unreal Editor 中运行时，会禁用“打开项目”和“卸载 TAPython”，退出项目后自动恢复
  - 卸载前检测 TAPython 二进制文件占用；若 `UnrealEditor-TAPython.dll` 仍被 UE 锁定，会提示关闭 UE 或残留 `UnrealEditor.exe` 后重试
  - 从 `.uproject` 中移除 `TAPython` 启用项，同时保留可能被其他工具使用的 `PythonScriptPlugin`
  - 从 `DefaultEngine.ini` 中移除安装器写入的 TAPython Python 附加路径
  - 默认保留项目 `TA/TAPython/Python` 下的用户脚本
  - 卸载时直接删除 TAPython 插件目录，不再备份插件，确保 UE 不会继续扫描到 TAPython
  - 自动删除旧版本留在 `Plugins/` 或 `Marketplace/` 中的 TAPython 备份目录，避免卸载后 UE 仍显示插件
- **深色安装工作台**
  - 自定义深色窗口标题栏，避免原生白色窗口边框割裂
  - 默认窗口尺寸调整为 `1350 x 790`，为安装配置、日志和脚本工具管理留出更宽的工作区
  - 安装诊断、准备状态、进度与操作流程分区展示
  - 安装日志支持自动换行、纵向滚动和历史记录查看
  - 左侧可折叠导航栏支持入口点击切换和平滑过渡，默认入口为“安装诊断”
  - “TAPython 脚本工具”入口内置“当前项目工具”和“TAPython 工具分享网站”两个分页，可点击或左右拖动切换
  - 安装页内部流程徽章（01 项目 → 02 版本 → 03 选项 → 04 安装）实时反映当前安装阶段
  - 左下角 Installer 更新面板显示当前版本、远端最新版本和更新状态，并提供主动检查、一键自更新与更新日志按钮
  - 更新日志按钮会在程序中央打开中文版本说明窗口；客户端检测到本机第一次打开当前版本时，会自动弹出更新日志窗口提示本次版本变化，并使用本地版本记录避免同一版本重复弹出
  - 科技流动动效背景：斜向流动光带、网格漂移层、横竖交叉扫描线持续循环
  - 专属 TA 字母应用图标，支持 16px 到 256px 多尺寸，任务栏与窗口标题栏同步显示
- **项目脚本工具管理**
  - 在“脚本工具”页扫描当前项目 `TA/TAPython/Python` 下的自定义工具，并默认忽略 TAPython 内置工具
  - 支持将选中工具导出为 TAPython Tool Package v2 `.tapython-tool.zip`，包内包含工具文件、v2 `manifest.json`、菜单项、快捷键项和 `ExternalJson` 引用信息
  - 导入工具包时自动校验结构、复制工具文件，并合并 `MenuConfig.json` / `HotkeyConfig.json` 中的相关配置；导入流程兼容 v2 工具包和旧版项目工具包
  - 新增 **上传工具** 按钮，可直接打开 Tool Hub 网站的“提交或发布工具”页面；上传与审核继续在网站后台完成
  - 支持将外部 `.zip` / `.tapython-tool.zip` 工具包直接拖入“当前项目工具”区域完成导入，拖拽导入复用按钮导入的校验、覆盖确认和备份流程
  - 删除工具前自动备份工具目录、`MenuConfig.json` 和 `HotkeyConfig.json`，随后移除对应菜单与快捷键引用
  - 当前项目工具列表会在工具名称旁显示当前版本；Hub 托管工具优先读取安装记录，本地工具可读取 `manifest.json`
- **TAPython Tool Hub 浏览**
  - “TAPython 工具分享网站”分页直接读取 Tool Hub 内网 API：`http://10.2.13.8:8787/api/tools`
  - 支持按名称/说明/作者/标签搜索，并按分类、风险等级、审核状态和当前 UE 兼容性筛选
  - 展示工具版本、作者团队、兼容 UE、包体积、sha256 摘要、标签和说明
  - 支持读取安装计划模板，下载工具 ZIP 到本地缓存并校验 SHA256，预览目标路径、文件数量、MenuConfig 新增项、风险提示和安装后操作；新版 Tool Hub 下载包可直接采用 TAPython Tool Package v2 manifest 作为安装事实源
  - 安装预览会扫描 ZIP 结构并统计新增、覆盖、跳过和不安全路径；确认后可将工具安装到当前项目，并自动备份覆盖文件与 UI 配置
  - 预览结果会显示通过、警告、阻止或失败状态；可点击“重新下载”清理缓存包并重新校验
  - 安装预览新增项目预检，安装完成后会验证工具目录、入口文件和 MenuConfig 引用；已安装工具可点击“修复安装”整理旧版双层目录布局
  - 已安装的 Hub 工具会根据安装记录识别本地版本，支持更新、重新安装和卸载；卸载会复用“当前项目工具”删除逻辑，先备份工具目录与 UI 配置，再移除菜单、快捷键和 Hub 安装记录
  - 从“当前项目工具”页删除 Hub 托管工具时，会同步清理 Tool Hub 安装记录并刷新 Hub 列表状态
  - Tool Hub 安装、更新、重装、卸载和修复都会在预览区输出结构化结果摘要，方便核对写入、覆盖、删除、备份和配置变更
- **Agent Skills 部署**
  - 安装器内置 `tapython-generator`、`tapython-hub-publisher` 与 `ue-api-navigator` 三个 Skill，无需目标用户提前安装
  - 在“安装配置 → Agent Skills”中勾选后，会部署到当前 Windows 用户的 `.copilot/skills/<skill-name>`
  - 若目标目录已有同名 Skill，安装器会保持用户现有 Skill 不变并跳过部署
- **压缩发布包**：Release 单文件 EXE 启用 .NET 单文件压缩，继续保持自包含免安装运行时，同时将发行包体积从约 155 MB 降至约 69 MB
- **桌面运行保护**：同一时间只允许打开一个 `TAPythonInstaller.exe` 实例，避免重复操作同一项目或安装目录

### 系统要求

- Windows 10 / 11
- Unreal Engine 5.x 项目

### 使用方法

1. 从 [Releases](https://github.com/BOOHHP/TAPython_installer/releases) 页面下载最新的 `TAPythonInstaller.exe`
2. 运行程序，选择你的 UE 项目（`.uproject` 文件）
3. 选择要安装的引擎版本与 TAPython 版本
4. 勾选所需选项，点击 **一键安装 TAPython**；如勾选 Agent Skills，安装器会同步部署 `tapython-generator`、`tapython-hub-publisher` 与 `ue-api-navigator` 到当前用户 `.copilot/skills`；如果项目中已有 `TA/TAPython/Python` 脚本，安装器会先识别并在日志中列出当前已有工具，同时避免覆盖用户文件
5. 如需卸载，选择对应安装位置后点击 **卸载 TAPython**，确认后程序会直接删除插件目录、清理安装器写入的配置，并保留项目 Python 脚本
6. 左下角 Installer 面板会自动检查安装器自身版本；也可以点击 **检查** 主动复查，检测到新版后点击 **更新** 下载新版、替换当前程序并自动重启；点击 **更新日志** 可查看中文版本说明
7. 在 **脚本工具** 页选择当前项目工具后，可使用 **导入 / 导出 / 删除** 管理项目侧 TAPython 工具；导入和删除会先备份相关工具与配置文件

### 从源码构建

需要 .NET 8 SDK。

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

### 发布单文件 EXE

项目默认将发布产物输出到仓库根目录的 `dist/` 文件夹。该目录是本地构建产物，默认不会提交到 Git。

Release 发布会自动启用单文件压缩和关闭调试符号输出，以减小 `TAPythonInstaller.exe` 体积，同时保留自包含运行能力。

```bash
dotnet publish TAPythonInstaller/TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### 许可证

MIT

---

## English

### Overview

TAPython Installer is a Windows GUI installer for **Unreal Engine 5** users that enables one-click installation and configuration of the [TAPython](https://github.com/cgerchenhp/UE_TAPython_Plugin_Release) plugin — no manual file copying or config editing required.

The current version has been migrated to **WPF / .NET 8** with a dark workstation-style interface: a dynamic left step rail tracks install progress in real time; project, engine, and setup options are on the left; readiness, progress, workflow, and scrollable install logs are on the right; primary actions stay at the bottom. The background features a continuous tech-flow animation.

> **Recommended download: `v1.10.14` or newer.** `v1.5.0` is the first release with in-app one-click self-update support; once you have installed `v1.5.0+`, you can check for and install later versions directly from the bottom-left Installer panel. Newer releases continue to improve script tool management, Agent Skills deployment, Tool Hub browsing, v2 package import/export, and installation; see the in-app changelog or Releases page for details.

### Features

- **Auto engine detection**: Scans for Epic Games Launcher engines, registry-registered source builds, and custom engine directories; when project `EngineAssociation` is empty, it can infer the last valid source engine from project Saved config and CrashContext history
- **Version matching**: Fetches TAPython releases via GitHub API, falls back to the Releases page and local cache, then filters by the currently selected engine's UE patch version automatically
- **Offline install**: Supports selecting a local ZIP package when network access is limited
- **Flexible install target**: Install to project `Plugins/` or engine `Engine/Plugins/Marketplace/`
- **Dual-path install detection**: After selecting a `.uproject`, the installer checks both project `Plugins` and the current engine `Marketplace`; TAPython in either location is shown as installed, and the progress panel also displays **Installed**
- **One-click uninstall**: Remove TAPython from the currently selected project or engine target while preserving project-side user Python scripts
- **Auto configuration**
  - Enable `PythonScriptPlugin` / `TAPython` in project descriptor
  - Configure Python additional paths
  - Deploy `tapython-generator` / `tapython-hub-publisher` / `ue-api-navigator` into the current user's `.copilot/skills`
  - Copy TAPython default Python files into project `TA/TAPython/Python`
  - Detect existing TA Python scripts/tools before install; when existing files are found, preserve user files and only add missing default resources
  - Create an ASCII project directory junction for non-ASCII project paths to avoid UE Python crashes
  - Auto-fix BuildId for source-built engines
  - Backup existing plugin before overwriting
- **Safe uninstall**
  - Shows the plugin directory, config changes, and preserved content before uninstalling
  - Disables **Open Project** and **Uninstall TAPython** while the current project is already running in Unreal Editor, then restores the actions after the project exits
  - Detects TAPython binary file locks before uninstall; if `UnrealEditor-TAPython.dll` is still held by UE, it prompts the user to close UE or any residual `UnrealEditor.exe` process and retry
  - Removes `TAPython` from `.uproject` while keeping `PythonScriptPlugin` intact for other tools
  - Removes the TAPython Python additional path written by the installer from `DefaultEngine.ini`
  - Preserves user scripts under project `TA/TAPython/Python` by default
  - Uninstall directly deletes the TAPython plugin folder without backup so UE no longer detects TAPython
  - Automatically deletes old TAPython backup folders left under `Plugins/` or `Marketplace/` so UE no longer detects them after uninstall
- **Dark installer workspace**
  - Custom dark window chrome to avoid a mismatched native white title bar
  - Default window size is now `1350 x 790`, giving install options, logs, and script-tool management more workspace
  - Separate areas for diagnostics, readiness, progress, workflow, and actions
  - Install log supports line wrapping, vertical scrolling, and history review
  - Collapsible left navigation supports click-to-switch with smooth transitions; the default entry is Install Diagnostics
  - **TAPython Script Tools** includes two tabs: current project tools and TAPython Tool Hub resources; switch by clicking tabs or dragging horizontally
  - Install-page step badges (01 Project → 02 Version → 03 Options → 04 Install) reflect the current install phase live
  - Bottom-left Installer update panel shows the current version, latest remote version, and update status, with manual check, one-click self-update, and changelog buttons
  - The changelog button opens a centered Chinese release-notes window; when the client detects that the current version is being launched for the first time on the machine, it opens the changelog automatically and records the version to avoid repeated popups for the same version
  - Animated tech-flow background: diagonal light bands, drifting grid layer, and cross scan lines running in continuous loops
  - Dedicated **TA** letter app icon with multi-size support (16 px to 256 px), shown in taskbar and title bar
- **Project script tool management**
  - Scan custom tools under project `TA/TAPython/Python` while ignoring TAPython built-in tools by default
  - Export the selected tool as a TAPython Tool Package v2 `.tapython-tool.zip` package containing tool files, a v2 `manifest.json`, menu entries, hotkey entries, and `ExternalJson` reference metadata
  - Import tool packages with structure validation, tool file extraction, and `MenuConfig.json` / `HotkeyConfig.json` config merging; the import flow supports both v2 packages and legacy project-tool packages
  - Use the new **Upload Tool** button to open the Tool Hub website's submit/publish page directly; upload and review stay in the website workflow
  - Drag external `.zip` / `.tapython-tool.zip` packages directly onto **Current Project Tools** to import them with the same validation, overwrite confirmation, and backup flow as the import button
  - Back up the tool folder, `MenuConfig.json`, and `HotkeyConfig.json` before deleting a tool, then remove related menu and hotkey references
  - The current project tools list shows each tool version next to its name; Hub-managed tools read install metadata first, and local tools can read `manifest.json`
- **TAPython Tool Hub browsing**
  - The **TAPython Tool Hub** tab reads the Tool Hub intranet API directly from `http://10.2.13.8:8787/api/tools`
  - Search by name, description, author, or tags, and filter by category, risk level, review status, and current UE compatibility
  - Shows version, author/team, UE compatibility, package size, sha256 summary, tags, and description
  - Reads install-plan templates, downloads the tool ZIP into a local cache, verifies SHA256, and previews target path, file count, MenuConfig additions, risk notes, and post-install steps; newer Tool Hub downloads can use the TAPython Tool Package v2 manifest as the installation source of truth
  - The install preview scans ZIP structure and summarizes new, overwritten, skipped, and unsafe paths; after confirmation, tools can be installed into the current project with overwritten files and UI configs backed up automatically
  - Preview results show pass, warning, blocked, or failed states; use **Redownload** to clear the cached package and validate again
  - Install preview now includes project preflight checks; completed installs validate the tool directory, entry files, and MenuConfig references, and installed tools can use **Repair Install** to clean up legacy double-nested layouts
  - Installed Hub tools are tracked by local metadata and now support update, reinstall, and uninstall; uninstall mirrors the **Current Project Tools** delete flow by backing up the tool folder and UI configs before removing menu entries, hotkeys, and the Hub install record
  - Deleting a Hub-managed tool from **Current Project Tools** also clears the Tool Hub install record and refreshes the Hub list state
  - Tool Hub install, update, reinstall, uninstall, and repair operations write structured summaries into the preview panel so file writes, overwrites, deletes, backups, and config changes are easy to verify
- **Agent Skills deployment**
  - Bundles `tapython-generator`, `tapython-hub-publisher`, and `ue-api-navigator` so target users do not need to install them beforehand
  - When selected in **Install Config → Agent Skills**, deploys them to `.copilot/skills/<skill-name>` for the current Windows user
  - Existing same-name skills are left untouched; the installer skips deployment for those skills
- **Compressed release package**: Release single-file EXE builds enable .NET single-file compression, keeping the app self-contained while reducing the package from about 155 MB to about 69 MB
- **Desktop runtime guard**: Only one `TAPythonInstaller.exe` instance can run at a time to avoid duplicate operations on the same project or install target

### Requirements

- Windows 10 / 11
- Unreal Engine 5.x project

### Usage

1. Download the latest `TAPythonInstaller.exe` from the [Releases](https://github.com/BOOHHP/TAPython_installer/releases) page
2. Run the program and select your UE project (`.uproject` file)
3. Choose the target engine and TAPython version
4. Select desired options and click **Install TAPython**; when Agent Skills are checked, the installer also deploys `tapython-generator`, `tapython-hub-publisher`, and `ue-api-navigator` to the current user's `.copilot/skills`; if the project already contains `TA/TAPython/Python` scripts, the installer detects them, lists the current tools in the log, and avoids overwriting user files
5. To uninstall, choose the matching install target and click **Uninstall TAPython**; the app directly deletes the plugin folder, removes installer-written config, and preserves project Python scripts
6. The bottom-left Installer panel checks the app version automatically; click **Check** to refresh manually, then **Update** to download the new executable, replace the current app, and restart automatically; click **Changelog** to view Chinese release notes
7. In **Script Tools**, select a current project tool and use **Import / Export / Delete** to manage project-side TAPython tools; import and delete operations back up related tool and config files first

### Build from Source

Requires .NET 8 SDK.

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

### Publish Single-file EXE

The project publishes output to the repository-level `dist/` folder by default. This folder is a local build artifact and is ignored by Git.

Release publishing enables single-file compression and disables debug symbol output automatically to reduce `TAPythonInstaller.exe` size while keeping the app self-contained.

```bash
dotnet publish TAPythonInstaller/TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### License

MIT
