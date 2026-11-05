namespace DiskWriteTracker.Core.Config;

/// <summary>
/// 引擎运行参数。每个字段都有真实消费者，default.json 与这里的字段严格一一对应。
/// 不预留"先占位、以后实现"的键：那种键只会让用户以为自己能改，改了却没有任何效果。
/// </summary>
public sealed record AppConfig
{
    /// <summary>默认数据目录（可含 %VAR%），便携目录不存在时生效。</summary>
    public string DataDirectory { get; init; } = @"%ProgramData%\DiskWriteTracker";

    /// <summary>ProgramData 不可写时的降级目录。</summary>
    public string FallbackDirectory { get; init; } = @"%LOCALAPPDATA%\DiskWriteTracker";

    /// <summary>样本保留天数。采样成功后据此剪枝。</summary>
    public int RetentionDays { get; init; } = 3650;

    /// <summary>单次计划任务子进程的超时秒数。</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>每日采样时间，HH:mm。</summary>
    public string DailyTime { get; init; } = "00:05";

    /// <summary>是否启用磨损一致性闸门。关闭后 SATA 读数降级为 fallback 而非 reliable。</summary>
    public bool RequireWearGate { get; init; } = true;

    /// <summary>INFO 或 ERROR。ERROR 时不写采样成功行。</summary>
    public string LogLevel { get; init; } = "INFO";

    /// <summary>日志按文件名日期保留的天数，与样本的 RetentionDays 互相独立。</summary>
    public int LogRetentionDays { get; init; } = 30;

    /// <summary>单个日志文件的体积上限，达到即滚动。默认 1 MiB。</summary>
    public long LogMaxFileBytes { get; init; } = 1024 * 1024;

    /// <summary>加载过程中的非致命问题，界面与 doctor 必须展示，不得静默。</summary>
    public string? LoadWarning { get; init; }

    /// <summary>实际读取到的配置文件路径，未找到时为空串。</summary>
    public string SourcePath { get; init; } = string.Empty;

    public static AppConfig Defaults { get; } = new();
}
