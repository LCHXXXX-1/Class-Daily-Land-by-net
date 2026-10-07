using System.Diagnostics;
using System.IO;
using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Updates;

/// <summary>拉起更新器的结果。</summary>
/// <param name="Success">是否成功（被源码保护拦下也算「不算失败」）。</param>
/// <param name="Message">失败原因；成功或被拦下时为空。</param>
/// <param name="Spawned">是否真的拉起了更新器。</param>
/// <param name="Hits">源码保护的命中列表（未命中时为空）。</param>
public sealed record UpdateLaunchResult(
    bool Success,
    string Message = "",
    bool Spawned = false,
    IReadOnlyList<SourceEntryHit>? Hits = null);

/// <summary>
/// 更新链路。对应源模块：launch_updater.py。
///
/// 更新器是一个独立的可执行文件（Launcher.exe），由主程序以
/// <c>--target &lt;程序目录&gt; --pid &lt;当前进程号&gt; [--silent]</c> 拉起；
/// 它下载新版本、等待本进程退出、替换文件，再用
/// <c>%TEMP%/ClassDailyLandLauncher/quit.flag</c> 这类标志文件与主程序约定退出时机。
///
/// 「源码运行保护」把三处入口的拦截统一收在这里：
/// 检测到未打包的主入口源码就不拉起更新器 ——
/// 启动时静默说明、手动检查时弹窗提醒备份并请求确认。
/// </summary>
public sealed class UpdateLauncher
{
    /// <summary>更新器主程序名（优先用 exe）。</summary>
    public const string LauncherExeName = "Launcher.exe";

    /// <summary>更新器脚本名（exe 不可用时的兜底，对应编辑器环境）。</summary>
    public const string LauncherScriptName = "launcher.py";

    /// <summary>与更新器约定的退出标志相对路径。</summary>
    private const string QuitFlagRelativePath = "ClassDailyLandLauncher/quit.flag";

    private readonly IPathService _paths;
    private readonly ISettingsService _settings;
    private readonly string _appDirectory;

    public UpdateLauncher(IPathService paths, ISettingsService settings, string? appDirectory = null)
    {
        _paths = paths;
        _settings = settings;
        _appDirectory = appDirectory ?? paths.AppRoot;
    }

    /// <summary>更新器可执行文件的完整路径；找不到返回 null。</summary>
    public string? LauncherPath => FindFile(LauncherExeName);

    /// <summary>当前是否有可用的更新器。</summary>
    public bool IsUpdaterAvailable => LauncherPath is not null || FindFile(LauncherScriptName) is not null;

    // ================= 入口 =================

    /// <summary>
    /// 启动时后台静默检查更新（对应 launch_hidden）。
    /// 检测到本地源码时**只弹说明、不拉起更新器**。
    /// </summary>
    public UpdateLaunchResult LaunchHidden(bool force = false)
        => Run(silent: true, force: force);

    /// <summary>
    /// 用户主动检查更新（对应 launch_visible）。
    /// 检测到本地源码时先请求确认，用户不点「仍然更新」就不会拉起。
    /// </summary>
    /// <param name="confirm">确认回调：返回 true 表示用户坚持更新。
    /// 传 null 时按「不影响展示层」处理 —— 视为取消。</param>
    public UpdateLaunchResult LaunchVisible(Func<IReadOnlyList<SourceEntryHit>, bool>? confirm = null, bool force = false)
        => Run(silent: false, force: force, confirm: confirm);

    /// <summary>
    /// 手动检查更新。返回 (是否放行, 命中列表)；
    /// 放行后由调用方决定是拉起更新器还是先弹确认。
    /// </summary>
    public (bool Allowed, IReadOnlyList<SourceEntryHit> Hits) EvaluateSourceGuard()
    {
        if (!_settings.Current.BlockUpdateOnSource) return (true, Array.Empty<SourceEntryHit>());

        var hits = SourceTreeGuard.Scan(_appDirectory);
        return (hits.Count == 0, hits);
    }

    /// <summary>备份提示文案（对应 _backup_tip）。</summary>
    public static string BackupTip(IReadOnlyList<SourceEntryHit> hits)
        => "建议先手动备份：把整个程序目录复制一份（或先 git commit），"
           + "否则更新会用发布版直接覆盖这些文件，你本地没保存的改动会丢。\n"
           + $"命中文件：{SourceTreeGuard.BriefNames(hits)}";

    // ================= 核心 =================

    private UpdateLaunchResult Run(
        bool silent,
        bool force,
        Func<IReadOnlyList<SourceEntryHit>, bool>? confirm = null)
    {
        // ---- 源码运行保护：命中主入口就别拉起更新器 ----
        // 只有两种结果：要么直接走人（不拉起），要么让用户确认后**继续往下**。
        if (!force)
        {
            var (allowed, hits) = EvaluateSourceGuard();

            if (!allowed)
            {
                // 启动时：不拉起，弹一次说明（只有一个「知道了」，不要求做决定）
                if (silent) return new UpdateLaunchResult(true, Spawned: false, Hits: hits);

                // 主动检查：用户取消 / 关窗 —— 一律不拉起，也不算失败
                if (confirm is null || !confirm(hits))
                    return new UpdateLaunchResult(true, Spawned: false, Hits: hits);

                // 用户点了「仍然更新（已备份）」—— 不返回，继续下面的拉起
            }
        }

        var arguments = new List<string>
        {
            "--target", _appDirectory,
            "--pid", Environment.ProcessId.ToString(),
        };

        if (silent) arguments.Add("--silent");

        // 1) 优先用 exe
        var launcher = FindFile(LauncherExeName);
        if (launcher is not null)
            return Spawn(launcher, arguments, Path.GetDirectoryName(launcher)!);

        // 2) exe 不可用 → 用脚本兜底（编辑器环境）
        var script = FindFile(LauncherScriptName);
        if (script is not null)
        {
            var python = FindPython();
            if (python is null)
                return new UpdateLaunchResult(false, $"找到 {LauncherScriptName}，但找不到可用的 Python 解释器");

            var scriptArguments = new List<string> { script };
            scriptArguments.AddRange(arguments);

            return Spawn(python, scriptArguments, Path.GetDirectoryName(script)!);
        }

        return new UpdateLaunchResult(false, $"既没找到 {LauncherExeName}，也没找到 {LauncherScriptName}");
    }

    private static UpdateLaunchResult Spawn(string fileName, IEnumerable<string> arguments, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            Process.Start(startInfo);
            return new UpdateLaunchResult(true, Spawned: true);
        }
        catch (Exception ex)
        {
            return new UpdateLaunchResult(false, $"启动更新器失败：{ex.Message}");
        }
    }

    // ================= 退出标志 =================

    /// <summary>退出信号标志文件路径（Launcher 写它，主程序轮询它）。</summary>
    public static string GetQuitFlagPath()
        => Path.Combine(Path.GetTempPath(), QuitFlagRelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>清掉上一次遗留的退出标志。</summary>
    public static void ClearQuitFlag()
    {
        try
        {
            var path = GetQuitFlagPath();
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就算了，下面轮询时会重试
        }
    }

    /// <summary>更新器是否已请求本进程退出。</summary>
    public static bool QuitRequested()
    {
        try
        {
            return File.Exists(GetQuitFlagPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ================= 文件定位 =================

    /// <summary>
    /// 更新器可能存在的目录，按优先级排列。
    /// 与源实现一致：先看上级目录（发布版把更新器放在程序目录旁边），
    /// 再看程序目录本身与 _internal 子目录。
    /// </summary>
    private IEnumerable<string> SearchDirectories()
    {
        var parent = Path.GetDirectoryName(_appDirectory.TrimEnd(Path.DirectorySeparatorChar));

        if (!string.IsNullOrEmpty(parent)) yield return parent;
        yield return _appDirectory;
        yield return Path.Combine(_appDirectory, "_internal");
        yield return _paths.ResourceRoot;
    }

    private string? FindFile(string name)
    {
        foreach (var directory in SearchDirectories())
        {
            if (string.IsNullOrEmpty(directory)) continue;

            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // 路径非法就跳过
            }
        }

        return null;
    }

    private static string? FindPython()
    {
        // 优先用当前进程的宿主（源码运行时的 dotnet 环境不适用，这里只兜底脚本模式）
        foreach (var candidate in new[] { "python.exe", "python3.exe", "python" })
        {
            var found = FindOnPath(candidate);
            if (found is not null) return found;
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable)) return null;

        foreach (var directory in pathVariable.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;

            try
            {
                var candidate = Path.Combine(directory.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                // 无效目录跳过
            }
        }

        return null;
    }
}
