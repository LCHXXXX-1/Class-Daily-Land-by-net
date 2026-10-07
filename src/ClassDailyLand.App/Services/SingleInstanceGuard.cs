using System.IO;
using System.Threading;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 单实例守卫。对应源模块：single_instance.py。
///
/// 用命名互斥体实现，避免多个实例同时读写配置造成冲突。
/// 源项目通过 Win32 CreateMutexW + ERROR_ALREADY_EXISTS 判断；
/// .NET 的 Mutex(createdNew) 语义等价，且跨平台可用。
/// </summary>
internal static class SingleInstanceGuard
{
    private const string MutexName = "ClassDailyLand_SingleInstance_Mutex";

    /// <summary>持有引用直到进程结束，释放即表示退出。</summary>
    private static Mutex? _mutex;

    /// <summary>尝试获取单实例锁。</summary>
    /// <param name="message">获取失败时的提示文案。</param>
    public static bool TryAcquire(out string message)
    {
        message = "";

        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);

            if (createdNew) return true;

            // 互斥体已存在，但上一个实例可能异常退出（被遗弃）。
            // 尝试零等待获取：能拿到说明持有者已消失，可以继续运行。
            try
            {
                if (_mutex.WaitOne(0)) return true;
            }
            catch (AbandonedMutexException)
            {
                // 前一个实例崩溃留下的遗弃互斥体 —— 视为已获取
                return true;
            }

            message = "Class Daily Land 已经在运行啦，请先退出已有实例。";
            _mutex.Dispose();
            _mutex = null;
            return false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // 互斥体创建异常时不阻塞用户使用（与源项目不同：源会直接退出并报错）
            message = $"单实例锁创建失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>释放锁（进程退出时调用）。</summary>
    public static void Release()
    {
        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 当前线程并不持有该互斥体，忽略
        }
        finally
        {
            _mutex?.Dispose();
            _mutex = null;
        }
    }
}
