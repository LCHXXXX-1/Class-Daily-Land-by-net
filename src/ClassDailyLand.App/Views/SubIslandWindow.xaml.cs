using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.App.Views.Controls;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Plugin.Abstractions;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 副岛窗口。对应源模块：sub_island.py 的 SubIsland。
///
/// 行为（与源项目一致）：
///   · 仅在主岛处于 compact / countdown / alert 时显示，其余状态隐藏
///   · 无插件内容时收成 40px 并显示占位符「—」
///   · 收起态显示图标或文案首字，展开态显示图标 + 文案
///   · 展开长度由插件声明，夹在 44–380 之间
///   · 仅在「无内容」或插件声明 auto_collapse=true 时启动自动收起计时
///   · 新内容接管且 auto_collapse=false 时自动展开（典型场景：媒体开始播放）
///   · 左键点击 → 转发插件点击回调，然后切换收起/展开
///   · 右键菜单 → 收起/展开、隐藏副岛
/// </summary>
public partial class SubIslandWindow : Window
{
    private const int AnimMs = 180;
    private const string ActiveMainStateCompact = "compact";
    private const string ActiveMainStateCountdown = "countdown";
    private const string ActiveMainStateAlert = "alert";

    private readonly ISettingsService _settings;
    private readonly PluginRuntime _plugins;
    private readonly DispatcherTimer _autoCollapseTimer;
    private readonly DispatcherTimer _pinTimer;

    private AppSettings _current = new();

    private bool _enabled = true;
    private bool _collapsed;
    private bool _collapsedSetting;
    private string _mainState = "hidden";
    private string? _specKey;

    private double _length = SubIslandVisual.DefaultLength;
    private double _anchorX;
    private double _anchorY;

    public SubIslandWindow(ISettingsService settings, PluginRuntime plugins)
    {
        _settings = settings;
        _plugins = plugins;

        InitializeComponent();

        _current = settings.Current;
        _enabled = _current.ShowSubIsland;
        _collapsed = _current.SubIslandCollapsed;
        _collapsedSetting = _collapsed;

        _autoCollapseTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _autoCollapseTimer.Tick += (_, _) =>
        {
            _autoCollapseTimer.Stop();
            Collapse();
        };

        // 副岛也需要周期性置顶，避免被其它窗口盖住
        _pinTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _pinTimer.Tick += (_, _) => PinTopmost();

        _plugins.SubIslandUpdateRequested += (_, _) => RefreshContent();

        MouseLeftButtonUp += OnClicked;
        MouseRightButtonUp += OnContextMenu;
        Loaded += (_, _) =>
        {
            PinTopmost();
            _pinTimer.Start();
        };
    }

    // ================= 内容 =================

    /// <summary>刷新插件内容（对应 refresh_content）。</summary>
    public void RefreshContent()
    {
        _current = _settings.Current;

        var previousWidth = EffectiveWidth();

        var spec = _plugins.GetSubIslandSpec();
        var hasContent = spec is not null
                         && (!string.IsNullOrWhiteSpace(spec.Text) || spec.Draw is not null);

        var source = hasContent ? "plugin" : "empty";
        _length = hasContent ? ClampLength(spec!.Length) : SubIslandVisual.CollapsedWidth;

        // 新内容接管且要求保持展开时自动展开
        if (hasContent)
        {
            var key = $"{spec!.Text}|{spec.AutoCollapse}|{spec.Draw?.GetHashCode() ?? 0}";

            if (key != _specKey && _collapsed && !spec.AutoCollapse)
                _collapsed = false;

            _specKey = key;
        }
        else
        {
            _specKey = null;
        }

        Visual.SetContent(
            hasContent ? spec!.Text : null,
            hasContent ? spec!.Icon : null,
            hasContent ? spec!.Draw : null,
            _length,
            _collapsed);

        UpdateAutoCollapseTimer();

        if (Math.Abs(EffectiveWidth() - previousWidth) > 0.5)
            SyncGeometry(animate: true);
        else
            SyncGeometry(animate: false);

        ApplyVisibility();
    }

    private static double ClampLength(double value)
    {
        if (double.IsNaN(value) || value <= 0) return SubIslandVisual.DefaultLength;
        return Math.Clamp(value, SubIslandVisual.MinLength, SubIslandVisual.MaxLength);
    }

    private double EffectiveWidth()
        => _collapsed ? SubIslandVisual.CollapsedWidth : _length;

    // ================= 自动收起 =================

    private void UpdateAutoCollapseTimer()
    {
        var spec = _plugins.GetSubIslandSpec();

        // 无内容，或插件允许自动收起时才计时
        var wantAuto = !Visual.HasContent || spec?.AutoCollapse == true;

        if (!wantAuto || _collapsed)
        {
            _autoCollapseTimer.Stop();
            return;
        }

        if (!_autoCollapseTimer.IsEnabled) RestartAutoCollapse();
    }

    private void RestartAutoCollapse()
    {
        _autoCollapseTimer.Stop();

        var seconds = _current.SubIslandAutoCollapseSec;
        if (seconds <= 0) return;

        _autoCollapseTimer.Interval = TimeSpan.FromSeconds(seconds);
        _autoCollapseTimer.Start();
    }

    // ================= 几何 =================

    private void SyncGeometry(bool animate)
    {
        var width = EffectiveWidth();
        var targetLeft = _anchorX;
        var targetTop = _anchorY;

        var leftChanged = Math.Abs(Left - targetLeft) > 0.5;
        var widthChanged = Math.Abs(Visual.Width - width) > 0.5;

        if (!leftChanged && !widthChanged && Math.Abs(Top - targetTop) <= 0.5) return;

        if (!animate || !IsVisible)
        {
            CancelAnimations();
            Left = targetLeft;
            Top = targetTop;
            Visual.Width = width;
            ResizeToContent();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(AnimMs);

        CancelAnimations();

        BeginAnimation(LeftProperty, new DoubleAnimation(Left, targetLeft, duration) { EasingFunction = ease });
        BeginAnimation(TopProperty, new DoubleAnimation(Top, targetTop, duration) { EasingFunction = ease });

        var widthAnimation = new DoubleAnimation(Visual.Width, width, duration) { EasingFunction = ease };
        widthAnimation.Completed += (_, _) =>
        {
            Visual.BeginAnimation(WidthProperty, null);
            Visual.Width = width;
            ResizeToContent();
        };

        Visual.BeginAnimation(WidthProperty, widthAnimation);
        ResizeToContent();
    }

    private void CancelAnimations()
    {
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Visual.BeginAnimation(WidthProperty, null);
    }

    private void ResizeToContent()
    {
        Width = Visual.Width;
        Height = SubIslandVisual.CapsuleHeight;
    }

    /// <summary>跟随主岛重定位（对应 reposition）。</summary>
    public void Reposition(double islandRight, double islandTop)
    {
        var newX = islandRight + SubIslandVisual.Gap;
        var newY = islandTop;

        if (Math.Abs(newX - _anchorX) < 0.5 && Math.Abs(newY - _anchorY) < 0.5) return;

        _anchorX = newX;
        _anchorY = newY;

        SyncGeometry(animate: IsVisible);
    }

    // ================= 状态 =================

    /// <summary>主岛状态变化（对应 set_main_state）。</summary>
    public void SetMainState(string state)
    {
        _mainState = state ?? "hidden";
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        var active = _mainState is ActiveMainStateCompact or ActiveMainStateCountdown or ActiveMainStateAlert;
        var want = _enabled && active;

        if (want && !IsVisible)
        {
            SyncGeometry(animate: false);
            Show();
            PinTopmost();
            _pinTimer.Start();
        }
        else if (!want && IsVisible)
        {
            _pinTimer.Stop();
            Hide();
        }
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        ApplyVisibility();
    }

    public void ApplySettings(AppSettings settings)
    {
        _current = settings;
        _enabled = settings.ShowSubIsland;

        // 默认收起状态被改动时，立即同步
        if (settings.SubIslandCollapsed != _collapsedSetting)
        {
            _collapsedSetting = settings.SubIslandCollapsed;
            _collapsed = settings.SubIslandCollapsed;
        }

        RefreshContent();
    }

    // ================= 折叠控制 =================

    public void Collapse()
    {
        if (_collapsed) return;

        _collapsed = true;
        _autoCollapseTimer.Stop();
        RefreshContent();
    }

    public void Expand()
    {
        if (!_enabled) return;

        if (!_collapsed)
        {
            RefreshContent();
            return;
        }

        _collapsed = false;
        RefreshContent();
    }

    private void Toggle()
    {
        if (_collapsed) Expand();
        else Collapse();
    }

    // ================= 交互 =================

    private void OnClicked(object sender, MouseButtonEventArgs e)
    {
        // 先把点击转给插件，再切换折叠状态（与源项目顺序一致）
        _plugins.RaiseSubIslandClick();
        Toggle();
    }

    private void OnContextMenu(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var toggleItem = new System.Windows.Controls.MenuItem
        {
            Header = _collapsed ? "展开" : "收起",
        };
        toggleItem.Click += (_, _) => Toggle();
        menu.Items.Add(toggleItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var hideItem = new System.Windows.Controls.MenuItem { Header = "隐藏副岛" };
        hideItem.Click += (_, _) => _settings.Update(s => s.ShowSubIsland = false);
        menu.Items.Add(hideItem);

        menu.PlacementTarget = this;
        menu.IsOpen = true;
    }

    private void PinTopmost()
    {
        if (!IsVisible) return;

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            ClassDailyLand.Win32.WindowEffects.TryPinTopmost(hwnd);
        }
        catch (InvalidOperationException)
        {
            // 窗口尚未创建句柄，忽略
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _autoCollapseTimer.Stop();
        _pinTimer.Stop();
        base.OnClosed(e);
    }
}
