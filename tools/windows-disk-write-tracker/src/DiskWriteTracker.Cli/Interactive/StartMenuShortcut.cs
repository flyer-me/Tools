using System.Reflection;
using System.Runtime.InteropServices;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 开始菜单快捷方式。用 WScript.Shell 晚绑定，不引 COM 互操作程序集，也不依赖 dynamic
/// （dynamic 会拖进 Microsoft.CSharp 绑定器，将来若开裁剪会多一层风险）。
/// </summary>
internal static class StartMenuShortcut
{
    private const string Description = "DiskWriteTracker 硬盘写入量记录";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Start Menu", "Programs", "DiskWriteTracker.lnk");

    /// <summary>存在且指向当前 exe 就什么都不做；否则（不存在/指向 .cmd/指向已删除的路径）重建。</summary>
    public static bool Ensure(string exePath) => RepairIfStale(exePath) || Exists(exePath);

    /// <summary>老版本的快捷方式指向 DiskWriteTracker.cmd，脚本删除后它会变成死链，这里自动改指向 exe。</summary>
    public static bool RepairIfStale(string exePath)
    {
        try
        {
            string? current = ReadTarget();
            if (current is null)
                return false;

            bool pointsToScript = current.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || current.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            bool targetGone = !File.Exists(current);

            if (!pointsToScript && !targetGone)
                return false;

            return Create(exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or TargetInvocationException)
        {
            return false;
        }
    }

    public static bool Remove()
    {
        try
        {
            if (!File.Exists(ShortcutPath))
                return true;

            File.Delete(ShortcutPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Exists(string exePath)
    {
        string? current = ReadTarget();
        return current is not null && string.Equals(current, exePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Create(string exePath)
    {
        object? shell = null;
        object? link = null;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return false;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return false;

            string directory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(Path.GetDirectoryName(ShortcutPath)!);

            link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [ShortcutPath]);
            if (link is null)
                return false;

            Type linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [exePath]);
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, [directory]);
            linkType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, link, [exePath + ",0"]);
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link, [Description]);
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

            return true;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
                Marshal.ReleaseComObject(link);

            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }

    private static string? ReadTarget()
    {
        object? shell = null;
        object? link = null;

        try
        {
            if (!File.Exists(ShortcutPath))
                return null;

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return null;

            link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [ShortcutPath]);
            if (link is null)
                return null;

            object? target = link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null);
            string? text = target as string;
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link))
                Marshal.ReleaseComObject(link);

            if (shell is not null && Marshal.IsComObject(shell))
                Marshal.ReleaseComObject(shell);
        }
    }
}
