using System.Globalization;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// 一个按日切分的样本文件。文件名里就是日期，读取时据此跳过老文件，
/// 不必先打开再解析——这是窗口读取能便宜的唯一原因。
///
/// 文件名解析原先散在采样剪枝、菜单计数两处各写一遍，格式一改就会两处不同步，
/// 因此收到这里做唯一实现。
/// </summary>
public readonly record struct SampleFile(string Path, DateOnly Date)
{
    public const string Prefix = "samples-";
    public const string Suffix = ".jsonl";

    public static bool TryParse(string path, out SampleFile file)
    {
        file = default;

        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Length != Prefix.Length + 8 || !name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!DateOnly.TryParseExact(name.AsSpan(Prefix.Length), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date))
            return false;

        file = new SampleFile(path, date);
        return true;
    }

    /// <summary>按文件名日期升序枚举样本文件；解析不出日期的文件直接忽略。</summary>
    public static List<SampleFile> Enumerate(string directory)
    {
        var files = new List<SampleFile>();
        if (!System.IO.Directory.Exists(directory))
            return files;

        foreach (string path in System.IO.Directory.EnumerateFiles(directory, Prefix + "*" + Suffix))
        {
            if (TryParse(path, out SampleFile file))
                files.Add(file);
        }

        files.Sort(static (a, b) => a.Date.CompareTo(b.Date));
        return files;
    }
}
