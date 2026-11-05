namespace DiskWriteTracker.Core.Config;

/// <summary>
/// 数据目录解析。优先级：显式参数 → exe 同目录的 data\（便携）→ ProgramData → LOCALAPPDATA。
///
/// 便携目录必须由引擎自己识别，不能只靠调用方传 --data-dir：
/// 计划任务注册的命令是写死的 `sample --quiet`，不带任何参数，只有引擎自己判断，
/// 计划任务与手动采样才会落在同一个目录。exe 被拷到别处，数据也跟着走。
/// </summary>
public static class RuntimePaths
{
    private const string MigrationMarker = ".migrated-from-legacy";

    /// <summary>便携目录：exe 同级的 data\。存在即视为便携部署。</summary>
    public static string PortableDataDirectory => Path.Combine(AppContext.BaseDirectory, "data");

    public static string PreferredDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskWriteTracker");

    public static string FallbackDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskWriteTracker");

    public sealed record Resolution(
        string Directory,
        bool IsFallback,
        bool IsPortable,
        string? Warning,
        string? MigrationNote);

    public static Resolution ResolveDataDirectory(string? explicitDirectory = null, AppConfig? config = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
        {
            string expanded = Environment.ExpandEnvironmentVariables(explicitDirectory);
            return TryEnsure(expanded, out string? error)
                ? new Resolution(expanded, false, false, null, null)
                : new Resolution(expanded, false, false, $"指定目录不可写：{error}", null);
        }

        if (Directory.Exists(PortableDataDirectory) && TryEnsure(PortableDataDirectory, out _))
        {
            return new Resolution(PortableDataDirectory, false, true, null, MigrateLegacy(PortableDataDirectory));
        }

        string preferred = string.IsNullOrWhiteSpace(config?.DataDirectory)
            ? PreferredDataDirectory
            : Environment.ExpandEnvironmentVariables(config.DataDirectory);

        string fallback = string.IsNullOrWhiteSpace(config?.FallbackDirectory)
            ? FallbackDataDirectory
            : Environment.ExpandEnvironmentVariables(config.FallbackDirectory);

        if (TryEnsure(preferred, out string? preferredError))
            return new Resolution(preferred, false, false, null, null);

        TryEnsure(fallback, out string? fallbackError);

        return new Resolution(
            fallback,
            true,
            false,
            $"ProgramData 不可写（{preferredError}），已降级到用户目录。fallback: {fallbackError ?? "ok"}",
            null);
    }

    /// <summary>
    /// 首次切到便携目录时，把 ProgramData 里的历史样本复制一份过来。
    /// 复制源文件并保留，重复执行幂等。
    /// </summary>
    private static string? MigrateLegacy(string target)
    {
        try
        {
            if (File.Exists(Path.Combine(target, MigrationMarker)))
                return null;

            string legacy = PreferredDataDirectory;

            if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                return null;

            if (!Directory.Exists(legacy))
                return null;

            // 便携目录已经有数据就不动，避免覆盖用户自己拷进来的记录
            if (Directory.EnumerateFiles(target, "samples-*.jsonl").Any())
                return null;

            string[] files = Directory.GetFiles(legacy, "samples-*.jsonl");
            if (files.Length == 0)
                return null;

            int copied = 0;
            foreach (string file in files)
            {
                string dest = Path.Combine(target, Path.GetFileName(file));
                if (File.Exists(dest))
                    continue;

                File.Copy(file, dest);
                copied++;
            }

            string marker = Path.Combine(target, MigrationMarker);
            File.WriteAllText(
                marker,
                $"from={legacy}{Environment.NewLine}files={copied}{Environment.NewLine}at={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}");

            Store.FileLock.EnsureHidden(marker);

            return copied > 0
                ? $"已从 {legacy} 复制 {copied} 个历史样本文件到便携目录 {target}"
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 迁移失败时源文件保留在原目录
            return null;
        }
    }

    private static bool TryEnsure(string directory, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(directory);

            // 探测文件名必须唯一：两个进程同时用同一个名字写/删会互相抢成 IOException，
            // 进而被误判成"目录不可写"，导致并发采样静默少写样本。
            string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = "拒绝访问";
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
