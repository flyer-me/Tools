using System.Diagnostics;

namespace DiskWriteTracker.Core.Store;

/// <summary>
/// 跨进程写锁：在数据目录放一个常驻的 `.write.lock`，用 FileShare.None 独占打开。
///
/// 为什么不用命名 Mutex：计划任务以 S4U 方式运行在独立会话，`Local\` 前缀的互斥体跨不过
/// 会话边界；`Global\` 则涉及命名空间 DACL，在受限环境更容易被拒。
/// 文件锁不依赖权限，进程终止时由内核自动释放句柄，也不会留下"死锁文件"（这个文件本来就不删）。
/// </summary>
internal static class FileLock
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>获取独占锁。超时抛 <see cref="TimeoutException"/>，由调用方决定放弃写入还是重试。</summary>
    public static FileStream Acquire(string lockPath, TimeSpan timeout)
    {
        string? dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // 打开之前成功率最高：此时还没有进程持锁
        EnsureHidden(lockPath);

        var watch = Stopwatch.StartNew();
        int delay = 250;

        while (true)
        {
            try
            {
                var stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);

                // 首次创建出来的文件不继承隐藏属性，这里补一次。
                // 自己正以 FileShare.None 持有句柄，SetAttributes 可能被拒，失败也无妨——下次 Acquire 会补上。
                EnsureHidden(lockPath);
                return stream;
            }
            catch (Exception ex) when (IsTransient(ex) && watch.Elapsed < timeout)
            {
                Thread.Sleep(delay);
                delay = Math.Min(2000, (int)(delay * 1.6));
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw new TimeoutException(
                    $"等待写入锁超时（{timeout.TotalSeconds:0} 秒）：{lockPath}", ex);
            }
        }
    }

    /// <summary>
    /// 这两个异常在并发抢锁时都可能出现，且都是瞬态的：
    /// 别人持 FileShare.None 时打开得到共享冲突（IOException），
    /// 属性设置与打开交错时得到 ERROR_ACCESS_DENIED（UnauthorizedAccessException）。
    /// 真正的权限问题不在这里判断——写入前的可写性探测会先失败并给出明确提示。
    /// </summary>
    private static bool IsTransient(Exception ex) =>
        ex is IOException or UnauthorizedAccessException;

    /// <summary>
    /// 把锁文件标成隐藏，让数据目录在资源管理器里只露出样本、日志、导出这三类用户能理解的东西。
    /// 纯外观处理，任何失败都忽略：拿不到属性不影响加锁本身。
    /// </summary>
    internal static void EnsureHidden(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            FileAttributes attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Hidden) == 0)
                File.SetAttributes(path, attrs | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 被别的进程独占持有、或文件系统不支持，都不该影响采样
        }
    }
}
