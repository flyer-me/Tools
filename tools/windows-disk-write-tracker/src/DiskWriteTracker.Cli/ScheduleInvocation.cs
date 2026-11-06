using System.Text;
using DiskWriteTracker.Core.Scheduling;

namespace DiskWriteTracker.Cli;

/// <summary>
/// schedule.ps1 卸载参数的统一拼装，命令行动词与菜单共用。
/// 开关参数只能在"打开"时出现：用 -File 调用脚本时，-PurgeData:$false 会被当成
/// 字符串塞给 SwitchParameter，参数绑定直接失败；这条路径只有真的去卸载才会走到。
/// </summary>
internal static class ScheduleInvocation
{
    /// <param name="purgeMode">none / data / all。all 覆盖 data。</param>
    public static string[] BuildUninstallPsArgs(string dataDirectory, string purgeMode, bool dryRun)
    {
        var args = new List<string>
        {
            "-Action", "uninstall",
            "-TaskName", ScheduleManager.DefaultTaskName
        };

        if (purgeMode == "all")
            args.Add("-PurgeAll");
        else if (purgeMode == "data")
            args.Add("-PurgeData");

        if (dryRun)
            args.Add("-DryRun");

        args.Add("-DataDirectory");
        args.Add(dataDirectory);

        return [.. args];
    }

    /// <summary>提权重跑时用的命令行参数，与上面的 PowerShell 参数一一对应。</summary>
    public static string BuildUninstallCliArguments(string purgeMode, bool dryRun)
    {
        var sb = new StringBuilder("schedule uninstall");

        if (purgeMode == "all")
            sb.Append(" --purge-all");
        else if (purgeMode == "data")
            sb.Append(" --purge-data");

        if (dryRun)
            sb.Append(" --dry-run");

        return sb.ToString();
    }
}
