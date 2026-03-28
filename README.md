# Mod Organizer - Sideloader Analyzer Module

本目录是 mod 整理项目中的一个独立子模块，用于分析以下目录中的 zipmod 使用情况：

- `Sideloader Modpack`
- `Sideloader Modpack - Exclusive HS2`

当前实现为纯 DLL 模式，不依赖 KKManager 源码。

## 目录结构

- `Program.cs`：主程序入口与分析逻辑
- `ConsoleApp1.csproj`：`net472` 控制台项目（x64）
- `lib/KKManager/`：运行所需的 `KKManager.Core.dll` 及其依赖
- `ConsoleApp1.slnx`：最小解决方案文件

## 运行环境

- Windows
- .NET SDK（可构建 `net472`）
- 游戏目录可访问（默认：`D:\ProgramData\HS2_DXBR_R16`）

## 构建

```powershell
dotnet build ConsoleApp1.csproj -p:NuGetAudit=false
```

## 运行

查看参数：

```powershell
dotnet run --project ConsoleApp1.csproj -- --help
```

默认分析（仅统计，不移动文件）：

```powershell
dotnet run --project ConsoleApp1.csproj -- --dry-run
```

指定报告输出：

```powershell
dotnet run --project ConsoleApp1.csproj -- --dry-run --report .\out\reports\analysis.txt
```

执行实际移动（谨慎使用）：

```powershell
dotnet run --project ConsoleApp1.csproj -- --move-unused
```

## 参数说明

- `--game-dir <path>`：游戏根目录
- `--report <path>`：报告输出路径
- `--modpack <name>`：目标 modpack 名称（可重复）
- `--move-unused`：将未使用 zipmod 移动到输出目录
- `--dry-run`：仅规划，不执行移动
- `--unused-output <path>`：未使用 zipmod 的输出根目录

## 与主项目集成建议

- 作为主工程的一个子命令或子任务调用。
- 建议在 CI 中仅跑 `--dry-run`，避免自动修改文件系统。
- 生产整理流程中，先输出报告、人工确认后再执行 `--move-unused`。
