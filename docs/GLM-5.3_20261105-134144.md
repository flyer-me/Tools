# Tools 仓库重构指导（供 agent 执行）

- 文档：GLM-5.3 · 初稿 2026-11-05 13:41 · 修订 2026-11-05 14:03（依维护者四项决策收敛）
- 基线：`validate-tools.ps1` 通过（8 工具 / manifest schema 2.1），git 工作树干净

---

## 执行规则

1. 分支命名 `codex/<topic>`，任务独立提交；每项改动记入 `CHANGELOG.md` 的 `Unreleased`；涉及 schema 或流程的改动同步更新 `docs/` 对应文档。
2. 顺序：阶段 1 → 阶段 2 → 阶段 3。每完成一个任务，先跑该任务的验收命令，再进入下一个。
3. 与"不变量"冲突时，停下修改任务方案，不绕过不变量。
4. 净效果导向：本方案删除 1 张手工索引表与 568 MB 本地残渣，新增 1 个脚手架脚本（约 80 行）与 1 组 schema 2.2 可选字段。除此之外零新增、零新抽象。

## 维护者决策（方案依据，已定）

| # | 决策 | 落点 |
|---|---|---|
| D1 | 工具索引必须自生成：根 README 不维护索引，任何文档不得写死工具清单 | 任务 1.1 |
| D2 | 测试的"运行"由仓库统一控制，测试的"代码"归工具特定；修改时尽量保原测试与自定义构建不失效；无选择才做破坏性更新；最终交付 exe 必须功能正常 | 任务 2.1，不变量 4/8 |
| D3 | `licenseTier` = 授权层级（免费无限 / 免费有限 / 付费），实现方式权衡中 | 任务 2.2 |
| D4 | 目标：新工具接入极简；需要闭源二进制工具的组织与交付方案 | 任务 2.3、2.4 |

## 不变量（任何步骤不得破坏）

1. 工具 = `tools/<tool-id>/tool.manifest.json`，深度恰好一层，无分组目录。
2. `toolId` = 目录名 = tag 段 = 包名前缀，kebab-case，全局唯一；已发布的 id 不改名。
3. tag 格式 `tool/<tool-id>/vX.Y.Z`，一 tag 一工具，版本只来自 tag；已存在的 release 拒绝覆盖。
4. 本地与 CI 共享 `scripts/build-tool.ps1` 单一构建分派；构建与测试参数唯一来源是 manifest。自任务 2.1 起，此入口同时是统一测试控制的载体。
5. validate（静态合法性，干净 clone 零依赖可跑）与 assert-artifacts（构建后存在性）职责分离；存在性检查不得塞回 validate。
6. `risk`（机器被改动程度）与 `licenseTier`（分发授权层级）语义分离；`elevated` 必须有 `## Risk Notice`；`low` 命中特权命令模式即红。
7. 打包最小化：只打包 manifest `artifacts` 声明命中的文件；未声明即不发布。
8. 【新】无手工工具索引：工具清单唯一权威来源是 validate 生成的 `out/tool-index.json`；README 及任何文档不维护工具清单（示例引用与 CHANGELOG 历史记录除外）。
9. 【新】闭源二进制永不入 git；交付物只经 release 资产或私有通道分发。

---

## 阶段 0：准备

1. `git checkout -b codex/architecture-refactor`。
2. 确认基线绿色：`pwsh -File .\scripts\validate-tools.ps1` 输出 `Validated 8 tools with manifest schema 2.1.`。

---

## 阶段 1（P0）：索引自生成 + 清理

### 任务 1.1 移除手工工具索引（D1）

> **状态：✅ 已完成（2026-11-05 14:30）**。README 索引表已删除并替换为 `## Enumerating tools`；validate 末尾打印每工具一行概览；agent-handoff 第 7 步（更新 README 索引）已移除；CHANGELOG 已记录。

**动机**：根 README 的 Tool Index 表是全仓唯一需要人工同步的清单，历史上已发生过漏登记（CHANGELOG 有记录）；工具增长无止境，人工索引必然腐化。

**动作**：
1. 删除根 `README.md` 的 `## Tool Index` 整节；`Repository Conventions` 及其余小节保留。
2. README 增加一小节 `## Enumerating tools`：说明工具清单由 validate 生成——`pwsh -File .\scripts\validate-tools.ps1` 产出 `out/tool-index.json` 并在控制台打印概览。
3. `validate-tools.ps1` 成功路径末尾打印索引概览（每工具一行：`toolId / risk / build.kind / runtime.kind`，约 10 行代码）——给人类即时视图，替代 README 表。
4. `docs/agent-handoff.md` 的 "Independent New-Tool Flow" 删除第 7 步（Update root README tool index）；第 8 步（CHANGELOG）保留并重新编号。
5. 全仓检查：除示例与历史记录外，无文档维护工具清单（`docs/tool-manifest-schema.md` 的 Example、`docs/release-policy.md` 的工具特定 Known limitation 属于示例/注意事项，保留）。

**验收**：validate 通过且末尾打印 8 行索引概览；README 无索引表；`git grep -n "Tool Index" -- README.md docs/` 无维护性索引残留。

**风险**：无。纯删减 + 生成式替代。

### 任务 1.2 清理本地残渣（tempbin，实测 567.9 MB）

> **状态：✅ 已完成（2026-11-05 14:33）**。`tempbin/` 整目录删除（568 MB），用户已确认；未被 git 跟踪，仓库状态不变。

**动机**：`tempbin/dwt-sqlite-spike` 是已否决方案的构建残骸，gitignored、无引用。

**动作**：整目录删除。**不可逆操作，执行前向用户确认**。`.vs/` 属 IDE 本地状态，不动。

**验收**：`git status --short` 仍为空（未被跟踪）。

### 任务 1.3 DWT 内部脚本定位声明（收敛为文档动作）

**动机**：决策 D2 明确保留工具特定的构建/测试脚本；原"双构建入口"问题改由"接线"解决（任务 2.1），而非删除。本任务只剩定位澄清。

**动作**：
1. DWT `README.md` 增改 `## Build & test` 一节，写清三条定位：
   - 发布与 CI 路径 = 仓库根 `scripts/build-tool.ps1`（含 verify 门禁，见任务 2.1）；
   - `tools/build.ps1` 与 `tools/verify.ps1` 是本工具的自定义构建/验收脚本，本地可直接使用；
   - `tools/bootstrap-sdk.ps1` + `tools/dotnet-install.ps1` 服务于无全局 SDK 的本机环境。
2. 不删除任何 DWT 内部脚本。vendor 的 `dotnet-install.ps1`（76 KB 第三方副本）瘦身为可选项，见阶段 3.3。

**验收**：文档更新；validate / build / assert 全绿（无代码变化）。

**风险**：无。

---

## 阶段 2（P1）：schema 2.2 —— 统一测试控制、授权层级、接入简化、闭源支持

### 任务 2.1 统一测试控制：可选 `build.verify` 字段（核心，D2）

**动机**：当前发布链路只证明"产物存在"，不证明"产物能用"。DWT 的 `tools/verify.ps1`（fixture 行为验收，27 KB）是现成的工具特定测试代码，却从不被任何流水线执行。D2 的模型即：治理层决定"何时跑测试"（构建后、发布前、本地与 CI 同一入口），工具自己决定"测试是什么"。

**动作**：
1. `docs/tool-manifest-schema.md` 升 2.2：新增可选 `build.verify`——相对路径，指向工具目录内的 `.ps1`；语义 = 构建成功后必须通过的验收断言；**约束 = 必须可在干净 CI 机器上安全运行**（只读/离线；破坏性检查属于手工验收，不进 verify）。文档写明这是"统一控制、工具特定代码"的接缝。
2. `validate-tools.ps1`：`$supportedManifestVersion` 升 `"2.2"`；对 `build.verify` 复用 `build.script` 的现成校验实现（存在、相对路径、不逃逸工具目录）。
3. `build-tool.ps1`：构建成功（或 manual skip）之后，若声明了 `build.verify` 则执行 `& pwsh -NoProfile -File <verify>`，非零退出码即失败。对 manual 工具同样生效；两类 CI workflow 零改动自动获得该门禁（单一分派入口的设计收益）。
4. DWT manifest 增 `"verify": "tools/verify.ps1"`。兼容性已核对：`verify.ps1` 的默认 `-Dist` 解析到工具根的 `dist/`，与根脚本 `-o dist` 输出一致，无需改动 verify。若后续构建参数变化导致 verify 失效，**允许适配 verify.ps1 本身**（它属于工具特定测试代码）；交付 exe 的功能正确性由 assert-artifacts + verify 双门禁保证——这是 D2"最终交付 exe 功能正常"的落点。
5. 其余 7 个 manifest 的 `manifestVersion` 升 2.2（无行为变化）。

**验收**：本地 `build-tool.ps1 -ToolId windows-disk-write-tracker` 构建后自动执行 verify 断言并透传其输出；人为破坏一个 fixture 期望值 → 命令红；恢复后绿；validate 报 8 工具 schema 2.2 通过。

**风险**：verify 必须 CI 安全——本任务只接线 DWT，其断言为离线只读，满足约束。

### 任务 2.2 `licenseTier` 落地为分发授权层级（D3）

**判断（此字段是否必要）**：**保留，但必须立即获得真实消费者，否则是装饰性字段**。在当前"GitHub 公开 Release"的分发模型里，不引入支付系统就能真实执行的层级语义只有一个——**发布通道门禁**：免费工具走公开 release，受限/付费工具不进公开渠道。工具内订阅校验（license key、在线授权）超出本仓库职责，属远期路线。

**动作**：
1. schema 2.2 重定义枚举：`free`（免费无限）/ `free-limited`（免费有限）/ `paid`（付费）。语义 = 分发授权层级，enforcement 点 = release 工作流。
2. `login-accessibility-cmd-launch` 的 `restricted` → `free-limited`（唯一现存非 free 值）。
3. `validate-tools.ps1` 增加 licenseTier 枚举校验（当前只查非空）。
4. `tool-release.yml` 的 Publish 步骤加门禁：`free` → 正常公开 release；`free-limited` / `paid` → 创建 **draft release**（`--draft`，资产照常上传，公开不可见，可后续转化）。同步更新 `docs/release-policy.md`。
5. schema 文档记录三档语义与"当前 enforcement 仅在分发通道"。
6. 远期路线（只记录，不实现）：分发门禁（本任务）→ 私有通道/下载凭证（结合任务 2.4）→ 工具内许可校验（构建期注入，超出仓库范围）。

**验收**：validate 对非法 tier 红；对 `free-limited` 工具打 tag → 产出 draft release 而非公开 release；free 工具行为不变。

**风险**：draft release 不出现在公开 releases 列表——可见性语义必须写进 release-policy，避免误以为发布失败。

### 任务 2.3 接入脚手架：`scripts/new-tool.ps1`（D4a，唯一新增脚本）

**对"当前能否做到接入极简"的回答**：接近但未到。manual 类工具今天已只要四步（建目录、写 manifest、写 README、validate 绿），剩余摩擦是 manifest 样板与流程记忆。本任务把"从零到绿色基线"压缩为一条命令。

**动作**：
1. 新增 `scripts/new-tool.ps1`（约 80 行，全仓唯一新增脚本）：`-ToolId <kebab-id>` → 校验命名 → 创建 `tools/<id>/`、`tool.manifest.json`（2.2 / manual / low 占位）、`README.md` 骨架（含 `## Risk Notice` 模板与填写指引）、`scripts/run.ps1` 桩（保证 manual 的 entry 存在性校验立即通过）→ 末尾提示"运行 validate 查看基线；替换桩脚本与占位字段"。
2. 与"少脚本"偏好的权衡显式声明：该脚本是接入体验的杠杆点——纯生成、无依赖、不改既有行为；若不采纳，替代方案是 docs 内的模板清单（样板错误仍会发生，收益打折）。
3. `docs/agent-handoff.md` 流程相应简化：1) `new-tool.ps1 -ToolId <id>`；2) 替换桩与字段；3) validate；4)（可构建的工具）build + assert；5) CHANGELOG。

**验收**：`new-tool.ps1 -ToolId sample-tool` 后 validate 全绿（9 工具）；删除该目录后回到 8 工具基线。

### 任务 2.4 闭源二进制工具：组织与交付（D4b）

**原则**：本仓库是源仓库（README 自述 source of truth），闭源二进制**永不入 git**（`.gitignore` 的 `**/dist/**` 已天然拦截）。组织 = manifest 契约照常、产物由外部供给；交付 = release 资产或私有通道。

**关键事实**：现有管道的 validate/assert 职责切分**已经支持**这种形态——工具只要不是 manual kind，entry 存在性就不在静态阶段要求，而在装配后由 assert-artifacts 强制。缺的只是一个显式 kind 与交付通道决策。

**动作**：
1. schema 2.2 新增 `build.kind: binary`：语义 = 无构建步骤、产物由外部供给；可选声明 `build.script`（装配脚本：从私有存储下载或从本地路径复制进 `dist/`）；entry / artifacts 存在性只在 assert-artifacts 阶段强制。
2. `validate-tools.ps1`：将 `binary` 加入"不强制 build.script"的豁免清单（与 manual 同列）；entry 存在性校验对 binary 不适用（同其他非 manual kind）。
3. 交付通道（按推荐序，写入 `docs/release-policy.md`）：
   - **draft release + licenseTier 门禁**（当下可用，零新基础设施）：闭源工具打 tag → draft release（非公开）；授权用户经维护者转化或转移。与任务 2.2 天然联动。
   - **私有镜像仓库**（规模化正解）：同一套治理脚本与 manifest 结构复制到 private repo，二进制进私有 Release（下载需授权）。治理脚本本是通用的，迁移成本≈复制。
   - **外部对象存储 + 签名 URL**（仓库只留 manifest 与文档，适合纯分发型）。
4. schema 文档写入闭源工具最小 recipe：binary kind + 装配脚本 + artifacts pattern 的示例 manifest。

**验收**：构造一个 fixture 闭源工具（binary kind + 拷贝式装配脚本 + 假 exe）走 build → assert → package 全链路通过；validate 在干净 clone（未装配二进制）上不红。

**风险**：装配脚本需要凭据时，凭据只进 CI secrets 或本地环境，绝不进 manifest 或仓库。

### 任务 2.5 发布属性属地划分

**动机**：RID / self-contained / 输出目录应只有一处权威定义。CHANGELOG 已宣布方向："发布属性固化进项目文件，裸 `dotnet publish` 即产出交付物"。

**动作**：确认 DWT 的 `DiskWriteTracker.Cli.csproj` 已含 `RuntimeIdentifier` / `SelfContained` / 输出路径属性（缺则先补项目文件），然后从 manifest `build.args` 移除 `-r win-x64 --self-contained true -o dist`，只保留项目文件覆盖不了的参数。

**验收**：`build-tool.ps1 -ToolId windows-disk-write-tracker` 产物路径与内容不变，assert-artifacts + verify（任务 2.1）双确认。

### 任务 2.6 接入约定文档化

1. 新增工具的 runtime entry 文件名使用 ASCII kebab-case；中文语义放 `displayName` 与 README。存量中文文件名工具不强制改名（能跑就不动）。
2. 保留值声明：`node` / `go` / `rust` = 经 `build.script` 驱动，无专用分派；`container` / `library` = 保留未实现。
3. 落点：`docs/tool-manifest-schema.md` 新增 `## New tool conventions` 一节。

---

## 阶段 3（P2，可选，按触发条件执行）

| 任务 | 内容 | 触发条件 |
|---|---|---|
| 3.1 | manifest 枚举去重：`scripts/lib/manifest.ps1`（dot-source 单文件），validate / assert / package 共用 | 枚举口径再次变更时；仅一处消费变化时不做——"少脚本"偏好下，注释锚定三处各约 10 行的重复是可接受的 KISS 权衡 |
| 3.2 | 校验器 fixture 测试：1 个合法 + 4 个非法 manifest（嵌套 / toolId 不符 / low 含特权命令 / 缺 Risk Notice） | 治理脚本下次大改时（schema 2.2 升级顺手做） |
| 3.3 | DWT vendor installer 瘦身：`bootstrap-sdk.ps1` 改为按需下载 `dotnet-install.ps1` 而非维护 76 KB 副本 | 需用户确认；D2 语境下默认保留现状 |
| 3.4 | dotnet 工具启用 `packages.lock.json`，恢复 `setup-dotnet` 缓存 | CI 变慢时 |

---

## 问题 → 方案映射

| 问题 | 方案 | 阶段 |
|---|---|---|
| 手工索引漂移（历史已发生） | 1.1 索引自生成 | 1 |
| tempbin 567.9 MB 本地残渣 | 1.2 | 1 |
| DWT 内部脚本定位不清 | 1.3 定位声明 + 2.1 接线 | 1/2 |
| 验收断言未进流水线（只证存在、不证能用） | 2.1 `build.verify` 统一测试控制 | 2 |
| `licenseTier` 无语义无消费者 | 2.2 三档枚举 + draft 门禁 | 2 |
| 接入样板摩擦 | 2.3 `new-tool.ps1` | 2 |
| 闭源二进制无组织方案 | 2.4 `binary` kind + 交付通道 | 2 |
| 发布属性 manifest/csxproj 双写 | 2.5 | 2 |
| entry 文件名编码风险；保留 kind 未声明 | 2.6 | 2 |
| 枚举重复 / 校验器无测试 / vendor 瘦身 / 锁文件 | 3.1–3.4 | 3 |

---

## 总验收清单（重构完成后逐项执行）

```powershell
# 1. 静态校验 + 索引概览打印：期望 8 工具 schema 2.2
pwsh -File .\scripts\validate-tools.ps1

# 2. 构建全部工具（DWT 构建后自动执行 verify 验收断言）
pwsh -File .\scripts\build-tool.ps1

# 3. 产物存在性
pwsh -File .\scripts\assert-artifacts.ps1

# 4. 打包 + SHA256
pwsh -File .\scripts\package-tool.ps1 -Version 0.0.0-local

# 5. 脚手架往返：创建 → validate 绿（9 工具）→ 删除
pwsh -File .\scripts\new-tool.ps1 -ToolId sample-tool
pwsh -File .\scripts\validate-tools.ps1

# 6. 工作树干净（out/ 已被 gitignore）
git status --short

# 7. 无手工索引残留
git grep -n "Tool Index" -- README.md docs/
```

**文档同步项**：`CHANGELOG.md`（每任务一条）；`docs/tool-manifest-schema.md`（2.2 版本历史 + New tool conventions + 闭源 recipe）；`docs/agent-handoff.md`（流程简化）；`docs/release-policy.md`（draft 门禁 + 闭源交付通道）；DWT `README.md`（构建定位）；根 `README.md`（索引节替换为 Enumerating tools）。
