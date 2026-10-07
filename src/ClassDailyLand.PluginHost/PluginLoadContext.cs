using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace ClassDailyLand.PluginHost;

/// <summary>
/// 插件专属程序集加载上下文（AssemblyLoadContext）。
///
/// 作用：
/// 1. 隔离 —— 每个插件独立上下文，插件间依赖不互相干扰，也便于整体卸载；
/// 2. 共享契约 —— 契约程序集（Core / Plugin.Abstractions）必须由默认上下文提供。
///    否则插件里看到的 IPlugin 与宿主里的 IPlugin 会是两个不同类型，
///    反射断言会失败，这是 .NET 插件体系最典型的坑。
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    /// <summary>必须由默认上下文解析的共享程序集。</summary>
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClassDailyLand.Core",
        "ClassDailyLand.Plugin.Abstractions",
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath)
        : base($"Plugin:{Path.GetFileNameWithoutExtension(pluginAssemblyPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 返回 null 表示「本上下文不处理」→ 交由默认上下文解析，从而共享同一份类型
        if (assemblyName.Name is not null && SharedAssemblies.Contains(assemblyName.Name))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
