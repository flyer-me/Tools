using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiskWriteTracker.Core.Scheduling;

namespace DiskWriteTracker.Cli.Interactive;

internal sealed record TaskStatusSnapshot(
    bool Installed,
    string? State,
    string? RunLevel,
    string? LastRunTime,
    long? LastResult,
    string? LastResultText,
    string? NextRunTime,
    int? MissedRuns)
{
    public static TaskStatusSnapshot? FromJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return null;

        try
        {
            var node = JsonNode.Parse(rawJson);
            if (node is null)
                return null;

            return new TaskStatusSnapshot(
                Installed: node["installed"]?.GetValue<bool>() ?? false,
                State: node["state"]?.ToString(),
                RunLevel: node["runLevel"]?.ToString(),
                LastRunTime: node["lastRunTime"]?.ToString(),
                LastResult: TryGetLong(node["lastResult"]),
                LastResultText: node["lastResultText"]?.ToString(),
                NextRunTime: node["nextRunTime"]?.ToString(),
                MissedRuns: TryGetInt(node["missedRuns"]));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>表头里的一行任务状态。颜色只在最近一次运行失败时变黄。</summary>
    public (string Text, ConsoleColor? Color) FormatLine()
    {
        if (!Installed)
            return ("未安装（按 4 安装，之后每天自动记录）", null);

        string level = RunLevel == "Highest" ? "最高权限" : "普通权限";
        string last = FormatTime(LastRunTime);

        if (!string.IsNullOrWhiteSpace(LastResultText))
            last += $"（{LastResultText}）";

        string next = FormatTime(NextRunTime);
        string text = $"已安装 ｜ {level} ｜ 上次 {last} ｜ 下次 {next}";

        return (text, LastResult is > 0 ? ConsoleColor.Yellow : null);
    }

    internal static string FormatTime(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
            return "—";

        return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : iso;
    }

    private static long? TryGetLong(JsonNode? node)
    {
        if (node is null)
            return null;

        try { return node.GetValue<long>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is null)
            return null;

        try { return node.GetValue<int>(); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return null; }
    }
}

internal static class TaskStatusReader
{
    /// <summary>查询计划任务状态。内部会起一次 powershell（ScheduledTasks 模块），约 0.3–0.6 秒。</summary>
    public static TaskStatusSnapshot? Query()
    {
        var result = ScheduleManager.Invoke("-Action", "status", "-TaskName", ScheduleManager.DefaultTaskName);

        // 失败时 RawJson 里往往仍是合法的 {ok:false,...}，解析出来 installed=false 即可，
        // 区分不了"查询被拒"和"确实没装"，所以调用方把 null 当作"未知"
        return result.Ok ? TaskStatusSnapshot.FromJson(result.RawJson) : null;
    }
}
