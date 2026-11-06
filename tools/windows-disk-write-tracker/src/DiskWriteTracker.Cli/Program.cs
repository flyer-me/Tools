using System.Text.Json;
using System.Text.Json.Nodes;
using DiskWriteTracker.Cli.Interactive;
using DiskWriteTracker.Cli.Reports;
using DiskWriteTracker.Core.Analysis;
using DiskWriteTracker.Core.Collect;
using DiskWriteTracker.Core.Config;
using DiskWriteTracker.Core.Discovery;
using DiskWriteTracker.Core.Export;
using DiskWriteTracker.Core.Logging;
using DiskWriteTracker.Core.Scheduling;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Cli;

internal static class Program
{
    /// <summary>--data-dir 覆盖，用于便携模式与测试。</summary>
    private static string? DataDirOverride;

    private static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
        }
        catch (IOException)
        {
            // 没有控制台的宿主（服务、某些终端包装器）无法设置编码
        }

        if (args.Length == 0)
        {
            // 双击 = 真终端，进菜单；被管道/重定向/脚本拉起时保持旧行为（打印帮助并返回 0）
            if (Interactive.InteractiveMenu.CanRun())
                return Interactive.InteractiveMenu.Run();

            return Help();
        }

        if (args[0] is "-h" or "--help" or "help")
            return Help();

        var options = CommandOptions.Parse(args);
        DataDirOverride = options.Get("data-dir");
        string verb = args[0].ToLowerInvariant();

        try
        {
            return verb switch
            {
                "doctor" => Doctor(options),
                "disks" => Disks(options),
                "probe" => Probe(options),
                "sample" => Sample(options),
                "daily" => Daily(options),
                "report" => Report(options),
                "export" => Export(options),
                "schedule" => Schedule(options),
                _ => Unknown(verb)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("执行失败: " + ex.Message);

            // 计划任务路径没人看 stdout/stderr，崩溃必须留下可追溯的一行
            try
            {
                EngineLog.Error(RuntimePaths.ResolveDataDirectory(DataDirOverride).Directory, verb, ex);
            }
            catch
            {
                // 数据目录解析失败时跳过该行日志
            }

            return 5;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            DiskWriteTracker — 硬盘累计写入量记录工具

            用法:
              DiskWriteTracker doctor                                 环境自检
              DiskWriteTracker disks                                  枚举物理磁盘
              DiskWriteTracker probe                                  只读预览累计写入，不落盘
              DiskWriteTracker sample                                 采样一次并记录
              DiskWriteTracker daily    [--days N]                    查看每日增量
              DiskWriteTracker report   [--days N] [--out 路径]       生成离线 HTML 报表
              DiskWriteTracker export   csv [--days N] [--out 目录]   导出 CSV（日统计 + 原始样本）
              DiskWriteTracker schedule status                        查看定时任务状态
              DiskWriteTracker schedule install [--time HH:mm]        安装/更新每日任务
              DiskWriteTracker schedule uninstall [--purge-data | --purge-all] [--dry-run]
                                                                      卸载任务（默认只删任务）

            数据来自硬盘固件上报的累计写入计数器，只读采样，不联网。
            """);
        return 0;
    }

    private static int Unknown(string verb)
    {
        Console.Error.WriteLine($"未知命令: {verb}");
        Console.Error.WriteLine("运行 DiskWriteTracker --help 查看可用命令。");
        return 2;
    }

    // ---------- 基础命令 ----------

    private static int Doctor(CommandOptions options)
    {
        bool elevated = IsElevated();
        var disks = DiskEnumerator.Enumerate();
        var config = AppConfigLoader.Load();
        var resolution = RuntimePaths.ResolveDataDirectory(DataDirOverride, config);
        // 自检只需要"有多少条"，不需要每条的内容 —— 数行不开解析器
        int sampleCount = new JsonlSampleStore(resolution.Directory).CountLines();

        Console.WriteLine("环境自检");        Console.WriteLine($"  OS           : {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");
        Console.WriteLine($"  .NET 运行时  : {Environment.Version}");
        Console.WriteLine($"  管理员权限   : {(elevated ? "是" : "否（受限模式，累计写入仍可读取）")}");
        Console.WriteLine($"  枚举到磁盘   : {disks.Count} 块");
        Console.WriteLine($"  部署方式     : {(resolution.IsPortable ? "便携（数据跟在 exe 同级 data 目录）" : "系统目录")}");
        Console.WriteLine($"  数据目录     : {resolution.Directory}{(resolution.IsFallback ? "  [已降级]" : string.Empty)}");
        Console.WriteLine($"  已记录样本   : {sampleCount} 条");
        Console.WriteLine($"  偏好设置     : {Interactive.UserSettingsStore.PathFor(resolution.Directory)}");
        Console.WriteLine($"  日志目录     : {LogRetention.DirectoryFor(resolution.Directory)}");
        Console.WriteLine($"  配置文件     : {(config.SourcePath.Length > 0 && File.Exists(config.SourcePath) ? config.SourcePath : "未找到，使用内置默认值")}");
        Console.WriteLine($"  采样设置     : 每天 {config.DailyTime} ｜ 保留 {config.RetentionDays} 天 ｜ 任务超时 {config.TimeoutSeconds}s");
        if (resolution.MigrationNote is not null)
            Console.WriteLine($"  迁移         : {resolution.MigrationNote}");
        if (resolution.Warning is not null)
            Console.WriteLine($"  警告         : {resolution.Warning}");
        if (config.LoadWarning is not null)
            Console.WriteLine($"  配置警告     : {config.LoadWarning}");
        return 0;
    }

    private static int Disks(CommandOptions options)
    {
        var disks = DiskEnumerator.Enumerate();

        if (disks.Count == 0)
        {
            Console.WriteLine("未发现物理磁盘。若系统盘位于 RAID 卡后，可能无法直通读取。");
            return 1;
        }

        foreach (var d in disks)
        {
            bool synthetic = d.HasSyntheticSerial;
            Console.WriteLine($"[{d.DriveNumber}] {d.Model}  ({d.BusType})");
            Console.WriteLine($"     序列号   : {d.SerialNumber}{(synthetic ? "  [合成串]" : string.Empty)}");
            Console.WriteLine($"     固件     : {d.Firmware}");
            Console.WriteLine($"     容量     : {FormatBytes(d.SizeBytes)}");
            Console.WriteLine($"     磁盘标识 : {SampleRecord.BuildDiskKey(d.Model, d.SerialNumber, d.SizeBytes, synthetic)}");
        }

        return 0;
    }

    private static int Probe(CommandOptions options)
    {
        var results = DiskProbeService.ProbeAll();
        bool any = results.Any(r => r.TotalBytes.HasValue);

        if (results.Count == 0)
        {
            Console.Error.WriteLine("未发现可读取的物理磁盘。");
            return 4;
        }

        foreach (var r in results)
        {
            Console.WriteLine($"[{r.DriveNumber}] {r.Model}  ({r.BusType})  序列号 {r.SerialNumber}");
            Console.WriteLine($"     累计写入 : {(r.TotalBytes.HasValue ? FormatBytes(r.TotalBytes) : "不可用")}");
            Console.WriteLine($"     换算     : {r.RawValue} × {r.UnitScale}  [{r.RawUnit}]");
            Console.WriteLine($"     数据质量 : {QualityLabel(r.Quality)}");
            if (r.ImpliedTbwTb.HasValue)
                Console.WriteLine($"     隐含 TBW : {r.ImpliedTbwTb.Value:0.#} TB");

            var metrics = new List<string>();
            if (r.TemperatureCelsius.HasValue) metrics.Add($"温度 {r.TemperatureCelsius.Value:0.#}°C");
            if (r.PowerOnHours.HasValue) metrics.Add($"通电 {r.PowerOnHours.Value} 小时");
            if (r.PercentageUsed.HasValue) metrics.Add($"已用寿命 {r.PercentageUsed.Value}%");
            if (metrics.Count > 0)
                Console.WriteLine($"     健康     : {string.Join("  ", metrics)}");

            Console.WriteLine();
        }

        return any ? 0 : 4;
    }

    private static int Sample(CommandOptions options)
    {
        var outcome = SamplingRunner.Run(DataDirOverride);

        if (!options.Quiet)
        {
            if (outcome.MigrationNote is not null)
                Console.WriteLine(outcome.MigrationNote);

            if (outcome.Records.Count == 0)
            {
                Console.Error.WriteLine("未检测到可读取的累计写入计数器（无盘，或全部读取失败）。");
                Console.Error.WriteLine("  可运行 doctor 查看每块盘的失败原因。");
                return 4;
            }

            Console.WriteLine($"采样完成  {outcome.Records.Count} 块盘");
            foreach (var r in outcome.Records)
                Console.WriteLine($"  {r.Model,-24} {FormatBytes(r.Bytes),10}  {QualityLabel(r.Quality)}");

            if (outcome.PathWarning is not null)
                Console.Error.WriteLine("注意: " + outcome.PathWarning);
        }

        return outcome.Records.Count > 0 ? 0 : 4;
    }

    // ---------- 每日增量 ----------

    private static int Daily(CommandOptions options)
    {
        string sub = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "show";
        if (sub != "show")
        {
            Console.Error.WriteLine($"未知的 daily 子命令: {sub}（仅支持 show）");
            return 2;
        }

        int days = options.GetInt("days", 30);

        // 窗口读取会带回起点之前的前驱，用于计算窗口第一天的增量
        var (_, samples) = LoadSamples(days);

        if (samples.Count == 0)
        {
            // 说明"窗口内没有"而不是"完全没有"，否则用户会以为历史数据丢了
            Console.WriteLine($"最近 {days} 天没有采样数据。先运行 DiskWriteTracker sample，或用 --days 放宽窗口。");

            return 4;
        }

        var stats = DailyAggregator.Aggregate(samples);
        string from = ViewBuilder.WindowStartText(days, DateOnly.FromDateTime(DateTime.Now));
        var filtered = stats.Where(s => string.CompareOrdinal(s.LocalDate, from) >= 0).ToList();

        if (filtered.Count == 0)
        {
            Console.WriteLine($"近 {days} 天没有日统计。当前可能只有一条基线样本，明天再采样即可产生第一天增量。");
            return 0;
        }

        Console.WriteLine($"每日写入增量（近 {days} 天）");
        Console.WriteLine($"{"日期",-12}{"磁盘",-26}{"日增量",12}  {"质量",-8}备注");
        foreach (var s in filtered.OrderByDescending(x => x.LocalDate, StringComparer.Ordinal).ThenBy(x => x.Model, StringComparer.Ordinal))
            Console.WriteLine($"{s.LocalDate,-12}{Trim(s.Model, 24),-26}{FormatBytes(s.DeltaBytes),12}  {QualityLabel(s.Quality),-6}{s.Note}");

        return 0;
    }

    // ---------- 报表与导出 ----------

    private static int Report(CommandOptions options)
    {
        int days = options.GetInt("days", 90);
        var (resolution, samples) = LoadSamples(days);

        if (samples.Count == 0)
        {
            Console.WriteLine("尚无采样数据，无法生成报表。先运行 DiskWriteTracker sample。");
            return 4;
        }

        // 未指定 --out 时写固定名，避免每次生成堆积新文件；同一天另存一份归档。
        // 显式给了 --out 就只写那一份，不产生归档（显式意图优先）。
        var outcome = ReportWriter.Generate(samples, resolution.Directory, days, options.Get("out"), DateTimeOffset.Now);

        Console.WriteLine($"报表已生成: {outcome.File}");
        Console.WriteLine($"  区间 {days} 天，{outcome.Disks} 块盘，{outcome.Rows} 条日记录，{outcome.Bytes / 1024.0:0.#} KB");
        if (outcome.Rows == 0)
            Console.WriteLine("  注意: 当前仅建立基线，尚无增量数据。");
        return 0;
    }

    private static int Export(CommandOptions options)
    {
        string kind = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "csv";
        if (kind != "csv")
        {
            Console.Error.WriteLine($"暂不支持的导出类型: {kind}（当前仅支持 csv）");
            return 2;
        }

        int days = options.GetInt("days", 90);
        var (resolution, samples) = LoadSamples(days);

        // 默认落数据目录下的 exports，与 report 一致。
        // 早先锚定 exe 目录，会在用户手工执行 export 时于程序目录旁边凭空长出一个 exports 文件夹。
        string dir = options.Get("out") ?? Path.Combine(resolution.Directory, "exports");
        var outcome = ExportWriter.WriteCsv(samples, days, dir);

        Console.WriteLine($"已导出 {outcome.DailyRows} 条日统计 -> {outcome.DailyFile}");
        Console.WriteLine($"已导出 {outcome.SampleRows} 条原始样本 -> {outcome.SamplesFile}");
        if (outcome.DailyRows == 0)
            Console.WriteLine("注意: 日统计为空，当前可能只有基线样本。");
        return 0;
    }

    // ---------- 计划任务 ----------

    private static int Schedule(CommandOptions options)
    {
        string sub = options.Positional.Count > 0 ? options.Positional[0].ToLowerInvariant() : "status";

        string exePath = ResolveSelfPath();

        ScheduleResult result = sub switch
        {
            "install" => options.Has("dry-run")
                ? ScheduleManager.Invoke("-Action", "install", "-ExePath", exePath, "-TaskName", ScheduleManager.DefaultTaskName, "-Time", options.Get("time") ?? "00:05", "-DryRun")
                : ScheduleManager.Invoke("-Action", "install", "-ExePath", exePath, "-TaskName", ScheduleManager.DefaultTaskName, "-Time", options.Get("time") ?? "00:05"),

            "uninstall" => InvokeUninstall(options),

            "run" => ScheduleManager.Invoke("-Action", "run", "-TaskName", ScheduleManager.DefaultTaskName),

            _ => ScheduleManager.Invoke("-Action", "status", "-TaskName", ScheduleManager.DefaultTaskName)
        };

        if (!result.Ok)
        {
            string detail = result.Error ?? result.RawJson;
            Console.Error.WriteLine("计划任务操作失败。");

            if (detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("拒绝访问", StringComparison.Ordinal) ||
                detail.Contains("0x80070005", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("原因: 注册计划任务需要管理员权限。");
                Console.Error.WriteLine();
                Console.Error.WriteLine("请以管理员身份打开终端后执行：");
                Console.Error.WriteLine($"  \"{exePath}\" schedule install");
            }
            else
            {
                Console.Error.WriteLine("  " + detail.ReplaceLineEndings(" ").Trim());
            }

            return 7;
        }

        PrintScheduleHuman(sub, result.RawJson);
        return 0;
    }

    /// <summary>
    /// 卸载参数用增量方式拼。开关参数只能在"打开"时出现：
    /// 用 -File 调用脚本时，`-PurgeData:$false` 会被当成一个字符串塞给 SwitchParameter，
    /// 直接参数绑定失败 —— 而这条路径只有真的去卸载时才会走到，平时测不出来。
    /// </summary>
    private static ScheduleResult InvokeUninstall(CommandOptions options)
    {
        var args = new List<string>
        {
            "-Action", "uninstall",
            "-TaskName", ScheduleManager.DefaultTaskName
        };

        // --purge-all 覆盖 --purge-data，两个都给时按更彻底的那个算
        if (options.Has("purge-all"))
            args.Add("-PurgeAll");
        else if (options.Has("purge-data"))
            args.Add("-PurgeData");

        if (options.Has("dry-run"))
            args.Add("-DryRun");

        args.Add("-DataDirectory");
        args.Add(RuntimePaths.ResolveDataDirectory(DataDirOverride).Directory);

        return ScheduleManager.Invoke([.. args]);
    }

    private static void PrintScheduleHuman(string sub, string rawJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawJson);
        }
        catch (JsonException)
        {
            Console.WriteLine(rawJson);
            return;
        }

        if (node is null)
        {
            Console.WriteLine(rawJson);
            return;
        }

        if (sub == "status")
        {
            if (node["installed"]?.GetValue<bool>() != true)
            {
                Console.WriteLine("定时任务: 未安装");
                Console.WriteLine("  运行 DiskWriteTracker schedule install 创建每日自动采样任务。");
                return;
            }

            Console.WriteLine("定时任务: 已安装");
            Console.WriteLine($"  状态       : {node["state"]}");
            Console.WriteLine($"  运行权限   : {(node["runLevel"]?.ToString() == "Highest" ? "最高" : "普通")}");
            Console.WriteLine($"  上次运行   : {TaskStatusSnapshot.FormatTime(node["lastRunTime"]?.ToString())}（{node["lastResultText"]}）");
            Console.WriteLine($"  下次运行   : {TaskStatusSnapshot.FormatTime(node["nextRunTime"]?.ToString())}");
            Console.WriteLine($"  错过的运行 : {node["missedRuns"]}");
            return;
        }

        if (sub == "install")
        {
            if (node["action"]?.ToString() == "dryrun")
            {
                Console.WriteLine("任务配置校验通过（未写入任务计划程序）:");
                Console.WriteLine($"  名称     : {node["taskName"]}");
                Console.WriteLine($"  路径     : {node["taskPath"]}");
                Console.WriteLine($"  程序     : {node["execute"]}");
                Console.WriteLine($"  参数     : {node["arguments"]}");
                Console.WriteLine($"  起始目录 : {node["workingDir"]}");
                Console.WriteLine($"  触发     : 每天 {node["triggerTime"]}");
                Console.WriteLine($"  运行权限 : {node["runLevel"]}  登录类型 {node["logonType"]}");
                Console.WriteLine();
                Console.WriteLine("实际安装需要管理员权限，请以管理员身份执行 schedule install。");
                return;
            }

            bool created = node["created"]?.GetValue<bool>() ?? false;
            Console.WriteLine(created ? "定时任务已创建。" : "定时任务已更新，未产生重复任务。");
            Console.WriteLine($"  名称     : {node["taskName"]}");
            Console.WriteLine($"  时间     : 每天 {node["time"]}");
            Console.WriteLine($"  运行权限 : {(node["runLevel"]?.ToString() == "Highest" ? "最高" : "普通")}");
            Console.WriteLine($"  下次运行 : {TaskStatusSnapshot.FormatTime(node["nextRunTime"]?.ToString())}");

            string? warning = node["warning"]?.ToString();
            if (!string.IsNullOrWhiteSpace(warning))
                Console.WriteLine($"  提示     : {warning}");
            return;
        }

        if (sub == "uninstall")
        {
            if (node["dryRun"]?.GetValue<bool>() == true)
            {
                string mode = node["purgeMode"]?.ToString() ?? "none";
                Console.WriteLine("卸载预演（没有删除任何东西）:");
                Console.WriteLine($"  定时任务 : {(node["taskInstalled"]?.GetValue<bool>() == true ? "将移除" : "未安装")}");
                Console.WriteLine($"  清理档位 : {PurgeModeLabel(mode)}");

                var files = node["willDeleteFiles"] as JsonArray ?? [];
                var dirs = node["willDeleteDirs"] as JsonArray ?? [];
                var kept = node["willKeep"] as JsonArray ?? [];

                Console.WriteLine($"  将删除   : {files.Count} 个文件，{dirs.Count} 个目录");
                foreach (var file in files.Take(20))
                    Console.WriteLine("    - " + file);

                if (files.Count > 20)
                    Console.WriteLine($"    …… 另有 {files.Count - 20} 个文件");

                if (kept.Count > 0)
                {
                    Console.WriteLine($"  将保留   : {kept.Count} 个文件");
                    foreach (var item in kept.Take(10))
                        Console.WriteLine("    + " + item);
                }

                if (node["rootRemovable"]?.GetValue<bool>() == true)
                    Console.WriteLine($"  目录       : {node["dataDirectory"]} 会被整体移除");

                return;
            }

            Console.WriteLine(node["taskRemoved"]?.GetValue<bool>() == true ? "定时任务已移除。" : "定时任务本就不存在。");
            Console.WriteLine("  " + node["dataNote"]);

            if (node["kept"] is JsonArray keptFiles && keptFiles.Count > 0)
                Console.WriteLine($"  保留偏好设置 {keptFiles.Count} 个文件（{node["dataDirectory"] ?? "config"}）。");

            if (node["errors"] is JsonArray errors && errors.Count > 0)
            {
                Console.WriteLine($"  未能删除 {errors.Count} 项：");
                foreach (var error in errors.Take(10))
                    Console.WriteLine("    " + error);
            }
        }
    }

    private static string PurgeModeLabel(string mode) => mode switch
    {
        "all" => "全部删除（含偏好设置）",
        "data" => "只删数据，保留偏好设置",
        _ => "不删数据（仅移除任务）"
    };

    // ---------- 公共 ----------

    /// <summary>
    /// 载入样本。days 为 null 表示全量；给天数则只打开窗口内的文件。
    /// 返回值里可能混有"窗口起点之前的前驱"—— 那是算第一天增量的锚点，
    /// 展示层要按日期再筛一次，别把它当成用户数据。
    /// </summary>
    private static (RuntimePaths.Resolution Resolution, IReadOnlyList<SampleRecord> Samples) LoadSamples(int days)
    {
        var resolution = RuntimePaths.ResolveDataDirectory(DataDirOverride, AppConfigLoader.Load());
        var store = new JsonlSampleStore(resolution.Directory);

        return (resolution, store.ReadForWindow(days, DateOnly.FromDateTime(DateTime.Now)));
    }

    /// <summary>
    /// 计划任务要指向真正的程序本体。若当前是用 dotnet 宿主跑的（dotnet DiskWriteTracker.dll），
    /// 取同目录的 apphost 可执行文件，避免把任务注册成 "dotnet.exe sample"。
    /// </summary>
    internal static string ResolveSelfPath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return processPath;

        string candidate = Path.Combine(AppContext.BaseDirectory, "DiskWriteTracker.exe");
        return File.Exists(candidate) ? candidate : (processPath ?? candidate);
    }

    internal static string FormatBytes(long? bytes) => CsvExporter.FormatBytes(bytes);

    internal static string QualityLabel(string quality) => HtmlReportBuilder.QualityLabel(quality);

    private static string Trim(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

    internal static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}

/// <summary>极简参数解析：动词 + 位置参数 + --key value / --flag。</summary>
internal sealed class CommandOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public bool Quiet => Has("quiet");

    public static CommandOptions Parse(string[] args)
    {
        var options = new CommandOptions();
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                string key = arg[2..];
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    options._values[key] = args[i + 1];
                    i++;
                }
                else
                {
                    options._values[key] = null;
                }
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
            {
                options._values[arg[1..]] = null;
            }
            else
            {
                options.Positional.Add(arg);
            }
        }

        return options;
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out string? v) ? v : null;

    public int GetInt(string key, int fallback)
        => int.TryParse(Get(key), out int value) && value > 0 ? value : fallback;
}
