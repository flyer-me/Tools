# DiskWriteTracker

## 目录结构

```
src/DiskWriteTracker.Core/
  Interop/                    Windows 存储 IOCTL 封装
  Discovery/                  磁盘枚举与身份识别
  Collect/                    采样编排
  Units/                      单位换算与磨损一致性校验
  Analysis/                   每日增量聚合
  Store/                      JSONL 样本存储
  Export/                     CSV、报表数据契约、内嵌模板
    Report/report-template.html|css|js  报表前端
  Scheduling/                 计划任务管理
  Config/                     配置加载
  Logging/                    引擎与菜单日志
src/DiskWriteTracker.Cli/
  Program.cs                  命令行入口与参数解析
  Interactive/                菜单、动作、提权、快捷方式、用户设置
  Reports/                    ViewBuilder / ReportWriter / ExportWriter
src/DiskWriteTracker.Core.Tests/  xUnit
tools/
  verify.ps1                  进程契约断言
  bootstrap-sdk.ps1           按需安装 .NET SDK
  dist-assets/README.txt      随产物发给最终用户的说明
config/default.json           引擎配置
```

---

## 分发

```
dist\
  DiskWriteTracker.exe    压缩单文件，自含运行时，双击即菜单
  README.txt              给用户看的说明
  data\                   数据根（存在即触发便携模式）
```

发布为 `net10.0-windows` / `win-x64` 压缩单文件自包含形态。

---

## 快速开始

双击 `DiskWriteTracker.exe`，出现菜单：

```
  1  立即采样并刷新报表
  2  只看报表（不采样）
  3  导出 CSV
  4  安装 / 更新每日计划任务      ← 需要管理员
  5  卸载计划任务
  6  打开数据目录
  7  查看日志
  0  退出
```

---

## 功能状态

| 功能                            | 状态               |
| ------------------------------- | ------------------ |
| 读取 NVMe / SATA 累计写入量     | 可用               |
| 多盘识别（型号+序列号）         | 可用               |
| 单位换算与磨损一致性校验        | 可用               |
| 采样记录（JSONL，带跨进程写锁） | 可用               |
| 每日增量计算                    | 可用               |
| 可排序筛选的离线 HTML 报表      | 可用               |
| CSV 导出（UTF-8 BOM + CRLF）    | 可用               |
| 计划任务                        | 可用，**需管理员** |
| 图形界面                        | 离线 HTML 报表页   |

---

## 取值原理

| 盘型      | 调用                                                         | 取值                                  |
| --------- | ------------------------------------------------------------ | ------------------------------------- |
| NVMe      | `IOCTL_STORAGE_QUERY_PROPERTY`，Log Page `0x02`              | `Data Units Written` × 512000 字节    |
| NVMe 身份 | 同 IOCTL，`DataType=Identify`                                | NVMe Identify Controller 的序列号     |
| SATA      | `IOCTL_STORAGE_PREDICT_FAILURE`，回退 `SMART_RCV_DRIVE_DATA` | SMART 属性 246 / 241 / 233 × 逻辑扇区 |

两类读取均以标准用户权限完成。若某块盘读取失败，该盘记为「缺失」

---

## 构建

**发布与 CI 路径**：仓库根 `scripts\build-tool.ps1 -ToolId windows-disk-write-tracker`。
它是本地与 CI 共享的唯一构建分派入口；manifest 声明了 `build.verify` 后，
构建成功会自动执行 `tools\verify.ps1`（段首 `dotnet test`，其后进程契约断言），非零退出码即失败。

`tools\` 下的脚本是本工具的自定义构建与验收资产：
`verify.ps1`（验收断言）、`bootstrap-sdk.ps1` + `dotnet-install.ps1`
（无全局 SDK 的本机环境按需安装）。它们不参与发布管道——发布只走根分派器加 verify 门禁。

```powershell
# 已有可用 SDK 就跳过；没有才免提权装到当前用户目录
powershell -ExecutionPolicy Bypass -File tools\bootstrap-sdk.ps1
```

目标框架 `net10.0-windows` / `win-x64`，`global.json` 钉 SDK `10.0.403`。
发布属性固化在 `DiskWriteTracker.Cli.csproj`（`PublishSingleFile` +
`EnableCompressionInSingleFile`），裸 `dotnet publish -c Release -o dist` 即产出交付物。
`Directory.Build.targets` 的 `CleanDistRoot` 清 dist 根目录旧文件，`StageDist` 补 `README.txt` 与 `data\`；
`data\` 及其内容不参与清理。

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

**便携判断由引擎自己完成**：计划任务注册的命令为 `sample --quiet`，引擎据此识别
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

`--data-dir <路径>` 可临时指定数据目录。各命令输出人读文本，`report` 生成 HTML，
`export csv` 生成 CSV；`schedule.ps1` 与引擎之间用 JSON 交换结构化结果。

退出码：`0` 成功 / `1` 无物理磁盘 / `2` 参数错误 / `4` 无数据 / `5` 执行异常 / `7` 计划任务失败。

`report` / `export` 未指定 `--out` 时落到**数据目录**下的 `exports\`。

---

## 每日增量计算

- 当日增量 = 当日最后一次有效读数 − **前一个有效日**的最后一次读数；以日为单位取基准，
  使一天内多次采样被完整累计
- 首次采样只有记录
- 中间缺失若干天时，增量为跨期累计，标注「跨期累计 / 低可信」
- 计数器变小视为回退（换盘、固件更新、计数重置），增量钳制为 0 并标「异常」
- 数据来源切换时以切换日为界分段计算
- 自然日按采样当时的本地时区确定并固定，采样后即锁定

---

## 数据质量标记

| 标记   | 含义                                   |
| ------ | -------------------------------------- |
| 可靠   | 直接读自固件，且单位通过一致性校验     |
| 回退   | 使用备用读取通道或换算路径             |
| 低可信 | 单位无法唯一确定、跨源切换、或中间缺日 |
| 异常   | 计数器回退，已钳制为 0                 |
| 缺失   | 该日无有效读数，保留空值               |

SATA 的单位判定用「磨损一致性」交叉验证：累计写入 ÷ 已用寿命百分比 = 隐含标称 TBW，
必须落在合理区间。若原始值其实是字节而非扇区，隐含 TBW 会偏离 500 倍以上，据此排除。
单位无法唯一确定时字节数留空，该样本标为「低可信」。

---

## 并发与写入安全

只有引擎写数据，且同一时刻只允许一个引擎进程写：

- 写：`.write.lock` + `FileShare.None`，250ms 起退避（上限 2s），总预算 15 秒；
  超时即跳过本次并返回 5，以保证文件完整
- 读：`FileShare.ReadWrite | FileShare.Delete`，写者持锁期间读操作仍可并行
- 报表：先写同目录临时文件再 `File.Move` 原子覆盖

---

## 日志

| 文件                              | 来源 | 内容                                  |
| --------------------------------- | ---- | ------------------------------------- |
| `data\logs\engine-YYYYMMDD.log`   | 引擎 | 每次采样一行汇总                      |
| `data\logs\launcher-YYYYMMDD.log` | 菜单 | 每个动作的退出码、耗时、stdout/stderr |

---

## 数据位置

```
<数据目录>\
  samples-YYYYMMDD.jsonl         每次采样的原始记录
  config\settings.json           菜单偏好
  .write.lock                    跨进程写锁，常驻 0 字节，隐藏属性
  exports\report.html            报表（固定名，覆盖；同日另存 archive\report-YYYYMMDD.html）
  exports\*.csv                  导出的 CSV
  logs\                          引擎与菜单日志
```

---

## 验收

```powershell
dotnet test
tools\verify.ps1
```

`tools\verify.ps1` 由 manifest 的 `build.verify` 声明，`scripts\build-tool.ps1` 构建后统一调用。

---

## 已知限制

1. 页面仅作展示，采样入口统一为菜单选项 1；`file://` 限制使页面无法启动本地程序。
2. 无参 + 输出重定向时打印帮助并退出，使构建脚本、CI、管道调用直接返回。
3. 报表支持新版 Edge / Chrome / Firefox；Firefox 在 `file://` 下可能忽略下载文件名，此时退化为「复制内容」。
4. 换机器或换盘符后，计划任务里的绝对路径会失效，必须重装任务（菜单 5 再 4）。
5. 卸载时「同时删除数据」可能被本机安全策略拦下（`Remove-Item -Recurse` 被 fail-closed），此时会打印 `rd /s /q` 替代命令并如实报告结果。
6. `.write.lock` 被安全软件锁住会导致采样超时报错，README.txt 已标注该文件需保留。
7. `percentage_used` 部分固件上报偏乐观（本机 NVMe 实测 27.8 TB 写入仅报 1%），仅用于展示，判定以累计写入为准。
8. 部分 NVMe 在 Windows 下序列号为 `0000_0000_...` 合成。
9. 累计写入为主机侧写入量，与 NAND 实际编程量分列；写入放大另计。
10. 位于 RAID 卡后的磁盘可能无法直通读取，会标记为缺失。
11. 并发读取时个别盘可能瞬时读不到，该次记为缺失并保留空值，日增量在下一个有效日跨期累计。
