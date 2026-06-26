# TAPython Installer

[中文](#中文说明) | [English](#english)

---

## 中文说明

### 简介

TAPython Installer 是一款面向 **Unreal Engine 5** 用户的 Windows 桌面安装器（WPF / .NET 8），通过可视化界面一键完成 [TAPython](https://github.com/cgerchenhp/UE_TAPython_Plugin_Release) 插件的安装、配置、卸载与工具管理，无需手动复制文件或编辑配置。

> 推荐下载最新版本。`v1.5.0+` 支持应用内一键自更新，详细版本变化见应用内更新日志或 [Releases](https://github.com/BOOHHP/TAPython_installer/releases) 页面。

### 核心功能

- **一键安装 / 卸载**：自动启用插件、配置 Python 路径、修复源码引擎 BuildId、安装前备份；卸载时清理插件目录与配置并保留用户 Python 脚本
- **引擎自动识别**：扫描 Launcher 引擎、注册表源码引擎、自定义目录，支持从项目历史与 AntAgent 包管理器推断引擎路径
- **版本智能匹配**：通过 GitHub API（含网页与本地缓存兜底）拉取 TAPython Release，按引擎 UE 版本自动筛选兼容版本
- **镜像加速下载**：插件下载与安装器自更新优先走 GitHub 镜像加速，镜像不可用时自动回退直连；也支持本地 ZIP 离线安装
- **记住上次项目**：启动时自动加载上次打开的 `.uproject`，免去重复选择
- **双安装位置**：支持项目 `Plugins/` 与引擎 `Engine/Plugins/Marketplace/`，两处均会检测安装状态；任一位置已安装 TAPython 时“一键安装 TAPython”按钮自动置灰
- **项目脚本工具管理**：扫描、导入、导出（Tool Package v2）、删除项目侧 TAPython 工具，支持拖拽导入，操作前自动备份
- **AI 发布桥接**：为当前项目工具生成 Copilot 发布上下文、v2 包和 `tapython-hub-publisher` 请求，支持回读发布报告并提交 Tool Hub 审核队列
- **Tool Hub 浏览与安装**：内置 Tool Hub 客户端，支持搜索筛选、SHA256 校验、安装预览、更新 / 重装 / 卸载 / 修复
- **Agent Skills 部署**：内置 `tapython-generator` / `tapython-hub-publisher` / `ue-api-navigator`，可一键部署到当前用户 `.copilot/skills`
- **中文路径兼容**：中文项目路径自动通过 ASCII 目录联接规避 UE Python 崩溃
- **应用内自更新**：左下角面板检查新版、一键下载替换并自动重启，附中文更新日志
- **深色工作台界面**：流程轨道、状态化主按钮、分级安装日志、可折叠导航与科技流动背景；单实例运行保护

### 系统要求

- Windows 10 / 11
- Unreal Engine 5.x 项目

### 使用方法

1. 从 [Releases](https://github.com/BOOHHP/TAPython_installer/releases) 下载最新 `TAPythonInstaller.exe` 并运行（自包含，免安装运行时）
2. 选择 UE 项目（`.uproject`），程序会自动识别引擎并筛选兼容的 TAPython 版本
3. 勾选所需安装选项，点击 **一键安装 TAPython**
4. 卸载、工具管理、Tool Hub 安装与安装器自更新均可在对应界面内完成

### 从源码构建

需要 .NET 8 SDK。

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

发布单文件 EXE（输出到 `dist/`，该目录不入库）：

```bash
dotnet publish TAPythonInstaller/TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### 许可证

MIT

---

## English

### Overview

TAPython Installer is a Windows desktop installer (WPF / .NET 8) for **Unreal Engine 5** users, providing one-click installation, configuration, uninstall, and tool management for the [TAPython](https://github.com/cgerchenhp/UE_TAPython_Plugin_Release) plugin — no manual file copying or config editing required.

> Recommended: download the latest version. `v1.5.0+` supports in-app one-click self-update; see the in-app changelog or the [Releases](https://github.com/BOOHHP/TAPython_installer/releases) page for version details.

### Key Features

- **One-click install / uninstall**: auto-enables plugins, configures Python paths, fixes BuildId for source builds, backs up before overwriting; uninstall cleans plugin folders and config while preserving user Python scripts
- **Auto engine detection**: scans Launcher engines, registry source builds, and custom directories; infers engine paths from project history and the AntAgent package manager
- **Smart version matching**: fetches TAPython releases via GitHub API (with HTML and local-cache fallbacks) and filters by engine UE version
- **Mirror-accelerated downloads**: plugin downloads and installer self-update try GitHub mirror proxies first with automatic fallback to direct connection; local ZIP offline install also supported
- **Remembers last project**: automatically reloads the last opened `.uproject` on startup
- **Dual install targets**: project `Plugins/` or engine `Engine/Plugins/Marketplace/`, with install status detected in both; the **Install TAPython** button is disabled when TAPython is already present in either location
- **Project script tool management**: scan, import, export (Tool Package v2), and delete project-side TAPython tools, with drag-and-drop import and automatic backups
- **AI publishing bridge**: generates a Copilot publishing context, v2 package, and `tapython-hub-publisher` request for the selected project tool, then reads publish reports and submits Tool Hub review packages
- **Tool Hub browsing & install**: built-in Tool Hub client with search/filter, SHA256 verification, install preview, update / reinstall / uninstall / repair
- **Agent Skills deployment**: bundles `tapython-generator` / `tapython-hub-publisher` / `ue-api-navigator`, deployable to the current user's `.copilot/skills`
- **Non-ASCII path compatibility**: ASCII directory junction workaround for UE Python crashes with non-ASCII project paths
- **In-app self-update**: bottom-left panel checks, downloads, replaces, and restarts automatically, with Chinese release notes
- **Dark workstation UI**: step rail, state-aware primary action, categorized install logs, collapsible navigation, animated tech-flow background; single-instance guard

### Requirements

- Windows 10 / 11
- Unreal Engine 5.x project

### Usage

1. Download the latest `TAPythonInstaller.exe` from [Releases](https://github.com/BOOHHP/TAPython_installer/releases) and run it (self-contained, no runtime install needed)
2. Select your UE project (`.uproject`); the installer detects the engine and filters compatible TAPython versions
3. Choose install options and click **Install TAPython**
4. Uninstall, tool management, Tool Hub installs, and installer self-update are all available in the corresponding panels

### Build from Source

Requires .NET 8 SDK.

```bash
git clone https://github.com/BOOHHP/TAPython_installer.git
cd TAPython_installer
dotnet build TAPythonInstaller.sln
```

Publish a single-file EXE (output to `dist/`, not tracked by Git):

```bash
dotnet publish TAPythonInstaller/TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### License

MIT
