# TAPython Installer

[中文](#中文说明) | [English](#english)

---

## 中文说明

### 简介

TAPython Installer 是一款面向 **Unreal Engine 5** 用户的 Windows 桌面安装器，帮助你通过可视化界面一键完成 [TAPython](https://github.com/cgerchenhp/UE_TAPython_Plugin_Release) 插件的安装与配置，无需手动复制文件或编辑配置。

当前版本已迁移到 **WPF / .NET 8**，采用深色工作台界面：左侧流程轨道实时跟踪安装进度，主区域左侧负责项目、引擎和安装配置，右侧展示准备状态、安装进度、操作流程和可滚动安装日志，底部保留主操作按钮。应用背景采用连续科技流动动效。

### 功能特性

- **自动识别引擎**：扫描 Epic Games Launcher 安装的标准引擎、注册表中的源码引擎以及自定义引擎目录
- **版本匹配**：通过 GitHub API / Releases 页面兜底 / 本地缓存获取 TAPython Release 列表，并按项目 UE 版本自动筛选兼容版本
- **离线安装**：支持选择本地 ZIP 包，网络受限时也能正常使用
- **灵活安装位置**：支持安装到项目 `Plugins/` 或引擎 `Engine/Plugins/Marketplace/`
- **一键卸载**：可从当前选择的项目或引擎安装位置卸载 TAPython，并保留项目中的用户 Python 脚本
- **自动配置**
  - 启用 `PythonScriptPlugin` / `TAPython` 插件
  - 配置 Python 附加路径
  - 复制 TAPython 默认 Python 文件到项目 `TA/TAPython/Python`
  - 安装前检测项目中已有的 TA Python 脚本/工具；若已存在，则保留用户文件并仅补齐缺失的默认资源
  - 中文项目路径自动通过 ASCII 项目目录联接规避 UE Python 崩溃
  - 自动修复 BuildId（解决自编译引擎兼容性问题）
  - 安装前备份旧版本
- **安全卸载**
  - 卸载前展示将处理的插件目录、配置项和保留内容
  - 从 `.uproject` 中移除 `TAPython` 启用项，同时保留可能被其他工具使用的 `PythonScriptPlugin`
  - 从 `DefaultEngine.ini` 中移除安装器写入的 TAPython Python 附加路径
  - 默认保留项目 `TA/TAPython/Python` 下的用户脚本
  - 卸载时直接删除 TAPython 插件目录，不再备份插件，确保 UE 不会继续扫描到 TAPython
  - 自动删除旧版本留在 `Plugins/` 或 `Marketplace/` 中的 TAPython 备份目录，避免卸载后 UE 仍显示插件
- **深色安装工作台**
  - 自定义深色窗口标题栏，避免原生白色窗口边框割裂
  - 安装诊断、准备状态、进度与操作流程分区展示
  - 安装日志支持自动换行、纵向滚动和历史记录查看
  - 左侧可折叠导航栏支持入口点击切换和上下拖动排序，默认入口为“安装诊断”
  - “TAPython 脚本工具”入口内置“当前项目工具”和“TAPython 工具分享网站”两个分页，可点击或左右拖动切换
  - 安装页内部流程徽章（01 项目 → 02 版本 → 03 选项 → 04 安装）实时反映当前安装阶段
  - 科技流动动效背景：斜向流动光带、网格漂移层、横竖交叉扫描线持续循环
  - 专属 TA 字母应用图标，支持 16px 到 256px 多尺寸，任务栏与窗口标题栏同步显示

### 系统要求

- Windows 10 / 11
- Unreal Engine 5.x 项目

### 使用方法

1. 从 [Releases](https://github.com/BOOHHP/TAPython_installer/releases) 页面下载最新的 `TAPythonInstaller.exe`
2. 运行程序，选择你的 UE 项目（`.uproject` 文件）
3. 选择要安装的引擎版本与 TAPython 版本
4. 勾选所需选项，点击 **一键安装 TAPython**；如果项目中已有 `TA/TAPython/Python` 脚本，安装器会先识别并在日志中列出当前已有工具，同时避免覆盖用户文件
5. 如需卸载，选择对应安装位置后点击 **卸载 TAPython**，确认后程序会直接删除插件目录、清理安装器写入的配置，并保留项目 Python 脚本

### 从源码构建

需要 .NET 8 SDK。

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

### 发布单文件 EXE

项目默认将发布产物输出到仓库根目录的 `dist/` 文件夹。该目录是本地构建产物，默认不会提交到 Git。

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

### Features

- **Auto engine detection**: Scans for Epic Games Launcher engines, registry-registered source builds, and custom engine directories
- **Version matching**: Fetches TAPython releases via GitHub API, falls back to the Releases page and local cache, then filters by your project's UE version automatically
- **Offline install**: Supports selecting a local ZIP package when network access is limited
- **Flexible install target**: Install to project `Plugins/` or engine `Engine/Plugins/Marketplace/`
- **One-click uninstall**: Remove TAPython from the currently selected project or engine target while preserving project-side user Python scripts
- **Auto configuration**
  - Enable `PythonScriptPlugin` / `TAPython` in project descriptor
  - Configure Python additional paths
  - Copy TAPython default Python files into project `TA/TAPython/Python`
  - Detect existing TA Python scripts/tools before install; when existing files are found, preserve user files and only add missing default resources
  - Create an ASCII project directory junction for non-ASCII project paths to avoid UE Python crashes
  - Auto-fix BuildId for source-built engines
  - Backup existing plugin before overwriting
- **Safe uninstall**
  - Shows the plugin directory, config changes, and preserved content before uninstalling
  - Removes `TAPython` from `.uproject` while keeping `PythonScriptPlugin` intact for other tools
  - Removes the TAPython Python additional path written by the installer from `DefaultEngine.ini`
  - Preserves user scripts under project `TA/TAPython/Python` by default
  - Uninstall directly deletes the TAPython plugin folder without backup so UE no longer detects TAPython
  - Automatically deletes old TAPython backup folders left under `Plugins/` or `Marketplace/` so UE no longer detects them after uninstall
- **Dark installer workspace**
  - Custom dark window chrome to avoid a mismatched native white title bar
  - Separate areas for diagnostics, readiness, progress, workflow, and actions
  - Install log supports line wrapping, vertical scrolling, and history review
  - Collapsible left navigation supports click-to-switch and vertical drag reordering; the default entry is Install Diagnostics
  - **TAPython Script Tools** includes two tabs: current project tools and TAPython Tool Hub resources; switch by clicking tabs or dragging horizontally
  - Install-page step badges (01 Project → 02 Version → 03 Options → 04 Install) reflect the current install phase live
  - Animated tech-flow background: diagonal light bands, drifting grid layer, and cross scan lines running in continuous loops
  - Dedicated **TA** letter app icon with multi-size support (16 px to 256 px), shown in taskbar and title bar

### Requirements

- Windows 10 / 11
- Unreal Engine 5.x project

### Usage

1. Download the latest `TAPythonInstaller.exe` from the [Releases](https://github.com/BOOHHP/TAPython_installer/releases) page
2. Run the program and select your UE project (`.uproject` file)
3. Choose the target engine and TAPython version
4. Select desired options and click **Install TAPython**; if the project already contains `TA/TAPython/Python` scripts, the installer detects them, lists the current tools in the log, and avoids overwriting user files
5. To uninstall, choose the matching install target and click **Uninstall TAPython**; the app directly deletes the plugin folder, removes installer-written config, and preserves project Python scripts

### Build from Source

Requires .NET 8 SDK.

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

### Publish Single-file EXE

The project publishes output to the repository-level `dist/` folder by default. This folder is a local build artifact and is ignored by Git.

```bash
dotnet publish TAPythonInstaller/TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### License

MIT
