using System.Text;
using DiskWriteTracker.Core.Logging;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 双击 exe 时的交互菜单。只在真终端里启动：输入或输出任一被重定向时退回打印帮助。
/// 文案在 menu-layout.txt：前三行 banner，其余每行 "key|label"。
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

    /// <summary>key 到动作的绑定。模板里的 key 必须在此出现，启动时校验。</summary>
    private static readonly Dictionary<string, Func<MenuSession, int>> Actions = new()
    {
        ["1"] = MenuActions.SampleAndReport,
        ["2"] = session => MenuActions.ShowReport(session),
        ["3"] = MenuActions.ExportCsv,
        ["4"] = MenuActions.InstallTask,
        ["5"] = MenuActions.UninstallTask,
        ["6"] = MenuActions.OpenDataDirectory,
        ["7"] = MenuActions.ShowLogs,
    };

    private static readonly (string[] Banner, (string Key, string Label)[] Items) Layout = LoadLayout();

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

            if (value == "0")
                return 0;

            if (Actions.TryGetValue(value, out var action))
            {
                Invoke(session, action);
                continue;
            }

            ConsoleUi.Warn($"  请输入 0 到 {Layout.Items.Length - 1} 之间的数字。");
            Thread.Sleep(700);
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

        foreach (string line in Layout.Banner)
            ConsoleUi.Accent(line);

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
        foreach (var (key, label) in Layout.Items)
            ConsoleUi.Info($"  {key}  {label}");

        ConsoleUi.Line();
    }

    private static (string[] Banner, (string Key, string Label)[] Items) LoadLayout()
    {
        var assembly = typeof(InteractiveMenu).Assembly;
        using var stream = assembly.GetManifestResourceStream("menu-layout.txt")
            ?? throw new InvalidOperationException("缺少内嵌菜单模板 menu-layout.txt");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        var lines = reader.ReadToEnd()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length < 4)
            throw new InvalidOperationException("菜单模板至少需要 3 行 banner 加 1 行菜单项");

        var banner = lines[..3];
        var items = lines[3..]
            .Select(line => line.Split('|', 2))
            .Select(p => (Key: p[0].Trim(), Label: p.Length > 1 ? p[1].Trim() : string.Empty))
            .ToArray();

        // 模板与动作字典的对齐问题在启动时暴露，不能等用户按键后才发现缺项
        var missing = items
            .Where(i => i.Key != "0" && !Actions.ContainsKey(i.Key))
            .Select(i => i.Key)
            .ToArray();

        if (missing.Length > 0)
            throw new InvalidOperationException("菜单模板里的 key 没有对应动作: " + string.Join(", ", missing));

        return (banner, items);
    }
}
