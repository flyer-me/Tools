using System.Globalization;
using DiskWriteTracker.Core.Config;

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
            if (!Directory.Exists(DataDirectory))
                return;

            DateOnly? newest = null;

            foreach (string file in Directory.EnumerateFiles(DataDirectory, "samples-*.jsonl"))
            {
                SampleCount += File.ReadLines(file).Count(line => !string.IsNullOrWhiteSpace(line));

                string name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != 16)
                    continue;

                if (!DateOnly.TryParseExact(name.AsSpan(8), "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly date))
                    continue;

                if (newest is null || date > newest.Value)
                    newest = date;
            }

            if (newest is not null)
            {
                int days = DateOnly.FromDateTime(DateTime.Now).DayNumber - newest.Value.DayNumber;
                StaleDays = days < 0 ? 0 : days;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到就当没有样本，菜单仍可用
        }
    }

    public void RefreshTaskStatus() => TaskStatus = TaskStatusReader.Query();
}
