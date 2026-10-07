using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Plugin.Abstractions;

namespace ClassDailyLand.PluginHost;

/// <summary>
/// 由宿主注入的插件能力工厂：为每个插件创建一份独立的 IPluginApi 实现。
/// </summary>
public delegate IPluginApi PluginApiFactory(string pluginId, string dataDirectory, PluginManifest manifest);

/// <summary>
/// 插件宿主。对应源模块：plugin_manager.py 中 PluginManager 的加载与卸载部分。
///
/// 约定：插件加载失败只记录、不抛出 —— 单个坏插件绝不能拖垮主程序启动。
/// </summary>
public sealed class PluginManager
{
    public const string ManifestFileName = "plugin.json";
    public const string DataDirectoryName = "data";

    /// <summary>依赖包目录，不是插件，扫描时需跳过。</summary>
    private const string PackagesDirectoryName = "packages";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IPathService _paths;

    private readonly List<LoadedPlugin> _plugins = new();
    private readonly List<PluginLoadFailure> _failures = new();
    private readonly Dictionary<string, PluginLoadContext> _contexts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LoadedPlugin> Plugins => _plugins;
    public IReadOnlyList<PluginLoadFailure> Failures => _failures;

    /// <summary>插件卸载前触发，供宿主实现执行插件的 OnStop 回调。</summary>
    public event EventHandler<LoadedPlugin>? PluginStopping;

    public PluginManager(IPathService paths) => _paths = paths;

    // ================= 加载 =================

    /// <summary>扫描插件目录并全部加载。已加载的插件会先被清空。</summary>
    public void LoadAll(IEnumerable<string>? disabledIds, PluginApiFactory apiFactory)
    {
        ArgumentNullException.ThrowIfNull(apiFactory);

        _plugins.Clear();
        _failures.Clear();

        var root = _paths.PluginsDirectory;
        if (!Directory.Exists(root)) return;

        var disabled = new HashSet<string>(
            disabledIds ?? Enumerable.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(directory);

            if (string.Equals(id, PackagesDirectoryName, StringComparison.OrdinalIgnoreCase)) continue;
            if (disabled.Contains(id)) continue;

            TryLoadOne(id, directory, apiFactory);
        }
    }

    /// <summary>加载单个插件目录，任何异常都被转换为失败记录。</summary>
    private void TryLoadOne(string id, string pluginDirectory, PluginApiFactory apiFactory)
    {
        try
        {
            var manifestPath = Path.Combine(pluginDirectory, ManifestFileName);
            if (!File.Exists(manifestPath))
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = $"缺少 {ManifestFileName}" });
                return;
            }

            PluginManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<PluginManifest>(
                    File.ReadAllText(manifestPath), ManifestOptions);
            }
            catch (JsonException ex)
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = $"{ManifestFileName} 解析失败", Exception = ex });
                return;
            }

            if (manifest is null)
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = $"{ManifestFileName} 内容为空" });
                return;
            }

            var assemblyPath = ResolveAssemblyPath(pluginDirectory, id, manifest);
            if (assemblyPath is null)
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = "找不到入口程序集" });
                return;
            }

            var context = new PluginLoadContext(assemblyPath);
            _contexts[id] = context;

            var assembly = context.LoadFromAssemblyPath(assemblyPath);

            var pluginType = ResolvePluginType(assembly, manifest.Type);
            if (pluginType is null)
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = "程序集中没有找到 IPlugin 实现" });
                ReleaseContext(id);
                return;
            }

            if (Activator.CreateInstance(pluginType) is not IPlugin instance)
            {
                _failures.Add(new PluginLoadFailure { Id = id, Reason = $"{pluginType.FullName} 无法实例化（需要无参构造函数）" });
                ReleaseContext(id);
                return;
            }

            var dataDirectory = Path.Combine(pluginDirectory, DataDirectoryName);
            Directory.CreateDirectory(dataDirectory);

            var api = apiFactory(id, dataDirectory, manifest);

            // 注册阶段抛异常同样只记录，不影响其它插件
            instance.Register(api);

            _plugins.Add(new LoadedPlugin
            {
                Id = id,
                Directory = pluginDirectory,
                Manifest = manifest,
                Instance = instance,
                Api = api,
                AssemblyPath = assemblyPath,
            });
        }
        catch (Exception ex)
        {
            _failures.Add(new PluginLoadFailure
            {
                Id = id,
                Reason = ex.Message,
                Exception = ex,
            });
            ReleaseContext(id);
        }
    }

    /// <summary>解析入口程序集：优先 manifest.Entry，其次按目录名推断。</summary>
    private static string? ResolveAssemblyPath(string pluginDirectory, string id, PluginManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            var explicitPath = Path.Combine(pluginDirectory, manifest.Entry);
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var preferred = Path.Combine(pluginDirectory, id + ".dll");
        if (File.Exists(preferred)) return preferred;

        // 退化为目录下第一个非契约程序集
        return Directory.EnumerateFiles(pluginDirectory, "*.dll")
            .FirstOrDefault(f =>
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                return !stem.Equals("ClassDailyLand.Core", StringComparison.OrdinalIgnoreCase)
                       && !stem.Equals("ClassDailyLand.Plugin.Abstractions", StringComparison.OrdinalIgnoreCase);
            });
    }

    /// <summary>定位 IPlugin 实现类型：优先 manifest.Type，否则扫描程序集。</summary>
    private static Type? ResolvePluginType(Assembly assembly, string? explicitTypeName)
    {
        if (!string.IsNullOrWhiteSpace(explicitTypeName))
            return assembly.GetType(explicitTypeName, throwOnError: false);

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // 部分类型加载失败时，仍尝试用成功加载的那部分
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        return types.FirstOrDefault(t =>
            typeof(IPlugin).IsAssignableFrom(t)
            && t is { IsInterface: false, IsAbstract: false }
            && t.GetConstructor(Type.EmptyTypes) is not null);
    }

    // ================= 卸载 =================

    /// <summary>停止全部插件并尝试卸载其加载上下文。</summary>
    public void StopAll()
    {
        foreach (var plugin in _plugins.ToList())
        {
            try
            {
                PluginStopping?.Invoke(this, plugin);
            }
            catch (Exception ex)
            {
                _failures.Add(new PluginLoadFailure { Id = plugin.Id, Reason = "停止回调异常", Exception = ex });
            }
            ReleaseContext(plugin.Id);
        }

        _plugins.Clear();
    }

    private void ReleaseContext(string id)
    {
        if (!_contexts.Remove(id, out var context)) return;

        try
        {
            // isCollectible 上下文可主动卸载；插件若持有未释放的引用会延迟回收，不影响正确性
            context.Unload();
        }
        catch (InvalidOperationException)
        {
            // 卸载正在进行中，忽略
        }
    }
}
