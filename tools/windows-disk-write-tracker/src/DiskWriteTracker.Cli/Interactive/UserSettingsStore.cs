using System.Text.Json;
using System.Text.Json.Nodes;
using DiskWriteTracker.Cli.Reports;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 用户偏好的读写。落在数据目录下的 settings.json，用户可以用记事本直接改。
/// 老版本把它放在 exe 同目录的 config\launcher.json，首次加载时会搬过来并删掉旧文件。
/// </summary>
internal static class UserSettingsStore
{
    private const string FileName = "settings.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static UserSettings Load(string dataDirectory, string? engineDailyTime)
    {
        string path = PathFor(dataDirectory);

        if (!File.Exists(path))
            MigrateLegacy(dataDirectory);

        var raw = TryReadFile(path);
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
            Directory.CreateDirectory(dataDirectory);

            string path = PathFor(dataDirectory);

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
                ["time"] = settings.Time,
                ["logDays"] = settings.LogDays
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
    /// 把老版本的 config\launcher.json 搬到 settings.json。成功才删原文件；
    /// 解析失败一律不动，避免吃掉用户手改过的内容。
    /// </summary>
    public static bool MigrateLegacy(string dataDirectory)
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

            try { File.Delete(legacy); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            // 只删空目录，绝不递归
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
                ReadString(root, "time") ?? d.Time,
                ReadInt(root, "logDays") ?? d.LogDays);
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
