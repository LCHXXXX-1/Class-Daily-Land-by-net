using System.Text.Json.Serialization;

namespace ClassDailyLand.Core.Models;

/// <summary>
/// 应用设置。
/// 字段与源项目 settings/settings.json 完全对齐（snake_case），
/// 因此 C# 版可直接读取 Python 版遗留的配置文件，实现数据平滑迁移。
/// 对应源模块：settings_manager.py 的 DEFAULTS 字典。
/// </summary>
public sealed class AppSettings
{
    // ---------- 外观 ----------
    [JsonPropertyName("theme_mode")]
    public string ThemeMode { get; set; } = "system";          // system / light / dark

    [JsonPropertyName("settings_mica")]
    public bool SettingsMica { get; set; } = true;             // 仅 Win11 生效，Win10 自动忽略

    // ---------- 主窗口 ----------
    [JsonPropertyName("main_width_ratio")]
    public double MainWidthRatio { get; set; } = 0.25;

    [JsonPropertyName("main_font_size")]
    public int MainFontSize { get; set; } = 14;

    [JsonPropertyName("main_auto_scroll")]
    public bool MainAutoScroll { get; set; } = true;

    [JsonPropertyName("main_scroll_interval")]
    public int MainScrollInterval { get; set; } = 50;

    [JsonPropertyName("main_scroll_step")]
    public int MainScrollStep { get; set; } = 1;

    [JsonPropertyName("main_scroll_pause")]
    public int MainScrollPause { get; set; } = 60;

    [JsonPropertyName("main_opacity")]
    public double MainOpacity { get; set; } = 1.0;

    [JsonPropertyName("show_main_window")]
    public bool ShowMainWindow { get; set; } = true;

    // ---------- 灵动岛 ----------
    [JsonPropertyName("island_top_margin")]
    public int IslandTopMargin { get; set; } = 6;

    [JsonPropertyName("island_hide_on_fullscreen")]
    public bool IslandHideOnFullscreen { get; set; } = true;

    [JsonPropertyName("island_show_wakeup_anim")]
    public bool IslandShowWakeupAnim { get; set; } = true;

    [JsonPropertyName("island_alert_width")]
    public int IslandAlertWidth { get; set; } = 300;

    [JsonPropertyName("island_alert_hold_ms")]
    public int IslandAlertHoldMs { get; set; } = 600;

    [JsonPropertyName("island_countdown_sec")]
    public int IslandCountdownSec { get; set; } = 60;

    [JsonPropertyName("show_island")]
    public bool ShowIsland { get; set; } = true;

    // ---------- 副岛 ----------
    [JsonPropertyName("show_sub_island")]
    public bool ShowSubIsland { get; set; } = true;

    [JsonPropertyName("sub_island_collapsed")]
    public bool SubIslandCollapsed { get; set; }

    [JsonPropertyName("sub_island_auto_collapse_sec")]
    public int SubIslandAutoCollapseSec { get; set; } = 5;

    // ---------- 渐显动画 ----------
    [JsonPropertyName("anim_fade_window")]
    public bool AnimFadeWindow { get; set; } = true;

    [JsonPropertyName("anim_fade_panel")]
    public bool AnimFadePanel { get; set; } = true;

    [JsonPropertyName("anim_fade_text")]
    public bool AnimFadeText { get; set; } = true;

    [JsonPropertyName("anim_duration")]
    public int AnimDuration { get; set; } = 320;

    // ---------- 更新 ----------
    [JsonPropertyName("check_update_on_start")]
    public bool CheckUpdateOnStart { get; set; } = true;

    [JsonPropertyName("block_update_on_source")]
    public bool BlockUpdateOnSource { get; set; } = true;

    // ---------- 插件市场 ----------
    [JsonPropertyName("disabled_plugins")]
    public List<string> DisabledPlugins { get; set; } = new();

    [JsonPropertyName("market_source")]
    public string MarketSource { get; set; } = "official";      // official / github / gitee

    [JsonPropertyName("packages_source")]
    public string PackagesSource { get; set; } = "gitee";       // gitee / github

    [JsonPropertyName("packages_base_url")]
    public string PackagesBaseUrl { get; set; } = "";           // 空串 = 按 packages_source 取内置地址
}
