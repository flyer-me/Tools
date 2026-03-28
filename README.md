# HS2 Modpack Analyzer 模块

这个目录是 mod 整理项目中的子模块，用于分析以下目录中的 zipmod 使用情况：

- `Sideloader Modpack`
- `Sideloader Modpack - Exclusive HS2`

当前实现依赖 KKManager Core。

## 统计结果

程序会输出并写入报告：

- 每个 zipmod 文件大小（`SizeBytes` / 可读大小）
- 每个目标目录的 `Total/Used/Unused/Invalid` 数量与体积
- 全部目标目录的 `Unused total size`（未使用 zipmod 总大小）

## 目录结构

- `Program.cs`：分析程序入口
- `ConsoleApp1.csproj`：`net472` / `x64` 项目
- `lib/KKManager/`：`KKManager.Core.dll` 及运行依赖
- `scripts/deploy-to-hs2.ps1`：单文件打包并部署到 HS2 目录
- `docs/integration.md`：模块集成说明

## 构建

```powershell
dotnet build ConsoleApp1.csproj -p:NuGetAudit=false
```

## 运行

查看参数：

```powershell
dotnet run --project ConsoleApp1.csproj -- --help
```

默认分析（不移动文件）：

```powershell
dotnet run --project ConsoleApp1.csproj -- --dry-run
```

默认会在结束时等待回车，方便双击运行时查看结果。  
如需无暂停运行，请加 `--no-pause`。

## 单文件打包

生成单文件：

```powershell
dotnet build ConsoleApp1.csproj -c Release -p:NuGetAudit=false
```

输出文件：

- `dist/HS2ModpackAnalyzer.exe`

## 部署到 HS2 安装目录

使用脚本直接部署到默认目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-to-hs2.ps1
```

指定安装目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\deploy-to-hs2.ps1 -Hs2InstallDir "D:\ProgramData\HS2_DXBR_R16"
```

也可直接执行 MSBuild 目标：

```powershell
dotnet msbuild ConsoleApp1.csproj /t:DeploySingleFileToHs2 /p:Configuration=Release /p:Hs2InstallDir="D:\ProgramData\HS2_DXBR_R16"
```
