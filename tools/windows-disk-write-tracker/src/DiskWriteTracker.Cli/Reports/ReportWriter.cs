using DiskWriteTracker.Core.Export;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli.Reports;

internal sealed record ReportOutcome(
    bool Ok,
    string? File,
    long Bytes,
    int Rows,
    int Disks,
    int ExitCode);

/// <summary>
/// 报表生成。命令行 <c>report</c> 与菜单选项 1/2 都走这里，保证两处产出的文件、路径、归档规则完全一致。
/// </summary>
internal static class ReportWriter
{
    public static string DefaultPath(string dataDirectory)
        => Path.Combine(dataDirectory, "exports", "report.html");

    /// <param name="outPath">为 null 时落数据目录下的固定名，并额外写一份当日归档。</param>
    public static ReportOutcome Generate(
        IReadOnlyList<SampleRecord> samples,
        string dataDirectory,
        int days,
        string? outPath,
        DateTimeOffset generatedAt)
    {
        if (samples.Count == 0)
            return new ReportOutcome(false, null, 0, 0, 0, 4);

        var (stats, summaries) = ViewBuilder.Build(samples, days);
        var data = ReportData.From(stats, summaries, dataDirectory, $"近 {days} 天", days, generatedAt);
        string html = HtmlReportBuilder.Build(data);

        bool implicitOut = string.IsNullOrWhiteSpace(outPath);
        string full = Path.GetFullPath(implicitOut ? DefaultPath(dataDirectory) : outPath!);

        WriteAtomic(full, html);

        if (implicitOut)
            WriteArchive(full, html, generatedAt);

        long size = new FileInfo(full).Length;
        return new ReportOutcome(true, full, size, stats.Count, summaries.Count, 0);
    }

    /// <summary>
    /// 报表是否已跟上最新样本。用于菜单选项 2：没必要每次都重算一遍 HTML。
    /// </summary>
    public static bool IsUpToDate(string reportPath, string dataDirectory)
    {
        try
        {
            if (!File.Exists(reportPath))
                return false;

            var dir = new DirectoryInfo(dataDirectory);
            if (!dir.Exists)
                return false;

            DateTime newest = dir.EnumerateFiles("samples-*.jsonl")
                .Select(f => f.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            return newest != DateTime.MinValue && File.GetLastWriteTimeUtc(reportPath) >= newest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 原子写文本：先写同目录临时文件再改名覆盖，避免浏览器正好打开到半截 HTML。
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try { File.Delete(tmp); } catch (IOException) { /* 残留临时文件不影响主文件 */ }
            }
        }
    }

    /// <summary>
    /// 同日归档一份，最多保留 30 个。只删文件、不递归删目录，避免触发本机的 fail-closed 钩子。
    /// </summary>
    private static void WriteArchive(string mainPath, string content, DateTimeOffset generatedAt)
    {
        string? dir = Path.GetDirectoryName(mainPath);
        if (string.IsNullOrEmpty(dir))
            return;

        string archiveDir = Path.Combine(dir, "archive");
        string archivePath = Path.Combine(archiveDir, $"report-{generatedAt:yyyyMMdd}.html");

        try
        {
            WriteAtomic(archivePath, content);
            HideDirectory(archiveDir);

            var stale = Directory.EnumerateFiles(archiveDir, "report-*.html")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal)
                .Skip(30)
                .ToList();

            foreach (string file in stale)
            {
                try { File.Delete(file); } catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 归档失败不影响主报表，已经生成的那份才是用户要看的
        }
    }

    /// <summary>归档是技术细节，藏着别让用户点开。失败无所谓。</summary>
    private static void HideDirectory(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) == 0)
                File.SetAttributes(path, attrs | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
