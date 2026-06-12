# TAPython Installer — AI 开发约定

## 项目概况

- WPF / .NET 8 Windows 桌面安装器，单项目结构：[TAPythonInstaller/](../TAPythonInstaller/)，主要逻辑集中在 `MainWindow.xaml.cs`
- 发布产物输出到 `dist/`（不入库），发布命令见 README「从源码构建」
- 版本号在 `TAPythonInstaller.csproj` 中维护（`Version` / `AssemblyVersion` / `FileVersion` / `InformationalVersion` 四处同步）

## 每次开发完成后必须执行（README 同步规则）

完成任何功能开发、行为变更或发版前，必须检查并同步 [README.md](../README.md)：

1. **新增/修改/移除功能** → 更新「核心功能」（中文）和「Key Features」（英文）对应条目，中英文必须同步修改
2. **保持摘要风格**：每个功能一行分组摘要，不写版本号、不堆 UI 细节；版本级变化只写进应用内更新日志（`MainWindow.xaml` 更新日志区块）和 GitHub Release 描述
3. **使用方法 / 构建命令变化** → 同步更新对应小节
4. README 变更与代码变更放在同一个 commit 中提交

## 发版流程

1. 升级 `TAPythonInstaller.csproj` 四处版本号
2. 在 `MainWindow.xaml` 更新日志区块顶部插入新版本条目（中文）
3. 按 README 同步规则检查并更新 README.md
4. `dotnet publish`（发布前确认 dist 中的 exe 未被运行中的进程占用）
5. 提交（含 README）、打 `vX.Y.Z` 标签、推送
6. 创建 GitHub Release 并上传 `dist/TAPythonInstaller.exe`；Release 描述使用英文（中文经 PowerShell API 提交会乱码）

## 已知注意事项

- 通过 PowerShell `Invoke-RestMethod` 向 GitHub API 提交中文内容会乱码，Release 描述一律用英文；复杂请求写成临时 `.ps1` 脚本执行后删除
- 单文件发布需 `-p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
