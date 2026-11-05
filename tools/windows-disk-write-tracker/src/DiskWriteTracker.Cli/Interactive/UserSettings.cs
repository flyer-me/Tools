using System.Globalization;
using System.Text.RegularExpressions;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 用户偏好。与程序配置分开：这里的每一项用户都可能想改，程序配置（保留期、磨损门限）则不。
///
/// schemaVersion 2 去掉了 logDays。它从第一版起就只被读写、从未参与剪枝，
/// 是个"看起来能配、配了没反应"的死字段。日志保留改由 config\default.json 的
/// logging.retentionDays 统一治理 —— 那是引擎行为，计划任务也受它约束，本来就不该藏在用户偏好里。
/// </summary>
internal sealed record UserSettings(int SchemaVersion, int ReportDays, int ExportDays, string Time)
{
    public const int CurrentSchemaVersion = 2;
    public const int MinDays = 1;
    public const int MaxDays = 3650;

    public static UserSettings Defaults { get; } = new(CurrentSchemaVersion, 400, 400, "00:05");

    private static readonly Regex TimePattern = new(@"^([01]\d|2[0-3]):[0-5]\d$", RegexOptions.Compiled);

    /// <summary>把外部来的值收进合法区间。任何越界都回落到默认值而不是就近截断，避免 0 天变成 1 天这种意外。</summary>
    public static UserSettings Normalize(UserSettings raw, string? engineDailyTime)
    {
        var d = Defaults;

        return new UserSettings(
            // 只升不降：把用户手写的未来版本号降回当前版本，会让新版本加的字段在
            // 一次保存后永久消失；未知字段原样保留。
            SchemaVersion: raw.SchemaVersion > CurrentSchemaVersion ? raw.SchemaVersion : CurrentSchemaVersion,
            ReportDays: InRange(raw.ReportDays) ? raw.ReportDays : d.ReportDays,
            ExportDays: InRange(raw.ExportDays) ? raw.ExportDays : d.ExportDays,
            Time: NormalizeTime(raw.Time) ?? NormalizeTime(engineDailyTime) ?? d.Time);
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
