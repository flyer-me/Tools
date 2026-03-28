# 集成文档

## 模块职责

该模块从卡片数据提取 sideloader 依赖 GUID，并和目标 modpack 内 zipmod 对比，输出：

- 已使用 zipmod
- 未使用 zipmod
- 无法解析 zipmod
- 重复 GUID 组
- 文件大小统计（单文件、目录汇总、unused 总计）

## 输入范围

- 游戏根目录（默认：`D:\ProgramData\HS2_DXBR_R16`）
- 扫描目录：
  - `UserData/chara`
  - `UserData/studio`
  - `UserData/Studio`
- 目标目录：
  - `mods/Sideloader Modpack`
  - `mods/Sideloader Modpack - Exclusive HS2`

## 输出

- 文本报告（默认 `ModpackAnalysisReport.txt`）
- 可选移动结果（启用 `--move-unused` 时）
- 大小字段：
  - 条目级：`SizeBytes`、`Size`
  - 目录级：`TotalSizeBytes/UsedSizeBytes/UnusedSizeBytes/InvalidSizeBytes`
  - 全局：`Unused Zipmod Total Size`

## 调用建议

1. 先执行 `--dry-run` 生成报告。
2. 解析 `Unused`、`Invalid`、`Duplicate GUID` 段落。
3. 人工确认后执行 `--move-unused`。
4. 将最终操作写入主项目审计日志。

## 打包与部署

- Release 构建会自动生成单文件：`dist/HS2ModpackAnalyzer.exe`
- 可用 `DeploySingleFileToHs2` 目标部署到 HS2 安装目录

## 已知边界

- Studio 非卡片格式目前未做专门解析，仍复用 PNG 卡片解析流程。
- 模块依赖 `lib/KKManager` 下 DLL，不依赖 KKManager 源码。
