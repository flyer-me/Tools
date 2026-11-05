using System.Globalization;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// samples-yyyyMMdd.jsonl 文件名的唯一解析入口。
///
/// 之所以要单独收一个类型：这个解析原先在 SamplingRunner（剪枝）、MenuSession（数样本）、
/// ReportWriter（判断报表是否过期）里各写了一遍，三处对"长度必须是 16"的写法还不完全一致。
/// 文件名格式一旦变，漏改一处就是"某条路径静默读不到样本"，而这种静默缺失最难查。
/// </summary>
public readonly record struct SampleFile(string Path, DateOnly Date)
{
    public const string Prefix = "samples-";
    public const string Suffix = ".jsonl";

    /// <summary>文件名（含扩展名），日志与提示里用。</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    public static bool TryParse(string path, out SampleFile file)
    {
        file = default;

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Length != Prefix.Length + 8 || !name.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        if (!DateOnly.TryParseExact(
                name.AsSpan(Prefix.Length),
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date))
        {
            return false;
        }

        file = new SampleFile(path, date);
        return true;
    }

    /// <summary>枚举目录下的样本文件，按日期升序。名字不合规的一律忽略，绝不猜测。</summary>
    public static List<SampleFile> Enumerate(string directory)
    {
        var result = new List<SampleFile>();
        if (!Directory.Exists(directory))
            return result;

        foreach (string path in Directory.EnumerateFiles(directory, Prefix + "*" + Suffix))
        {
            if (TryParse(path, out SampleFile file))
                result.Add(file);
        }

        result.Sort((a, b) => a.Date.CompareTo(b.Date));
        return result;
    }
}
