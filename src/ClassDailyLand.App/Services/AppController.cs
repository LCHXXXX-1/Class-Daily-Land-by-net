using ClassDailyLand.App.Views;
using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 应用协调器。对应源模块：controller.py 的 AppController。
///
/// 职责：
/// 1. 收口三窗口（主窗口 / 灵动岛 / 副岛）的可见性与设置下发；
/// 2. 打通「主岛几何变化 → 副岛重定位」的联动；
/// 3. 作为设置变更的唯一分发点，避免各窗口各自订阅造成更新时序混乱。
/// </summary>
internal sealed class AppController : IAppController
{
    private readonly ISettingsService _settings;

    private MainWindow? _main;
    private IslandWindow? _island;
    private SubIslandWindow? _subIsland;
    private TrayIconHost? _tray;

    public event EventHandler<bool>? MainVisibilityChanged;
    public event EventHandler<bool>? IslandVisibilityChanged;
    public event EventHandler<bool>? SubVisibilityChanged;

    public AppController(ISettingsService settings) => _settings = settings;

    /// <summary>绑定三个窗口（对应源项目 controller.bind）。</summary>
    public void Attach(MainWindow main, IslandWindow island, SubIslandWindow subIsland)
    {
        _main = main;
        _island = island;
        _subIsland = subIsland;

        // 主岛几何变化 → 副岛跟随重定位
        _island.GeometryChanged += (_, geometry) => OnIslandGeometry(geometry);
    }

    public void AttachTray(TrayIconHost tray) => _tray = tray;

    // ================= 可见性 =================

    public void ShowMain(bool visible)
    {
        _main?.SetVisible(visible);
        MainVisibilityChanged?.Invoke(this, visible);
    }

    public void ShowIsland(bool visible)
    {
        if (_island is not null) _island.SetEnabled(visible);
        IslandVisibilityChanged?.Invoke(this, visible);
    }

    public void ShowSubIsland(bool visible)
    {
        if (_subIsland is not null) _subIsland.SetEnabled(visible);
        SubVisibilityChanged?.Invoke(this, visible);
    }

    /// <summary>主窗口透明度与置顶状态等主窗口专属设置。</summary>
    public void SetMainOpacity(double opacity) => _main?.ApplyOpacity(opacity);

    // ================= 联动 =================

    /// <summary>
    /// 主岛几何或状态变化时同步给副岛（对应 on_island_geometry）。
    /// 副岛既需要位置（贴主岛右侧），也需要主岛状态（决定是否显示）。
    /// </summary>
    private void OnIslandGeometry(IslandGeometry geometry)
    {
        if (_subIsland is null) return;

        _subIsland.Reposition(geometry.Right, geometry.Top);
        _subIsland.SetMainState(geometry.MainState);
    }

    /// <summary>请求灵动岛重新拉取插件内容并重绘。</summary>
    public void NotifyIslandDirty()
    {
        _island?.RefreshFromPlugins();
        _subIsland?.RefreshContent();
    }

    /// <summary>让真实灵动岛跑一轮完整情景（对应 run_scenario_test）。</summary>
    public bool RunScenarioTest(int advanceSec, int countdownSec, int endHoldSec, double speed = 10.0)
        => _island?.StartScenarioTest(advanceSec, countdownSec, endHoldSec, speed) ?? false;

    public void RefreshTray() => _tray?.SyncState();

    // ================= 设置下发 =================

    /// <summary>
    /// 把当前设置一次性下发到所有组件（对应 controller.apply_settings）。
    /// 顺序刻意固定：先让窗口自身应用外观，再处理可见性，最后刷新内容。
    /// </summary>
    public void ApplySettings()
    {
        var settings = _settings.Current;

        _main?.ApplyMainSettings(settings);
        _island?.ApplySettings(settings);
        _subIsland?.ApplySettings(settings);

        ShowMain(settings.ShowMainWindow);
        ShowIsland(settings.ShowIsland);
        ShowSubIsland(settings.ShowSubIsland);

        SetMainOpacity(settings.MainOpacity);

        NotifyIslandDirty();
        RefreshTray();
    }

    /// <summary>退出前清理：停止插件、释放托盘图标。</summary>
    public void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
