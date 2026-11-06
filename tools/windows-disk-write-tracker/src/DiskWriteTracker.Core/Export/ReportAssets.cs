using System.Text;

namespace DiskWriteTracker.Core.Export;

/// <summary>
/// 读取内嵌的报表样式与脚本，以 EmbeddedResource 随程序集打包。
/// </summary>
internal static class ReportAssets
{
    public static string Css { get; } = Load("report.css");

    public static string Js { get; } = Load("report.js");

    public static string Template { get; } = Load("report-template.html");

    private static string Load(string logicalName)
    {
        var stream = typeof(ReportAssets).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException("缺少内嵌报表资源: " + logicalName);

        using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            return reader.ReadToEnd();
        }
    }
}
