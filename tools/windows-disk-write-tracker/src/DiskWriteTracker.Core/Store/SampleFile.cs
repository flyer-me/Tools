using System.Globalization;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// samples-yyyyMMdd.jsonl 文件名的唯一解析入口。
///
/// 解析原先分散在 SamplingRunner（剪枝）、MenuSession（数样本）、ReportWriter（判断报表是否过期），
/// 三处对"长度必须是 16"的判断不一致。集中后文件名格式变更只需改一处。
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
