using System.Diagnostics;
using System.Text;
using DiskWriteTracker.Core.Config;

namespace DiskWriteTracker.Core.Scheduling;

public sealed record ScheduleResult(bool Ok, string RawJson, int ExitCode, string? Error);

/// <summary>
/// 计划任务管理。委托给内嵌的 schedule.ps1，使用 ScheduledTasks 模块。
/// 不调用 schtasks.exe —— 部分环境该程序被安全策略禁用。
/// </summary>
public static class ScheduleManager
{
    public const string DefaultTaskName = "DiskWriteTracker_DailySample";

    /// <summary>把内嵌脚本释放到磁盘。必须带 UTF-8 BOM，否则 Windows PowerShell 5.1 会把中文注释解析乱。</summary>
    public static string? ExtractScript()
    {
        var assembly = typeof(ScheduleManager).Assembly;
        string? resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("schedule.ps1", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
            return FindScriptOnDisk();

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return FindScriptOnDisk();

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string content = reader.ReadToEnd();

        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskWriteTracker",
            "scripts");
        Directory.CreateDirectory(dir);

        string path = Path.Combine(dir, "schedule.ps1");
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return path;
    }

    private static string? FindScriptOnDisk()
    {
        string? candidate = Path.Combine(AppContext.BaseDirectory, "Scripts", "schedule.ps1");
        return File.Exists(candidate) ? candidate : null;
    }

    public static ScheduleResult Invoke(params string[] arguments)
    {
        string? script = ExtractScript();
        if (script is null)
            return new ScheduleResult(false, string.Empty, -1, "未找到内嵌的 schedule.ps1");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        foreach (string arg in arguments)
            psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new ScheduleResult(false, string.Empty, -1, "无法启动 powershell.exe");

            // 先异步读完两个流再等退出：同步 ReadToEnd 在两个流都写满时会互相阻塞
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            int timeoutMs = Math.Clamp(AppConfigLoader.Load().TimeoutSeconds * 1000, 5_000, 600_000);
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                return new ScheduleResult(false, string.Empty, -1, $"powershell 执行超时（{timeoutMs / 1000} 秒）");
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            string combined = string.IsNullOrWhiteSpace(stderr)
                ? stdout
                : stdout + Environment.NewLine + "[stderr] " + stderr;

            return new ScheduleResult(process.ExitCode == 0, combined.Trim(), process.ExitCode, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ScheduleResult(false, string.Empty, -1, ex.Message);
        }
    }
}
