# ModpackManager 架构文档 - 第三部分：接口契约与工程结构

## 5. 核心接口设计

核心接口是 `Core` 层与外界沟通的契约。接口设计保持精简，拒绝为不必要的灵活性设计过多的多态。

### 5.1 外部适配器契约 (Interfaces)
为了完美隔离 `KKManager Core`，使其不暴露给 UI 和核心逻辑：

```csharp
namespace ModpackManager.Core.Interfaces
{
    using System.Threading.Tasks;
    using ModpackManager.Core.Models;

    public interface IKKManagerAdapter
    {
        /// <summary>
        /// 解析指定路径的 zipmod 文件，将其转换为本系统的标准元数据模型
        /// </summary>
        /// <param name="filePath">zipmod 文件的绝对路径</param>
        /// <returns>若解析失败或非合法 Mod，返回 null</returns>
        Task<ModMetadata?> ParseZipmodAsync(string filePath);
    }
}

```

### 5.2 扫描与引擎契约

```csharp
namespace ModpackManager.Core.Interfaces
{
    using System;
    using System.Collections.Generic;
    using ModpackManager.Core.Models;

    public interface IModScanner
    {
        /// <summary>
        /// 异步流式扫描目标文件夹，支持进度回调与取消令牌
        /// </summary>
        IAsyncEnumerable<ModMetadata> ScanDirectoryAsync(
            string folderPath, 
            IProgress<double>? progress = null, 
            System.Threading.CancellationToken cancellationToken = default);
    }

    public interface IModDependencyResolver
    {
        /// <summary>
        /// 针对指定 Mod 集合，计算其完整的依赖树状态（包括已安装、未安装、循环依赖等）
        /// </summary>
        DependencyReport ResolveDependencies(IEnumerable<ModMetadata> targetMods);
    }
}

```

### 5.3 基础设施与仓储契约

```csharp
namespace ModpackManager.Core.Interfaces
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using ModpackManager.Core.Models;

    public interface IModRepository
    {
        Task SaveBatchAsync(IEnumerable<ModMetadata> mods);
        Task<IEnumerable<ModMetadata>> GetAllModsAsync();
        Task<ModMetadata?> GetByGuidAsync(string guid);
        Task ClearAllAsync();
    }

    public interface IDownloader
    {
        Task DownloadFileAsync(
            string url, 
            string savePath, 
            IProgress<DownloadProgressInfo>? progress = null, 
            CancellationToken cancellationToken = default);
    }
}

```

---

## 6. 全局目录结构

为了彻底贯彻 **反过度设计** 的理念，整个解决方案仅包含 4 个物理项目。以下为推荐的物理工程及命名空间目录树：

```text
src/
├── ModpackManager.sln                  # 解决方案入口
│
├── ModpackManager.UI/                  # WPF 壳工程（可执行程序）
│   ├── App.xaml / App.xaml.cs          # 全局生命周期与样式配置
│   ├── Program.cs                      # 包含 Main 方法，负责 Host 创建及 DI 容器初始化
│   ├── Views/                          # 仅存放纯粹的 XAML 视图与必要的 View-Behind
│   │   ├── MainWindow.xaml
│   │   ├── ModListView.xaml
│   │   └── DownloadManagerView.xaml
│   ├── ViewModels/                     # 界面驱动逻辑（继承自 CommunityToolkit.Mvvm）
│   │   ├── MainWindowViewModel.cs
│   │   ├── ModListViewModel.cs
│   │   └── DownloadViewModel.cs
│   └── Converters/                     # WPF 值转换器
│
├── ModpackManager.Core/                # 核心业务库（纯 .NET Standard / Core，无 UI 依赖）
│   ├── Models/                         # 领域实体/贫血模型（纯属性）
│   │   ├── ModMetadata.cs              # Mod 元数据实体
│   │   ├── DependencyReport.cs         # 依赖解析报告
│   │   └── AppConfig.cs                # 全局配置项
│   ├── Engine/                         # 核心算法引擎实现
│   │   ├── ModScanner.cs               # 异步扫描器
│   │   ├── Deduplicator.cs             # 去重处理器
│   │   └── DependencyResolver.cs       # 拓扑解析器
│   ├── Services/                       # 业务应用服务
│   │   ├── SyncService.cs              # 远程仓储同步服务
│   │   └── DownloadScheduler.cs        # 下载调度管理器
│   └── Interfaces/                          # 所有核心对外的接口/契约定义（同上文第5章）
│
├── ModpackManager.Adapter.KKManager/   # 外部防腐层实现项目
│   ├── KKManagerAdapter.cs             # 内部引用 KKManager.Core.dll，实现 IKKManagerAdapter
│   └── ModelExtensions.cs              # 外部对象与内部模型的 mapping 逻辑
│
└── ModpackManager.Storage/             # 存储驱动实现项目
    ├── LiteDbRepository.cs             # IModRepository 的 LiteDB 实现
    └── LocalConfigStorage.cs           # 基于 Json 文件的配置持久化

```