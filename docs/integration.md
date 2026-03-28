# 集成文档

## 模块定位

该模块用于从角色卡/工作室卡中提取 sideloader 依赖 GUID，并与两个目标 modpack 的 zipmod 清单做比对，输出：

- 已使用 zipmod
- 未使用 zipmod
- 无法解析 zipmod
- 跨目标目录的重复 GUID

## 输入

- 游戏目录（默认 `D:\ProgramData\HS2_DXBR_R16`）
- 扫描来源：
  - `UserData/chara`
  - `UserData/studio`
  - `UserData/Studio`
- 目标目录：
  - `mods/Sideloader Modpack`
  - `mods/Sideloader Modpack - Exclusive HS2`

## 输出

- 文本报告（默认 `ModpackAnalysisReport.txt`）
- 可选移动结果（当启用 `--move-unused`）

## 推荐调用流程

1. 先运行 `--dry-run`，生成报告。
2. 读取报告中的 `Unused`、`Invalid`、`Duplicate GUID` 段落。
3. 人工确认后再运行 `--move-unused`。
4. 将移动结果写入主项目的审计日志。

## 稳定性边界

- Studio 非卡片格式目前未解析，仅复用 PNG 卡片解析流程。
- 该模块依赖 `lib/KKManager` 目录内 DLL，不再依赖 KKManager 源码。
