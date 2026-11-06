# DiskWriteTracker 交接文档

自包含的接手资料：架构、命令、构建、数据布局、红线、Git 现状、遗留待办与已定决策。最后更新：2026-11-06。
接手时读本文件，再跑一次 `git status --short` 与 `git log --oneline -3`。

## 1. 当前状态

- **唯一开发主线**：`F:\User_Data\Code\Tools\tools\windows-disk-write-tracker`（下称 B）；git 根是 `F:\User_Data\Code\Tools`，remote 为 `github.com/flyer-me/Tools`。
- **已归档副本**：`F:\User_Data\Agents\Disk`（下称 A，独立 git 仓库，无 remote），2026-11-06 起只读。A 的源码曾与 B 逐字节一致且落后 3 个提交，无未迁移代码；禁止在 A 继续开发。
- **两仓关系**：B 是开发主线，额外保留 2 个仓库适配文件：`tool.manifest.json`（构建声明）与 `Directory.Build.targets`（清理 dist 根目录、补 `README.txt` 与 `data\`）。
- 项目：Windows 磁盘累计写入量追踪，.NET 10 / win-x64，自包含发布。
- 测试夹具由 `Core.Tests` 的数据工厂与 `tools\verify.ps1` 运行时生成，统一使用脱敏序列号。
- 2026-11-06 收尾：代码审查后的六步改造完成（JSONL 改 STJ、删 `--json`、HTML 模板化、菜单重构、测试双层、单文件压缩发布）。

## 2. 架构

| 项目 | 职责 |
|---|---|
| `src\DiskWriteTracker.Core\` | 零 NuGet 依赖（仅 BCL + Win32 P/Invoke）：采集、存储、分析、配置、日志、调度、导出原语 |
| `src\DiskWriteTracker.Cli\` | 命令分发、交互菜单、报表编排、人读输出 |
| `src\DiskWriteTracker.Core.Tests\` | xUnit 算法层：日增量边界、JSONL 往返、CSV 转义与编码 |

关键文件（相对 `src\`）：

- `Cli\Program.cs` — 8 动词分发、UTF-8 输出、统一异常出口
- `Cli\Interactive\{InteractiveMenu,MenuActions,MenuSession,UserSettings,UserSettingsStore}.cs` — 菜单门控、动作、会话快照、偏好读写；文案在 `menu-layout.txt`
- `Cli\ScheduleInvocation.cs` — 卸载参数拼装，CLI 与菜单共用
- `Cli\Reports\{ReportWriter,ViewBuilder,ExportWriter}.cs` — 报表生成/归档、`WindowStart` 口径唯一定义处、CSV
- `Core\Collect\{SamplingRunner,DiskProbeService}.cs` — 采样编排与单盘读数
- `Core\Discovery\DiskEnumerator.cs` — 逐个打开 `\\.\PhysicalDriveN`，走设备 IOCTL
- `Core\Interop\{NativeMethods,AtaSmart,Nvme,StorageQuery}.cs` — IOCTL 封装；SATA 优先 `IOCTL_STORAGE_PREDICT_FAILURE`，回退 `SMART_RCV_DRIVE_DATA`
- `Core\Store\{JsonlSampleStore,StoreJsonContext,SampleFile,SampleRecord,FileLock}.cs` — 按日 JSONL（STJ 源生成上下文）、文件名解析入口、数据契约、`.write.lock` 跨进程锁
- `Core\Logging\{EngineLog,LogRetention}.cs` — 极简引擎日志、按天+按体积双阈值回收
- `Core\Config\{AppConfig,AppConfigLoader,RuntimePaths}.cs` — 9 键配置（与 `config\default.json` 严格 1:1）、数据目录优先级
- `Core\Analysis\DailyAggregator.cs`、`Core\Units\UnitResolver.cs` — 日增量聚合、磨损一致性闸门
- `Core\Export\{HtmlReportBuilder,ReportData,CsvExporter,ReportAssets,ReportJsonContext}.cs` — 离线 HTML、内嵌 JSON 契约、CSV、内嵌资源
- `Core\Scheduling\{ScheduleManager.cs,schedule.ps1}` — 计划任务 install/status/run/uninstall；脚本为内嵌资源，**必须带 UTF-8 BOM**
- `Cli\Interactive\StartMenuShortcut.cs` — COM 晚绑定快捷方式

内嵌资源：`Scheduling\schedule.ps1`、`Export\Report\report-template.html|css|js`、
`Cli\Interactive\menu-layout.txt`、`config\default.json`。只复制 `*.cs` 会漏这些。

## 3. 命令与退出码

8 个动词：`doctor`、`disks`、`probe`、`sample`、`daily`、`report`、`export`、`schedule`。
全局参数 `--quiet`、`--data-dir <路径>`、`--days N`、`--out`、`--time`、`--dry-run`、`--purge-data`、`--purge-all`。
用户可见输出为人读文本、HTML 报表、CSV；`schedule.ps1` 与引擎之间用 JSON 交换结构化结果。
子命令：`daily` 仅 `show`（可省略）、`export` 仅 `csv`、`schedule` 支持 `install|uninstall|run|status`。
默认值：`daily --days 30`、`report/export --days 90`、`schedule install --time 00:05`。
`history`、`export --all`、`daily rebuild` 已删除且不再恢复。

退出码：`0` 成功 / `1` 无物理磁盘 / `2` 参数或子命令错误 / `4` 无数据 / `5` 顶层异常 / `7` 计划任务失败。
无参且真终端时进菜单；输入或输出被重定向时打印帮助并返回 0。

## 4. 构建与验证

- 框架 `net10.0-windows`，`global.json` 钉 `sdk 10.0.403`（`rollForward=latestFeature`），RID `win-x64`。
- 本机唯一 SDK：`C:\Program Files\dotnet\dotnet.exe`。`Resolve-Dotnet` 逐个试候选 `dotnet --version`，系统那份优先；`bootstrap-sdk.ps1` 先探测已有 SDK，缺失时才免提权安装。
- 发布属性固化在 `DiskWriteTracker.Cli.csproj`：`PublishSingleFile=true`、`EnableCompressionInSingleFile=true`、`PublishTrimmed=true`、`PublishReadyToRun=false`、`SelfContained=true`、`RuntimeIdentifier=win-x64`、`JsonSerializerIsReflectionEnabledByDefault=false`、`SatelliteResourceLanguages=en;zh-Hans`。实测 exe 11,777,303 B，`dist\` 根目录恰 3 项。
- `Directory.Build.props` 设 `IncludeSourceRevisionInInformationalVersion=false`，关闭 SDK 把 git commit 嵌进 `AssemblyInformationalVersion` 的行为，保证同一份源码产出可复现的字节。
- `DebugType=none` 放在 props（放 csproj 只对入口项目生效）；`EnforceCodeStyleInBuild` 让 `.editorconfig` 的 IDE0051/IDE0052/CS0169/CS0414 四条 error 规则在命令行构建生效。
- 构建与门禁：`scripts\build-tool.ps1 -ToolId windows-disk-write-tracker` → `scripts\assert-artifacts.ps1 -ToolId ...` → `scripts\package-tool.ps1 -ToolId ... -Version 0.0.0-ci`；仓库另有 `scripts\validate-tools.ps1`。四步全过，包 3 文件 / 6.46 MB。
- `tool.manifest.json`：`build.args` 为 `["-o","dist"]`，`artifacts` 三条（`dist/*.exe`、`dist/README.txt`、`dist/data`）。`Directory.Build.targets` 的 `CleanDistRoot` 单层清 dist 根目录旧文件（排除本次 exe、`README.txt`、`data\`），`StageDist` 在 publish 后复制 `README.txt` 并建 `dist\data`。
- `tools\verify.ps1` 是双层门禁：段首调 `dotnet test`（Core.Tests，37 个用例），其后 98 条进程契约断言；数值读 `daily.csv`，doctor / schedule dry-run / 卸载三档解析人读文本。`build.verify` 由 `scripts\build-tool.ps1` 统一调用。

## 5. 数据布局

- 数据目录优先级：显式 `--data-dir` → exe 同级 `data\`（存在即便携）→ `data.directory` → `%ProgramData%\DiskWriteTracker` → `%LOCALAPPDATA%\DiskWriteTracker`。
- `data\config\settings.json`：`schemaVersion=2`、`reportDays=400`、`exportDays=400`、`time="00:05"`；范围 1–3650 / `HH:mm`，越界与解析失败均回落默认值。`logDays` 已废弃。
- 迁移链：`exe\config\launcher.json` → `data\settings.json`(v1) → `data\config\settings.json`(v2)；构建时一步搬到最终位置，失败中止构建。
- 日志：`logging.retentionDays=30`（按天）、`logging.maxFileBytes=1048576`（按体积）；`LogRetention.MaxSegments=3`，分片 `stem.N.log`；解析不出日期的文件一律保留。
- 样本：`samples-yyyyMMdd.jsonl`，`SampleFile` 是文件名解析唯一入口。`ReadForWindow` 只读窗口内文件并回扫前驱（每块盘取窗口起点之前最后一条 `bytes` 有值记录），`MaxPredecessorScanDays=90`。
- 报表：固定名 `exports\report.html` + 同日 `exports\archive\report-yyyyMMdd.html`，归档上限 30 个；显式 `--out` 不产生归档。
- 卸载三档：默认仅移除任务；`--purge-data` 删样本/日志但保留 `config\settings.json`；`--purge-all` 全删。三档共用 `Get-PurgePlan` 作为预览与实删的唯一来源，`--dry-run` 前置。

## 6. 操作红线

- 编码：`.ps1` / `.md` / `.txt` = UTF-8 **带 BOM** + CRLF；`.cs` 无 BOM + CRLF。改动时逐文件沿用原 BOM 与行尾，用精确字符串替换。（例外：A 仓 `README.md` 历史上无 BOM，保持原状。）
- 两仓同步：复制 A 的文件到 B，跳过 `.git`、`.workbuddy`、`dist`、`data`、`logs`、`exports`、`bin`、`obj`；B 的 `tool.manifest.json` 与 `Directory.Build.targets` 保留。夹具一并复制，两处均为脱敏值。
- 禁用 `Remove-Item -Recurse`（本机安全策略 fail-closed），用 `[IO.File]::Delete` / `[IO.Directory]::Delete($dir,$false)` 自底向上删。
- PowerShell 5.1 默认按控制台代码页解码子进程 stdout，必须显式设 `[Console]::OutputEncoding` 及 `StandardOutputEncoding/StandardErrorEncoding` 为 UTF-8，否则中文断言误判乱码。
- `Get-PurgePlan` 按目录层级判定保留项，按路径前缀比较会失配（`TEMP` 解析成 8.3 短名 `ADMINI~1`）。
- `-PurgeData:$false` 在 `-File` 调用下会绑定失败，开关参数只在「打开」时增量拼入。
- `SqliteConnection` 需 `Pooling=False` 才能释放文件句柄（历史 spike 结论，当前代码未引入 SQLite，仅供回溯）。

## 7. Git 现状

| | A | B |
|---|---|---|
| git 根 | `F:\User_Data\Agents\Disk` | `F:\User_Data\Code\Tools` |
| remote | 无 | `origin https://github.com/flyer-me/Tools.git` |
| 分支 | 本地 `master` | `master` 跟踪 `origin/master` |

B 仓库级文件 `.github/workflows/*.yml`、`CHANGELOG.md`、`scripts\*.ps1` 属于整个工具仓库，改动时连同本工具一起提交。最新提交号由 `git log --oneline -3` 读取。

## 8. 遗留待办（已核实真实状态）

| 项 | 状态 |
|---|---|
| 前驱回扫上限 | `MaxPredecessorScanDays = 90` 仍在，离线超 90 天会显示「首次采样」 |
| `Append` 的 `Flush(flushToDisk:true)` | 仍在，提频后是写入放大点 |
| 形态断言 | 收紧为根目录恰 3 项（exe / README.txt / data）、无旁置 dll 与 json、exe 体积 9–14 MB |
| `JsonSerializerIsReflectionEnabledByDefault` | 已改为 false；全部序列化走源生成上下文，反射序列化器由裁剪器移除 |
| `StartMenuShortcut` COM 晚绑定 | 单文件产物烟测下快捷方式文件仍存在；IL2072/IL2075 是裁剪分析告警，运行时走 `IDispatch` 与托管程序集加载无关 |
| IL20xx 裁剪警告 | 2026-11-06 构建日志仍有 IL2026（`JsonSerializer.Serialize` options 重载）与 IL2072/IL2075（StartMenuShortcut）；构建通过，功能烟测全绿 |
| B 仓历史提交 `8ac833f` | 含旧版真实序列号，后续提交 `9c8e0c2` 已在工作区脱敏；清除历史需 force push 重写全部提交 |

已解决：报表归档上限 30 个（`ReportWriter.cs` 的 `Skip(30)`）；`dist\` 旧版 native 残留（4.3 MB）；压缩单文件交付；真实 `dist\data` 31 个文件逐哈希未变，改造前后 14 行日统计 CSV 逐字段一致。

## 9. 已定 / 已否决

**已定**

- 命令集固定为 8 动词；`history`、`export --all`、`daily rebuild` 已删除且不再恢复。
- 压缩单文件自包含发布（`PublishSingleFile` + `EnableCompressionInSingleFile`）；升级覆盖 exe，`data\` 保留。
- `net10.0-windows` / `global.json 10.0.403` / `win-x64`；本机只保留一个 SDK。
- 存储采用 JSONL + `.write.lock`；序列化走 STJ 源生成上下文。
- 偏好落在 `data\config\settings.json`；产物校验采用根目录恰 3 项。
- B 为唯一开发主线，A 已归档只读；测试夹具由代码生成。
- 交付产物可复现：关闭 git commit 嵌入、publish 前清理根目录旧文件。
- 文档口径：技术否定句改肯定式保留信息，安抚/自夸/铺垫/说教删除；过时事实一律修正。

**已否决**

- 恢复三个已删命令、SQLite 存储、GUI 程序、网页内「立即采样」按钮。
- 命名 Mutex 取代文件锁（S4U 会话跨不过）、删除 `.write.lock`。
- `Remove-Item -Recurse`；在已归档的 A 仓继续开发；夹具分别维护真实/脱敏序列号。
- 重写 B 仓 git 历史：工作区已达成目标，重写需 force push 且改动全部 28 个提交与其他 7 个工具的 SHA。历史里的旧序列号如需彻底清除，单独授权后再做。

## 10. 文档同步状态

2026-11-06 六步改造后，全部文档按现行契约同步：

- `tools\dist-assets\README.txt` 升级段改为覆盖单个 exe。
- `README.md` 分发形态、构建参数、验收分层、输出口径全部更新。
- `docs\handoff.md` 架构、发布属性、门禁、遗留待办、已定决策全部更新。
- `docs\io-layout-review.md` 保留三组实测数字与冷启动数据，结论与命令改按现行参数表述。
- `CHANGELOG.md` 旧条目里描述当时状态的句子保留为历史，`[Unreleased]` 记录本次改造。
- `scripts\build-tool.ps1`:87 的发布属性注释与现状一致。
- 旧 `tools\verify-manual.md` 与 `tests\fixtures\` 已随测试双层重写删除。
