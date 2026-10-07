using ClassDailyLand.App.Views;

namespace ClassDailyLand.App.Services;

/// <summary>
/// UI 协调接缝。
///
/// 为什么需要它：插件运行时（PluginRuntime）需要回调灵动岛（弹提醒、刷新），
/// 但灵动岛构造时又需要 PluginRuntime —— 形成循环依赖。
/// 用一个可变持有者打断环：PluginRuntime 通过它延迟取到窗口实例，
/// 避免为了解开依赖而引入服务定位器式写法。
/// </summary>
internal sealed class UiBridge
{
    /// <summary>灵动岛窗口（窗口创建后由 App 注入）。</summary>
    public IslandWindow? Island { get; set; }

    /// <summary>请求主控制器刷新灵动岛与副岛内容。</summary>
    public Action? RequestRefresh { get; set; }
}
