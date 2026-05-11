# TAPython Installer 开发日志

## 2026-05-11

### WPF 工作台导航与脚本工具页优化

本轮重点完善左侧导航和 `TAPython 脚本工具` 页面，让安装器从单页安装面板扩展为可继续承载工具管理能力的工作台界面。

#### 导航栏交互

- 移除左侧导航入口拖拽排序逻辑，改为点击入口进行页面切换。
- 为导航页面增加淡入与横向滑动过渡，避免页面切换时生硬闪烁。
- 增加预留入口占位页面，保证未实现入口也有明确的切换反馈。
- 完善导航栏收起/展开按钮：
  - 支持 216px 与 72px 宽度之间的平滑动画。
  - 收起态隐藏品牌文字和底部提示，只保留窄栏入口。
  - 修复按钮位于 `WindowChrome` 标题栏区域导致点击事件被拦截的问题。

#### 按钮与焦点视觉

- 修复按钮点击后蓝色边框持续残留的问题。
- 将按钮焦点视觉改为独立 `FocusVisualStyle`，鼠标点击不再污染常态边框，同时保留键盘导航可访问性。

#### 脚本工具页

- 新增 `当前项目扫描到的工具` 与 `TAPython 工具分享网站` 双分页。
- 当前项目工具扫描默认忽略 TAPython 原始自带脚本工具：
  - `ChameleonGallery`
  - `ChameleonSketch`
  - `Example`
  - `ImageCompareTools`
  - `QueryTools`
  - `ShelfTools`
  - `Utilities`
- 工具说明展示从目录名改为说明文本：
  - 优先读取项目级 `TA/TAPython/UI/MenuConfig.json`。
  - 根据 `ChameleonTools` 路径映射到对应工具目录。
  - 使用同一菜单项的 `tooltip` 作为工具说明。
  - 若项目级配置未命中，再回退读取工具目录内 `MenuConfig.json` 的 `tooltip`。
- 为工具列表新增独立深色选中态，避免复用左侧导航白底样式导致文字被遮挡。
- 支持点击工具列表或工具面板空白区域取消选中，并同步清除键盘焦点，避免蓝色高亮边框残留。

#### 仓库维护

- `.gitignore` 增加 `dev-logs/`，忽略本地运行产生的开发日志目录。

### 验证结果

- `dotnet build .\TAPythonInstaller.sln` 通过。
- `dotnet publish .\TAPythonInstaller\TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true` 通过。
- 发布产物 `dist\TAPythonInstaller.exe` 可正常启动，窗口标题初始化为 `TAPython 快速安装器`。

## 2026-05-09

### 背景

团队希望把 TAPython 的安装流程从脚本方案升级为面向 UE5 用户的一键快捷安装器，目标是让用户通过可视化界面完成以下操作：

- 选择 TAPython 下载版本
- 选择要安装的 UE 项目路径
- 选择或自动识别 UE 引擎路径
- 可选自动启用插件
- 一键安装 TAPython
- 安装完成后自动配置 Python 路径

团队环境同时存在两类 UE 引擎：

- Epic Games Launcher 安装的标准引擎
- 自编译源码引擎，通常位于 `D:\AntLibs\WS`

### 技术选型

采用 `.NET 8 WinForms` 开发 Windows 桌面安装器。

选择原因：

- 可直接发布为 Windows `.exe`
- 自包含发布后无需用户额外安装 .NET Runtime
- 对安装器类表单界面足够轻量
- 原生支持文件选择、进度条、日志窗口、注册表读取、HTTP 下载、ZIP 解压等能力

### 项目结构

```text
D:\Claude\project\TAPython_installer\
├── TAPythonInstaller.sln
├── TAPythonInstaller\
│   ├── TAPythonInstaller.csproj
│   ├── Program.cs
│   ├── Form1.cs
│   └── Form1.Designer.cs
└── dist\
    └── TAPythonInstaller.exe
```

### 已完成功能

#### 项目选择与识别

- 支持选择 `.uproject` 文件
- 自动读取 `.uproject` 中的 `EngineAssociation`
- 显示当前项目中是否已安装 `Plugins/TAPython/TAPython.uplugin`
- 显示当前已安装插件版本（读取 `VersionName`）

#### 引擎扫描

已实现三类引擎路径扫描：

- Epic Games Launcher 标准引擎
  - 注册表：`HKLM\SOFTWARE\EpicGames\Unreal Engine`
  - 注册表：`HKLM\SOFTWARE\WOW6432Node\EpicGames\Unreal Engine`
  - 默认目录：`C:\Program Files\Epic Games\UE_*`
- 注册表 Source Builds
  - `HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds`
- 团队源码引擎目录
  - `D:\AntLibs\WS`

扫描到引擎后会读取：

- 引擎版本：`Engine/Build/Build.version`
- BuildId：`Engine/Binaries/Win64/UnrealEditor.modules`

#### TAPython 版本选择

- 通过 GitHub API 拉取 TAPython Release 列表
- API 地址：`https://api.github.com/repos/cgerchenhp/UE_TAPython_Plugin_Release/releases`
- 自动筛选 `.zip` 资源
- 根据项目 UE 版本过滤候选 Release
- 支持选择本地 ZIP 进行离线安装，作为 GitHub 下载失败时的兜底方案

#### 安装选项

界面提供以下可选项：

- 启用 `PythonScriptPlugin`
- 启用 `TAPython`
- 配置 Python 附加路径
- 自动修复 BuildId
- 覆盖前备份旧版本
- 安装到项目 `Plugins/`
- 安装到引擎 `Engine/Plugins/Marketplace/`

#### 安装流程

点击“一键安装 TAPython”后执行：

1. 校验项目路径、引擎路径和安装源
2. 下载远程 ZIP 或读取本地 ZIP
3. 解压 ZIP 到临时目录
4. 自动查找 `TAPython.uplugin`
5. 安装到目标目录
6. 若目标已存在，根据选项备份或覆盖
7. 修改 `.uproject` 插件启用状态
8. 修改 `Config/DefaultEngine.ini`，写入 Python 路径
9. 修复 `UnrealEditor.modules` 中的 BuildId
10. 校验安装结果
11. 安装完成后允许一键打开项目

#### Python 路径配置

写入位置：

```ini
[/Script/PythonScriptPlugin.PythonScriptPluginSettings]
bDeveloperMode=True
+AdditionalPaths=(Path="TA/TAPython/Python")
```

同时会确保目录存在：

```text
<Project>/TA/TAPython/Python
```

#### BuildId 修复

实现逻辑：

1. 从选中引擎读取：
   `Engine/Binaries/Win64/UnrealEditor.modules`
2. 获取其中的 `BuildId`
3. 写入已安装 TAPython 的：
   `Plugins/TAPython/Binaries/Win64/UnrealEditor.modules`

用于规避 UE 启动时常见的 Missing Module 弹窗。

#### 安装验证

安装后检查：

- `TAPython.uplugin` 是否存在
- `Binaries/Win64` 是否存在
- `DefaultEngine.ini` 是否包含 `TA/TAPython/Python`

### 构建结果

已成功编译并发布为 Windows x64 自包含单文件 exe。

最终产物：

```text
D:\Claude\project\TAPython_installer\dist\TAPythonInstaller.exe
```

当前 exe 大小约：`68 MB`

构建命令：

```powershell
dotnet build .\TAPythonInstaller.sln -c Release
Remove-Item .\dist -Recurse -Force
dotnet publish .\TAPythonInstaller\TAPythonInstaller.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\dist
Get-Item .\dist\TAPythonInstaller.exe | Select-Object FullName,Length
```

### 当前关键源码

主要逻辑集中在：

```text
D:\Claude\project\TAPython_installer\TAPythonInstaller\Form1.cs
```

该文件包含：

- UI 构建
- 项目选择
- 引擎扫描
- GitHub Release 拉取
- ZIP 下载与解压
- 插件安装
- `.uproject` 修改
- `DefaultEngine.ini` 修改
- BuildId 修复
- 安装验证
- 日志输出

### 已知风险与注意事项

- GitHub API 或下载可能受网络影响，已提供本地 ZIP 兜底。
- Source Build 的 `EngineAssociation` 可能是 GUID，当前版本会通过引擎扫描列表让用户手动选择对应源码引擎。
- `D:\AntLibs\WS` 扫描目前限制在两层目录内，避免深度递归导致卡顿。
- 当前版本没有做管理员权限自动提升；如果用户选择安装到受保护目录（如 `C:\Program Files` 下的引擎目录），可能需要手动以管理员身份运行。
- 当前尚未做图标、签名、Windows 安装包和自动更新。
- `.uproject` 写回会重新格式化 JSON，但不应破坏语义。
- `DefaultEngine.ini` 插入逻辑较保守，后续可改为更严格的 INI 解析器以处理复杂项目配置。

### 下一步建议

高优先级：

- 增加管理员权限检测与“以管理员身份重启”按钮
- 增加安装前 dry-run 预览页，显示将修改的文件和目标路径
- 增加失败回滚：安装失败时恢复备份目录与配置文件
- 对 `.uproject` 和 `DefaultEngine.ini` 修改前自动备份

中优先级：

- 增加工具图标与品牌化 UI
- 增加版本缓存，避免每次都请求 GitHub API
- 增加下载代理或镜像地址配置
- 增加安装日志导出按钮
- 增加“仅配置 Python 路径 / 仅修复 BuildId / 仅启用插件”的维护模式

低优先级：

- 使用 Inno Setup 或 MSIX 生成 Windows 安装包
- 增加自动更新机制
- 增加多语言界面
- 增加团队内部默认配置模板
