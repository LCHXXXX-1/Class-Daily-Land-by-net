namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 统一路径管理。
/// 对应源模块：paths.py。
/// 关键设计：严格区分「可写数据目录」与「只读资源目录」，
/// 以兼容自包含单文件发布（数据在 exe 旁，资源在解包目录）。
/// </summary>
public interface IPathService
{
    /// <summary>可写数据（settings / plugins）的根目录。</summary>
    string AppRoot { get; }

    /// <summary>只读资源（图标等）的根目录。</summary>
    string ResourceRoot { get; }

    /// <summary>配置目录：{AppRoot}/settings。</summary>
    string ConfigDirectory { get; }

    /// <summary>插件目录：{AppRoot}/plugins。</summary>
    string PluginsDirectory { get; }

    /// <summary>插件市场索引缓存目录：{ConfigDirectory}/plugin-repo。</summary>
    string MarketCacheDirectory { get; }

    /// <summary>第三方依赖包根目录：{PluginsDirectory}/packages。</summary>
    string PackagesDirectory { get; }

    /// <summary>程序图标路径（找不到时返回首选路径）。</summary>
    string IconPath { get; }

    /// <summary>确保所有运行时目录存在。</summary>
    void EnsureDirectories();

    /// <summary>拼接配置目录下的某个文件路径。</summary>
    string ConfigFile(string fileName);

    /// <summary>拼接插件目录下的某个子路径。</summary>
    string PluginPath(params string[] segments);
}
