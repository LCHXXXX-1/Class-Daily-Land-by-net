using System.IO;
using System.Windows;
using System.Windows.Threading;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Plugin.Abstractions;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 单个插件的 API 实现：收集插件注册的扩展点，并代理宿主能力。
/// 对应源项目 plugin_manager.py 中的 PluginAPI 类。
/// </summary>
internal sealed class PluginApiImplementation : IPluginApi
{
    private readonly PluginRuntime _runtime;
    private readonly IJsonStore _store;
    private readonly string _dataDirectory;
    private readonly List<DispatcherTimer> _timers = new();

    public string PluginId { get; }
    public PluginManifest Manifest { get; }

    // ---- 收集到的扩展点 ----
    public List<Func<string>> IslandTextProviders { get; } = new();
    public List<Func<IslandExtraSpec?>> IslandExtraProviders { get; } = new();
    public List<Func<IslandBannerSpec?>> IslandBannerProviders { get; } = new();
    public List<Func<PanelSpec?>> PanelProviders { get; } = new();
    public List<Func<SubIslandSpec?>> SubIslandProviders { get; } = new();
    public List<Action> SubIslandClickHandlers { get; } = new();

    public List<Action> LoadCallbacks { get; } = new();
    public List<Action> StartCallbacks { get; } = new();
    public List<Action> StopCallbacks { get; } = new();

    public List<PluginSettingsPage> SettingsPages { get; } = new();

    /// <summary>副岛长度覆盖（插件通过 SetSubIslandLength 设置）。</summary>
    public double? SubIslandLengthOverride { get; set; }

    /// <summary>副岛折叠状态覆盖。</summary>
    public bool? SubIslandCollapsedOverride { get; set; }

    public PluginApiImplementation(
        PluginRuntime runtime,
        IJsonStore store,
        string pluginId,
        string dataDirectory,
        PluginManifest manifest)
    {
        _runtime = runtime;
        _store = store;
        PluginId = pluginId;
        _dataDirectory = dataDirectory;
        Manifest = manifest;
    }

    // ================= 生命周期 =================

    public void OnLoad(Action callback) => LoadCallbacks.Add(callback);
    public void OnStart(Action callback) => StartCallbacks.Add(callback);
    public void OnStop(Action callback) => StopCallbacks.Add(callback);

    // ================= 灵动岛扩展点 =================

    public void AddIslandText(Func<string> provider) => IslandTextProviders.Add(provider);

    public void AddIslandExtra(Func<IslandExtraSpec?> provider) => IslandExtraProviders.Add(provider);

    public void AddIslandBanner(Func<IslandBannerSpec?> provider) => IslandBannerProviders.Add(provider);

    public void AddPanel(Func<PanelSpec?> provider) => PanelProviders.Add(provider);

    // ================= 副岛 =================

    public void AddSubIsland(Func<SubIslandSpec?> provider) => SubIslandProviders.Add(provider);

    public void SetSubIslandLength(double pixels)
    {
        SubIslandLengthOverride = pixels;
        _runtime.RequestSubIslandUpdate();
    }

    public void SetSubIslandCollapsed(bool collapsed)
    {
        SubIslandCollapsedOverride = collapsed;
        _runtime.RequestSubIslandUpdate();
    }

    public void CollapseSubIsland() => _runtime.CollapseSubIsland();

    public void ExpandSubIsland() => _runtime.ExpandSubIsland();

    public void OnSubIslandClick(Action handler) => SubIslandClickHandlers.Add(handler);

    public void SubIslandShowWeather(bool enabled) => _runtime.SetSubIslandWeather(enabled);

    // ================= 设置页 =================

    public void AddSettingsPage(string title, Func<IPluginApi, object> factory, string icon = "")
        => SettingsPages.Add(new PluginSettingsPage(title, factory, icon, this));

    // ================= 定时器 =================

    public void SetInterval(TimeSpan interval, Action callback, bool start = true)
        => CreateTimer(interval, callback, repeating: true, start: start);

    public void SetTimeout(TimeSpan delay, Action callback)
        => CreateTimer(delay, callback, repeating: false, start: true);

    private void CreateTimer(TimeSpan interval, Action callback, bool repeating, bool start)
    {
        // 统一在 UI 线程创建，回调天然落在 UI 线程，插件无需自己处理线程切换
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher)
        {
            Interval = interval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : interval,
        };

        timer.Tick += (_, _) =>
        {
            if (!repeating) timer.Stop();

            try
            {
                callback();
            }
            catch (Exception ex)
            {
                _runtime.ReportPluginError(PluginId, "定时器回调异常", ex);
            }
        };

        _timers.Add(timer);
        if (start) timer.Start();
    }

    /// <summary>插件停止时清理定时器，避免回调打到已卸载的程序集上。</summary>
    public void DisposeTimers()
    {
        foreach (var timer in _timers)
        {
            try
            {
                timer.Stop();
            }
            catch (InvalidOperationException)
            {
                // 调度器已关闭，忽略
            }
        }
        _timers.Clear();
    }

    // ================= 数据 =================

    public string GetDataDirectory() => _dataDirectory;

    public T? GetConfig<T>(T? fallback = null) where T : class
        => _store.ReadAt<T>(ConfigPath) ?? fallback;

    public void SetConfig<T>(T data) where T : class
        => _store.WriteAt(ConfigPath, data);

    private string ConfigPath => Path.Combine(_dataDirectory, "data.json");

    // ================= 宿主能力 =================

    public ScheduleStatus GetScheduleStatus() => _runtime.GetScheduleStatus();

    public bool IsInClass()
        => GetScheduleStatus().Status == ScheduleStatusKind.Ongoing;

    public void Notify(string text, bool isEnd = false, bool force = false)
        => _runtime.Notify(text, isEnd, force);

    public void RequestRefresh() => _runtime.RequestRefresh();

    public void Log(params object?[] args)
        => _runtime.Log(PluginId, args);
}

/// <summary>插件注册的设置页描述。</summary>
internal sealed record PluginSettingsPage(
    string Title,
    Func<IPluginApi, object> Factory,
    string Icon,
    PluginApiImplementation Owner);

/// <summary>
/// 插件运行时：插件 API 的工厂与扩展点聚合器。
/// 对应源项目 plugin_manager.py 中 PluginManager 的聚合职责
/// （get_island_text / get_panel_specs / get_subisland_spec / get_island_extra）。
/// </summary>
public sealed class PluginRuntime
{
    private readonly IJsonStore _store;
    private readonly Action<string, bool, bool> _notify;
    private readonly Action _refresh;
    private readonly Func<ScheduleStatus> _scheduleStatus;

    private readonly List<PluginApiImplementation> _apis = new();
    private readonly List<string> _log = new();

    public PluginRuntime(
        IJsonStore store,
        Func<ScheduleStatus> scheduleStatus,
        Action<string, bool, bool> notify,
        Action refresh)
    {
        _store = store;
        _scheduleStatus = scheduleStatus;
        _notify = notify;
        _refresh = refresh;
    }

    /// <summary>所有插件的 API 实例（供设置中心列出插件设置页）。</summary>
    internal IReadOnlyList<PluginApiImplementation> Apis => _apis;

    /// <summary>插件日志缓冲（诊断用）。</summary>
    public IReadOnlyList<string> Logs => _log;

    /// <summary>副岛内容或折叠状态变化时的回调。</summary>
    public event EventHandler? SubIslandUpdateRequested;

    private bool _subIslandWeatherEnabled;

    /// <summary>副岛是否展示天气位。</summary>
    public bool SubIslandWeatherEnabled => _subIslandWeatherEnabled;

    // ================= 生命周期 =================

    /// <summary>为单个插件创建独立的 API 实例。</summary>
    public IPluginApi CreateApi(string pluginId, string dataDirectory, PluginManifest manifest)
    {
        var api = new PluginApiImplementation(this, _store, pluginId, dataDirectory, manifest);
        _apis.Add(api);
        return api;
    }

    /// <summary>全部插件注册完毕后调用 OnLoad 回调。</summary>
    public void NotifyLoaded()
    {
        foreach (var api in _apis)
            foreach (var callback in api.LoadCallbacks)
                Invoke(api, callback, "OnLoad");
    }

    /// <summary>窗口就绪、进入事件循环后调用 OnStart 回调。</summary>
    public void NotifyStarted()
    {
        foreach (var api in _apis)
            foreach (var callback in api.StartCallbacks)
                Invoke(api, callback, "OnStart");
    }

    /// <summary>单个插件停止：清理定时器并触发 OnStop。</summary>
    public void NotifyStopping(string pluginId)
    {
        var api = _apis.FirstOrDefault(a =>
            string.Equals(a.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        if (api is null) return;

        foreach (var callback in api.StopCallbacks)
            Invoke(api, callback, "OnStop");

        api.DisposeTimers();
        _apis.Remove(api);
    }

    private static void Invoke(PluginApiImplementation api, Action callback, string tag)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            // 单个插件的回调异常绝不能影响宿主或其它插件
            System.Diagnostics.Debug.WriteLine($"[plugin:{api.PluginId}] {tag} 异常: {ex}");
        }
    }

    // ================= 扩展点聚合 =================

    /// <summary>汇总所有插件追加到主岛文本的片段。</summary>
    public string GetIslandText()
    {
        var parts = new List<string>();

        foreach (var api in _apis)
        {
            foreach (var provider in api.IslandTextProviders)
            {
                try
                {
                    var text = provider();
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
                }
                catch (Exception ex)
                {
                    ReportPluginError(api.PluginId, "岛屿文本提供者异常", ex);
                }
            }
        }

        return string.Join("  ", parts);
    }

    /// <summary>汇总主岛加长片段（按优先级排序）。</summary>
    public IReadOnlyList<IslandExtraSpec> GetIslandExtras()
    {
        var specs = new List<IslandExtraSpec>();

        foreach (var api in _apis)
            foreach (var provider in api.IslandExtraProviders)
                SafeCollect(api, provider, specs, "岛屿加长片段异常");

        return specs.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>汇总主岛横幅片段（按优先级排序）。</summary>
    public IReadOnlyList<IslandBannerSpec> GetIslandBanners()
    {
        var specs = new List<IslandBannerSpec>();

        foreach (var api in _apis)
            foreach (var provider in api.IslandBannerProviders)
                SafeCollect(api, provider, specs, "岛屿横幅异常");

        return specs.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>汇总下拉面板条目。</summary>
    public IReadOnlyList<PanelSpec> GetPanelSpecs()
    {
        var specs = new List<PanelSpec>();

        foreach (var api in _apis)
            foreach (var provider in api.PanelProviders)
                SafeCollect(api, provider, specs, "下拉面板异常");

        return specs.OrderBy(s => s.Order).ToList();
    }

    /// <summary>取优先级最高的副岛内容。</summary>
    public SubIslandSpec? GetSubIslandSpec()
    {
        var specs = new List<SubIslandSpec>();

        foreach (var api in _apis)
            foreach (var provider in api.SubIslandProviders)
                SafeCollect(api, provider, specs, "副岛内容异常");

        return specs.OrderByDescending(s => s.Priority).FirstOrDefault();
    }

    /// <summary>触发副岛点击回调（全部插件都会收到）。</summary>
    public void RaiseSubIslandClick()
    {
        foreach (var api in _apis)
            foreach (var handler in api.SubIslandClickHandlers)
                Invoke(api, handler, "副岛点击");
    }

    private static void SafeCollect<T>(
        PluginApiImplementation api,
        Delegate provider,
        List<T> sink,
        string tag) where T : class
    {
        try
        {
            var result = provider.DynamicInvoke();
            if (result is T value) sink.Add(value);
        }
        catch (Exception ex)
        {
            // DynamicInvoke 会把真实异常包在 TargetInvocationException 里
            var inner = ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;
            System.Diagnostics.Debug.WriteLine($"[plugin:{api.PluginId}] {tag}: {inner.Message}");
        }
    }

    // ================= 宿主能力转发 =================

    public ScheduleStatus GetScheduleStatus() => _scheduleStatus();

    public void Notify(string text, bool isEnd, bool force) => _notify(text, isEnd, force);

    public void RequestRefresh() => _refresh();

    public void Log(string pluginId, object?[] args)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{pluginId}] {string.Join(" ", args.Select(a => a?.ToString() ?? "null"))}";
        _log.Add(line);

        // 日志缓冲上限，避免长时间运行内存增长
        if (_log.Count > 500) _log.RemoveAt(0);

        System.Diagnostics.Debug.WriteLine(line);
    }

    public void ReportPluginError(string pluginId, string message, Exception ex)
        => Log(pluginId, new object?[] { $"{message}: {ex.Message}" });

    public void RequestSubIslandUpdate() => SubIslandUpdateRequested?.Invoke(this, EventArgs.Empty);

    public void CollapseSubIsland() => _collapseRequested?.Invoke(true);

    public void ExpandSubIsland() => _collapseRequested?.Invoke(false);

    public void SetSubIslandWeather(bool enabled) => _subIslandWeatherEnabled = enabled;

    private Action<bool>? _collapseRequested;

    /// <summary>由宿主注入副岛折叠控制（对应源项目 set_subisland_control）。</summary>
    public void SetSubIslandControl(Action<bool> collapse)
        => _collapseRequested = collapse;
}
