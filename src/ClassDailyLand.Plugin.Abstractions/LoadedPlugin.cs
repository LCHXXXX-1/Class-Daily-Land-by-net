namespace ClassDailyLand.Plugin.Abstractions;

/// <summary>
/// 单个插件的加载信息（宿主侧的观察对象）。
/// </summary>
public sealed class LoadedPlugin
{
    /// <summary>插件目录名，作为唯一标识。</summary>
    public string Id { get; init; } = "";

    public string Directory { get; init; } = "";

    public PluginManifest Manifest { get; init; } = new();

    public IPlugin? Instance { get; init; }

    public IPluginApi? Api { get; init; }

    /// <summary>插件程序集入口路径。</summary>
    public string AssemblyPath { get; init; } = "";

    public bool IsLoaded => Instance is not null;
}

/// <summary>
/// 插件加载失败记录。
/// 源项目约定「插件加载失败不影响主程序」，因此失败只记录不抛出。
/// </summary>
public sealed class PluginLoadFailure
{
    public string Id { get; init; } = "";

    public string Reason { get; init; } = "";

    public Exception? Exception { get; init; }
}
