using KKManager.Data.Cards;
using KKManager.Data.Zipmods;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

AnalysisOptions options;
try
{
    options = AnalysisOptions.Parse(args, Environment.CurrentDirectory);
}
catch (Exception ex)
{
    Console.WriteLine("ERROR: " + ex.Message);
    PauseIfNeeded(true);
    return;
}

if (options.ShowHelp)
{
    AnalysisOptions.PrintUsage();
    PauseIfNeeded(options.PauseOnExit);
    return;
}

if (!Directory.Exists(options.GameDirectory))
{
    Console.WriteLine("ERROR: game directory does not exist: " + options.GameDirectory);
    PauseIfNeeded(options.PauseOnExit);
    return;
}

var scanDirectories = new[]
{
    Path.Combine(options.GameDirectory, "UserData", "chara"),
    Path.Combine(options.GameDirectory, "UserData", "studio"),
    Path.Combine(options.GameDirectory, "UserData", "Studio")
}.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

var usedZipmodGuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
var scanSummaries = new List<ScanDirectorySummary>();

Console.WriteLine("Scanning cards for sideloader dependencies...");
foreach (var scanDirectory in scanDirectories)
{
    scanSummaries.Add(ScanPngDirectoryForUsedZipmodGuids(scanDirectory, usedZipmodGuids));
}

var cardFilesVisited = scanSummaries.Sum(x => x.Visited);
var cardFilesParsed = scanSummaries.Sum(x => x.Parsed);
var cardFilesFailed = scanSummaries.Sum(x => x.Failed);

Console.WriteLine("Analyzing target modpack directories...");
var reports = options.TargetModpackNames
    .Select(modpackName => AnalyzeModpackDirectory(options.GameDirectory, modpackName, usedZipmodGuids))
    .ToList();
var totalUnusedSizeBytes = reports.Sum(x => x.UnusedSizeBytes);

var duplicateGuidGroups = reports
    .SelectMany(report => report.Entries)
    .Where(entry => string.IsNullOrWhiteSpace(entry.ErrorMessage) && !string.IsNullOrWhiteSpace(entry.Guid))
    .GroupBy(entry => entry.Guid, StringComparer.OrdinalIgnoreCase)
    .Where(group => group.Count() > 1)
    .OrderByDescending(group => group.Count())
    .ToList();

var moveSummary = MoveSummary.Empty;
if (options.MoveUnused)
{
    Console.WriteLine(options.DryRun
        ? "Planning move operations (--dry-run)..."
        : "Moving unused zipmods...");
    moveSummary = ProcessUnusedMods(reports, options.UnusedOutputRoot, options.DryRun);
}

WriteReport(
    options.ReportPath,
    options,
    cardFilesVisited,
    cardFilesParsed,
    cardFilesFailed,
    usedZipmodGuids,
    scanSummaries,
    reports,
    duplicateGuidGroups,
    moveSummary);

Console.WriteLine();
Console.WriteLine("Analysis complete");
Console.WriteLine("-----------------");
Console.WriteLine("Mode: " + (options.MoveUnused ? (options.DryRun ? "Move plan (dry-run)" : "Move executed") : "Analyze only"));
Console.WriteLine("Card scan visited: " + cardFilesVisited);
Console.WriteLine("Card scan parsed: " + cardFilesParsed);
Console.WriteLine("Card scan failed: " + cardFilesFailed);
Console.WriteLine("Referenced zipmod GUIDs: " + usedZipmodGuids.Count);
Console.WriteLine();

foreach (var summary in scanSummaries)
{
    Console.WriteLine("Scan source: " + summary.DirectoryPath);
    Console.WriteLine("  Exists: " + summary.Exists);
    Console.WriteLine("  Visited: " + summary.Visited);
    Console.WriteLine("  Parsed: " + summary.Parsed);
    Console.WriteLine("  Failed: " + summary.Failed);
    Console.WriteLine("  New GUIDs: " + summary.NewGuids);
    Console.WriteLine();
}

foreach (var report in reports)
{
    Console.WriteLine("Directory: " + report.DisplayName);
    Console.WriteLine("Path: " + report.DirectoryPath);
    Console.WriteLine("Total zipmods: " + report.TotalZipmods);
    Console.WriteLine("Total size: " + report.TotalSizeBytes + " bytes (" + FormatBytes(report.TotalSizeBytes) + ")");
    Console.WriteLine("Used: " + report.UsedCount);
    Console.WriteLine("Used size: " + report.UsedSizeBytes + " bytes (" + FormatBytes(report.UsedSizeBytes) + ")");
    Console.WriteLine("Unused: " + report.UnusedCount);
    Console.WriteLine("Unused size: " + report.UnusedSizeBytes + " bytes (" + FormatBytes(report.UnusedSizeBytes) + ")");
    Console.WriteLine("Invalid: " + report.InvalidCount);
    Console.WriteLine("Invalid size: " + report.InvalidSizeBytes + " bytes (" + FormatBytes(report.InvalidSizeBytes) + ")");
    if (options.MoveUnused)
    {
        Console.WriteLine("Move planned: " + report.MovePlannedCount);
        Console.WriteLine("Moved: " + report.MovedCount);
        Console.WriteLine("Move failed: " + report.MoveFailedCount);
    }
    Console.WriteLine();
}

Console.WriteLine("Unused total size: " + totalUnusedSizeBytes + " bytes (" + FormatBytes(totalUnusedSizeBytes) + ")");
Console.WriteLine();

if (options.MoveUnused)
{
    Console.WriteLine("Move summary");
    Console.WriteLine("  Candidates: " + moveSummary.Candidates);
    Console.WriteLine("  Planned: " + moveSummary.Planned);
    Console.WriteLine("  Moved: " + moveSummary.Moved);
    Console.WriteLine("  Failed: " + moveSummary.Failed);
    Console.WriteLine("  Output root: " + options.UnusedOutputRoot);
    Console.WriteLine();
}

Console.WriteLine("Duplicate GUID groups: " + duplicateGuidGroups.Count);
Console.WriteLine("Report written to: " + options.ReportPath);
PauseIfNeeded(options.PauseOnExit);

static ScanDirectorySummary ScanPngDirectoryForUsedZipmodGuids(
    string scanDirectory,
    HashSet<string> usedZipmodGuids)
{
    if (!Directory.Exists(scanDirectory))
    {
        Console.WriteLine("Skipping missing scan directory: " + scanDirectory);
        return new ScanDirectorySummary(scanDirectory, false, 0, 0, 0, 0);
    }

    var startGuidCount = usedZipmodGuids.Count;
    var visited = 0;
    var parsed = 0;
    var failed = 0;

    foreach (var filePath in Directory.EnumerateFiles(scanDirectory, "*.png", SearchOption.AllDirectories))
    {
        visited++;

        try
        {
            Card card;
            if (!CardLoader.TryParseCard(new FileInfo(filePath), out card) || card == null)
            {
                failed++;
                continue;
            }

            parsed++;

            foreach (var pluginData in card.Extended.Values.Where(x => x != null))
            {
                foreach (var guid in pluginData.RequiredZipmodGUIDs.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    usedZipmodGuids.Add(guid.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            failed++;
            Console.WriteLine("WARN: Failed to read card: " + filePath + " | " + ex.Message);
        }
    }

    var newGuids = usedZipmodGuids.Count - startGuidCount;
    return new ScanDirectorySummary(scanDirectory, true, visited, parsed, failed, newGuids);
}

static ModpackDirectoryReport AnalyzeModpackDirectory(
    string gameDirectory,
    string modpackDirectoryName,
    HashSet<string> usedZipmodGuids)
{
    var modpackPath = ResolveModpackDirectory(gameDirectory, modpackDirectoryName);
    var report = new ModpackDirectoryReport(modpackDirectoryName, modpackPath);

    if (!Directory.Exists(modpackPath))
    {
        return report;
    }

    foreach (var filePath in Directory.EnumerateFiles(modpackPath, "*.*", SearchOption.AllDirectories))
    {
        if (!SideloaderModLoader.IsValidZipmodExtension(Path.GetExtension(filePath)))
            continue;

        report.TotalZipmods++;
        var fileSizeBytes = TryGetFileSizeBytes(filePath);
        report.TotalSizeBytes += fileSizeBytes;

        try
        {
            var mod = SideloaderModLoader.LoadFromFile(filePath);
            var isUsed = !string.IsNullOrWhiteSpace(mod.Guid) && usedZipmodGuids.Contains(mod.Guid);

            report.Entries.Add(new ModpackEntryReport(
                filePath,
                mod.Guid,
                mod.Version,
                mod.Name,
                mod.Author,
                fileSizeBytes,
                isUsed,
                null));

            if (isUsed)
            {
                report.UsedCount++;
                report.UsedSizeBytes += fileSizeBytes;
            }
            else
            {
                report.UnusedCount++;
                report.UnusedSizeBytes += fileSizeBytes;
            }
        }
        catch (Exception ex)
        {
            report.InvalidCount++;
            report.InvalidSizeBytes += fileSizeBytes;
            report.Entries.Add(new ModpackEntryReport(
                filePath,
                null,
                null,
                null,
                null,
                fileSizeBytes,
                false,
                ex.Message));
        }
    }

    return report;
}

static MoveSummary ProcessUnusedMods(
    IEnumerable<ModpackDirectoryReport> reports,
    string outputRoot,
    bool dryRun)
{
    var summary = new MoveSummary();

    foreach (var report in reports)
    {
        var destinationGroupDir = Path.Combine(outputRoot, SanitizeFolderName(report.DisplayName));

        foreach (var entry in report.Entries.Where(x => !x.IsUsed && string.IsNullOrWhiteSpace(x.ErrorMessage)))
        {
            summary.Candidates++;
            summary.Planned++;
            report.MovePlannedCount++;

            var fileName = Path.GetFileName(entry.FilePath);
            var destinationPath = GetUniqueDestinationPath(destinationGroupDir, fileName);
            entry.Action = dryRun ? "WouldMove" : "MovePending";
            entry.ActionDetail = destinationPath;

            if (dryRun)
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(destinationGroupDir);
                File.Move(entry.FilePath, destinationPath);
                entry.Action = "Moved";
                entry.ActionDetail = destinationPath;
                summary.Moved++;
                report.MovedCount++;
            }
            catch (Exception ex)
            {
                entry.Action = "MoveFailed";
                entry.ActionDetail = ex.Message;
                summary.Failed++;
                report.MoveFailedCount++;
            }
        }
    }

    return summary;
}

static string ResolveModpackDirectory(string gameDirectory, string modpackDirectoryName)
{
    var candidates = new[]
    {
        Path.Combine(gameDirectory, "mods", modpackDirectoryName),
        Path.Combine(gameDirectory, modpackDirectoryName)
    };

    return candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
}

static string SanitizeFolderName(string name)
{
    var invalidChars = Path.GetInvalidFileNameChars();
    var chars = name.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray();
    return new string(chars).Trim().TrimEnd('.');
}

static string GetUniqueDestinationPath(string destinationDirectory, string fileName)
{
    var candidate = Path.Combine(destinationDirectory, fileName);
    if (!File.Exists(candidate))
        return candidate;

    var baseName = Path.GetFileNameWithoutExtension(fileName);
    var extension = Path.GetExtension(fileName);

    for (var i = 1; i < int.MaxValue; i++)
    {
        var next = Path.Combine(destinationDirectory, baseName + "_" + i + extension);
        if (!File.Exists(next))
            return next;
    }

    throw new IOException("Could not create unique destination filename for " + fileName);
}

static void WriteReport(
    string reportPath,
    AnalysisOptions options,
    int cardFilesVisited,
    int cardFilesParsed,
    int cardFilesFailed,
    HashSet<string> usedZipmodGuids,
    IEnumerable<ScanDirectorySummary> scanSummaries,
    IEnumerable<ModpackDirectoryReport> reports,
    IEnumerable<IGrouping<string?, ModpackEntryReport>> duplicateGuidGroups,
    MoveSummary moveSummary)
{
    var reportList = reports as IList<ModpackDirectoryReport> ?? reports.ToList();
    var totalUnusedSizeBytes = reportList.Sum(x => x.UnusedSizeBytes);

    var reportDirectory = Path.GetDirectoryName(reportPath);
    if (!string.IsNullOrWhiteSpace(reportDirectory))
    {
        Directory.CreateDirectory(reportDirectory);
    }

    using (var writer = new StreamWriter(reportPath, false, Encoding.UTF8))
    {
        writer.WriteLine("KKManager Modpack Analysis");
        writer.WriteLine("==========================");
        writer.WriteLine("GameDirectory: " + options.GameDirectory);
        writer.WriteLine("Mode: " + (options.MoveUnused ? (options.DryRun ? "Move plan (dry-run)" : "Move executed") : "Analyze only"));
        writer.WriteLine();
        writer.WriteLine("Notes");
        writer.WriteLine("-----");
        writer.WriteLine("Studio scan currently reuses card PNG parsing.");
        writer.WriteLine("Non-card scene data formats are not parsed in this tool.");
        writer.WriteLine();
        writer.WriteLine("Card Scan");
        writer.WriteLine("---------");
        writer.WriteLine("Visited: " + cardFilesVisited);
        writer.WriteLine("Parsed: " + cardFilesParsed);
        writer.WriteLine("Failed: " + cardFilesFailed);
        writer.WriteLine("Referenced Zipmod GUIDs: " + usedZipmodGuids.Count);
        writer.WriteLine("Unused Zipmod Total Size Bytes: " + totalUnusedSizeBytes);
        writer.WriteLine("Unused Zipmod Total Size: " + FormatBytes(totalUnusedSizeBytes));
        writer.WriteLine();
        writer.WriteLine("Scan Sources");
        writer.WriteLine("------------");
        foreach (var summary in scanSummaries)
        {
            writer.WriteLine("Directory: " + summary.DirectoryPath);
            writer.WriteLine("Exists: " + summary.Exists);
            writer.WriteLine("Visited: " + summary.Visited);
            writer.WriteLine("Parsed: " + summary.Parsed);
            writer.WriteLine("Failed: " + summary.Failed);
            writer.WriteLine("NewGuidCount: " + summary.NewGuids);
            writer.WriteLine();
        }

        if (options.MoveUnused)
        {
            writer.WriteLine("Move Summary");
            writer.WriteLine("------------");
            writer.WriteLine("Candidates: " + moveSummary.Candidates);
            writer.WriteLine("Planned: " + moveSummary.Planned);
            writer.WriteLine("Moved: " + moveSummary.Moved);
            writer.WriteLine("Failed: " + moveSummary.Failed);
            writer.WriteLine("OutputRoot: " + options.UnusedOutputRoot);
            writer.WriteLine();
        }

        foreach (var report in reportList)
        {
            writer.WriteLine(report.DisplayName);
            writer.WriteLine(new string('-', report.DisplayName.Length));
            writer.WriteLine("Path: " + report.DirectoryPath);
            writer.WriteLine("TotalZipmods: " + report.TotalZipmods);
            writer.WriteLine("TotalSizeBytes: " + report.TotalSizeBytes);
            writer.WriteLine("TotalSize: " + FormatBytes(report.TotalSizeBytes));
            writer.WriteLine("Used: " + report.UsedCount);
            writer.WriteLine("UsedSizeBytes: " + report.UsedSizeBytes);
            writer.WriteLine("UsedSize: " + FormatBytes(report.UsedSizeBytes));
            writer.WriteLine("Unused: " + report.UnusedCount);
            writer.WriteLine("UnusedSizeBytes: " + report.UnusedSizeBytes);
            writer.WriteLine("UnusedSize: " + FormatBytes(report.UnusedSizeBytes));
            writer.WriteLine("Invalid: " + report.InvalidCount);
            writer.WriteLine("InvalidSizeBytes: " + report.InvalidSizeBytes);
            writer.WriteLine("InvalidSize: " + FormatBytes(report.InvalidSizeBytes));
            if (options.MoveUnused)
            {
                writer.WriteLine("MovePlanned: " + report.MovePlannedCount);
                writer.WriteLine("Moved: " + report.MovedCount);
                writer.WriteLine("MoveFailed: " + report.MoveFailedCount);
            }
            writer.WriteLine();

            foreach (var entry in report.Entries.OrderBy(entry => entry.IsUsed).ThenBy(entry => entry.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine("File: " + entry.FilePath);
                writer.WriteLine("  Name: " + (entry.Name ?? "<unknown>"));
                writer.WriteLine("  Author: " + (entry.Author ?? "<unknown>"));
                writer.WriteLine("  GUID: " + (entry.Guid ?? "<missing>"));
                writer.WriteLine("  Version: " + (entry.Version ?? "<missing>"));
                writer.WriteLine("  SizeBytes: " + entry.FileSizeBytes);
                writer.WriteLine("  Size: " + FormatBytes(entry.FileSizeBytes));
                writer.WriteLine("  Used: " + entry.IsUsed);
                if (!string.IsNullOrWhiteSpace(entry.ErrorMessage))
                    writer.WriteLine("  Error: " + entry.ErrorMessage);
                if (!string.IsNullOrWhiteSpace(entry.Action))
                    writer.WriteLine("  Action: " + entry.Action + " | " + entry.ActionDetail);
                writer.WriteLine();
            }
        }

        writer.WriteLine("Duplicate GUIDs Across Target Modpacks");
        writer.WriteLine("--------------------------------------");
        foreach (var group in duplicateGuidGroups)
        {
            writer.WriteLine("GUID: " + group.Key);
            foreach (var entry in group.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                writer.WriteLine("  " + entry.FilePath);
            }
            writer.WriteLine();
        }
    }
}

static long TryGetFileSizeBytes(string filePath)
{
    try
    {
        return new FileInfo(filePath).Length;
    }
    catch
    {
        return 0L;
    }
}

static string FormatBytes(long bytes)
{
    if (bytes < 1024)
        return bytes + " B";

    var size = (double)bytes;
    var units = new[] { "KB", "MB", "GB", "TB", "PB" };
    var unitIndex = -1;

    while (size >= 1024 && unitIndex < units.Length - 1)
    {
        size /= 1024;
        unitIndex++;
    }

    return size.ToString("0.##") + " " + units[unitIndex];
}

static void PauseIfNeeded(bool pauseOnExit)
{
    if (!pauseOnExit || Console.IsOutputRedirected || !Environment.UserInteractive)
        return;

    if (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
        return;

    Console.WriteLine();
    Console.Write("Press Enter to exit...");
    Console.ReadLine();
}

internal sealed class AnalysisOptions
{
    public string GameDirectory { get; private set; } = @"D:\ProgramData\HS2_DXBR_R16";
    public List<string> TargetModpackNames { get; private set; } = new List<string>
    {
        "Sideloader Modpack",
        "Sideloader Modpack - Exclusive HS2"
    };
    public string ReportPath { get; private set; } = string.Empty;
    public bool MoveUnused { get; private set; }
    public bool DryRun { get; private set; } = true;
    public bool PauseOnExit { get; private set; } = true;
    public string UnusedOutputRoot { get; private set; } = string.Empty;
    public bool ShowHelp { get; private set; }

    public static AnalysisOptions Parse(string[] args, string currentDirectory)
    {
        var options = new AnalysisOptions
        {
            ReportPath = Path.Combine(currentDirectory, "ModpackAnalysisReport.txt"),
            UnusedOutputRoot = Path.Combine(currentDirectory, "UnusedMods")
        };

        var overrideModpacks = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
                case "--game-dir":
                    i = RequireValueIndex(args, i);
                    options.GameDirectory = args[i];
                    break;
                case "--report":
                    i = RequireValueIndex(args, i);
                    options.ReportPath = ToAbsolutePath(args[i], currentDirectory);
                    break;
                case "--unused-output":
                    i = RequireValueIndex(args, i);
                    options.UnusedOutputRoot = ToAbsolutePath(args[i], currentDirectory);
                    break;
                case "--modpack":
                    i = RequireValueIndex(args, i);
                    overrideModpacks.Add(args[i]);
                    break;
                case "--move-unused":
                    options.MoveUnused = true;
                    options.DryRun = false;
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--no-pause":
                    options.PauseOnExit = false;
                    break;
                case "--pause":
                    options.PauseOnExit = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + args[i]);
            }
        }

        if (overrideModpacks.Count > 0)
            options.TargetModpackNames = overrideModpacks;

        options.GameDirectory = ToAbsolutePath(options.GameDirectory, currentDirectory);
        options.ReportPath = ToAbsolutePath(options.ReportPath, currentDirectory);
        options.UnusedOutputRoot = ToAbsolutePath(options.UnusedOutputRoot, currentDirectory);

        return options;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  HS2ModpackAnalyzer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --game-dir <path>        Game root directory.");
        Console.WriteLine("  --report <path>          Report output path.");
        Console.WriteLine("  --modpack <name>         Target modpack name (repeatable).");
        Console.WriteLine("  --move-unused            Move unused zipmods to output folder.");
        Console.WriteLine("  --dry-run                Plan move operations without file changes.");
        Console.WriteLine("  --unused-output <path>   Output root for moved/planned unused zipmods.");
        Console.WriteLine("  --no-pause               Do not wait for Enter before exit.");
        Console.WriteLine("  --pause                  Wait for Enter before exit (default).");
        Console.WriteLine("  -h, --help               Show this help.");
    }

    private static int RequireValueIndex(string[] args, int index)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException("Missing value for argument: " + args[index]);
        return index + 1;
    }

    private static string ToAbsolutePath(string path, string currentDirectory)
    {
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(currentDirectory, path));
    }
}

internal sealed class MoveSummary
{
    public static MoveSummary Empty => new MoveSummary();

    public int Candidates { get; set; }
    public int Planned { get; set; }
    public int Moved { get; set; }
    public int Failed { get; set; }
}

internal sealed class ScanDirectorySummary
{
    public ScanDirectorySummary(string directoryPath, bool exists, int visited, int parsed, int failed, int newGuids)
    {
        DirectoryPath = directoryPath;
        Exists = exists;
        Visited = visited;
        Parsed = parsed;
        Failed = failed;
        NewGuids = newGuids;
    }

    public string DirectoryPath { get; private set; }
    public bool Exists { get; private set; }
    public int Visited { get; private set; }
    public int Parsed { get; private set; }
    public int Failed { get; private set; }
    public int NewGuids { get; private set; }
}

internal sealed class ModpackDirectoryReport
{
    public ModpackDirectoryReport(string displayName, string directoryPath)
    {
        DisplayName = displayName;
        DirectoryPath = directoryPath;
        Entries = new List<ModpackEntryReport>();
    }

    public string DisplayName { get; private set; }
    public string DirectoryPath { get; private set; }
    public int TotalZipmods { get; set; }
    public int UsedCount { get; set; }
    public int UnusedCount { get; set; }
    public int InvalidCount { get; set; }
    public long TotalSizeBytes { get; set; }
    public long UsedSizeBytes { get; set; }
    public long UnusedSizeBytes { get; set; }
    public long InvalidSizeBytes { get; set; }
    public int MovePlannedCount { get; set; }
    public int MovedCount { get; set; }
    public int MoveFailedCount { get; set; }
    public List<ModpackEntryReport> Entries { get; private set; }
}

internal sealed class ModpackEntryReport
{
    public ModpackEntryReport(
        string filePath,
        string? guid,
        string? version,
        string? name,
        string? author,
        long fileSizeBytes,
        bool isUsed,
        string? errorMessage)
    {
        FilePath = filePath;
        Guid = guid;
        Version = version;
        Name = name;
        Author = author;
        FileSizeBytes = fileSizeBytes;
        IsUsed = isUsed;
        ErrorMessage = errorMessage;
    }

    public string FilePath { get; private set; }
    public string? Guid { get; private set; }
    public string? Version { get; private set; }
    public string? Name { get; private set; }
    public string? Author { get; private set; }
    public long FileSizeBytes { get; private set; }
    public bool IsUsed { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string? Action { get; set; }
    public string? ActionDetail { get; set; }
}
