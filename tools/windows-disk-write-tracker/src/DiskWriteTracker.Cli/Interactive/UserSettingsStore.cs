using System.Text.Json;
using System.Text.Json.Nodes;
using DiskWriteTracker.Cli.Reports;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 用户偏好的读写。落在数据目录的 config\settings.json，用户可以用记事本直接改。
///
/// 为什么不在 data 根：data 根是"数据"的地盘（samples-*.jsonl、logs\、exports\），
/// 而 settings.json 是"配置"。混在一层，任何一个清理动作都无法用"删数据、留配置"这一条规则表达，
/// 只能给单个文件名开特例 —— 特例是漏删和误删的共同来源。
///
/// 迁移链：exe\config\launcher.json（最早） → data\settings.json（schemaVersion 1） → data\config\settings.json（2）。
/// 每一步都是"解析成功才落新址、落址成功才删旧文件"，解析失败一律原样留着当证据。
/// </summary>
internal static class UserSettingsStore
{
    private const string FileName = "settings.json";
    private const string ConfigDirName = "config";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string PathFor(string dataDirectory)
        => Path.Combine(dataDirectory, ConfigDirName, FileName);

    /// <summary>schemaVersion 1 的位置：直接躺在 data 根。</summary>
    public static string LegacyPathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static UserSettings Load(string dataDirectory, string? engineDailyTime)
    {
        string path = PathFor(dataDirectory);

        if (!File.Exists(path))
            MigrateFromDataRoot(dataDirectory);

        if (!File.Exists(path))
            MigrateFromExeConfig(dataDirectory);

        var raw = TryReadWithBackup(path);
        var settings = UserSettings.Normalize(raw ?? UserSettings.Defaults, engineDailyTime);

        // 第一次运行就把文件写出来，用户才知道有这么个东西可以改。
        // 解析失败的情况（raw == null 且文件存在）不写回，保留原文件当证据。
        if (!File.Exists(path) && raw is null)
            TrySave(dataDirectory, settings);

        return settings;
    }

    public static bool TrySave(string dataDirectory, UserSettings settings)
    {
        try
        {
            string path = PathFor(dataDirectory);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            var payload = new JsonObject
            {
                ["schemaVersion"] = settings.SchemaVersion,
                ["reportDays"] = settings.ReportDays,
                ["exportDays"] = settings.ExportDays,
                ["time"] = settings.Time
            };

            ReportWriter.WriteAtomic(path, payload.ToJsonString(WriteOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// schemaVersion 1 → 2：把 data\settings.json 搬到 data\config\settings.json。
    /// 解析成功才搬，搬完读回来确认再删旧文件；顺序反了会在磁盘满或权限异常时丢掉用户偏好。
    /// </summary>
    public static bool MigrateFromDataRoot(string dataDirectory)
    {
        try
        {
            string legacy = LegacyPathFor(dataDirectory);
            string target = PathFor(dataDirectory);

            if (!File.Exists(legacy) || File.Exists(target))
                return false;

            var parsed = TryReadFile(legacy);
            if (parsed is null)
                return false;

            if (!TrySave(dataDirectory, UserSettings.Normalize(parsed, null)))
                return false;

            // 读回来确认新文件真的可解析，再删旧文件
            if (TryReadFile(target) is null)
                return false;

            try { File.Delete(legacy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 更早的版本把偏好放在 exe 同目录的 config\launcher.json。搬到数据目录下的 config\settings.json，
    /// 成功才删原文件与随之变空的 config 目录（只删空目录，绝不递归）。
    /// </summary>
    public static bool MigrateFromExeConfig(string dataDirectory)
    {
        try
        {
            string legacy = Path.Combine(AppContext.BaseDirectory, "config", "launcher.json");
            string target = PathFor(dataDirectory);

            if (!File.Exists(legacy) || File.Exists(target))
                return false;

            var parsed = TryReadFile(legacy);
            if (parsed is null)
                return false;

            if (!TrySave(dataDirectory, UserSettings.Normalize(parsed, null)))
                return false;

            if (TryReadFile(target) is null)
                return false;

            try { File.Delete(legacy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            try
            {
                string? dir = Path.GetDirectoryName(legacy);
                if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 主文件读不出来就退到 .bak —— 每次保存前都会留一份，它是最后一个"用户真的这么设过"的证据。
    /// 都没有才回落默认值。
    /// </summary>
    private static UserSettings? TryReadWithBackup(string path)
    {
        var primary = TryReadFile(path);
        if (primary is not null)
            return primary;

        return TryReadFile(path + ".bak");
    }

    private static UserSettings? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var d = UserSettings.Defaults;

            return new UserSettings(
                ReadInt(root, "schemaVersion") ?? d.SchemaVersion,
                ReadInt(root, "reportDays") ?? d.ReportDays,
                ReadInt(root, "exportDays") ?? d.ExportDays,
                ReadString(root, "time") ?? d.Time);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
            return null;

        return v.TryGetInt32(out int n) ? n : null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
            return null;

        string? text = v.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
