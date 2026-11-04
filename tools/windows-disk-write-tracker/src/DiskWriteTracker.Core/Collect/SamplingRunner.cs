using System.Diagnostics;
using System.Globalization;
using DiskWriteTracker.Core.Config;
using DiskWriteTracker.Core.Discovery;
using DiskWriteTracker.Core.Logging;
using DiskWriteTracker.Core.Store;

namespace DiskWriteTracker.Core.Collect;

public sealed record SamplingOutcome(
    string RunId,
    IReadOnlyList<SampleRecord> Records,
    bool FallbackDataDirectory,
    string? PathWarning,
    string DataDirectory,
    string? FilePath,
    bool IsPortable = false,
    string? MigrationNote = null);

/// <summary>一次完整采样：解析数据目录 → 读取硬件 → 归一化身份 → 追加 JSONL → 按保留期剪枝。</summary>
public static class SamplingRunner
{
    public static SamplingOutcome Run(string? explicitDataDirectory = null)
    {
        var config = AppConfigLoader.Load();
        var resolution = RuntimePaths.ResolveDataDirectory(explicitDataDirectory, config);

        // 显式指定但不可写时立刻用中文失败，避免后面抛英文 UnauthorizedAccessException
        if (!string.IsNullOrWhiteSpace(explicitDataDirectory) && resolution.Warning is not null)
            throw new IOException($"数据目录不可写：{resolution.Directory}（{resolution.Warning}）");

        var store = new JsonlSampleStore(resolution.Directory);
        var watch = Stopwatch.StartNew();

        string runId = Guid.NewGuid().ToString("N")[..12];
        var nowUtc = DateTimeOffset.UtcNow;
        var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, TimeZoneInfo.Local);
        string localDate = nowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var probes = DiskProbeService.ProbeAll();
        var records = new List<SampleRecord>(probes.Count);

        foreach (var probe in probes)
        {
            bool synthetic = PhysicalDiskInfo.LooksSynthetic(probe.SerialNumber);

            records.Add(new SampleRecord
            {
                RunId = runId,
                CapturedUtc = nowUtc.ToString("O", CultureInfo.InvariantCulture),
                CapturedLocal = nowLocal.ToString("O", CultureInfo.InvariantCulture),
                LocalDate = localDate,
                TzOffsetMinutes = (int)nowLocal.Offset.TotalMinutes,
                DiskKey = SampleRecord.BuildDiskKey(probe.Model, probe.SerialNumber, probe.SizeBytes, synthetic),
                DriveNumber = probe.DriveNumber,
                Model = probe.Model,
                SerialNumber = probe.SerialNumber,
                Firmware = probe.Firmware,
                BusType = probe.BusType,
                Source = probe.Source,
                AttributeRef = probe.AttributeRef,
                RawValue = probe.RawValue,
                RawUnit = probe.RawUnit,
                UnitScale = probe.UnitScale,
                Bytes = probe.TotalBytes,
                Quality = probe.Quality,
                Note = probe.Note,
                TemperatureC = probe.TemperatureCelsius,
                PowerOnHours = probe.PowerOnHours,
                PowerCycles = probe.PowerCycles,
                PercentageUsed = probe.PercentageUsed
            });
        }

        if (records.Count > 0)
            store.Append(records);

        int pruned = PruneOldSamples(resolution.Directory, config.RetentionDays);

        string? file = records.Count > 0 && DateOnly.TryParseExact(localDate, "yyyy-MM-dd", out DateOnly date)
            ? store.FilePathFor(date)
            : null;

        watch.Stop();

        if (!string.Equals(config.LogLevel, "ERROR", StringComparison.OrdinalIgnoreCase))
        {
            EngineLog.Info(resolution.Directory, string.Format(
                CultureInfo.InvariantCulture,
                "sample run={0} disks={1} dir={2} portable={3} fallback={4} pruned={5} elapsed={6}ms",
                runId, records.Count, resolution.Directory, resolution.IsPortable,
                resolution.IsFallback, pruned, watch.ElapsedMilliseconds));
        }

        return new SamplingOutcome(
            runId, records, resolution.IsFallback, resolution.Warning,
            resolution.Directory, file, resolution.IsPortable, resolution.MigrationNote);
    }

    /// <summary>按文件名里的日期删除超期样本。只删文件，不递归删目录。</summary>
    private static int PruneOldSamples(string directory, int retentionDays)
    {
        if (retentionDays <= 0 || !Directory.Exists(directory))
            return 0;

        var cutoff = DateOnly.FromDateTime(DateTime.Now).AddDays(-retentionDays);
        int removed = 0;

        foreach (SampleFile file in SampleFile.Enumerate(directory))
        {
            if (file.Date >= cutoff)
                continue;

            try
            {
                File.Delete(file.Path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 被占用或没权限，下次再试
            }
        }

        return removed;
    }
}
