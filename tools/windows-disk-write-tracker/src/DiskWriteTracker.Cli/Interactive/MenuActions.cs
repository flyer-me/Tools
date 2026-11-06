using System.Diagnostics;
using DiskWriteTracker.Cli.Reports;
using DiskWriteTracker.Core.Collect;
using DiskWriteTracker.Core.Logging;
using DiskWriteTracker.Core.Scheduling;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli.Interactive;

/// <summary>
/// 菜单的 8 个动作。采样与报表都在本进程内直接调 Core —— 写入口仍是 JsonlSampleStore + 跨进程文件锁，
/// </summary>
internal static class MenuActions
{
    public static int SampleAndReport(MenuSession session)
    {
        var watch = Stopwatch.StartNew();
        ConsoleUi.Faint("  正在读取硬盘计数器...");

        SamplingOutcome outcome;
        try
        {
            outcome = SamplingRunner.Run();
        }
        catch (Exception ex)
        {
            watch.Stop();
            ConsoleUi.Fail("  采样失败：" + ex.Message);
            EngineLog.Error(session.DataDirectory, "menu/1 sample", ex);
            EngineLog.Action(session.DataDirectory, "menu/1 sample", 5, watch.Elapsed.TotalMilliseconds, null, ex.Message);
            return 5;
        }

        watch.Stop();

        if (outcome.Records.Count == 0)
        {
            ConsoleUi.Warn("  没有读到任何硬盘的累计写入计数。");
            ConsoleUi.Faint("  虚拟磁盘、部分 RAID 卡后面挂的盘不提供这个计数器。运行 doctor 可看详情。");
            EngineLog.Action(session.DataDirectory, "menu/1 sample", 4, watch.Elapsed.TotalMilliseconds, "disks=0", null);
            return 4;
        }

        ConsoleUi.Ok($"  采样完成，{outcome.Records.Count} 块盘");

        foreach (var record in outcome.Records)
        {
            string name = ConsoleUi.Pad(ConsoleUi.Fit(record.Model, 26), 28);
            string size = ConsoleUi.Pad(Program.FormatBytes(record.Bytes), 10);
            ConsoleUi.Info($"    {name}{size}  {Program.QualityLabel(record.Quality)}");
        }

        if (outcome.MigrationNote is not null)
            ConsoleUi.Faint("  " + outcome.MigrationNote);

        EngineLog.Action(session.DataDirectory, "menu/1 sample", 0, watch.Elapsed.TotalMilliseconds,
            $"disks={outcome.Records.Count}", null);

        session.RefreshSampleStats();

        ConsoleUi.Line();
        return ShowReport(session, forceRegenerate: true);
    }

    public static int ShowReport(MenuSession session, bool forceRegenerate = false)
    {
        if (!forceRegenerate && ReportWriter.IsUpToDate(session.ReportPath, session.DataDirectory))
        {
            ConsoleUi.Faint("  报表已是最新，直接打开。");
            return OpenReport(session);
        }

        var samples = LoadSamples(session, session.Settings.ReportDays);
        if (samples.Count == 0)
        {
            ConsoleUi.Warn($"  最近 {session.Settings.ReportDays} 天没有采样数据。先选 1 采样一次。");
            return 4;
        }

        var watch = Stopwatch.StartNew();
        var outcome = ReportWriter.Generate(samples, session.DataDirectory, session.Settings.ReportDays, null, DateTimeOffset.Now);
        watch.Stop();

        if (!outcome.Ok || outcome.File is null)
        {
            ConsoleUi.Fail("  报表生成失败。");
            EngineLog.Action(session.DataDirectory, "menu/2 report", outcome.ExitCode, watch.Elapsed.TotalMilliseconds, null, "no data");
            return outcome.ExitCode;
        }

        ConsoleUi.Ok("  报表已生成");
        ConsoleUi.Faint($"  {outcome.Disks} 块盘，{outcome.Rows} 条日记录，{outcome.Bytes / 1024.0:0.#} KB");

        EngineLog.Action(session.DataDirectory, "menu/2 report", 0, watch.Elapsed.TotalMilliseconds, outcome.File, null);

        return OpenReport(session);
    }

    public static int ExportCsv(MenuSession session)
    {
        var samples = LoadSamples(session, session.Settings.ExportDays);
        if (samples.Count == 0)
        {
            ConsoleUi.Warn($"  最近 {session.Settings.ExportDays} 天没有采样数据。先选 1 采样一次。");
            return 4;
        }

        var watch = Stopwatch.StartNew();
        var outcome = ExportWriter.WriteCsv(samples, session.Settings.ExportDays, session.ExportDir);
        watch.Stop();

        ConsoleUi.Ok("  已导出");
        ConsoleUi.Info($"    日统计   {outcome.DailyRows} 条  {outcome.DailyFile}");
        ConsoleUi.Info($"    原始样本 {outcome.SampleRows} 条  {outcome.SamplesFile}");

        EngineLog.Action(session.DataDirectory, "menu/3 export", 0, watch.Elapsed.TotalMilliseconds,
            $"daily={outcome.DailyRows} samples={outcome.SampleRows}", null);

        if (!ShellLauncher.RevealDirectory(session.ExportDir))
            ConsoleUi.Faint("  打开文件夹失败，可以手动进 " + session.ExportDir);

        return 0;
    }

    public static int InstallTask(MenuSession session)
    {
        string current = session.Settings.Time;

        string? input = ConsoleUi.Prompt($"  每天几点采样？（24 小时制 HH:mm，直接回车用 {current}）");
        if (input is null)
            return 0;

        string time = string.IsNullOrWhiteSpace(input) ? current : input.Trim();
        if (!UserSettings.IsValidTime(time))
        {
            ConsoleUi.Warn("  时间格式不对，应形如 00:05。");
            return 1;
        }

        var watch = Stopwatch.StartNew();
        string exe = Program.ResolveSelfPath();

        var (ok, error) = RunPrivileged(
            session,
            ["-Action", "install", "-ExePath", exe, "-TaskName", ScheduleManager.DefaultTaskName, "-Time", time],
            $"schedule install --time {time}");

        watch.Stop();
        session.RefreshTaskStatus();

        // 提权子进程拿不到输出，所以以"任务是否真的存在"为准；状态查不到时按退出码算
        bool installed = ok && session.TaskStatus?.Installed != false;

        if (installed)
        {
            ConsoleUi.Ok($"  每日任务已就绪：每天 {time} 自动记录");

            session.Settings = session.Settings with { Time = time };
            UserSettingsStore.TrySave(session.DataDirectory, session.Settings);
            StartMenuShortcut.Ensure(exe);

            EngineLog.Action(session.DataDirectory, $"menu/4 install {time}", 0, watch.Elapsed.TotalMilliseconds, null, null);
            return 0;
        }

        ConsoleUi.Fail("  安装未能完成。");
        if (!string.IsNullOrWhiteSpace(error))
            ConsoleUi.Faint("  " + error.ReplaceLineEndings(" ").Trim());

        EngineLog.Action(session.DataDirectory, $"menu/4 install {time}", 7, watch.Elapsed.TotalMilliseconds, null, error);
        return 7;
    }

    public static int UninstallTask(MenuSession session)
    {
        ConsoleUi.Info("  移除每日任务。数据怎么处理？");
        ConsoleUi.Faint("    直接回车       只移除任务，样本、日志、偏好全部保留");
        ConsoleUi.Faint("    输入 DELETE     移除任务并删除样本与日志，保留偏好设置");
        ConsoleUi.Faint("    输入 DELETE ALL 连同偏好设置一起删除");

        string? answer = ConsoleUi.Prompt("  请输入");
        if (answer is null)
            return 0;

        // 三档而不是两档：偏好设置（窗口天数、采样时间）删掉就得重配一遍，
        // 而样本删掉只要重新采样。把两者绑在同一个"确认删除"里，用户没有选择权。
        string mode = answer.Trim().ToUpperInvariant() switch
        {
            "DELETE" => "data",
            "DELETE ALL" => "all",
            _ => "none"
        };

        var watch = Stopwatch.StartNew();

        var (ok, error) = RunPrivileged(
            session,
            ScheduleInvocation.BuildUninstallPsArgs(session.DataDirectory, mode, dryRun: false),
            ScheduleInvocation.BuildUninstallCliArguments(mode, dryRun: false));

        watch.Stop();
        session.RefreshTaskStatus();
        session.RefreshSampleStats();

        string arguments = ScheduleInvocation.BuildUninstallCliArguments(mode, dryRun: false);
        EngineLog.Action(session.DataDirectory, "menu/5 " + arguments, ok ? 0 : 7, watch.Elapsed.TotalMilliseconds, null, error);

        if (!ok)
        {
            ConsoleUi.Fail("  卸载未能完成。");
            if (!string.IsNullOrWhiteSpace(error))
                ConsoleUi.Faint("  " + error.ReplaceLineEndings(" ").Trim());
            return 7;
        }

        ConsoleUi.Ok(mode switch
        {
            "all" => "  任务已移除，样本与偏好设置都已删除。",
            "data" => "  任务已移除，样本已删除，偏好设置保留。",
            _ => "  任务已移除，数据全部保留。"
        });
        ConsoleUi.Faint("  开始菜单里的快捷方式不再需要时可以手动删除。");
        return 0;
    }

    public static int OpenDataDirectory(MenuSession session)
    {
        if (!Directory.Exists(session.DataDirectory))
        {
            ConsoleUi.Warn("  数据目录还不存在。先选 1 采样一次。");
            return 1;
        }

        if (!ShellLauncher.RevealDirectory(session.DataDirectory))
        {
            ConsoleUi.Warn("  打开失败，路径是：" + session.DataDirectory);
            return 1;
        }

        return 0;
    }

    public static int ShowLogs(MenuSession session)
    {
        if (!Directory.Exists(session.LogDir))
        {
            ConsoleUi.Warn("  还没有日志。先选 1 采样一次。");
            return 1;
        }

        List<FileInfo> logs;
        try
        {
            logs = new DirectoryInfo(session.LogDir)
                .EnumerateFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleUi.Warn("  读不到日志目录。");
            return 1;
        }

        if (logs.Count == 0)
        {
            ConsoleUi.Warn("  还没有日志文件。");
            return 1;
        }

        ConsoleUi.Info("  最近的日志（最新在上）：");
        foreach (var log in logs)
        {
            string when = log.LastWriteTime.ToString("MM-dd HH:mm");
            string size = log.Length < 1024 ? $"{log.Length} B" : $"{log.Length / 1024.0:0.#} KB";
            ConsoleUi.Info($"    {when}  {ConsoleUi.Pad(size, 9)}{log.Name}");
        }

        ShellLauncher.Open(logs[0].FullName);
        return 0;
    }

    private static int OpenReport(MenuSession session)
    {
        if (!File.Exists(session.ReportPath))
        {
            ConsoleUi.Warn("  报表文件不存在，先选 1 生成一次。");
            return 4;
        }

        if (!ShellLauncher.Open(ShellLauncher.ReportUri(session.ReportPath)))
        {
            ConsoleUi.Warn("  没能自动打开浏览器，可以手动打开：" + session.ReportPath);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// 只读窗口内的样本文件。窗口读取会顺带带回起点之前的前驱，
    /// 报表与导出都按窗口再筛一次日期，所以这里不需要额外过滤。
    /// </summary>
    private static IReadOnlyList<SampleRecord> LoadSamples(MenuSession session, int days)
    {
        try
        {
            var store = new JsonlSampleStore(session.DataDirectory);
            return store.ReadForWindow(days, DateOnly.FromDateTime(DateTime.Now));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 需要管理员的操作统一入口：已提权时直调 schedule.ps1，未提权时自我提权按命令行参数重跑。
    /// </summary>
    private static (bool Ok, string? Error) RunPrivileged(MenuSession session, string[] psArgs, string cliArguments)
    {
        if (session.IsElevated)
        {
            var result = ScheduleManager.Invoke(psArgs);
            return (result.Ok, result.Error ?? result.RawJson);
        }

        var elevated = Elevation.RunSelf(cliArguments);
        if (elevated.Ok)
            return (true, null);

        PrintElevationFallback(Program.ResolveSelfPath(), cliArguments, elevated);
        return (false, elevated.Error);
    }

    /// <summary>提权失败时的兜底：给两条能直接复制到管理员终端的命令。</summary>
    private static void PrintElevationFallback(string exe, string arguments, ElevationResult result)
    {
        if (result.Denied)
            ConsoleUi.Warn("  已取消管理员授权。");
        else
            ConsoleUi.Warn("  无法自动请求管理员权限（本机可能禁止了提权）。");

        ConsoleUi.Line();
        ConsoleUi.Info("  请右键点开始菜单里的「终端(管理员)」，或按下面方式手动执行：");
        ConsoleUi.Line();
        ConsoleUi.Accent($"    \"{exe}\" {arguments}");
        ConsoleUi.Info("");
        ConsoleUi.Faint("  提示：也可以右键 DiskWriteTracker.exe 选「以管理员身份运行」，再回到这个菜单操作。");
    }
}
