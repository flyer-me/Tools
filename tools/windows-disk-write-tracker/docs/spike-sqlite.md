# SQLite 单文件体积实测（spike）

**结论：体积不是拒绝本地数据库的理由。**

- 引入 `Microsoft.Data.Sqlite`：单文件 exe **+1.18 MB（+3.4%）**，publish 目录仍只有 1 个文件，无旁文件，冷/热启动无可测劣化。
- 走系统自带 `winsqlite3.dll`：**+154 B**，即零成本。
- 因此 **P4（派生索引）的体积门禁 = GO**；但 P4 的另一半门禁是性能触发（见 `design.md` 数据增长一节），当前 2 条/天远未命中，**P4 仍不立项**。

本文件是决策留痕，不是待办。数字与机器、SDK、包版本强相关，换环境需重测。

## 环境

| 项 | 值 |
|---|---|
| 日期 | 2026-11-04 |
| OS | Windows（本机） |
| SDK | 8.0.427（`%LOCALAPPDATA%\Microsoft\dotnet`） |
| 目标框架 | `net8.0-windows`，`win-x64`，自包含单文件 |
| 对照基线 | `src\dist\DiskWriteTracker.exe` = **37,261,981 B（35.54 MB）**，commit `e6d0524` |
| 包版本 | `Microsoft.Data.Sqlite` 8.0.11（SQLitePCLRaw 2.1.6） |

## 方法

三组一次性工程，均**逐字复制**发布属性（`src\DiskWriteTracker.Cli\DiskWriteTracker.Cli.csproj:14-23`），唯一变量是依赖：

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<PublishReadyToRun>true</PublishReadyToRun>
<DebugType>none</DebugType>
```

| 变体 | 依赖 | 做什么 |
|---|---|---|
| A | 无 | 写读一个临时文本文件（对照组，抵消框架本身体积） |
| B | `Microsoft.Data.Sqlite` | open / create / insert / `select count(*)` |
| C | 无，`DllImport("winsqlite3.dll")` | 同一套操作走系统 DLL |

三组都**真的执行**了 SQL 或等价 IO：R2R 只编译可达方法，只写不跑会低估增量。

工程放在 `F:\User_Data\Code\Tools\tempbin\dwt-sqlite-spike\`（`.gitignore` 已忽略 `/tempbin/`，且在 `tools\` 子树外，不继承工具级 `Directory.Build.props`，也不在 `DiskWriteTracker.sln` 里，不会污染离线构建与 CI）。完整源码见下方「复现」。

## 实测结果

| 变体 | exe 字节 | Δ vs A | Δ vs 基线 | publish 条目 | 冷启动 | 热启动 |
|---|---:|---:|---:|---:|---:|---:|
| A（对照） | 36,877,218 | — | −0.37 MB | 1 | 646 ms | 171 ms |
| B（Microsoft.Data.Sqlite） | 38,113,606 | **+1,236,388 B** | +0.81 MB | 1 | 418 ms | 187 ms |
| C（winsqlite3） | 36,877,372 | **+154 B** | −0.37 MB | 1 | 581 ms | 169 ms |
| 基线 DiskWriteTracker | 37,261,981 | — | — | — | — | — |

- 三组 publish 目录都只有 1 个 `.exe`，`.dll` / `.json` 计数为 0 → **不触发** `scripts\verify.ps1:109-111` 的旁文件断言。
- 启动差异在测量噪声内（冷启动首跑包含解压，波动 ±200 ms），**不构成**「引入 SQLite 会拖慢计划任务」的证据。
- 基线 exe 比对照组大 384,763 B，是应用自身代码 + 内嵌资源（`report.css` / `report.js` / `schedule.ps1` / `default.json`）。

## 依赖闭包与体积去向

`Microsoft.Data.Sqlite` 一个包拉入 7 个：

```
Microsoft.Data.Sqlite 8.0.11
Microsoft.Data.Sqlite.Core 8.0.11
SQLitePCLRaw.bundle_e_sqlite3 2.1.6
SQLitePCLRaw.core 2.1.6
SQLitePCLRaw.lib.e_sqlite3 2.1.6
SQLitePCLRaw.provider.e_sqlite3 2.1.6
System.Memory 4.5.3
```

主要体积来自原生库：`e_sqlite3.dll` **1,691,648 B**，运行时被解压到 `%TEMP%\.net\spike-b\`（`IncludeNativeLibrariesForSelfExtract` 生效，因此**不会**在 dist 根目录留下随行 dll）。1.69 MB 原生 + 托管封装，压缩进 bundle 后净增 1.18 MB。

## 判定

| 门禁 | 阈值 | 实测 | 结论 |
|---|---|---|---|
| `Δ(B−A)` | ≤ 4 MB | 1.18 MB | ✅ GO |
| publish 目录条目数 | = 1 | 1 | ✅ |
| 旁文件（`.dll`/`.json`） | = 0 | 0 | ✅ |
| 冷启动增量 | ≤ 300 ms | 噪声内 | ✅ |

**但 P4 是双重门禁**：体积通过只是必要条件。另一条是 `report --days 90` 超过 300 ms 或 `samples-*.jsonl` 合计超过 50 MB —— 当前 2 条/天、0.5 MB/年，都不沾边。P1 的窗口读取落地后，90 天报表只打开 90 个文件、解析约 270 行，毫秒级。

**所以：体积讨论到此结束，数据库暂时仍不该上。** 触发条件命中时再回来立项。

若将来立项，两条路可选：

| 路线 | 增量 | 代价 |
|---|---|---|
| **C：`winsqlite3.dll`** | ≈ 0 | 依赖 Windows 10 1803+ 的系统组件；需自写约 200 行 P/Invoke 封装；API 是 OS 子集，微软未面向第三方承诺兼容 |
| **B：`Microsoft.Data.Sqlite`** | +1.18 MB | 首次 restore 需联网；多 6 个传递包；换 SDK 大版本时要跟包版本 |

按「exe 每 MB 都要用户下载一次」的定位，1.18 MB 买不到任何当前收益；若真要上，C 更符合这个工具的零依赖取向。

## 复现

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
foreach ($v in @('a','b','c')) {
    & $dotnet publish "F:\User_Data\Code\Tools\tempbin\dwt-sqlite-spike\$v\Spike$($v.ToUpper()).csproj" -c Release
    Get-ChildItem "F:\User_Data\Code\Tools\tempbin\dwt-sqlite-spike\$v\bin\Release\net8.0-windows\win-x64\publish"
}
```

变体 C 的关键调用面（`winsqlite3.dll` 全部按 `Cdecl` + `LPUTF8Str` 绑定）：

```csharp
sqlite3_open_v2(path, out db, 0x2 | 0x4, IntPtr.Zero);   // READWRITE | CREATE
sqlite3_exec(db, "create table ...", IntPtr.Zero, IntPtr.Zero, out err);
sqlite3_prepare_v2(db, "select count(*) from t", -1, out stmt, IntPtr.Zero);
sqlite3_step(stmt); sqlite3_column_int(stmt, 0); sqlite3_finalize(stmt);
```

实测输出 `libversion=3053004`（SQLite 3.53.4），证明系统 DLL 可用、绑定正确。

## 边界

- 本文件**不允许**被加进 `DiskWriteTracker.sln`，也不允许把 spike 工程复制进 `tools\`：会破坏离线构建与 CI。
- 数字随 SDK、包版本、压缩算法变化；换环境重测一次再引用。
- 未覆盖：数据库文件本身的增长（索引大小）、WAL 模式下的额外文件、多进程并发写。这些属于 P4 立项后的设计问题，不属本次体积 spike 范围。
