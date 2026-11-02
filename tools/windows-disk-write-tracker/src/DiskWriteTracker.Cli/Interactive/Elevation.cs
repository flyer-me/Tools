using System.ComponentModel;
using System.Diagnostics;

namespace DiskWriteTracker.Cli.Interactive;

internal sealed record ElevationResult(bool Ok, int ExitCode, bool Denied, string? Error);

/// <summary>
/// 自我提权。走 ShellExecute 的 runas 动词，和右键"以管理员身份运行"是同一条路径。
/// 注意：这一步拿不到子进程的输出（UseShellExecute 的限制），所以调用方必须用计划任务状态复核结果。
/// </summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;
    private const int ErrorAccessDenied = 5;

    public static bool IsElevated => Program.IsElevated();

    public static ElevationResult RunSelf(string arguments, int timeoutMs = 120_000)
    {
        string exe = Program.ResolveSelfPath();

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            };

            using var process = Process.Start(psi);
            if (process is null)
                return new ElevationResult(false, -1, false, "无法启动提权进程");

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                return new ElevationResult(false, -1, false, "提权进程超时未结束");
            }

            return new ElevationResult(process.ExitCode == 0, process.ExitCode, false, null);
        }
        catch (Win32Exception ex)
        {
            bool denied = ex.NativeErrorCode is ErrorCancelled or ErrorAccessDenied;
            return new ElevationResult(false, -1, denied, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return new ElevationResult(false, -1, false, ex.Message);
        }
    }
}
