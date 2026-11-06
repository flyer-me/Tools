using System.Text;
using DiskWriteTracker.Core.Export;
using Xunit;

namespace DiskWriteTracker.Core.Tests;

public class CsvExporterTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(999, "999 B")]
    [InlineData(1000, "1 KB")]
    [InlineData(1536, "1.536 KB")]
    [InlineData(1048576, "1.049 MB")]
    [InlineData(1_000_000_000_000, "1 TB")]
    public void 字节数转人类可读(long bytes, string expected)
    {
        Assert.Equal(expected, CsvExporter.FormatBytes(bytes));
    }

    [Fact]
    public void 空值按调用方给的占位符呈现()
    {
        Assert.Equal(string.Empty, CsvExporter.FormatBytes(null));
        Assert.Equal("—", CsvExporter.FormatBytes(null, "—"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("a\r\nb", "\"a\r\nb\"")]
    [InlineData("", "")]
    public void 转义与公式注入防护(string input, string expected)
    {
        Assert.Equal(expected, CsvExporter.Escape(input));
    }

    [Fact]
    public void 写出文件带UTF8BOM与CRLF()
    {
        string path = Path.Combine(Path.GetTempPath(), "dwt-csv-" + Guid.NewGuid().ToString("N") + ".csv");
        try
        {
            CsvExporter.WriteFile(path, [["日期", "值"], ["2026-01-01", "1"]]);

            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Contains("\r\n", Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
