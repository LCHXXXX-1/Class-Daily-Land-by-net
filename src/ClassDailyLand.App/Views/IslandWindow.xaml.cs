using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.App.Views.Controls;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Win32;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 灵动岛窗口。对应源模块：dynamic_island.py 的 DynamicIsland。
///
/// 状态机（与源项目一致）：
///   Hidden     完全收起（屏幕上方之外）
///   Mini       48×40，只显示状态点；唤醒与收起的过渡态
///   Compact    240×40，左侧进度圆环 + 课程文案
///   Countdown  展开到 600×40，整条填充蓝色进度（上课前倒计时窗口内）
///   Alert      居中粗体文字，独占显示
///
/// 状态切换规则（源项目实际行为）：
///   上课中 → 下节/结束   → 只收起面板，**不提醒**
///   下节 → 上课中        → 收起面板 + 弹「上课了！」
///   进入倒计时窗口       → 展开到 600px 并进入蓝色填充态（Office 前台时不进入）
///   放假前最后一个上学日的最后一节课下课后 → 弹一次「放假啦」
///
/// 隐藏规则：全屏应用前台 → 休眠；WPS/Office 前台 → 仅退出倒计时。
/// </summary>
public partial class IslandWindow : Window
{
    // ---------- 尺寸（对应源项目 COMPACT_W / COMPACT_H / RING / COUNTDOWN_W / MINI_W）----------
    private const double CompactWidth = IslandVisual.CompactWidth;   // 240
    private const double CompactHeight = IslandVisual.CompactHeight; // 40
    private const double MiniWidth = IslandVisual.MiniWidth;         // 48
    private const double CountdownWidth = 600;

    // ---------- 节奏常量 ----------
    private const int AnimMs = 200;
    private const int StepGapMs = 60;
    private const int SlideOutHoldMs = 600;
    private const int TextRollMs = 140;          // 文字切换滚动时长
    private const int FillTickMs = 33;           // 倒计时填充刷新（约 30fps）
    private const int FillRampMs = 180;          // 首次填充的缓动时长
    private const int MarqueeTickMs = 33;
    private const double MarqueeSpeed = 32.0;
    private const double MarqueePauseSec = 0.9;
    private const double MarqueeGap = 40.0;

    private readonly IScheduleService _schedule;
    private readonly ISettingsService _settings;
    private readonly PluginRuntime _plugins;

    private readonly DispatcherTimer _tickTimer;       // 1s：状态机主循环
    private readonly DispatcherTimer _fillTimer;       // 33ms：倒计时填充
    private readonly DispatcherTimer _rollTimer;       // 16ms：文字切换滚动
    private readonly DispatcherTimer _marqueeTimer;    // 33ms：溢出跑马灯

    private readonly System.Diagnostics.Stopwatch _marqueeClock = new();
    private readonly System.Diagnostics.Stopwatch _rollClock = new();
    private readonly System.Diagnostics.Stopwatch _fillClock = new();

    private AppSettings _current = new();
    private IslandState _state = IslandState.Hidden;
    private bool _enabled = true;

    // ---------- 提醒 ----------
    private string? _alertText;
    private bool _alertIsEnd;
    private DateTime _alertUntil = DateTime.MinValue;

    // ---------- 状态切换检测 ----------
    private ScheduleStatus? _lastStatus;
    private string _lastComposedText = "";
    private string _holidayAlertedDate = "";

    // ---------- 倒计时 ----------
    private DateTime _countdownEnd;
    private double _countdownWindow;
    private string _countdownDismissedKey = "";

    /// <summary>主岛几何变化，供副岛跟随定位。</summary>
    public event EventHandler<IslandGeometry>? GeometryChanged;

    private enum IslandState
    {
        Hidden,
        Mini,
        Compact,
        Countdown,
    }

    public IslandWindow(IScheduleService schedule, ISettingsService settings, PluginRuntime plugins)
    {
        _schedule = schedule;
        _settings = settings;
        _plugins = plugins;

        InitializeComponent();

        _current = settings.Current;

        _tickTimer = NewTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, Tick);
        _fillTimer = NewTimer(TimeSpan.FromMilliseconds(FillTickMs), DispatcherPriority.Render, FillTick);
        _rollTimer = NewTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, RollTick);
        _marqueeTimer = NewTimer(TimeSpan.FromMilliseconds(MarqueeTickMs), DispatcherPriority.Render, MarqueeTick);

        _schedule.Changed += (_, _) => Tick();

        // 点击胶囊 → 展开/收起下拉面板（对应源项目的点击轮询）
        MouseLeftButtonUp += OnIslandClicked;

        SizeChanged += (_, _) => PublishGeometry();
        LocationChanged += (_, _) => PublishGeometry();
    }

    private DispatcherTimer NewTimer(TimeSpan interval, DispatcherPriority priority, Action onTick)
    {
        var timer = new DispatcherTimer(priority, Dispatcher) { Interval = interval };
        timer.Tick += (_, _) => onTick();
        return timer;
    }

    // ================= 生命周期 =================

    /// <summary>启动计时并执行唤醒动画（对应源项目 _on_start → _wake_up）。</summary>
    public void StartTicking()
    {
        _tickTimer.Start();
        WakeUp();
    }

    public void StopTicking()
    {
        _tickTimer.Stop();
        _fillTimer.Stop();
        _rollTimer.Stop();
        _marqueeTimer.Stop();

        _scenarioId++;
        _testing = false;
        StopScenarioTimer();

        PushUpPanels(immediate: true);
    }

    // ================= 主循环 =================

    private void Tick()
    {
        if (!_enabled)
        {
            Sleep();
            return;
        }

        // 情景演示期间由 _scenarioTimer 独占驱动，常规状态机让位
        if (_testing) return;

        _current = _settings.Current;
        CheckForeground();

        if (_state == IslandState.Hidden) return;

        RefreshImpl();
        UpdateMarquee();
    }

    /// <summary>
    /// 前台检测（对应 _check_foreground）。
    /// 全屏与 Office 前台处理不同：全屏休眠；Office 只退出倒计时。
    /// </summary>
    private void CheckForeground()
    {
        if (ForegroundMonitor.GetForegroundWindowHandle() == IntPtr.Zero) return;

        if (ForegroundMonitor.IsOfficeForeground() && _state == IslandState.Countdown)
        {
            LeaveCountdown();
            return;
        }

        if (!ForegroundMonitor.IsFullscreen()) return;
        if (!_current.IslandHideOnFullscreen) return;

        Sleep();
    }

    /// <summary>每秒刷新入口（对应 _refresh_impl）。</summary>
    private void RefreshImpl()
    {
        if (_state == IslandState.Hidden) return;
        if (_testing) return;

        var status = _schedule.GetStatus();
        var previous = _lastStatus;
        _lastStatus = status;

        // ---- 放假提醒：优先于其它显示 ----
        if (MaybeHolidayReminder()) return;

        // 提醒展示期间不再刷新常规内容
        if (_alertText is not null)
        {
            if (DateTime.Now < _alertUntil)
            {
                RenderAlert();
                return;
            }

            // 到期：窗口侧与视觉层的提醒文字都要清，
            // 否则绘制分支（_alertText is not null）会永远命中，卡死在提醒画面
            _alertText = null;
            Visual.ClearAlert();
        }

        // ---- 状态切换处理 ----
        if (previous is not null)
        {
            var from = previous.Status;
            var to = status.Status;

            if (from == ScheduleStatusKind.Ongoing &&
                to is ScheduleStatusKind.Upcoming or ScheduleStatusKind.Done)
            {
                // 下课不提醒，只收起面板
                PushUpPanels();
            }
            else if (from == ScheduleStatusKind.Upcoming && to == ScheduleStatusKind.Ongoing)
            {
                PushUpPanels();
                ShowAlert("上课了！", isEnd: false);
                return;
            }
        }

        // ---- 倒计时进入 / 退出 ----
        if (ShouldEnterCountdown(status))
        {
            if (_state != IslandState.Countdown) EnterCountdown(status);
        }
        else if (_state == IslandState.Countdown)
        {
            LeaveCountdown();
        }

        RenderCompact(status);
    }

    // ================= 倒计时 =================

    /// <summary>是否应进入倒计时（对应 _is_countdown）。</summary>
    private bool ShouldEnterCountdown(ScheduleStatus status)
    {
        if (status.Status != ScheduleStatusKind.Upcoming) return false;

        if (ForegroundMonitor.IsOfficeForeground()) return false;   // Office 前台不进入
        if (CountdownKey(status) == _countdownDismissedKey) return false;

        var window = _current.IslandCountdownSec;
        if (window <= 0) return false;

        return (status.UntilSec ?? int.MaxValue) <= window;
    }

    private static string CountdownKey(ScheduleStatus status)
        => $"{status.Index}|{status.Course}|{status.Start}";

    private void EnterCountdown(ScheduleStatus status)
    {
        PushUpPanels();

        _state = IslandState.Countdown;
        _countdownWindow = Math.Max(1, _current.IslandCountdownSec);
        _countdownEnd = DateTime.Now.AddSeconds(status.UntilSec ?? _countdownWindow);

        _fillClock.Restart();
        _fillTimer.Start();

        RenderCompact(status);
        AnimateVisualWidth(CountdownWidth);
    }

    private void LeaveCountdown()
    {
        _state = IslandState.Compact;
        _fillTimer.Stop();

        var target = CompactWidth + Visual.ExtraWidth;
        AnimateVisualWidth(target);
    }

    /// <summary>倒计时填充刷新（对应 _fill_tick）。</summary>
    private void FillTick()
    {
        if (_state != IslandState.Countdown)
        {
            _fillTimer.Stop();
            return;
        }

        var remaining = Math.Max(0, (_countdownEnd - DateTime.Now).TotalSeconds);
        var ratio = Math.Min(1.0, remaining / _countdownWindow);

        // 首次填充加 OutCubic 缓动；真实倒计时比例仍保持线性
        var ramp = Math.Min(1.0, _fillClock.Elapsed.TotalMilliseconds / FillRampMs);
        var eased = 1 - Math.Pow(1 - ramp, 3);

        Visual.SetFillRatio(Math.Min(eased, ratio));

        if (remaining <= 0) _fillTimer.Stop();
    }

    // ================= 放假提醒 =================

    /// <summary>
    /// 放假前最后一个上学日的最后一节课下课后弹一次「放假啦」（对应 _maybe_holiday_reminder）。
    /// </summary>
    private bool MaybeHolidayReminder()
    {
        try
        {
            var now = DateTime.Now;
            var today = DateOnly.FromDateTime(now);

            if (!_schedule.IsLastSchoolDayBeforeHoliday(today)) return false;

            var key = today.ToString("yyyy-MM-dd");
            if (_holidayAlertedDate == key) return false;

            var lastEnd = _schedule.LastCourseEndToday(now);
            if (lastEnd is null || now < lastEnd) return false;

            _holidayAlertedDate = key;
            ShowAlert("放假啦，撒花 🎉", isEnd: true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[island] 放假提醒异常: {ex.Message}");
            return false;
        }
    }

    // ================= 渲染 =================

    private void RenderAlert()
    {
        Visual.SetAlertState(_alertText ?? "", _alertIsEnd);

        // 对应 _animate_to(_alert_width)：提醒条是独立宽度（默认 300），
        // 从倒计时的 600 / 紧凑的 240 平滑过渡，而不是沿用之前的宽度
        var width = Math.Max(120, _current.IslandAlertWidth);
        AnimateVisualWidth(width);
        PublishGeometry();
    }

    private void RenderCompact(ScheduleStatus status)
    {
        var accent = ResolveRingAccent(status, out var ringRatio);
        var text = ComposeText(status);

        var fillRatio = _state == IslandState.Countdown
            ? -1   // 倒计时由高频时钟单独驱动填充，避免每秒跳变
            : -1;

        var extras = _plugins.GetIslandExtras();
        var extra = extras.FirstOrDefault();
        var extraWidth = extra?.Width ?? 0;

        if (extra is not null) Visual.SetExtraSegment(extraWidth, extra.Draw);
        else Visual.SetExtraSegment(0, null);

        Visual.SetCompactState(text, IslandVisual.Accent(accent), ringRatio, fillRatio, extraWidth);
        Visual.SetCapsuleWidth(_state == IslandState.Countdown
            ? CountdownWidth
            : CompactWidth + extraWidth);

        // ---- 文字变化 → 滚动切换动画 ----
        if (text != _lastComposedText)
        {
            if (!string.IsNullOrEmpty(_lastComposedText))
            {
                Visual.StartTextRoll(_lastComposedText);
                _rollClock.Restart();
                if (!_rollTimer.IsEnabled) _rollTimer.Start();
            }

            _lastComposedText = text;
            _marqueeClock.Restart();
            Visual.SetMarqueeOffset(0);
        }

        ResizeToContent();
        PublishGeometry();
    }

    /// <summary>文字切换滚动推进（对应 _roll_tick）。</summary>
    private void RollTick()
    {
        var progress = _rollClock.Elapsed.TotalMilliseconds / TextRollMs;

        if (progress >= 1)
        {
            _rollTimer.Stop();
            Visual.SetRollProgress(1);
            return;
        }

        Visual.SetRollProgress(progress);
    }

    /// <summary>圆环颜色与比例（对应 _ring_spec）。</summary>
    private static IslandAccent ResolveRingAccent(ScheduleStatus status, out double ratio)
    {
        switch (status.Status)
        {
            case ScheduleStatusKind.Upcoming:
            {
                var total = Math.Max(1, Math.Max(status.AdvanceSec ?? 0, status.UntilSec ?? 0));
                ratio = (double)(status.UntilSec ?? 0) / total;
                return IslandAccent.Blue;
            }

            case ScheduleStatusKind.Ongoing:
            {
                var total = Math.Max(1, status.DurationSec ?? 0);
                ratio = (double)(status.RemainSec ?? 0) / total;
                return IslandAccent.Green;
            }

            default:
                ratio = 0;
                return IslandAccent.Gray;
        }
    }

    /// <summary>文案拼装（对应 _text_for，逐字对齐）。</summary>
    private string ComposeText(ScheduleStatus status)
    {
        string baseText;

        if (_state == IslandState.Countdown)
        {
            baseText = $"下节 {status.Course} · {status.UntilSec ?? 0}s";
        }
        else
        {
            baseText = status.Status switch
            {
                ScheduleStatusKind.Upcoming => $"下节 {status.Course} · {status.UntilMin}分钟后",
                ScheduleStatusKind.Ongoing => $"本节：{status.Course} 还剩 {status.RemainMin}分钟",
                ScheduleStatusKind.Holiday => string.IsNullOrWhiteSpace(status.Name)
                    ? "假期中"
                    : $"假期中 · {status.Name}",
                ScheduleStatusKind.None => "今日无课",
                _ => "今日课程已结束",
            };
        }

        var pluginText = _plugins.GetIslandText();
        return string.IsNullOrWhiteSpace(pluginText) ? baseText : $"{baseText} · {pluginText}";
    }

    // ================= 跑马灯 =================

    private void UpdateMarquee()
    {
        if (Visual.IsTextOverflowing && Visual.TextAreaWidth > 4)
        {
            if (!_marqueeTimer.IsEnabled)
            {
                _marqueeClock.Restart();
                _marqueeTimer.Start();
            }
        }
        else if (_marqueeTimer.IsEnabled)
        {
            _marqueeTimer.Stop();
            Visual.SetMarqueeOffset(0);
        }
    }

    private void MarqueeTick()
    {
        if (!Visual.IsTextOverflowing)
        {
            _marqueeTimer.Stop();
            return;
        }

        var span = Visual.MeasureTextWidth() + MarqueeGap;
        var cycle = span / MarqueeSpeed + MarqueePauseSec;
        var phase = _marqueeClock.Elapsed.TotalSeconds % cycle;

        var offset = phase < MarqueePauseSec ? 0 : (phase - MarqueePauseSec) * MarqueeSpeed;
        Visual.SetMarqueeOffset(offset);
    }

    // ================= 唤醒 / 休眠 =================

    private void WakeUp()
    {
        if (!_enabled || _state != IslandState.Hidden) return;

        var topMargin = Math.Max(0, _current.IslandTopMargin);
        var offScreenTop = -CompactHeight - 24;

        Visual.SetMiniState(BuildMiniDot());
        Visual.SetCapsuleWidth(MiniWidth);
        Left = Math.Max(0, (ScreenWidth - MiniWidth) / 2);
        Top = offScreenTop;
        _state = IslandState.Mini;
        Show();

        AnimateTop(offScreenTop, topMargin, () =>
        {
            if (_state != IslandState.Mini) return;

            if (!_current.IslandShowWakeupAnim)
            {
                FinishExpand();
                return;
            }

            var gap = NewTimer(TimeSpan.FromMilliseconds(StepGapMs), DispatcherPriority.Normal, () => { });
            gap.Tick += (_, _) =>
            {
                gap.Stop();
                if (_state == IslandState.Mini) FinishExpand();
            };
            gap.Start();
        });
    }

    private void FinishExpand()
    {
        // 唤醒动画与情景演示可能重叠：演示优先，避免把测试画面顶回常规内容
        if (_testing) return;

        _state = IslandState.Compact;

        var extras = _plugins.GetIslandExtras();
        var extra = extras.FirstOrDefault();
        if (extra is not null) Visual.SetExtraSegment(extra.Width, extra.Draw);

        var target = CompactWidth + (extra?.Width ?? 0);

        RefreshImpl();

        AnimateVisualWidth(target);
    }

    private void Sleep()
    {
        if (_state == IslandState.Hidden) return;

        PushUpPanels(immediate: true);

        _fillTimer.Stop();
        _marqueeTimer.Stop();
        Visual.SetMarqueeOffset(0);

        _state = IslandState.Mini;
        Visual.SetMiniState(BuildMiniDot());
        Visual.SetCapsuleWidth(MiniWidth);

        AnimateVisualWidth(MiniWidth);

        var hold = NewTimer(TimeSpan.FromMilliseconds(SlideOutHoldMs), DispatcherPriority.Normal, () => { });
        hold.Tick += (_, _) =>
        {
            hold.Stop();
            if (_state != IslandState.Mini) return;

            _state = IslandState.Hidden;
            AnimateTop(Top, -CompactHeight - 24, () =>
            {
                if (_state == IslandState.Hidden) Hide();
            });
        };
        hold.Start();
    }

    private Brush BuildMiniDot()
    {
        var status = _schedule.GetStatus();
        var accent = status.Status switch
        {
            ScheduleStatusKind.Ongoing => IslandAccent.Green,
            ScheduleStatusKind.Upcoming or ScheduleStatusKind.Holiday => IslandAccent.Blue,
            _ => IslandAccent.Gray,
        };
        return IslandVisual.Accent(accent);
    }

    // ================= 点击与提醒 =================

    private void OnIslandClicked(object sender, MouseButtonEventArgs e)
    {
        if (!_enabled) return;

        if (_state == IslandState.Countdown)
        {
            // 倒计时期间点击 → 收起这节课的倒计时（对应 _dismiss_countdown）
            var status = _schedule.GetStatus();
            _countdownDismissedKey = CountdownKey(status);
            LeaveCountdown();
            return;
        }

        TogglePanels();
    }

    private void ShowAlert(string text, bool isEnd)
    {
        // 对应 _show_alert：停掉倒计时填充时钟，提醒态独占显示
        _fillTimer.Stop();
        _alertText = text;
        _alertIsEnd = isEnd;
        _alertUntil = DateTime.Now.AddMilliseconds(Math.Max(400, _current.IslandAlertHoldMs));

        RenderAlert();

        var revert = NewTimer(
            TimeSpan.FromMilliseconds(Math.Max(400, _current.IslandAlertHoldMs)),
            DispatcherPriority.Normal,
            () => { });

        revert.Tick += (_, _) =>
        {
            revert.Stop();
            if (DateTime.Now >= _alertUntil) DismissAlert();
        };
        revert.Start();
    }

    /// <summary>
    /// 提醒到期，回到常规状态（对应 _alert_return）。
    /// 必须同时清掉窗口侧与视觉层的提醒文字：视觉层不清会永远停在提醒画面。
    /// </summary>
    private void DismissAlert()
    {
        if (_alertText is null) return;

        _alertText = null;
        Visual.ClearAlert();
        Tick();
    }

    // ================= 定位与动画 =================

    private static double ScreenWidth => SystemParameters.PrimaryScreenWidth;

    private void ResizeToContent()
    {
        if (Visual.IsMini)
        {
            Width = MiniWidth;
            Height = CompactHeight;
            return;
        }

        Width = Visual.Width;
        Height = Visual.Height;
    }

    private void AnimateTop(double from, double to, Action? completed)
    {
        BeginAnimation(TopProperty, null);
        Top = from;

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(AnimMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        if (completed is not null)
        {
            animation.Completed += (_, _) =>
            {
                BeginAnimation(TopProperty, null);
                Top = to;
                completed();
            };
        }

        BeginAnimation(TopProperty, animation);
    }

    private void AnimateVisualWidth(double to)
    {
        var duration = Math.Max(100, Math.Min(500, _current.AnimDuration));

        Visual.BeginAnimation(WidthProperty, null);

        var animation = new DoubleAnimation(Visual.Width, to, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        animation.Completed += (_, _) =>
        {
            Visual.BeginAnimation(WidthProperty, null);
            Visual.Width = to;
        };

        Visual.BeginAnimation(WidthProperty, animation);
        ResizeToContent();
        AnimateWindowLeft(to, TimeSpan.FromMilliseconds(duration));
    }

    /// <summary>
    /// 窗口 Left 与目标宽度做同步动画：岛以屏幕中线对称展开 / 收起。
    /// 对应源项目 _animate_to 的做法 —— target = QRect(_center_x(w), ...)，
    /// 位置与尺寸一起插值。若只把 Left 一步跳到终值，岛就会从固定的左缘
    /// 向右生长，展开过程中看起来挤到右边去。
    /// </summary>
    private void AnimateWindowLeft(double targetWidth, TimeSpan duration)
    {
        var target = Math.Max(0, (ScreenWidth - targetWidth) / 2);

        BeginAnimation(LeftProperty, null);

        var animation = new DoubleAnimation(Left, target, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(LeftProperty, animation);
    }

    /// <summary>
    /// 对外暴露的状态名（供副岛判断是否显示）：
    /// 只有 compact / countdown 时副岛才出现，与源项目 ACTIVE_STATES 对齐。
    /// </summary>
    private string MainStateName => _state switch
    {
        IslandState.Hidden => "hidden",
        IslandState.Mini => "mini",
        IslandState.Countdown => "countdown",
        _ => _alertText is not null && DateTime.Now < _alertUntil ? "alert" : "compact",
    };

    private void PublishGeometry()
        => GeometryChanged?.Invoke(this, new IslandGeometry(
            Left, Top, ActualWidth, ActualHeight, MainStateName));

    // ================= 外部接口 =================

    /// <summary>启用 / 停用灵动岛（对应 set_enabled）。</summary>
    public void SetEnabled(bool enabled)
    {
        if (enabled == _enabled) return;
        _enabled = enabled;

        if (!enabled)
        {
            Sleep();
            return;
        }

        WakeUp();
    }

    public void ApplySettings(AppSettings settings)
    {
        _current = settings;

        if (!settings.ShowIsland)
        {
            SetEnabled(false);
            return;
        }

        if (_state == IslandState.Hidden)
        {
            WakeUp();
            return;
        }

        AnimateTop(Top, Math.Max(0, settings.IslandTopMargin), null);
        Tick();
    }

    /// <summary>重新拉取插件内容并重绘（对应 request_refresh）。</summary>
    public void RefreshFromPlugins()
    {
        if (_state == IslandState.Hidden) return;
        RenderCompact(_schedule.GetStatus());
    }

    /// <summary>弹出提醒（对应 notify，供插件调用）。</summary>
    public void Notify(string text, bool isEnd = false, bool force = false)
    {
        if (!_enabled) return;

        if (_state == IslandState.Hidden) WakeUp();
        ShowAlert(text, isEnd);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopTicking();
        base.OnClosed(e);
    }
}
