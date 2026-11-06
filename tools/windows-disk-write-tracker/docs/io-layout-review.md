# 数据目录组织方式审理

对 `DiskWriteTracker` 在数据目录里落盘的东西做一次体量与代价核算，回答四个问题：
`samples-*.jsonl` 会不会文件太多、日志会不会文件太多、`settings.json` 该不该放在 `data` 根、
要不要引入本地数据库。

所有数字都来自本机实测，不是估算；估算的地方会单独标出来。

---

## 结论摘要

| 问题 | 结论 | 处置 |
|---|---|---|
| `samples-*.jsonl` 文件数 | 365 个/年，10 年 3650 个。**文件数不是问题**，但"每次全量打开所有文件"是问题 | 已改为窗口读取，只打开窗口内文件 + 补前驱 |
| 日志文件数 | 730 个/年，**且此前只增不减**：`logDays` 只读写不剪枝，`keepRawPayloadDays` 完全没接线 | 已实现按天 + 按体积双阈值 |
| `settings.json` 位置 | 不该跟样本平铺在一层 | 已迁到 `data\config\settings.json`，卸载分三档 |
| 引入本地数据库 | **不划算**：多 1.17 MiB 换 730 行/年 的索引 | 不做，保留触发条件（见第五节） |

---

## 一、现有布局与实际体量

本机 `dist\data` 的真实内容（2026-11-04）：

```
samples-20261029.jsonl      11,862 B   18 行
samples-20261101.jsonl       1,332 B    2 行
samples-20261102.jsonl       3,996 B    6 行
samples-20261103.jsonl       3,996 B    6 行
samples-20261104.jsonl       1,332 B    2 行
logs\engine-*.log              150～900 B（每行约 150 B）
logs\launcher-*.log            180～181 B（每次菜单动作一块）
logs\.log.lock                      0 B（隐藏）
exports\report.html             52 KB
exports\archive\report-*.html   48～52 KB × 4
settings.json                  108 B
.write.lock                         0 B（隐藏）
.migrated-from-legacy              约 100 B（隐藏）
```

一条样本记录约 **660 字节**（`1,332 ÷ 2`）。2 块盘每天一行 → **约 1.3 KB/天**。

按此推算（每天采样一次、2 块盘、保留期取当前默认 3650 天）：

| 项目 | 1 年 | 10 年（保留期上限） |
|---|---|---|
| 样本文件数 | 365 | 3,650 |
| 样本体积 | 0.48 MB | 4.8 MB |
| 日志文件数 | 730（engine + launcher） | 7,300 |
| 日志体积 | 约 55 KB（engine）+ 约 1.3 MB（launcher，按每天 20 次菜单动作） | 约 13 MB |
| 报表归档 | 365 × 50 KB ≈ 18 MB | 约 180 MB |

---

## 二、问题一：`samples-*.jsonl` 会不会文件太多

**单看文件数：不会。** NTFS 单目录放几千个文件毫无压力，3,650 个条目在资源管理器里
也只是滚动条长一点，不影响可用性。体量更不是问题 —— 4.8 MB 还不到一个报表归档的十分之一。

**真正的问题在读取方式。** 改之前 `ReadAll()` 会把目录下每一个 `samples-*.jsonl` 全部打开解析，
而 `daily show` 默认只看 30 天、`report` 默认 90 天。也就是说：为了算 30 天的增量，
要打开 3,650 个文件。

实测边际开销（2,000 个样本文件、合计 2.4 MB，本机 SSD）：

| 窗口 | 打开文件数 | 耗时 |
|---|---|---|
| `--days 30` | 30 | 230 ms |
| `--days 400` | 400 | 377 ms |
| `--days 2000` | 2,000 | 1,028 ms |

扣除进程启动约 250 ms 后，每多打开一个文件大约 **0.4～0.5 ms**。按保留期上限 3,650 个文件外推：
全量读取约 **1.8 秒**，而 `--days 400` 的窗口只需要 **0.38 秒**。

结论：**文件数可以放着不管，读取范围不行。** 已实现的窗口读取把单次操作从"打开全部历史"
收敛到"打开窗口内 + 补齐前驱"。

### 窗口读取为什么必须补前驱

日增量的定义是「当天最后一条 − 前一天最后一条」。如果窗口第一天没有窗口之前的锚点，
它就会退化成"基线日"，这一天的写入量凭空消失 —— 而表面上一切正常，报表照样出，只是少了一天。

补前驱时容易写错成"多读一天"。正确的是：**从窗口起点往回找，每块盘取第一条 `bytes` 有值的记录**。
中间断过三天时，"窗口起点前一天"这个文件根本不存在，多读一天什么也拿不到。
实现在 `JsonlSampleStore.ReadForWindow`，往回最多扫 90 天（`MaxPredecessorScanDays`），
再久就放弃 —— 一块盘常年不在线时，继续往回扫只是把开文件数刷上去。

---

## 三、问题二：日志会不会文件太多

**这是四个问题里唯一真正越界的。**

改造前的状态：

- `engine-yyyyMMdd.log` 与 `launcher-yyyyMMdd.log` 按日切分，**没有任何清理逻辑**；
- `settings.json` 里有个 `logDays`（默认 30），`README.txt` 还专门介绍了它 ——
  但它从第一版起只被读写、**从未参与剪枝**，是个"配了没反应"的死字段；
- `config\default.json` 里的 `logging.keepRawPayloadDays` 更是完全没有对应的 C# 字段。

后果：文件数 730 个/年线性增长且永不回收；launcher 日志按"每次菜单动作一块"增长，
重灾区不是天数而是**频次** —— 一天点 100 次菜单就是 18 KB 的尖峰。只按天治理拦不住这种尖峰。

### 已实现的方案：两条阈值，缺一不可

| 阈值 | 配置项 | 默认值 | 拦住什么 |
|---|---|---|---|
| 按天 | `logging.retentionDays` | 30 | 文件个数随天数无限累积 |
| 按体积 | `logging.maxFileBytes` | 1048576（1 MiB） | 同一天内的写入尖峰 |

体积阈值触发时**就地滚动**：`engine-20261104.log` → `engine-20261104.1.log`，新内容写回同名文件。
分片最多 3 个（`LogRetention.MaxSegments`），满额时丢编号最大的那个。于是单个基名最多
4 个文件、4 MiB，一天之内怎么写都不会超过这个上界。

两个设计细节值得记下来：

1. **剪枝不能挂在 `EngineLog` 上。** `logLevel=ERROR` 时 `Info()` 根本不会被调用，
   把清理挂在写日志的路径上，等于在最需要清理的配置下永不触发。
   现在剪枝挂在两处独立入口：`SamplingRunner` 收尾（不判断日志级别）、`MenuSession` 启动
   （没装计划任务的纯手动用户就只有这一条路径）。
2. **解析不出日期的文件一律不删。** 用户自己丢进来的、旧版本留下别的命名的文件，
   删错的代价远大于留几个孤儿。

顺带删掉了 `logDays` 这个死字段（`settings.json` 升到 `schemaVersion 2`）。日志保留天数
是引擎行为，计划任务也受它约束，本来就不该藏在"用户偏好"里。
偏好读写落在 `data\config\settings.json`（`schemaVersion 2`），迁移链已随旧构建脚本一同删除。

---

## 四、问题三：`settings.json` 该不该放在 `data` 根

**不该。** 理由不是美观，是**清理语义**。

`data` 根是"数据"的地盘：`samples-*.jsonl`、`logs\`、`exports\`。它们都能重新生成 ——
样本可以重采，日志可以再写，报表可以重算。`settings.json` 不是：里面是用户的
窗口天数、导出天数、采样时间，删了就得重配一遍。

两者平铺在一层时，任何"清理"动作都无法用一条规则表达，只能给单个文件名开特例。
特例是漏删和误删的共同来源 —— 本次改造过程中就真的踩到了（见下）。

### 现在的布局

```
data\
  samples-*.jsonl        数据
  logs\                  数据
  exports\               数据
  config\settings.json   配置（唯一需要保留的东西）
```

对应地，卸载分成三档：

| 命令 | 移除任务 | 删样本与日志 | 删偏好 |
|---|---|---|---|
| `schedule uninstall` | 是 | | |
| `schedule uninstall --purge-data` | 是 | 是 | |
| `schedule uninstall --purge-all` | 是 | 是 | 是 |

三档都支持 `--dry-run`，逐个列出将要删除和将要保留的文件，一个都不真删。
dry-run 与实际删除共用 `Get-PurgePlan`，预览与实删保持一致。

### 踩到的坑：8.3 短名让前缀比较失效

第一版 `Get-PurgePlan` 用路径前缀判断某个文件是否在 `config\` 里。在本机跑测试时，
`TEMP` 解析成 `C:\Users\ADMINI~1\...`（8.3 短名），而 `Get-ChildItem` 枚举出来的 `FullName`
是全名 —— 两边前缀一比全不匹配，`config\settings.json` 被列进了删除清单。

也就是说：**`--purge-data` 会把用户偏好一起删掉，而预览界面看起来一切正常。**

改成按目录层级判断（只认"根目录第一层里名字叫 `config` 的那个目录"，与路径怎么写无关）后修复，
并补了一条断言专门盯这个：`--purge-data` 不得把 `config` 目录列进删除清单。

同类隐患还有一个：早期版本在 `DryRun` 判断**之前**就注销了计划任务，
所以 `uninstall --dry-run` 会真的把任务删掉。现在注销动作排在 dry-run 分支之后。

### 迁移链

`exe\config\launcher.json`（最早） → `data\settings.json`（schemaVersion 1） → `data\config\settings.json`（2）

每一步都是「解析成功才落新址、落址成功并读回校验后才删旧文件」。解析失败一律原样留着当证据，
绝不吃掉用户手改过的内容。另外 `TryReadWithBackup` 会在主文件读不出来时退到 `.bak` ——
每次保存前都会留一份，它是最后一个"用户真的这么设过"的证据。

---

## 五、问题四：要不要引入本地数据库

### 体积实测（三组，同一份源码、同一套发布参数）

> 历史实测：本节数据采于单文件发布时代，当时开启了 `PublishReadyToRun`。
> 现行参数为压缩单文件 + `PublishReadyToRun=false` + trim，实测 exe 11,777,303 B，
> 与下列 37 MB 数据不可直接比较；体积结论仅用于当时的取舍判断。

当时的发布参数：`--self-contained win-x64`、`PublishSingleFile` + `IncludeNativeLibrariesForSelfExtract`
+ `EnableCompressionInSingleFile` + `PublishReadyToRun`、`DebugType=none`。

| 变体 | exe 字节 | 相对 A | 产物文件数 | 冷启动（3 次） |
|---|---|---|---|---|
| A 对照（现状，零 NuGet） | 37,262,275 | — | 1 | 1055 / 674 / 269 ms |
| B `Microsoft.Data.Sqlite` 8.0.11 | 38,494,148 | **+1,231,873 B（+1.17 MiB，+3.3%）** | 1 | 1056 / 644 / 265 ms |
| C `winsqlite3.dll` P/Invoke | 37,262,722 | **+447 B** | 1 | 1066 / 670 / 257 ms |

三点观察：

1. **三组产出都只有 1 个 exe，没有旁文件、没有 dll、没有 `runtimes\` 目录。**
   `Microsoft.Data.Sqlite` 的 7 个依赖包里，`SQLitePCLRaw.lib.e_sqlite3` 带的原生
   `e_sqlite3.dll` 会被打进 bundle（运行时自解压到 `%TEMP%\.net\`），不落在交付目录里。
   所以"引入 NuGet 会破坏单文件交付形态"这个担心不成立。
2. **体积增量 1.17 MiB 对 35.6 MiB 的基线是 +3.3%**，一次性成本，可接受。
   `winsqlite3.dll`（Windows 10 1607+ 自带）几乎零成本（+447 B），但它把可用性绑死在系统版本上，
   且没有 Entity Framework 之类的生态。
3. **冷启动没有劣化**（首跑都含自解压，约 1.05 s；随后两次都约 0.26 s）。

所以**体积不是拒绝数据库的理由**。

### 但数据规模让数据库毫无意义

- 2 块盘 × 每天 1 次 = **730 行/年**，10 年 7,300 行，合计 4.8 MB；
- `--days 400` 的窗口读取实测 **377 ms**（其中约 250 ms 是进程启动）；
- 数据库能带来的唯一实际收益是"把按日文件变成索引查询"，省掉的正是这 0.4 ms/文件。

为一万行以内的数据引入 SQLite，换来的是：多一个依赖闭包、多一套 schema 迁移、
多一个"数据库文件损坏怎么办"的故障面，以及 JSONL 这个**能用记事本直接看**的性质
（出问题时用户/支持人员可以自己打开看，这是本工具当前最大的可用性优势）。

### 决策

**不做，但保留触发条件。** 只有同时满足以下任一条件才重新评估：

1. 采样频率提升到每天多次（例如 `maxConcurrency` / 多采样点落地），日增量从 2 行变成几十行；
2. `samples-*.jsonl` 总量超过 50 MB；
3. `report --days 90` 的端到端耗时超过 300 ms（当前实测 230～377 ms 量级，其中大头是进程启动，
   真要卡这条线得先分离启动耗时）。

这三条都写在本文件里而不是代码注释里，因为它们属于"何时重新立项"的判据，不是实现细节。

---

## 六、本次实施的改动

**Core**

| 文件 | 改动 |
|---|---|
| `Store/SampleFile.cs` | 新增。`samples-yyyyMMdd.jsonl` 文件名解析的唯一入口 |
| `Store/JsonlSampleStore.cs` | 抽出 `ReadInto`/`SortByCapturedUtc`；新增 `ReadForWindow`、`CountLines` |
| `Logging/LogRetention.cs` | 新增。按天剪枝 + 按体积滚动（分片上限 3） |
| `Logging/EngineLog.cs` | 写前滚动 |
| `Collect/SamplingRunner.cs` | 收尾剪枝日志（不依赖 logLevel）；剪枝改用 `SampleFile` |
| `Config/AppConfig.cs`、`Config/AppConfigLoader.cs` | 新增 `logging.retentionDays` / `logging.maxFileBytes` |
| `Scheduling/schedule.ps1` | `-PurgeAll`、`--dry-run`、三档 purge；注销动作移到 dry-run 之后 |

**CLI**

| 文件 | 改动 |
|---|---|
| `Program.cs` | `doctor` 暴露偏好与日志路径、用 `CountLines`；`LoadSamples(days)`；`daily`/`report`/`export` 走窗口；卸载三档 + 预演输出 |
| `Reports/ViewBuilder.cs` | `WindowStart` 单点定义"最近 N 天"的口径 |
| `Reports/ExportWriter.cs` | `allSamples` 参数；明细里剔除前驱 |
| `Interactive/UserSettings.cs` | 删 `logDays`，`schemaVersion` → 2，版本号只升不降 |
| `Interactive/UserSettingsStore.cs` | 落点改 `data\config\`，两级迁移，`.bak` 回落 |
| `Interactive/MenuSession.cs` | 用 `SampleFile` 解析；启动时剪枝日志 |
| `Interactive/MenuActions.cs` | 报表/导出走窗口；卸载三档（回车 / `DELETE` / `DELETE ALL`） |

**构建与验收**

| 文件 | 当时的改动 |
|---|---|
| `verify.ps1` | 窗口天数从夹具文件名反推（不写死 30）；新增窗口前驱、卸载三档、日志双阈值等断言 |
| `dist-assets/README.txt` | 数据布局、日志自动回收、卸载三档 |

当前验收已改为双层：`tools\verify.ps1` 段首调 `dotnet test`（37 个用例），其后 98 条进程契约断言全绿；
静态夹具与人工清单已删除，夹具由代码在运行时生成。`build.verify` 由 `scripts\build-tool.ps1` 统一调用。

---

## 七、遗留与未做的事

1. **报表归档上限已定为 30 份。** 当时按每天一份、每份约 50 KB、10 年约 180 MB 估算；
   `ReportWriter.cs` 的 `Skip(30)` 现在只保留最近 30 份。
2. **`JsonlSampleStore.Append` 每次 `Flush(flushToDisk: true)`。** 对每天一次的采样无所谓，
   但如果将来提高采样频率，这个 fsync 会成为写入放大点。
3. **前驱回扫上限 90 天**是经验值。一块盘离线超过 90 天后重新上线时，窗口第一天会是基线日，
   报表会显示"首次采样"而不是实际的跨期增量（跨期语义本身是对的，只是少一天）。

---

## 八、后续调整：三个命令被删除

本文第六节曾把 `daily rebuild`、`history`、`export --all` 记为"已落地"。复核后三者全部删除，
理由恰恰来自本文的实测数据——它们都不具备独占用途。

| 命令 | 实际产物 | 替代 | 本机使用记录 |
|---|---|---|---|
| `daily rebuild` | `daily.json` | `daily show` 与 `export csv` 的 `daily.csv` | 0 次（该文件从未生成） |
| `history` | 终端逐条打印 | `export csv` 的 `samples.csv`（列更多） | 无记录 |
| `export --all` | 全量 `samples.csv` | `--days` 给一个大数 | 0 次（`menu/3` 从未出现） |

三点值得记下来：

1. **`daily.json` 是"派生数据存成缓存"的典型误判。** 日统计每次都由 JSONL 现算，
   这个文件既不被程序读、也没被外部消费；要把日统计落盘，`export csv` 一条命令就够，
   不需要一个命令加一个写盘函数。删它同时也撤回了"供外部读取"这个从未兑现的承诺。
2. **`export --all` 是本次改造自己制造的复杂度。** 窗口读取会顺带带回前驱，明细因此需要过滤；
   为了留一个"不过滤"的出口，又加了一个开关。真正的需求只有"明细必须落在窗口内"这一条。
3. **`history` 与 `export csv` 数据同源**，区别只是终端表格与 CSV。终端里翻 730 条/年
   本来就不是可用场景。

当时命令数 16 → 13；`verify.ps1` 断言 118 → 113（删掉的 5 条正是这三个命令的契约断言），
随后补到 114 项。当前验收为双层：xUnit 37 个用例 + `verify.ps1` 98 条进程契约断言。
同时修正了 README 里两处过期描述：`settings.json` 已在 `data\config\` 下，
`default.json` 也不再有任何"标【保留】但不生效"的键。
