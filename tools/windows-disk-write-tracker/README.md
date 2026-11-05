# DiskWriteTracker

读取硬盘固件上报的**累计写入字节数**，按天记录增量，生成可离线查看的报表。

---

## 分发形态

最终用户拿到的是一个文件夹：

```
dist\
  DiskWriteTracker.exe    托管入口壳，双击即菜单
  *.dll                   自包含运行时，必须与 exe 同目录
  *.json                  运行时配置（runtimeconfig / deps）
  README.txt              给用户看的说明
  data\                   数据根（存在即触发便携模式）
```

发布为 `net10.0-windows` / `win-x64` 多文件自包含形态，产物约 22.7 MiB。
升级时用新版本的整个文件夹覆盖旧目录。

| 角色 | 触发方式 | 行为 |
|---|---|---|
| 菜单 | 双击（真终端，无重定向） | 8 项交互菜单，动作在进程内直调 Core |
| 命令行 | 带参数调用 | 一次一件事，输出结果或 `--json` |
| 计划任务 | `sample --quiet` | 每天自动采样，无输出 |

无参且输入或输出被重定向时打印帮助并退出，使脚本、管道、计划任务调用直接返回。

页面为纯静态离线 HTML，仅作展示；受 `file://` 限制，采样入口统一为菜单选项 1。

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

选 1 采样并打开浏览器里的报表；选 4 安装每日计划任务。
`tools\dist-assets\README.txt` 是同一份内容面向非技术用户的版本。

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
| 交互菜单 | 可用，运行在 exe 进程内 |
| 图形界面 | 浏览器内的离线 HTML 报表页 |

---

## 取值原理

| 盘型 | 调用 | 取值 |
|---|---|---|
| NVMe | `IOCTL_STORAGE_QUERY_PROPERTY`，Log Page `0x02` | `Data Units Written` × 512000 字节 |
| NVMe 身份 | 同 IOCTL，`DataType=Identify` | NVMe Identify Controller 的真实序列号，优先于 Windows 合成串 `0000_0000_...` |
| SATA | `IOCTL_STORAGE_PREDICT_FAILURE`，回退 `SMART_RCV_DRIVE_DATA` | SMART 属性 246 / 241 / 233 × 逻辑扇区 |

两类读取均以标准用户权限完成。若某块盘读取失败（例如位于 RAID 卡后），该盘记为「缺失」

---

## 构建

```powershell
# 已有可用 SDK 就跳过；没有才免提权装到当前用户目录
powershell -ExecutionPolicy Bypass -File tools\bootstrap-sdk.ps1

# 编译 + 发布 + 冒烟 + 验收
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

目标框架 `net10.0-windows` / `win-x64`，`global.json` 钉 SDK `10.0.403`。
`tools\build.ps1` 通过 `Resolve-Dotnet` 逐个试候选 `dotnet --version`，选出满足
`global.json` 的解释器；系统安装的那份优先，用户目录那份仅作兜底。

发布参数：`PublishSingleFile=false`、`PublishTrimmed=true`、`PublishReadyToRun=false`、
`SatelliteResourceLanguages=en;zh-Hans`；`SelfContained` 与 `RuntimeIdentifier` 由
`build.ps1` 命令行提供。产物为多文件自包含布局，约 22.7 MiB。

发布采用覆盖式：`dist\data` 保留，上一版遗留的 `DiskWriteTracker.cmd`、`launcher\`、
`config\` 按名清理；产物根目录须包含 `exe` / `README.txt` / `data` 三项，
其余文件按扩展名白名单 `.exe` / `.dll` / `.json` / `.txt` 校验。

旧版 `config\launcher.json` 在构建时一步迁移到 `data\config\settings.json`，
迁移失败直接中止构建，避免丢失用户偏好。

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

**便携判断由引擎自己完成**：计划任务注册的命令写死为 `sample --quiet`，引擎据此识别
exe 旁的 `data\`，使计划任务与菜单手动采样落到同一目录。

首次进入便携模式时，若 `data\` 还是空的而 `%ProgramData%` 里有历史样本，会自动复制一份过来，
并写 `.migrated-from-legacy` 标记（源文件保留，重复执行幂等）。

---

## 命令行

菜单与命令行共用同一套 Core：

```powershell
DiskWriteTracker doctor                      # 环境自检（含生效配置、部署方式、迁移记录）
DiskWriteTracker disks                       # 磁盘清单
DiskWriteTracker probe                       # 预览累计写入，仅输出
DiskWriteTracker sample                      # 采样一次并记录
DiskWriteTracker daily --days 30             # 查看每日增量
DiskWriteTracker report --days 90            # 生成 HTML 报表
DiskWriteTracker export csv --days 90        # 导出 CSV
DiskWriteTracker schedule status             # 定时任务状态
DiskWriteTracker schedule install --time 00:05 [--dry-run]
DiskWriteTracker schedule uninstall [--purge-data]
```

任意命令加 `--json` 输出 `{ok, data}` 结构化结果。`--data-dir <路径>` 可临时指定数据目录。

退出码：`0` 成功 / `1` 无物理磁盘 / `2` 参数错误 / `4` 无数据 / `5` 执行异常 / `7` 计划任务失败。

`report` / `export` 未指定 `--out` 时落到**数据目录**下的 `exports\`。

---

## 每日增量怎么算

- 当日增量 = 当日最后一次有效读数 − **前一个有效日**的最后一次读数；以日为单位取基准，
  使一天内多次采样被完整累计
- 首次采样仅建立基线
- 中间缺失若干天时，增量为跨期累计，标注「跨期累计 / 低可信」
- 计数器变小视为回退（换盘、固件更新、计数重置），增量钳制为 0 并标「异常」
- 数据来源切换时以切换日为界分段计算
- 自然日按采样当时的本地时区确定并固定，采样后即锁定

---

## 数据质量标记

| 标记 | 含义 |
|---|---|
| 可靠 | 直接读自固件，且单位通过一致性校验 |
| 回退 | 使用备用读取通道或换算路径 |
| 低可信 | 单位无法唯一确定、跨源切换、或中间缺日 |
| 异常 | 计数器回退，已钳制为 0 |
| 缺失 | 该日无有效读数，保留空值 |

SATA 的单位判定用「磨损一致性」交叉验证：累计写入 ÷ 已用寿命百分比 = 隐含标称 TBW，
必须落在合理区间。若原始值其实是字节而非扇区，隐含 TBW 会偏离 500 倍以上，据此排除。
单位无法唯一确定时字节数留空，该样本标为「低可信」。

---

## 权限

| 操作 | 需要管理员 |
|---|---|
| 读取 NVMe / SATA 累计写入 | 否 |
| 菜单采样、算增量、导出报表、打开页面、看日志 | 否 |
| 安装 / 卸载计划任务 | **是**（弹 UAC） |
| 写 `%ProgramData%` | 否（不可写时自动降级到用户目录） |

实测：非管理员调用 `Register-ScheduledTask` 会被系统拒绝（普通权限 + 交互登录同样被拒）。
因此 `schedule install` 必须以管理员身份运行；菜单遇到权限不足时打印两条可复制的人工指令。

提权走 `ProcessStartInfo.Verb = "runas"`（等价于资源管理器里的「以管理员身份运行」）。
UAC 被用户取消时捕获 `Win32Exception` 1223，按「已取消」提示并归入正常退出。

---

## 并发与写入安全

只有引擎写数据，且同一时刻只允许一个引擎进程写：

- 写：`.write.lock` + `FileShare.None`，250ms 起退避（上限 2s），总预算 15 秒；
  超时即跳过本次并返回 5，以保证文件完整
- 读：`FileShare.ReadWrite | FileShare.Delete`，写者持锁期间读操作仍可并行
- 报表：先写同目录临时文件再 `File.Move` 原子覆盖

抢锁时两种异常都视为**瞬态**并重试：别人持锁时的 `IOException`（共享冲突），
以及属性设置与打开交错时的 `UnauthorizedAccessException`（`Access denied`）。
后者在 8 进程并发下实测有约 25% 概率踩中，只重试前者会导致采样整体失败。
权限判定依据是写入前的可写性探测；上述两种异常属于并发瞬态。

`.write.lock` 带隐藏属性。属性设置失败仅影响资源管理器可见性。

---

## 配置

| 位置 | 谁改 | 说明 |
|---|---|---|
| 内嵌默认值（编译进 exe） | 只读 | 最终用户看到的默认来源 |
| `config\default.json`（exe 旁，可选） | 运维手放 | 存在即**覆盖**内嵌值；缺省时回落到内嵌默认值 |
| `<数据目录>\config\settings.json` | 菜单自动写 | `reportDays` `exportDays` `time` |

`doctor --json` 的 `config.sourcePath` 会告诉你当前生效的是哪一种：
`embedded:default.json` 表示走内嵌默认值。

`settings.json` 可以手改：`reportDays`/`exportDays` 限 1–3650，`time` 须为 `HH:mm`；
越界值与解析失败均回落到默认值。

内嵌默认值里的 9 个键全部真正生效：`data.directory` `data.fallbackDirectory`
`data.retentionDays` `sampling.dailyTime` `sampling.timeoutSeconds`
`units.requireWearGate` `logging.level` `logging.retentionDays` `logging.maxFileBytes`。
9 个键与 `AppConfig` 字段严格一一对应。

**报表页面只读展示配置**：运行参数在菜单里修改。

---

## 日志

| 文件 | 来源 | 内容 |
|---|---|---|
| `data\logs\engine-YYYYMMDD.log` | 引擎 | 每次采样一行汇总（盘数/目录/耗时/剪枝数）、未捕获异常 |
| `data\logs\launcher-YYYYMMDD.log` | 菜单 | 每个动作的退出码、耗时、stdout/stderr |

引擎侧只记这两个时机：计划任务以 `--quiet` 运行，`LastTaskResult` 仅返回 0/非 0，
引擎日志是每日采样结果的可观测依据。更密集的日志会引入写盘争用。

---

## 数据位置

```
<数据目录>\
  samples-YYYYMMDD.jsonl         每次采样的原始记录（原始值、单位、换算系数、来源、质量）
  config\settings.json           菜单偏好
  .write.lock                    跨进程写锁，常驻 0 字节，隐藏属性
  exports\report.html            报表（固定名，覆盖；同日另存 archive\report-YYYYMMDD.html）
  exports\*.csv                  导出的 CSV
  logs\                          引擎与菜单日志
```

---

## 验收

```powershell
# 自动化：114 项断言
tools\build.ps1            # 构建 + 发布 + 冒烟 + verify，全绿才算通过
tools\verify.ps1           # 只跑验收，不动产物

# 人工：13 项，需真终端与人工确认
tools\verify-manual.md
```

自动化覆盖：产物形态、无参直接返回、命令行契约、日增量四种边界、报表离线自包含、
固定名与归档、CSV 编码、8 进程并发写锁、便携目录、计划任务 dry-run、无数据行为。

人工覆盖：双击进菜单、UAC 提权装任务、到点自动采样、卸载确认词、报表页面交互、
整目录搬移、标准用户降级提示。UAC 提权需真机执行一次并记录结果，
清单见 `tools\verify-manual.md`。

---

## 已知限制

1. 页面仅作展示，采样入口统一为菜单选项 1；`file://` 限制使页面无法启动本地程序。
2. 无参 + 输出重定向时打印帮助并退出，使构建脚本、CI、管道调用直接返回。
3. 报表支持新版 Edge / Chrome / Firefox；Firefox 在 `file://` 下可能忽略下载文件名，此时退化为「复制内容」。
4. 换机器或换盘符后，计划任务里的绝对路径会失效，必须重装任务（菜单 5 再 4）。
5. 卸载时「同时删除数据」可能被本机安全策略拦下（`Remove-Item -Recurse` 被 fail-closed），此时会打印 `rd /s /q` 替代命令并如实报告结果。
6. `.write.lock` 被安全软件锁住会导致采样超时报错，README.txt 已标注该文件需保留。
7. `percentage_used` 部分固件上报偏乐观（本机 NVMe 实测 27.8 TB 写入仅报 1%），仅用于展示，判定以累计写入为准。
8. 部分 NVMe 在 Windows 下序列号为 `0000_0000_...` 合成串；本工具改用 NVMe Identify Controller 的真实序列号，取不到时才退到「型号+容量」并标注。
9. 累计写入为主机侧写入量，与 NAND 实际编程量分列；写入放大另计。
10. 位于 RAID 卡后的磁盘可能无法直通读取，会标记为缺失。
11. 并发读取时个别盘可能瞬时读不到，该次记为缺失并保留空值，日增量在下一个有效日跨期累计。
12. **任务计划程序窗口的「状态」列需按 F5 刷新**。任务实际 0.3 秒内就跑完（采样本体 15–17ms，
    加上启动与进程回收约 1–1.5 秒），界面却可能长期停在「正在运行」。按 F5 刷新，
    或直接用 `Get-ScheduledTask -TaskName DiskWriteTracker_DailySample` 看真实状态。
    真正的卡死判据是「进程仍在 + `LastTaskResult` 为 `0x41301`」同时成立；
    界面状态列仅作参考。

---

## 排查任务是否真的在跑

```powershell
# 真实状态（以计划任务 API 为准）
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
    Report/report.css|js      报表前端（随程序集内嵌）
  Scheduling/                 计划任务管理（内嵌 PowerShell 脚本）
  Config/                     数据目录解析、配置加载（外部优先，内嵌兜底）
  Logging/                    引擎与菜单日志
src/DiskWriteTracker.Cli/
  Program.cs                  命令行入口与参数解析
  Interactive/                菜单、动作、提权、快捷方式、用户设置
  Reports/                    ViewBuilder / ReportWriter / ExportWriter
tools/
  build.ps1                   构建 + 发布 + 扩展名白名单校验 + 冒烟 + 验收
  verify.ps1                  自动化验收（114 项）
  verify-manual.md            人工验收清单（13 项）
  bootstrap-sdk.ps1           按需免提权安装 .NET SDK
  dist-assets/README.txt      随产物发给最终用户的说明
config/default.json           引擎配置（编译进 exe 的默认值来源）
tests/fixtures/data/          多日测试样本（含基线/正常/回退/缺日四种边界）
```

---

## 编码红线

脚本与文本文件的编码直接决定可否运行：

| 类型 | 要求 | 原因 |
|---|---|---|
| `.ps1` | UTF-8 **带 BOM** + CRLF | PS 5.1 需 BOM 才能正确解析中文注释 |
| `.cmd` `.bat` | **纯 ASCII** + 无 BOM + CRLF | BOM 会被当成命令的一部分执行 |
| `.txt` `.md` | UTF-8 带 BOM + CRLF | 记事本友好 |

`.gitattributes` 已钉住换行，`verify.ps1` 第 1 节校验产物仅含允许的扩展名。
