using System.Globalization;

namespace DiskWriteTracker.Core.Logging;

/// <summary>
/// 日志目录的容量治理。两条阈值各自独立生效：
///   按天：文件名里的日期早于 retentionDays 天前就删，不管它多大；
///   按体积：单文件达到上限就就地滚动成 name.1.log、name.2.log……最多 <see cref="MaxSegments"/> 个分片。
///
/// 为什么光有按天不够：按天拦不住"一天点了一百次菜单"。launcher 日志一次动作约 180 B，
/// 一小时连点一百次就是 18 KB —— 这种尖峰只有体积闸门能截住。
/// 为什么光有按体积不够：文件按日切分后，体积永远长不起来，但文件个数会随天数无限累积。
///
/// 为什么剪枝不挂在 <see cref="EngineLog"/> 上：logLevel=ERROR 时 Info 根本不会被调用，
/// 剪枝就永远等不到触发时机。所以采样收尾与菜单启动各挂一次独立调用。
/// </summary>
public static class LogRetention
{
    public const string DirectoryName = "logs";

    /// <summary>单个日志基名允许保留的分片数。加上主文件，一天最多 4 个文件、4 MiB。</summary>
    public const int MaxSegments = 3;

    public static string DirectoryFor(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    /// <summary>
    /// 删掉文件名日期早于今天的 retentionDays 天的日志。只删文件，不碰目录与其他文件。
    /// 返回删除数量；IO 失败时跳过该文件。
    /// </summary>
    public static int PruneExpired(string? dataDirectory, int retentionDays, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || retentionDays <= 0)
            return 0;

        string dir = DirectoryFor(dataDirectory);
        if (!Directory.Exists(dir))
            return 0;

        DateOnly cutoff = today.AddDays(-retentionDays);
        int removed = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*.log"))
            {
                if (!TryParseDate(Path.GetFileName(file), out DateOnly date))
                    continue;

                if (date >= cutoff)
                    continue;

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 被这个进程自己刚打开、或被别的进程占着，下一轮再删
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 目录刚被删掉、或无权枚举
        }

        return removed;
    }

    /// <summary>
    /// 文件达到或超过 maxBytes 时滚动一次：把现有分片整体加一号，主文件变成 .1。
    /// 分片满额时先丢编号最大的那个 —— 分片越大越旧，丢它是对的。
    /// 返回是否发生了滚动。
    /// </summary>
    public static bool RollIfOversize(string path, long maxBytes)
    {
        if (maxBytes <= 0)
            return false;

        var info = new FileInfo(path);
        if (!info.Exists || info.Length < maxBytes)
            return false;

        string? dir = info.DirectoryName;
        if (dir is null)
            return false;

        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);

        var segments = new List<int>(MaxSegments);
        for (int i = 1; i <= MaxSegments; i++)
        {
            if (File.Exists(SegmentPath(dir, stem, ext, i)))
                segments.Add(i);
        }

        // 满额：先删最旧的（编号最大）
        if (segments.Count >= MaxSegments)
        {
            int victim = segments.Max();
            TryDelete(SegmentPath(dir, stem, ext, victim));
            segments.Remove(victim);
        }

        // 从大到小改号，避免自己覆盖自己
        foreach (int index in segments.OrderByDescending(i => i))
        {
            try
            {
                File.Move(SegmentPath(dir, stem, ext, index), SegmentPath(dir, stem, ext, index + 1), overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 改号失败就让它留在原号，下次滚动会再试
            }
        }

        try
        {
            File.Move(path, SegmentPath(dir, stem, ext, 1), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 滚不动就继续往原文件追加，宁可超限也不要丢日志
            return false;
        }

        return true;
    }

    /// <summary>
    /// 从 engine-yyyyMMdd.log / launcher-yyyyMMdd.2.log 里取出日期。
    /// 解析不出来的文件（用户自己丢进来的、旧的别的命名）一律不动，删错文件的代价远大于留几个孤儿。
    /// </summary>
    public static bool TryParseDate(string fileName, out DateOnly date)
    {
        date = default;

        string name = Path.GetFileNameWithoutExtension(fileName);
        int dash = name.IndexOf('-');
        if (dash < 0 || dash + 1 >= name.Length)
            return false;

        string rest = name[(dash + 1)..];
        int dot = rest.IndexOf('.');
        if (dot >= 0)
            rest = rest[..dot];

        return rest.Length == 8
            && DateOnly.TryParseExact(rest, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    private static string SegmentPath(string dir, string stem, string ext, int index)
        => Path.Combine(dir, string.Create(CultureInfo.InvariantCulture, $"{stem}.{index}{ext}"));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
