using System.Text;
using DiskWriteTracker.Core.Config;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Core.Logging;

/// <summary>
/// 引擎侧的极简日志。只记两个时机：一次采样结束的汇总，以及未捕获异常的堆栈首行。
///
/// 为什么需要它：计划任务跑的是 `sample --quiet`，标准输出无人接收，任务计划程序只给一个
/// 0/非 0。没有这一行，"每天 00:05 到底采到没有、采了几块盘"就完全不可观测。
/// 为什么不再多记：日志本身要写盘，写多了会与采样争锁，违背"低开销只读采样"的定位。
/// </summary>
public static class EngineLog
{
    public const string InfoLevel = "INFO";
    public const string ErrorLevel = "ERROR";

    public static void Info(string? dataDirectory, string message) => Write(dataDirectory, InfoLevel, message);

    public static void Error(string? dataDirectory, string message, Exception? exception = null)
    {
        string text = exception is null
            ? message
            : $"{message} :: {exception.GetType().Name}: {exception.Message}";

        if (exception?.StackTrace is { Length: > 0 } stack)
        {
            string first = stack.Split('\n')[0].Trim();
            if (first.Length > 0)
                text += " @ " + first;
        }

        Write(dataDirectory, ErrorLevel, text);
    }

    private static void Write(string? dataDirectory, string level, string message)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            return;

        try
        {
            string dir = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"engine-{DateTime.Now:yyyyMMdd}.log");
            string line = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}{3}",
                DateTime.Now, level, message, Environment.NewLine);

            using var logLock = FileLock.Acquire(Path.Combine(dir, ".log.lock"), TimeSpan.FromSeconds(2));

            // 写前滚动。体积闸门只在这里能生效：文件是按日切的，跨日不会触发，
            // 只有"同一天写了很多行"才会碰到上限。
            // 每次都读一遍配置而不是缓存：一次采样只写一行，读配置是毫秒级，
            // 换取的是运维改 default.json 后下一次采样立刻生效，不用重启任何东西。
            LogRetention.RollIfOversize(path, AppConfigLoader.Load().LogMaxFileBytes);

            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
        }
    }

    /// <summary>
    /// 记一次菜单动作：一行摘要 + 原始输出。写到 launcher-yyyyMMdd.log，
    /// 与 engine-*.log 分开，避免"用户点了什么"淹没"采样结果"。
    /// </summary>
    public static void Action(
        string? dataDirectory,
        string action,
        int exitCode,
        double elapsedMs,
        string? standardOutput,
        string? standardError)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory))
            return;

        try
        {
            string dir = Path.Combine(dataDirectory, "logs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd}.log");

            var sb = new StringBuilder();
            sb.Append('[')
              .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture))
              .Append("] action=\"")
              .Append(action)
              .Append("\" exit=")
              .Append(exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))
              .Append(' ')
              .Append(elapsedMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture))
              .Append("ms")
              .Append(Environment.NewLine);

            if (!string.IsNullOrWhiteSpace(standardOutput))
                sb.Append(standardOutput.TrimEnd()).Append(Environment.NewLine);

            if (!string.IsNullOrWhiteSpace(standardError))
                sb.Append("[stderr] ").Append(standardError.TrimEnd()).Append(Environment.NewLine);

            using var logLock = FileLock.Acquire(Path.Combine(dir, ".log.lock"), TimeSpan.FromSeconds(2));

            LogRetention.RollIfOversize(path, AppConfigLoader.Load().LogMaxFileBytes);

            File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
        }
    }
}
