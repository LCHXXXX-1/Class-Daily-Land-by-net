using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using ClassDailyLand.App.Views;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

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

    /// <summary>上一次下发时岛相关字段的签名，见 <see cref="ApplySettings"/>。</summary>
    private string? _appliedIslandSignature;

    /// <summary>上一次下发时三个显示开关的签名。</summary>
    private string? _appliedVisibilitySignature;

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
    ///
    /// ★ 这里刻意做了「按变化量分级」：<c>_settings.Update</c> 之后 Changed 一定会触发，
    ///   但事件本身不告诉你哪个字段变了。而下面这几件事都是重活 ——
    ///   重建作业列表、重绘灵动岛、重建托盘菜单。拖动滑块时每 150ms 就走一遍全量下发，
    ///   那些没变的项也跟着重做，表现出来就是「整个窗口一闪一闪」。
    ///   所以每件重活都由一组字段签名把关：签名没变就不做。
    /// </summary>
    public void ApplySettings()
    {
        var settings = _settings.Current;

        // 岛的外观 / 位置相关字段（island_* 与 sub_island_*；show_* 那三个不算）。
        var islandSignature = SignatureOf(settings,
            name => name.StartsWith("island_", StringComparison.Ordinal)
                 || name.StartsWith("sub_island_", StringComparison.Ordinal));

        var visibilitySignature = $"{settings.ShowMainWindow}|{settings.ShowIsland}|{settings.ShowSubIsland}";

        // 首次下发（启动）时两个签名都是 null，自然全量做一遍。
        var islandChanged = _appliedIslandSignature != islandSignature;
        var visibilityChanged = _appliedVisibilitySignature != visibilitySignature;

        _appliedIslandSignature = islandSignature;
        _appliedVisibilitySignature = visibilitySignature;

        _main?.ApplyMainSettings(settings);

        if (islandChanged || visibilityChanged)
        {
            // 这两个内部会走 AnimateTop + Tick（重绘）。岛相关的东西没变时，
            // 每 150ms 重绘一次灵动岛是纯浪费，而且看得见。
            _island?.ApplySettings(settings);
            _subIsland?.ApplySettings(settings);
        }

        if (visibilityChanged)
        {
            ShowMain(settings.ShowMainWindow);
            ShowIsland(settings.ShowIsland);
            ShowSubIsland(settings.ShowSubIsland);
        }

        SetMainOpacity(settings.MainOpacity);

        // 重新拉插件内容 / 副岛内容：只在岛相关设置真的变了时做
        if (islandChanged) NotifyIslandDirty();

        // 托盘菜单只有那三个勾选状态会随设置变
        if (visibilityChanged) RefreshTray();
    }

    /// <summary>
    /// 把一组设置字段拼成字符串签名，用来判断「这次的变化值不值得做那件重活」。
    ///
    /// 字段按配置键（snake_case）前缀挑，以后加了字段会自动纳入，不用回来改这里。
    /// </summary>
    private static string SignatureOf(AppSettings settings, Func<string, bool> pick)
    {
        var text = new StringBuilder();

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            if (!pick(name)) continue;

            text.Append(name).Append('=').Append(property.GetValue(settings)).Append(';');
        }

        return text.ToString();
    }

    /// <summary>退出前清理：停止插件、释放托盘图标。</summary>
    public void Shutdown()
    {
        _tray?.Dispose();
        _tray = null;
    }
}
