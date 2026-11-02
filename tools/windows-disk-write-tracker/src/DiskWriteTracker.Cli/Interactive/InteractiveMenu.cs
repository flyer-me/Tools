using DiskWriteTracker.Core.Logging;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 双击 exe 时的交互菜单。只在真终端里启动：输入或输出任一被重定向时退回打印帮助，
/// 保证脚本、管道、计划任务调用 exe 时不会挂在那里等键盘。
/// </summary>
internal static class InteractiveMenu
{
    /// <summary>
    /// 三个条件都要满足。只看 stdin 不够：`exe &gt; log.txt` 时 stdin 仍是真终端，
    /// 菜单会把清屏转义写进文件并阻塞等人输入。
    /// </summary>
    public static bool CanRun() =>
        Environment.UserInteractive
        && !Console.IsInputRedirected
        && !Console.IsOutputRedirected;

    public static int Run()
    {
        ConsoleUi.Interactive = true;

        MenuSession session;
        try
        {
            session = MenuSession.Create();
        }
        catch (Exception ex)
        {
            ConsoleUi.Fail("初始化失败：" + ex.Message);
            ConsoleUi.Faint(ex.GetType().Name);
            ConsoleUi.Pause();
            return 5;
        }

        while (true)
        {
            DrawHeader(session);
            DrawItems();

            string? choice = ConsoleUi.Prompt("  请选择 ");
            if (choice is null)
                return 0;

            string value = choice.Trim();
            if (value.Length == 0)
                value = "0";

            ConsoleUi.Line();

            switch (value)
            {
                case "1":
                    Invoke(session, MenuActions.SampleAndReport);
                    break;

                case "2":
                    Invoke(session, s => MenuActions.ShowReport(s));
                    break;

                case "3":
                    Invoke(session, MenuActions.ExportCsv);
                    break;

                case "4":
                    Invoke(session, MenuActions.InstallTask);
                    break;

                case "5":
                    Invoke(session, MenuActions.UninstallTask);
                    break;

                case "6":
                    Invoke(session, MenuActions.OpenDataDirectory);
                    break;

                case "7":
                    Invoke(session, MenuActions.ShowLogs);
                    break;

                case "0":
                    return 0;

                default:
                    ConsoleUi.Warn("  请输入 0 到 7 之间的数字。");
                    Thread.Sleep(700);
                    break;
            }
        }
    }

    private static void Invoke(MenuSession session, Func<MenuSession, int> action)
    {
        try
        {
            action(session);
        }
        catch (Exception ex)
        {
            ConsoleUi.Line();
            ConsoleUi.Fail("  操作出错：" + ex.Message);
            ConsoleUi.Faint("  " + ex.GetType().Name);

            try { EngineLog.Error(session.DataDirectory, "menu", ex); }
            catch (Exception) { }
        }

        ConsoleUi.Pause();
    }

    private static void DrawHeader(MenuSession session)
    {
        ConsoleUi.Clear();

        ConsoleUi.Accent("==============================================================");
        ConsoleUi.Accent("  DiskWriteTracker   硬盘写入量记录");
        ConsoleUi.Accent("==============================================================");
        ConsoleUi.Line();

        string mode = session.IsPortable ? "便携" : "系统目录";
        ConsoleUi.Info($"  数据目录 : {ConsoleUi.Fit(session.DataDirectory, 62)}  [{mode}]");
        ConsoleUi.Info($"  样本条数 : {session.SampleCount}");
        ConsoleUi.Info($"  报表窗口 : 近 {session.Settings.ReportDays} 天      采样时间 : 每天 {session.Settings.Time}");

        if (session.TaskStatus is { } status)
        {
            var (text, color) = status.FormatLine();
            if (color == ConsoleColor.Yellow)
                ConsoleUi.Warn("  计划任务 : " + text);
            else
                ConsoleUi.Info("  计划任务 : " + text);
        }
        else
        {
            ConsoleUi.Faint("  计划任务 : 状态未知（查询被拒绝或服务不可用）");
        }

        if (!session.IsElevated)
            ConsoleUi.Faint("  当前权限 : 普通用户（可采样、可看报表；安装/卸载任务需要管理员）");

        if (session.StaleDays > 1)
            ConsoleUi.Warn($"  [!] 已有 {session.StaleDays} 天没有成功采样，建议先选 1。");

        ConsoleUi.Line();
    }

    private static void DrawItems()
    {
        ConsoleUi.Info("  1  立即采样并刷新报表");
        ConsoleUi.Info("  2  只看报表（不采样）");
        ConsoleUi.Info("  3  导出 CSV");
        ConsoleUi.Info("  4  安装 / 更新每日计划任务");
        ConsoleUi.Info("  5  卸载计划任务");
        ConsoleUi.Info("  6  打开数据目录");
        ConsoleUi.Info("  7  查看日志");
        ConsoleUi.Info("  0  退出");
        ConsoleUi.Line();
    }
}
