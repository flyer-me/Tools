using System.Diagnostics;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 用系统默认程序打开资源。全部走 ShellExecute，不需要知道浏览器/记事本的路径。
/// </summary>
internal static class ShellLauncher
{
    public static bool Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        try
        {
            return Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    public static bool OpenUrl(string url) => Open(url);

    public static bool RevealDirectory(string path)
    {
        if (!Directory.Exists(path))
            return false;

        try
        {
            return Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 报表的 file:// 地址。带上文件时间戳做成 fragment，浏览器就不会拿上次缓存糊弄人。
    /// 用 Uri 而不是手工替换斜杠，含空格或中文的路径才不会变成坏链接。
    /// </summary>
    public static string ReportUri(string path)
    {
        string full = Path.GetFullPath(path);
        string uri = new Uri(full).AbsoluteUri;

        try
        {
            return uri + "#v=" + File.GetLastWriteTimeUtc(full).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return uri;
        }
    }
}
