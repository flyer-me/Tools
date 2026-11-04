# DiskWriteTracker

读取硬盘固件上报的**累计写入字节数**，按天记录增量，生成可离线查看的报表。

- 只读采样，不修改硬盘任何数据
- 不联网、不上传数据、不写注册表
- 数据跟着程序目录走，整个文件夹可拷到 U 盘

---

## 交付形态

最终用户拿到的只有三样东西：

```
src\dist\
  DiskWriteTracker.exe    自包含单文件，目标机不需要装 .NET
  README.txt              给用户看的说明
  data\                   数据根（存在即触发便携模式）
```

菜单、报表生成、CSV 导出、计划任务注册**全部在这一个 exe 里**。
没有 `.cmd` 启动器，没有 `.ps1` 脚本，没有配置文件目录，没有 JSON Schema。
双击 exe 就是菜单，不需要先弄清楚该点哪个文件。

引擎与入口是同一个可执行文件，但职责仍然分开：

| 角色 | 触发方式 | 行为 |
|---|---|---|
| 菜单 | 双击（真终端，无重定向） | 8 项交互菜单，动作在进程内直调 Core |
| 命令行 | 带参数调用 | 一次一件事，输出结果或 `--json` |
| 计划任务 | `sample --quiet` | 每天自动采样，无输出 |

**无参 + 输入或输出被重定向时打印帮助，不进菜单**。这样脚本、管道、计划任务调用
`DiskWriteTracker.exe` 时不会挂在那里等键盘。

页面是纯静态文件，浏览器不允许网页启动本地程序，所以**页面上没有「立即采样」按钮**——
要重新采样就打开菜单选 1。这不是省事，是 `file://` 的安全限制。

---

## 快速开始

双击 `DiskWriteTracker.exe`，出现菜单：

```
  1  立即采样并刷新报表
  2  只看报表（不采样）
  3  导出 CSV
  4  安装 / 更新每日计划任务      ← 需要管理员，会弹 UAC
  5  卸载计划任务
  6  打开数据目录
  7  查看日志
  0  退出
```

选 1 采样并打开浏览器里的报表；选 4 装好每日任务后就不用再管了。
`assets\README.txt` 是同一份内容面向非技术用户的版本。

---

## 功能状态

| 功能 | 状态 |
|---|---|
| 读取 NVMe / SATA 累计写入量 | 可用，非管理员即可 |
| 多盘识别（型号+序列号） | 可用 |
| 单位换算与磨损一致性校验 | 可用 |
| 采样记录（JSONL，带跨进程写锁） | 可用 |
| 每日增量计算 | 可用 |
| 可排序筛选的离线 HTML 报表 | 可用 |
| CSV 导出（UTF-8 BOM + CRLF） | 可用 |
| 每日自动采样（计划任务） | 可用，**安装需管理员** |
| 交互菜单 | 可用，内建于 exe |
| 图形界面程序 | **不做**，界面就是浏览器里的报表页 |

---

## 取值原理

| 盘型 | 调用 | 取值 |
|---|---|---|
| NVMe | `IOCTL_STORAGE_QUERY_PROPERTY`，Log Page `0x02` | `Data Units Written` × 512000 字节 |
| NVMe 身份 | 同 IOCTL，`DataType=Identify` | 真实序列号（而非 Windows 合成的 `0000_0000_...`） |
| SATA | `IOCTL_STORAGE_PREDICT_FAILURE`，回退 `SMART_RCV_DRIVE_DATA` | SMART 属性 246 / 241 / 233 × 逻辑扇区 |

两类读取都不需要管理员权限。若某块盘读取失败（例如位于 RAID 卡后），该盘记为「缺失」，
不推算、不填补。

---

## 构建

```powershell
# 安装 .NET 8 SDK 到当前用户目录（不需要管理员）
powershell -ExecutionPolicy Bypass -File scripts\bootstrap-sdk.ps1

# 编译 + 发布 + 冒烟 + 验收
powershell -ExecutionPolicy Bypass -File scripts\build.ps1
```

`scripts\build.ps1` 用绝对路径调用 `dotnet.exe`，不依赖 PATH。
发布参数：`PublishSingleFile` + `SelfContained` + `PublishReadyToRun` +
`IncludeNativeLibrariesForSelfExtract` + `EnableCompressionInSingleFile`，产物约 35 MB。

发布是**覆盖式**的：不删 `src\dist\data`，但会点名清掉上一版遗留的 `DiskWriteTracker.cmd`、
`launcher\`、`config\`，并断言 `src\dist\` 根目录**只有** `exe` / `README.txt` / `data` 三项。
多一个条目就构建失败——防止技术文件悄悄回流。

从旧版本升级时，`config\launcher.json` 会先迁移成 `data\settings.json` 再删除源文件，
顺序反了会丢用户偏好，所以迁移失败直接中止构建。

---

## 数据目录怎么定

优先级：

```
1. --data-dir 参数
2. exe 同级存在 data\      → 便携模式
3. 配置里的 data.directory
4. %ProgramData%\DiskWriteTracker
5. %LOCALAPPDATA%\DiskWriteTracker（降级）
```

**便携判断必须由引擎自己做**：计划任务注册的命令是写死的 `sample --quiet`，不带任何参数。
只有引擎自己识别 exe 旁的 `data\`，计划任务和菜单手动采样才会落进同一个目录，不会分裂成两份数据。

首次进入便携模式时，若 `data\` 还是空的而 `%ProgramData%` 里有历史样本，会自动复制一份过来，
并写 `.migrated-from-legacy` 标记（只复制不删除，重复执行无副作用，失败不影响采样）。

---

## 命令行

菜单是给人用的，命令行是给脚本和计划任务用的，两者共用同一套 Core：

```powershell
DiskWriteTracker doctor                      # 环境自检（含生效配置、部署方式、迁移记录）
DiskWriteTracker disks                       # 磁盘清单
DiskWriteTracker probe                       # 预览累计写入，不落盘
DiskWriteTracker sample                      # 采样一次并记录
DiskWriteTracker history --days 30           # 原始采样记录
DiskWriteTracker daily show --days 30        # 查看每日增量
DiskWriteTracker daily rebuild               # 重算日统计并写快照
DiskWriteTracker report --days 90            # 生成 HTML 报表
DiskWriteTracker export csv --days 90        # 导出 CSV
DiskWriteTracker schedule status             # 定时任务状态
DiskWriteTracker schedule install --time 00:05 [--dry-run]
DiskWriteTracker schedule uninstall [--purge-data]
```

任意命令加 `--json` 输出 `{ok, data}` 结构化结果。`--data-dir <路径>` 可临时指定数据目录。

退出码：`0` 成功 / `1` 无物理磁盘 / `2` 参数错误 / `4` 无数据 / `5` 执行异常 / `7` 计划任务失败。

`report` / `export` 不指定 `--out` 时一律落到**数据目录**下的 `exports\`，
不会在 exe 旁边另起一个 `exports\`。

---

## 每日增量怎么算

- 当日增量 = 当日最后一次有效读数 − **前一个有效日**的最后一次读数（不是「当天上一条样本」，
  否则一天采样多次只统计最后一段）
- 首次采样只建立基线，不产生增量
- 中间缺失若干天时，增量为跨期累计，标注「跨期累计 / 低可信」
- 计数器变小视为回退（换盘、固件更新、计数重置），增量记 0 并标「异常」，不输出负数
- 数据来源切换时不做跨源相减
- 自然日按采样当时的本地时区确定并固定，事后不重算

---

## 数据质量标记

| 标记 | 含义 |
|---|---|
| 可靠 | 直接读自固件，且单位通过一致性校验 |
| 回退 | 使用备用读取通道或换算路径 |
| 低可信 | 单位无法唯一确定、跨源切换、或中间缺日 |
| 异常 | 计数器回退，已钳制为 0 |
| 缺失 | 该日无有效读数，未做任何推算 |

SATA 的单位判定用「磨损一致性」交叉验证：累计写入 ÷ 已用寿命百分比 = 隐含标称 TBW，
必须落在合理区间。若原始值其实是字节而非扇区，隐含 TBW 会偏离 500 倍以上，据此排除。
无法唯一确定时不输出字节数，避免静默给出错误量级。

---

## 权限

| 操作 | 需要管理员 |
|---|---|
| 读取 NVMe / SATA 累计写入 | 否 |
| 菜单采样、算增量、导出报表、打开页面、看日志 | 否 |
| 安装 / 卸载计划任务 | **是**（弹 UAC） |
| 写 `%ProgramData%` | 否（不可写时自动降级到用户目录） |

实测：非管理员调用 `Register-ScheduledTask` 会被系统拒绝（普通权限 + 交互登录同样被拒）。
因此 `schedule install` 必须以管理员身份运行。菜单遇到这种情况会打印两条可复制的人工指令，
不会静默失败，也不会假装成功。

提权走 `ProcessStartInfo.Verb = "runas"`（等价于资源管理器里的「以管理员身份运行」）。
UAC 被用户取消时捕获 `Win32Exception` 1223，按「已取消」提示，不算异常。

---

## 并发与写入安全

只有引擎写数据，且同一时刻只允许一个引擎进程写：

- 写：`.write.lock` + `FileShare.None`，250ms 起退避（上限 2s），总预算 15 秒；
  超时则**放弃本次写入**并返回 5，宁可少一条也不写坏文件
- 读：`FileShare.ReadWrite | FileShare.Delete`，写者持锁期间读取不会互相阻塞
- 报表：先写临时文件再 `File.Move` 覆盖，浏览器不会读到半截 HTML

抢锁时两种异常都视为**瞬态**并重试：别人持锁时的 `IOException`（共享冲突），
以及属性设置与打开交错时的 `UnauthorizedAccessException`（`Access denied`）。
后者在 8 进程并发下实测有约 25% 概率踩中，只重试前者会导致采样整体失败。
`FileStream` 与 `UnauthorizedAccessException` 都不是「权限不足」的证据——
写入前的可写性探测才是，它会给出明确提示。

`.write.lock` 带隐藏属性，资源管理器里看不到。属性设置失败只影响外观，不影响加锁。

---

## 配置

| 位置 | 谁改 | 说明 |
|---|---|---|
| 内嵌默认值（编译进 exe） | 不直接改 | 最终用户看到的唯一来源 |
| `config\default.json`（exe 旁，可选） | 运维手放 | 存在即**覆盖**内嵌值；不存在不影响运行 |
| `<数据目录>\settings.json` | 菜单自动写 | `reportDays` `exportDays` `time` `logDays` |

`doctor --json` 的 `config.sourcePath` 会告诉你当前生效的是哪一种：
`embedded:default.json` 表示走内嵌默认值。

`settings.json` 可以手改，越界值会回落到默认（`reportDays`/`exportDays` 限 1–3650，
`time` 必须 `HH:mm`）。改坏了不会崩——解析失败即用默认值。

内嵌默认值里的 `data.retentionDays` `sampling.timeoutSeconds` `sampling.dailyTime`
`logging.level` `units.requireWearGate` 是真正生效的字段；其余标 `【保留】` 的部分
代码不读，改了不会有任何效果，这一点在 `config\default.json` 里逐条写明。

**报表页面只读展示配置**：运行参数在菜单里改，页面不做任何写操作。

---

## 日志

| 文件 | 来源 | 内容 |
|---|---|---|
| `data\logs\engine-YYYYMMDD.log` | 引擎 | 每次采样一行汇总（盘数/目录/耗时/剪枝数）、未捕获异常 |
| `data\logs\launcher-YYYYMMDD.log` | 菜单 | 每个动作的退出码、耗时、stdout/stderr |

引擎侧只记这两个时机：计划任务跑的是 `--quiet`，stdout 无人接收，`LastTaskResult` 只给一个 0/非 0；
没有这一行，「每天 00:05 到底采到没有」就完全不可观测。再多记会引入写盘争用，与低开销采样冲突。

---

## 数据位置

```
<数据目录>\
  samples-YYYYMMDD.jsonl         每次采样的原始记录（原始值、单位、换算系数、来源、质量）
  daily.json                     派生快照，供外部读取；命令本身不依赖它，始终从 JSONL 重算
  settings.json                  菜单偏好
  .write.lock                    跨进程写锁，常驻 0 字节，隐藏属性，正常现象
  exports\report.html            报表（固定名，覆盖；同日另存 archive\report-YYYYMMDD.html）
  exports\*.csv                  导出的 CSV
  logs\                          引擎与菜单日志
```

### 读取只打开窗口内的文件

文件名里带日期，读取时先按日期筛文件、再解析内容；**不是**每次枚举全部历史。

日增量与报表要算"当天相对前一条有效读数的差值"，因此窗口读取会额外回读窗口之前的文件，
直到窗口里出现的每块盘都拿到一条有效的前驱样本为止 —— 只多读一天是不够的：
窗口前一天该盘可能读取失败（`bytes=null`），真实前驱在更早。少读这一条，
`DailyAggregator` 会把窗口首日当成 `baseline`，一整天的写入量静默变成 0。

回读的代价上限是改造前的全量读取（某块盘若从无有效读数就会一直回读到最早的文件），
所以不会比"全部读一遍"更慢，只会更快。`history`、`daily rebuild`、`export --all`
仍走全量路径，窗口化只作用于按日期过滤的命令。

---

## 验收

```powershell
# 自动化：96 项断言
scripts\build.ps1            # 构建 + 发布 + 冒烟 + verify，全绿才算通过
scripts\verify.ps1           # 只跑验收，不动产物

# 人工：13 项，需要真终端和人眼
docs\verify-manual.md
```

自动化覆盖：产物形态、无参不挂起、命令行契约、日增量四种边界、报表离线自包含、
固定名与归档、CSV 编码、8 进程并发写锁、便携目录、计划任务 dry-run、无数据行为。

人工覆盖：双击进菜单、UAC 提权装任务、到点自动采样、卸载确认词、报表页面交互、
整目录搬移、标准用户降级提示。**UAC 那一项必须真机跑一次并记录**——
代码里写了异常处理不等于验证过。

---

## 已知限制

1. 页面无法触发采样——`file://` 的硬限制，采样统一走菜单。
2. 无参 + 输出重定向时打印帮助而不进菜单。这是刻意的：否则构建脚本、CI、管道调用会挂住。
3. 报表只支持新版 Edge / Chrome / Firefox，不支持 IE / IE 模式；Firefox 在 `file://` 下可能忽略下载文件名，此时会退化为「复制内容」。
4. 换机器或换盘符后，计划任务里的绝对路径会失效，必须重装任务（菜单 5 再 4）。
5. 卸载时「同时删除数据」可能被本机安全策略拦下（`Remove-Item -Recurse` 被 fail-closed），此时会打印 `rd /s /q` 替代命令，不假装成功。
6. `.write.lock` 被安全软件锁住会导致采样超时报错，README.txt 里已说明不要删除该文件。
7. `percentage_used` 部分固件上报偏乐观（本机 NVMe 实测 27.8 TB 写入仅报 1%），仅作展示，不参与判定。
8. 部分 NVMe 在 Windows 下序列号为 `0000_0000_...` 合成串；本工具改用 NVMe Identify Controller 的真实序列号，取不到时才退到「型号+容量」并标注。
9. 累计写入是主机侧写入量，不含写入放大，不等于 NAND 实际编程量。
10. 位于 RAID 卡后的磁盘可能无法直通读取，会标记为缺失。
11. 并发读取时个别盘可能瞬时读不到，该次记为缺失（不推算），日增量会在下一个有效日跨期累计。
12. **任务计划程序窗口的「状态」列不会自动刷新**。任务实际 0.3 秒内就跑完（采样本体 15–17ms，
    加上启动与进程回收约 1–1.5 秒），界面却可能长期停在「正在运行」。按 F5 刷新，
    或直接用 `Get-ScheduledTask -TaskName DiskWriteTracker_DailySample` 看真实状态。
    真正的卡死判据是「进程仍在 + `LastTaskResult` 为 `0x41301`」同时成立；
    单看界面上的状态列不算证据。

---

## 排查任务是否真的在跑

```powershell
# 真实状态（不依赖界面刷新）
Get-ScheduledTask -TaskName DiskWriteTracker_DailySample | Select-Object State
Get-ScheduledTaskInfo -TaskName DiskWriteTracker_DailySample |
    Select-Object LastRunTime, LastTaskResult, NextRunTime

# 有没有卡住的进程
Get-Process DiskWriteTracker -ErrorAction SilentlyContinue |
    Select-Object Id, StartTime, CPU

# 每次采样是否落盘、耗时多少（最可信的一手证据）
Get-Content .\data\logs\engine-YYYYMMDD.log -Tail 5
```

`engine-*.log` 里每行都有 `elapsed=NNms`。正常值是十几毫秒到几十毫秒；
若出现秒级或持续增长，才是采样侧的问题。

---

## 目录结构

```
src/DiskWriteTracker.Core/
  Interop/                    Windows 存储 IOCTL 封装
  Discovery/                  磁盘枚举与身份识别
  Collect/                    采样编排（写前可写性校验、保留期剪枝）
  Units/                      单位换算与磨损一致性校验
  Analysis/                   每日增量聚合
  Store/                      JSONL 样本存储 + 跨进程写锁
  Export/                     CSV、报表数据契约、内嵌 CSS/JS
    Report/report.css|js      报表前端（内嵌资源，单文件发布自动带入）
  Scheduling/                 计划任务管理（内嵌 PowerShell 脚本）
  Config/                     数据目录解析、配置加载（外部优先，内嵌兜底）
  Logging/                    引擎与菜单日志
src/DiskWriteTracker.Cli/
  Program.cs                  命令行入口与参数解析
  Interactive/                菜单、动作、提权、快捷方式、用户设置
  Reports/                    ViewBuilder / ReportWriter / ExportWriter
scripts/
  build.ps1                   构建 + 发布 + 形态断言 + 冒烟 + 验收
  verify.ps1                  自动化验收（96 项）
  bootstrap-sdk.ps1           免提权安装 .NET SDK
  dotnet-install.ps1          官方 SDK 安装脚本（bootstrap 内部使用）
docs/
  design.md                   本文（设计说明与取舍）
  verify-manual.md            人工验收清单（13 项）
assets/README.txt             随产物发给最终用户的说明
config/default.json           引擎配置（编译进 exe 的默认值来源）
tests/fixtures/data/          多日测试样本（含基线/正常/回退/缺日四种边界）
```

---

## 编码红线

脚本与文本文件的编码不是风格问题，是能否运行的问题：

| 类型 | 要求 | 原因 |
|---|---|---|
| `.ps1` | UTF-8 **带 BOM** + CRLF | PS 5.1 无 BOM 会把中文注释解析坏，脚本直接语法错误 |
| `.cmd` `.bat` | **纯 ASCII** + 无 BOM + CRLF | BOM 会被当成命令的一部分执行 |
| `.txt` `.md` | UTF-8 带 BOM + CRLF | 记事本友好 |

`.gitattributes` 已钉住换行，`verify.ps1` 第 1 节会检查产物里不再有脚本类文件。
