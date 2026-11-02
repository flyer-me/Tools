using System.Globalization;
using System.Text;

namespace DiskWriteTracker.Core.Export;

/// <summary>CSV 导出：UTF-8 BOM + CRLF + RFC4180 转义，Excel 直接打开不乱码。</summary>
public static class CsvExporter
{
    public static string ToCsv(IEnumerable<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(Escape)));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    public static void WriteFile(string path, IEnumerable<string[]> rows)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // UTF-8 with BOM
        File.WriteAllText(path, ToCsv(rows), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        bool needQuote = value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        string result = needQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;

        // 防公式注入
        if (result.Length > 0 && result[0] is '=' or '+' or '-' or '@' or '\t')
            result = "'" + result;

        return result;
    }

    /// <summary>字节数转人类可读，保留 3 位小数（不千分位，避免 Excel 类型推断）。</summary>
    public static string FormatBytes(long? bytes)
    {
        if (!bytes.HasValue)
            return string.Empty;

        double value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return value.ToString("0.###", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
