# ModpackManager 架构文档 - 第四部分：敏捷开发顺序与架构决策记录

## 7. 开发顺序与迭代路线

### 7.1 Phase 1：打通管道与基础解析 (第 1 周)

* **目标**：跑通依赖注入，能够正确读取并解析出一个 `.zipmod` 的信息。
* **任务**：
1. 创建解决方案，组装 4 个核心项目。
2. 在 `ModpackManager.UI` 中引入 `Microsoft.Extensions.DependencyInjection`，完成基础 IOC 容器搭建。
3. 实现 `ModpackManager.Adapter.KKManager`，编写单元测试，确保其能够成功调用 `KKManager Core` 并输出清洗后的 `ModMetadata`。


### 7.2 Phase 2：本地核心引擎与持久化 (第 2~3 周)

* **目标**：实现万级 Mod 的快速扫描、落库、去重和冲突检测。
* **任务**：
1. 引入 `LiteDB`，编写 `LiteDbRepository` 驱动。
2. 实现 `ModScanner`，使用 `IAsyncEnumerable` 逐步吐出数据，并集成去重、冲突检测算法。
3. 此时无需做高级 UI，编写一个控制台测试程序或极其简单的 WPF 按钮，确保扫描 1000 个 Mod 不卡顿且数据正确落库。


### 7.3 Phase 3：网络互联与依赖解析 (第 4 周)

* **目标**：支持远程同步，能够算出“我缺了哪个 Mod”。
* **任务**：
1. 基于 DFS（深度优先搜索）实现 `DependencyResolver`。
2. 编写 `SyncService`，利用 `HttpClient` 拉取远程 JSON 格式的元数据仓库并缓存。
3. 实现简易的 `Downloader` 队列，支持基本的暂停与进度回报。


### 7.4 Phase 4：UI 拼装、性能润色与交付 (第 5 周~)

* **目标**：构建精美的用户界面，确保生产环境丝滑稳定。
* **任务**：
1. 利用 `CommunityToolkit.Mvvm` 的 Source Generator 功能快速编写 ViewModels。
2. 拼装 WPF 界面，列表必须开启 `VirtualizingStackPanel.IsVirtualizing="True"`。
3. 接入全局异常处理（`App.xaml.cs` 中的 `DispatcherUnhandledException`），防止程序闪退，准备发布 V1.0。


---

## 8. 架构决策记录 (ADR)

### ADR-001: 采用 CommunityToolkit.Mvvm 替代 Prism / ReactiveUI

* **上下文**：WPF 项目必须选用一款 MVVM 框架以分离视图与逻辑。Prism 历史悠久但过于沉重，包含过度的模块化（Module）与复杂的导航（Navigation）概念；ReactiveUI 采用响应式编程，学习曲线过于陡峭，不利于 1~2 人快速交付。
* **决策**：决定全面采用微软官方推荐的 `CommunityToolkit.Mvvm`（原 MvvmLight 精神续作）。
* **理由**：
1. **生产力极高**：利用 C# 编译期源生成器（Source Generators），开发者只需写 `[ObservableProperty]` 和 `[RelayCommand]`，成百上千行的样板代码由编译器自动生成。
2. **极轻量**：无任何框架包袱，本质上只是对 `INotifyPropertyChanged` 的优雅封装，符合 KISS 原则。


### ADR-002: 使用 LiteDB 替代 SQLite + EF Core

* **上下文**：系统需要对海量的 Mod 元数据及远程仓库进行本地持久化，以便在二次启动时达到“秒开”体验。
* **决策**：选用纯 C# 编写的嵌入式 NoSQL 数据库 `LiteDB`，放弃传统的关系型数据库。
* **理由**：
1. **零部署负担**：SQLite 往往需要携带特定平台的原生底层 DLL（Runtimes），而 LiteDB 是 100% 纯托管 C# 代码，只有一个 DLL，分发极简。
2. **Schema-less（无模式）特性**：Mod 的元数据结构未来极有可能伴随游戏更新或 KKManager 的升级而频繁变动。如果使用 EF Core，频繁的 `Migration` 会带来巨大的维护成本。LiteDB 允许我们直接将 POCO 对象当作 Document 存取，极其自由。
3. **性能契合**：在万级数据量规模下，LiteDB 的 B+ 树索引查询速度极快，内存占用极小。


### ADR-003: 建立严格的 Adapter 隔离层（防腐层）

* **上下文**：`KKManager Core` 是社区现成的、最优秀的 zipmod 解析库，但其内部代码历史包袱较重，且其 API 随时可能发生破坏性变更（Breaking Changes）。
* **决策**：严禁在 `ModpackManager.Core` 和 `ModpackManager.UI` 中直接引用 KKManager 的任何程序集或命名空间。所有调用必须收拢在 `ModpackManager.Adapter.KKManager` 中。
* **理由**：
1. **资产保护**：确保我们自己编写的核心去重、依赖解析算法依赖的是绝对纯净、可控的 `ModMetadata` 模型。
2. **降低耦合**：如果未来 KKManager 停止维护，或者团队决定自行重写解析引擎，我们只需重写或替换 Adapter 项目，主程序的核心业务代码和 UI 代码可以做到“一行不改”。


### ADR-004: 选用 `IAsyncEnumerable<T>` 作为扫描流传递介质

* **上下文**：在扫描包含上万个 Mod 的物理文件夹时，如果采用传统的 `Task<List<ModMetadata>>` 异步返回，会导致主程序在扫描期间长时间静止，且在返回瞬间内存暴涨。
* **决策**：核心扫描接口全面采用 .NET Core 引入的 `IAsyncEnumerable<T>` 异步流。
* **理由**：
1. **流式消费**：扫描引擎每解析完一个 zipmod，就可以立刻通过 `yield return` 吐给上层，UI 界面可以立即感知并呈现在列表中，用户体验极佳。
2. **内存极低**：由于不需要在内存中积压整个列表，内存开销保持平稳，完美解决了性能瓶颈。