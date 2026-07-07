# ModpackManager 架构文档 - 第二部分：模块划分与数据流

## 3. 模块划分与职责说明

在单一解决方案内，我们通过合理的命名空间（Namespace）和模块划分，让 1~2 名开发者能够各司其职，互不干扰。

| 模块名称 | 隶属项目 | 职责描述 | 核心技术与关键点 |
| :--- | :--- | :--- | :--- |
| **UI 呈现模块** | `ModpackManager.UI` | 负责多页面路由、虚拟化列表展示、全局异常捕获以及皮肤样式。 | 利用 `VirtualizingStackPanel` 解决万级 Mod 列表滚动卡顿问题。 |
| **Mod 扫描引擎** | `ModpackManager.Core` | 遍历指定游戏目录，过滤出 `.zipmod` 文件，触发解析流程。 | 采用 `.NET ThreadPool` 与 `IAsyncEnumerable` 混合调度，避免死锁。 |
| **冲突与去重模块** | `ModpackManager.Core` | 比对 Mod 的哈希进行去重；比对内部文件路径识别 SideLoader 覆盖冲突。 | 使用高效的内存 `HashSet` 与位运算进行快速交集判定。 |
| **依赖解析引擎** | `ModpackManager.Core` | 解析 Mod 清单中的 `GUID` 依赖项，计算完整的安装拓扑序。 | 基于有向无环图（DAG）的深度优先搜索（DFS），支持循环依赖检测。 |
| **仓库同步与下载** | `ModpackManager.Core` | 抓取远程社区 Repository 的 JSON 索引，控制并发下载通道，实现断点续传。 | `HttpClient` 结合文件流写入，内置指数退避重试机制（Polly 简化版）。 |
| **外部适配器** | `ModpackManager.Adapter.*` | 隔离 `KKManager Core` 的内部复杂性。 | 严格的类型转换与异常吞咽，防止外部异常导致主程序崩溃。 |
| **轻量存储模块** | `ModpackManager.Storage` | 实现本地数据的增删改查。 | 基于 `LiteDB` 的 BsonDocument 或 POCO 直接映射，无 SQL 编写负担。 |

---

## 4. 核心数据流向设计

为了保持系统的 KISS 原则，我们避免使用复杂的事件溯源或全局事件总线，数据流采用 **单向响应式流动** 与 **显式管道传递**。

### 4.1 Mod 扫描、去重与冲突检测流
当用户指定游戏路径并点击“扫描”时，数据流向如下：


```text

[UI 触发扫描命令]
|
v
[ModScanner (Core)] ----> 异步遍历物理磁盘目录 (*.zipmod)
|
| (产生文件路径流: string)
v
[IKKManagerAdapter] ----> 调用 KKManager 提取 Zip 内部 manifest.xml / metadata
|
| (转换为系统统一模型: ModMetadata)
v
[Deduplicator] ---------> 计算元数据 Hash / GUID 查重 -> [标记为 Duplicate]
|
v
[ConflictDetector] -----> 比对内部资产路径（如 sub-assets） -> [标记为 Conflict]
|
v
[LiteDB Storage] -------> 批量非阻塞写入本地数据库缓存
|
| (通过 IProgress 或 异步流分批通知)
v
[UI ViewModels] --------> 将数据追加至 ObservableCollection -> [WPF 界面刷新]

```

### 4.2 依赖自动补全与下载流
当玩家勾选某个 Mod 并执行“检查并补全依赖”时：

```text
[UI 选中特定 Mod] -> 传入 [DependencyResolver (Core)]
|
v
[分析该 Mod 的 Dependencies 清单]
|
+---------------+---------------+
|                               |
v (本地查找)                    v (若本地缺失)
[查询本地 LiteDB]               [提取缺失的 Mod GUID]
|                               |
(已存在则跳过)                         v
[查询本地已同步的远程 Repo 缓存]
|
v
[获取对应的下载 Url 与文件大小]
|
v
[推入 Downloader 队列 (串行/并发2)]
|
v
[下载完成 -> 触发 Installer 释放至 Mods 目录]
|
v
[对该新文件局部重新扫描 -> 刷新 UI 状态]
```