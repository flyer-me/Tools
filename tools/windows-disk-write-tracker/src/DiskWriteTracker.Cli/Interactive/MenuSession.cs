using DiskWriteTracker.Core.Config;
using DiskWriteTracker.Core.Logging;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 一次菜单运行期间的只读状态快照。数据目录与路径解析只做一次，避免每个动作都重新算。
/// </summary>
internal sealed class MenuSession
{
    private MenuSession(string dataDirectory, bool isPortable, bool isElevated, UserSettings settings)
    {
        DataDirectory = dataDirectory;
        IsPortable = isPortable;
        IsElevated = isElevated;
        Settings = settings;
    }

    public string DataDirectory { get; }

    public bool IsPortable { get; }

    public bool IsElevated { get; }

    public UserSettings Settings { get; set; }

    public int SampleCount { get; private set; }

    public int StaleDays { get; private set; }

    public string ExportDir => Path.Combine(DataDirectory, "exports");

    public string ReportPath => Path.Combine(ExportDir, "report.html");

    public string LogDir => Path.Combine(DataDirectory, "logs");

    public TaskStatusSnapshot? TaskStatus { get; private set; }

    public static MenuSession Create()
    {
        var config = AppConfigLoader.Load();

        // 不传 --data-dir：让引擎自己判断便携（exe 同级有 data\ 就是便携），
        // 菜单传显式路径会把 IsPortable 改成 false，日志里的 portable= 字段就失真了。
        var resolution = RuntimePaths.ResolveDataDirectory(null, config);
        var settings = UserSettingsStore.Load(resolution.Directory, config.DailyTime);

        // 菜单是"每天至少一次"的第二个入口：装了计划任务的机器上采样已经剪过，
        // 纯手动用的机器上，剪枝只在此处触发。
        LogRetention.PruneExpired(
            resolution.Directory, config.LogRetentionDays, DateOnly.FromDateTime(DateTime.Now));

        var session = new MenuSession(resolution.Directory, resolution.IsPortable, Elevation.IsElevated, settings);
        session.RefreshSampleStats();
        session.RefreshTaskStatus();
        return session;
    }

    /// <summary>数行而不解析 JSON：7300 行也只要几毫秒。</summary>
    public void RefreshSampleStats()
    {
        SampleCount = 0;
        StaleDays = 0;

        try
        {
            var files = SampleFile.Enumerate(DataDirectory);
            if (files.Count == 0)
                return;

            SampleCount = new JsonlSampleStore(DataDirectory).CountLines();

            // Enumerate 已按日期升序，末位即最新
            int days = DateOnly.FromDateTime(DateTime.Now).DayNumber - files[^1].Date.DayNumber;
            StaleDays = days < 0 ? 0 : days;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读取失败时按无样本处理
        }
    }

    public void RefreshTaskStatus() => TaskStatus = TaskStatusReader.Query();
}
