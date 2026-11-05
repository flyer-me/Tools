using System.Text;
using System.Text.Json;

namespace DiskWriteTracker.Core.Config;

/// <summary>
/// 极简配置加载：优先读 exe 同目录的 config\default.json（运维覆盖用），
/// 外部文件不存在时读编进程序集的同一份副本 —— 最终用户拿到的目录里因此不需要 config 文件夹。
/// 文件缺失、字段缺失、JSON 损坏一律回退到内置默认值，并把原因放进 LoadWarning —— 绝不抛异常，
/// 否则计划任务会因为一个打错的逗号每天静默失败。
/// </summary>
public static class AppConfigLoader
{
    private const string EmbeddedResourceName = "default.json";
    private const string EmbeddedSourcePath = "embedded:default.json";

    public static AppConfig Load()
    {
        string externalPath = Path.Combine(AppContext.BaseDirectory, "config", "default.json");

        string? json = null;
        string sourcePath = externalPath;

        if (File.Exists(externalPath))
        {
            try
            {
                json = File.ReadAllText(externalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                json = null;
            }
        }

        if (json is null)
        {
            json = ReadEmbedded();
            if (json is not null)
                sourcePath = EmbeddedSourcePath;
        }

        if (json is null)
            return AppConfig.Defaults with { SourcePath = externalPath };

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var defaults = AppConfig.Defaults;

            return defaults with
            {
                DataDirectory = ReadString(root, "data", "directory") ?? defaults.DataDirectory,
                FallbackDirectory = ReadString(root, "data", "fallbackDirectory") ?? defaults.FallbackDirectory,
                RetentionDays = Clamp(ReadInt(root, "data", "retentionDays") ?? defaults.RetentionDays, 1, 36500),
                TimeoutSeconds = Clamp(ReadInt(root, "sampling", "timeoutSeconds") ?? defaults.TimeoutSeconds, 5, 600),
                DailyTime = NormalizeTime(ReadString(root, "sampling", "dailyTime")) ?? defaults.DailyTime,
                RequireWearGate = ReadBool(root, "units", "requireWearGate") ?? defaults.RequireWearGate,
                LogLevel = (ReadString(root, "logging", "level") ?? defaults.LogLevel).ToUpperInvariant(),
                LogRetentionDays = Clamp(ReadInt(root, "logging", "retentionDays") ?? defaults.LogRetentionDays, 1, 3650),
                LogMaxFileBytes = Clamp(ReadInt(root, "logging", "maxFileBytes") ?? (int)defaults.LogMaxFileBytes, 64 * 1024, 64 * 1024 * 1024),
                SourcePath = sourcePath
            };
        }
        catch (JsonException ex)
        {
            return AppConfig.Defaults with
            {
                SourcePath = sourcePath,
                LoadWarning = $"配置文件解析失败，已回退到内置默认值：{ex.Message}"
            };
        }
    }

    private static string? ReadEmbedded()
    {
        try
        {
            using var stream = typeof(AppConfigLoader).Assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null)
                return null;

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static JsonElement? Child(JsonElement root, string section)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(section, out var e) && e.ValueKind == JsonValueKind.Object
            ? e
            : null;

    private static string? ReadString(JsonElement root, string section, string name)
    {
        var s = Child(root, section);
        if (s is null) return null;
        if (!s.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        string? text = v.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadInt(JsonElement root, string section, string name)
    {
        var s = Child(root, section);
        if (s is null) return null;
        if (!s.Value.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out int n) ? n : null;
    }

    private static bool? ReadBool(JsonElement root, string section, string name)
    {
        var s = Child(root, section);
        if (s is null) return null;
        if (!s.Value.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string[] parts = value.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int hour)
            || !int.TryParse(parts[1], out int minute)
            || hour is < 0 or > 23
            || minute is < 0 or > 59)
        {
            return null;
        }

        return $"{hour:00}:{minute:00}";
    }
}
