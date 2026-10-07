using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Plugin.Abstractions;

/// <summary>
/// 宿主暴露给插件的能力集合。
/// 逐项对应源项目 plugin_manager.py 中 PluginAPI 的公开方法，
/// 是插件与宿主之间唯一的交互面。
/// </summary>
public interface IPluginApi
{
    // ================= 生命周期 =================

    /// <summary>插件注册完成后立即调用。</summary>
    void OnLoad(Action callback);

    /// <summary>应用进入事件循环、各窗口就绪后调用。</summary>
    void OnStart(Action callback);

    /// <summary>应用退出前调用，用于释放资源。</summary>
    void OnStop(Action callback);

    // ================= 灵动岛扩展点 =================

    /// <summary>注册函数，返回字符串追加到主岛文本。</summary>
    void AddIslandText(Func<string> provider);

    /// <summary>注册主岛右侧加长片段，返回 null 表示本次不展示。</summary>
    void AddIslandExtra(Func<IslandExtraSpec?> provider);

    /// <summary>注册主岛横幅片段。</summary>
    void AddIslandBanner(Func<IslandBannerSpec?> provider);

    /// <summary>注册下拉面板条目。</summary>
    void AddPanel(Func<PanelSpec?> provider);

    // ================= 副岛 =================

    /// <summary>注册副岛内容。</summary>
    void AddSubIsland(Func<SubIslandSpec?> provider);

    /// <summary>设置副岛展开长度。</summary>
    void SetSubIslandLength(double pixels);

    /// <summary>设置副岛折叠状态。</summary>
    void SetSubIslandCollapsed(bool collapsed);

    void CollapseSubIsland();
    void ExpandSubIsland();

    /// <summary>副岛点击回调。</summary>
    void OnSubIslandClick(Action handler);

    /// <summary>副岛是否展示天气位。</summary>
    void SubIslandShowWeather(bool enabled);

    // ================= 设置页 =================

    /// <summary>
    /// 注册插件自己的设置页。
    /// 返回值为任意 WPF 内容（FrameworkElement），由宿主嵌入设置中心。
    /// </summary>
    void AddSettingsPage(string title, Func<IPluginApi, object> factory, string icon = "");

    // ================= 定时器 =================

    /// <summary>创建循环定时器（回调在 UI 线程执行）。</summary>
    void SetInterval(TimeSpan interval, Action callback, bool start = true);

    /// <summary>创建单次定时器。</summary>
    void SetTimeout(TimeSpan delay, Action callback);

    // ================= 数据 =================

    /// <summary>插件专属数据目录（plugins/&lt;name&gt;/data）。</summary>
    string GetDataDirectory();

    /// <summary>读取插件自己的 data.json。</summary>
    T? GetConfig<T>(T? fallback = null) where T : class;

    /// <summary>写入插件自己的 data.json（原子写）。</summary>
    void SetConfig<T>(T data) where T : class;

    // ================= 宿主能力 =================

    /// <summary>获取当前课表状态（与灵动岛同源）。</summary>
    ScheduleStatus GetScheduleStatus();

    /// <summary>当前是否正在上课。</summary>
    bool IsInClass();

    /// <summary>让灵动岛弹出提醒。</summary>
    void Notify(string text, bool isEnd = false, bool force = false);

    /// <summary>请求刷新灵动岛与下拉面板。</summary>
    void RequestRefresh();

    /// <summary>输出插件日志（自动带插件名前缀）。</summary>
    void Log(params object?[] args);
}

/// <summary>
/// 插件实现入口。宿主通过反射找到实现类型并实例化，随后调用 <see cref="Register"/>。
/// 对应源项目插件 main.py 中的 register(api) 函数。
/// </summary>
public interface IPlugin
{
    /// <summary>在此注册全部扩展点。宿主会捕获异常，单个插件失败不影响主程序。</summary>
    void Register(IPluginApi api);
}
