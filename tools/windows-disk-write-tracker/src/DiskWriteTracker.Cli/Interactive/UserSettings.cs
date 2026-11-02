using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 用户偏好。与程序配置分开：这里的每一项用户都可能想改，程序配置（保留期、磨损门限）则不。
/// </summary>
internal sealed record UserSettings(int SchemaVersion, int ReportDays, int ExportDays, string Time, int LogDays)
{
    public const int CurrentSchemaVersion = 1;
    public const int MinDays = 1;
    public const int MaxDays = 3650;

    public static UserSettings Defaults { get; } = new(CurrentSchemaVersion, 400, 400, "00:05", 30);

    private static readonly Regex TimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    /// <summary>把外部来的值收进合法区间。任何越界都回落到默认值而不是就近截断，避免 0 天变成 1 天这种意外。</summary>
    public static UserSettings Normalize(UserSettings raw, string? engineDailyTime)
    {
        var d = Defaults;

        return new UserSettings(
            SchemaVersion: CurrentSchemaVersion,
            ReportDays: InRange(raw.ReportDays) ? raw.ReportDays : d.ReportDays,
            ExportDays: InRange(raw.ExportDays) ? raw.ExportDays : d.ExportDays,
            Time: NormalizeTime(raw.Time) ?? NormalizeTime(engineDailyTime) ?? d.Time,
            LogDays: InRange(raw.LogDays) ? raw.LogDays : d.LogDays);
    }

    public static bool IsValidTime(string? value) => !string.IsNullOrWhiteSpace(value) && TimePattern.IsMatch(value.Trim());

    private static bool InRange(int value) => value is >= MinDays and <= MaxDays;

    private static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        return TimePattern.IsMatch(trimmed)
            ? trimmed
            : null;
    }
}
