namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 应用协调器。
/// 对应源模块：controller.py 的 AppController。
/// 职责：收口三窗口（主窗口 / 灵动岛 / 副岛）的可见性与设置下发，
/// 并打通「主岛几何变化 → 副岛重定位」的联动。
/// </summary>
public interface IAppController
{
    /// <summary>主窗口可见性变化。</summary>
    event EventHandler<bool>? MainVisibilityChanged;

    /// <summary>灵动岛可见性变化。</summary>
    event EventHandler<bool>? IslandVisibilityChanged;

    /// <summary>副岛可见性变化。</summary>
    event EventHandler<bool>? SubVisibilityChanged;

    void ShowMain(bool visible);
    void ShowIsland(bool visible);
    void ShowSubIsland(bool visible);

    /// <summary>应用全部设置到各组件（主题、字号、透明度、可见性等）。</summary>
    void ApplySettings();

    /// <summary>请求灵动岛重新拉取插件内容并重绘。</summary>
    void NotifyIslandDirty();

    /// <summary>
    /// 让真实灵动岛跑一轮完整情景：提前提醒 → 倒计时 → 上课 → 下课 → 恢复。
    /// 对应源项目 controller.run_scenario_test。
    /// </summary>
    /// <returns>是否成功启动（灵动岛不可用时返回 false）。</returns>
    bool RunScenarioTest(int advanceSec, int countdownSec, int endHoldSec, double speed = 10.0);

    /// <summary>同步托盘菜单勾选状态。</summary>
    void RefreshTray();
}
