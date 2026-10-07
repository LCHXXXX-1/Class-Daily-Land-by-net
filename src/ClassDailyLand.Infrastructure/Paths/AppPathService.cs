using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Paths;

/// <summary>
/// 统一路径管理实现。对应源模块：paths.py。
///
/// 关键设计（与源项目一致）：
/// 严格区分「可写数据目录」与「只读资源目录」。
/// 自包含单文件发布时，exe 所在目录才是可写位置，
/// 而资源可能位于解包目录，因此不能混用同一个根。
/// </summary>
public sealed class AppPathService : IPathService
{
    private const string SettingsFolderName = "settings";
    private const string PluginsFolderName = "plugins";
    private const string MarketCacheFolderName = "plugin-repo";

    /// <summary>依赖包目录名。与源项目一致，且插件宿主扫描时会跳过它。</summary>
    private const string PackagesFolderName = "packages";
    private const string IconFileName = "icon.ico";

    public string AppRoot { get; }
    public string ResourceRoot { get; }
    public string ConfigDirectory { get; }
    public string PluginsDirectory { get; }
    public string MarketCacheDirectory { get; }

    /// <inheritdoc />
    public string PackagesDirectory { get; }

    public string IconPath { get; }

    public AppPathService(string? appRootOverride = null)
    {
        AppRoot = Normalize(appRootOverride ?? ResolveAppRoot());
        ResourceRoot = ResolveResourceRoot(AppRoot);

        ConfigDirectory = Path.Combine(AppRoot, SettingsFolderName);
        PluginsDirectory = Path.Combine(AppRoot, PluginsFolderName);
        MarketCacheDirectory = Path.Combine(ConfigDirectory, MarketCacheFolderName);
        PackagesDirectory = Path.Combine(PluginsDirectory, PackagesFolderName);
        IconPath = FindIcon();
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(MarketCacheDirectory);
    }

    public string ConfigFile(string fileName) => Path.Combine(ConfigDirectory, fileName);

    public string PluginPath(params string[] segments)
    {
        var parts = new List<string> { PluginsDirectory };
        parts.AddRange(segments);
        return Path.Combine(parts.ToArray());
    }

    /// <summary>
    /// 可写数据根目录。
    /// AppContext.BaseDirectory 在「框架依赖」与「自包含单文件」两种发布下
    /// 都指向 exe 所在目录，是单文件发布下唯一可靠的取法
    /// （Assembly.Location 在单文件模式下返回空串，不可用）。
    /// </summary>
    private static string ResolveAppRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir)) return baseDir;

        // 兜底一：进程可执行文件所在目录
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var processDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(processDir)) return processDir;
        }

        // 兜底二：当前工作目录
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// 只读资源根目录。WPF 通过 Resource 构建动作内嵌资源，正常无需外部文件；
    /// 但若把 icon.ico 作为内容随包发布，则与 exe 同目录，故此处回落到 AppRoot。
    /// </summary>
    private static string ResolveResourceRoot(string appRoot) => appRoot;

    private string FindIcon()
    {
        var candidates = new[]
        {
            Path.Combine(ResourceRoot, IconFileName),
            Path.Combine(ResourceRoot, "Assets", IconFileName),
            Path.Combine(AppRoot, IconFileName),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        // 两处都没有时返回首选路径（对应源项目 _find_icon 的行为）
        return candidates[0];
    }

    private static string Normalize(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
