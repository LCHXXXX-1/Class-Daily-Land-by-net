using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>
/// 灵动岛样式预览（对应源项目 test_dialog.py 的 IslandPreview）。
///
/// 46px 高的迷你胶囊：深色底 + 进度圆环 + 14px 粗体白字，
/// 倒计时态额外画一条蓝色填充，提醒态则只显示居中粗体文字。
/// </summary>
internal sealed class IslandPreview : FrameworkElement
{
    private const double PreviewHeight = 46;
    private const double RingSize = 26;
    private const double RingX = 6;
    private const double TextX = RingX + RingSize + 8;
    private const double RightPad = 10;

    private static readonly Brush CapsuleBrush = Frozen("#1c1c1e");
    private static readonly Brush RingTrackBrush = Frozen("#3a3a3c");
    private static readonly Brush FillBrush = Frozen("#0a84ff");
    private static readonly Brush TextBrush = Frozen("#ffffff");
    private static readonly Brush AlertEndBrush = Frozen("#ffffff");
    private static readonly Brush AlertLiveBrush = Frozen("#30d158");

    private static readonly Brush OngoingBrush = Frozen("#30d158");
    private static readonly Brush UpcomingBrush = Frozen("#4da3ff");
    private static readonly Brush IdleBrush = Frozen("#8a8a8e");

    private static readonly Typeface CapsuleTypeface =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Typeface AlertTypeface =
        new(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private string _text = "";
    private string _state = "none";
    private double _ratio;
    private double _widthRatio = 1.0;
    private bool _isEnd;

    public IslandPreview() => Height = PreviewHeight;

    public void SetState(string text, string state, double ratio = 0, double widthRatio = 1.0, bool isEnd = false)
    {
        _text = text ?? "";
        _state = state;
        _ratio = Math.Clamp(ratio, 0, 1);
        _widthRatio = Math.Clamp(widthRatio, 0, 1);
        if (_widthRatio <= 0) _widthRatio = 1.0;
        _isEnd = isEnd;

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = Math.Max(40, ActualWidth * _widthRatio);
        var x = (ActualWidth - width) / 2.0;
        var h = ActualHeight > 0 ? ActualHeight : PreviewHeight;
        var radius = h / 2.0;

        dc.DrawRoundedRectangle(CapsuleBrush, null, new Rect(x, 0, width, h), radius, radius);

        // ---- 提醒态：居中粗体文字，不再画圆环 ----
        if (_state == "alert")
        {
            var alert = BuildText(_text, AlertTypeface, 16);
            var brush = _isEnd ? AlertEndBrush : AlertLiveBrush;
            Alert(alert, brush);

            dc.DrawText(alert, new Point(x + Math.Max(0, (width - alert.Width) / 2), (h - alert.Height) / 2));
            return;
        }

        // ---- 倒计时态：蓝色进度填充（从左侧铺开） ----
        if (_state == "countdown" && _ratio > 0)
        {
            dc.PushClip(new RectangleGeometry(new Rect(x, 0, width, h)));

            var barWidth = width * _ratio;
            var barRadius = Math.Min(h / 2.0, barWidth / 2.0);

            dc.DrawRoundedRectangle(
                FillBrush, null,
                new Rect(x, 1, Math.Max(0, barWidth), h - 2),
                barRadius, barRadius);

            dc.Pop();
        }

        var accent = _state switch
        {
            "ongoing" => OngoingBrush,
            "upcoming" => UpcomingBrush,
            "countdown" => UpcomingBrush,
            _ => IdleBrush,
        };

        // ---- 进度圆环 ----
        var cy = h / 2.0;
        var ringRadius = (RingSize - 4) / 2.0;
        var center = new Point(x + RingX + RingSize / 2.0, cy);

        dc.DrawEllipse(null, new Pen(RingTrackBrush, 2), center, ringRadius, ringRadius);

        if (_ratio > 0)
        {
            // 与源项目一致：从正上方顺时针画弧
            var start = new Point(center.X, center.Y - ringRadius);
            var end = new Point(
                center.X + ringRadius * Math.Sin(_ratio * 2 * Math.PI),
                center.Y - ringRadius * Math.Cos(_ratio * 2 * Math.PI));

            var figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(ringRadius, ringRadius),
                IsLargeArc = _ratio > 0.5,
                SweepDirection = SweepDirection.Clockwise,
            });

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var pen = new Pen(accent, 2)
            {
                StartLineCap = PenLineCap.Flat,
                EndLineCap = PenLineCap.Flat,
            };

            dc.DrawGeometry(null, pen, geometry);
        }

        // 圆环中心的小圆点
        dc.DrawEllipse(accent, null, center, 3, 3);

        // ---- 文字：起点固定，放不下时裁剪 ----
        var text = BuildText(_text, CapsuleTypeface, 14);
        text.MaxTextWidth = Math.Max(0, x + width - TextX - RightPad);
        text.MaxTextHeight = h;
        text.Trimming = TextTrimming.CharacterEllipsis;

        dc.PushClip(new RectangleGeometry(new Rect(TextX, 0, Math.Max(0, x + width - TextX - RightPad), h)));
        dc.DrawText(text, new Point(TextX, (h - text.Height) / 2));
        dc.Pop();
    }

    /// <summary>整体改写文字颜色（提醒态用绿色 / 结束态用白色）。</summary>
    private static void Alert(FormattedText text, Brush brush) => text.SetForegroundBrush(brush);

    private static FormattedText BuildText(string text, Typeface typeface, double size)
        => new(
            text ?? "",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            Brushes.White,
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 状态测试。对应源模块：test_dialog.py 的 StatusTestDialog。
///
/// 叠加「提前提醒 + 倒计时窗口 + 时间偏移」三个变量，实时预览灵动岛会显示什么，
/// 并支持一键跑到**真实灵动岛**上做整段情景演示（提前 → 倒计时 → 上课 → 下课 → 恢复）。
/// </summary>
internal sealed class StatusTestDialog : Window
{
    private readonly IScheduleService _schedule;
    private readonly ISettingsService _settings;
    private readonly IAppController _controller;

    private readonly TextBox _timeBox;
    private readonly TextBox _offsetBox;
    private readonly TextBox _advanceBox;
    private readonly TextBox _countdownBox;
    private readonly ComboBox _speedBox;
    private readonly IslandPreview _preview;
    private readonly TextBlock _detail;
    private readonly Button _demoButton;

    private readonly DispatcherTimer _simTimer;
    private readonly System.Diagnostics.Stopwatch _simClock = new();

    private readonly List<FrameworkElement> _animated = new();

    private bool _demoRunning;

    // 演示各阶段时长（已按倍速压缩，单位秒）
    private double _simAdvance;
    private double _simCountdown;
    private double _simHold;
    private const double SimAlert = 1.0;

    public StatusTestDialog(IScheduleService schedule, ISettingsService settings, IAppController controller)
    {
        _schedule = schedule;
        _settings = settings;
        _controller = controller;

        Title = "状态测试";
        Width = 680;
        Height = 700;
        MinWidth = 580;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        _simTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _simTimer.Tick += (_, _) => SimTick();

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 18) };

        // ---------- 标题 ----------
        var title = new TextBlock
        {
            Text = "状态测试",
            FontSize = 22,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Margin = new Thickness(0, 0, 0, 2),
        };
        root.Children.Add(title);
        _animated.Add(title);

        var sub = new TextBlock
        {
            Text = "叠加「提前提醒 + 倒计时窗口 + 时间偏移」，实时预览灵动岛状态，并可整段演示。",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        root.Children.Add(sub);
        _animated.Add(sub);

        // ---------- 测试参数 ----------
        var paramCard = ScheduleDialogParts.Card("测试参数", "", out var paramBody);
        paramCard.Margin = new Thickness(0, 14, 0, 12);

        var grid = new Grid();

        // 5 列：标签 / 控件 / 标签 / 控件 / 尾部按钮
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var i = 0; i < 3; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _timeBox = ScheduleDialogParts.Input(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        Grid.SetRow(AddLabel(grid, "测试时刻", 0, 0), 0);
        Grid.SetColumn(_timeBox, 1);
        Grid.SetColumnSpan(_timeBox, 3);
        grid.Children.Add(_timeBox);

        var nowButton = ScheduleDialogParts.Action("现在", UseNow);
        Grid.SetColumn(nowButton, 4);
        grid.Children.Add(nowButton);

        _offsetBox = NumberInput(_schedule.TimeOffsetSeconds.ToString());
        AddRow(grid, 1, "时间偏移", _offsetBox, "提前提醒", _advanceBox = NumberInput(_schedule.AdvanceMinutes.ToString()));

        _countdownBox = NumberInput(_settings.Current.IslandCountdownSec.ToString());
        _speedBox = new ComboBox { Margin = new Thickness(0, 4, 6, 4), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in new[] { ("1×", 1.0), ("5×", 5.0), ("10×", 10.0), ("20×", 20.0), ("30×", 30.0) })
            _speedBox.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        _speedBox.SelectedIndex = 2;   // 10×

        AddRow(grid, 2, "倒计时", _countdownBox, "演示倍速", _speedBox);

        paramBody.Children.Add(grid);
        root.Children.Add(paramCard);
        _animated.Add(paramCard);

        // ---------- 预览 ----------
        var previewCard = ScheduleDialogParts.Card("灵动岛预览", "", out var previewBody);
        previewCard.Margin = new Thickness(0);

        _preview = new IslandPreview { Margin = new Thickness(0, 4, 0, 12) };
        previewBody.Children.Add(_preview);

        _detail = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 21,
            Opacity = 0.9,
        };
        previewBody.Children.Add(_detail);

        root.Children.Add(previewCard);
        _animated.Add(previewCard);

        // ---------- 按钮 ----------
        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 16, 0, 0),
        };

        _demoButton = ScheduleDialogParts.Action("▶ 开始演示", StartDemo, primary: true);
        foot.Children.Add(_demoButton);
        foot.Children.Add(ScheduleDialogParts.Action("保存到设置", Save));
        foot.Children.Add(ScheduleDialogParts.Action("关掉", Close));

        root.Children.Add(foot);
        _animated.Add(foot);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root,
        };

        // 任一参数变化即刻重算预览
        foreach (var box in new[] { _timeBox, _offsetBox, _advanceBox, _countdownBox })
            box.TextChanged += (_, _) => { if (!_demoRunning) Update(); };

        _speedBox.SelectionChanged += (_, _) => { if (!_demoRunning) Update(); };

        Update();
    }

    // ================= 参数行 =================

    private static FrameworkElement AddLabel(Grid grid, string text, int row, int column)
    {
        var label = new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
        };

        Grid.SetRow(label, row);
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
        return label;
    }

    private void AddRow(Grid grid, int row, string leftLabel, UIElement leftControl, string rightLabel, UIElement rightControl)
    {
        AddLabel(grid, leftLabel, row, 0);

        Grid.SetRow((FrameworkElement)leftControl, row);
        Grid.SetColumn((FrameworkElement)leftControl, 1);
        grid.Children.Add(leftControl);

        AddLabel(grid, rightLabel, row, 2);

        Grid.SetRow((FrameworkElement)rightControl, row);
        Grid.SetColumn((FrameworkElement)rightControl, 3);
        Grid.SetColumnSpan((FrameworkElement)rightControl, 2);
        grid.Children.Add(rightControl);
    }

    private static TextBox NumberInput(string value) => new()
    {
        Text = value,
        Width = 110,
        Padding = new Thickness(6, 4, 6, 4),
        Margin = new Thickness(0, 4, 6, 4),
        TextAlignment = TextAlignment.Right,
    };

    private static int ReadInt(TextBox box, int fallback, int min, int max)
        => int.TryParse(box.Text.Trim(), out var value) ? Math.Clamp(value, min, max) : fallback;

    /// <summary>测试时刻；解析失败时回落到当前时刻（不打断使用）。</summary>
    private DateTime TestTime
        => DateTime.TryParseExact(
            _timeBox.Text.Trim(),
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : DateTime.Now;

    private double SpeedValue
        => _speedBox.SelectedItem is ComboBoxItem { Tag: double value } ? value : 10.0;

    // ================= 基础行为 =================

    private void UseNow()
    {
        _timeBox.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        Update();
    }

    private void Save()
    {
        _schedule.AdvanceMinutes = ReadInt(_advanceBox, _schedule.AdvanceMinutes, 0, 60);
        _schedule.TimeOffsetSeconds = ReadInt(_offsetBox, _schedule.TimeOffsetSeconds, -1800, 1800);
        _schedule.Save();

        _settings.Update(s => s.IslandCountdownSec = ReadInt(_countdownBox, s.IslandCountdownSec, 5, 600));

        MessageBox.Show(this,
            $"已保存：提前 {_schedule.AdvanceMinutes} 分，偏移 {_schedule.TimeOffsetSeconds}s，"
            + $"倒计时窗口 {_settings.Current.IslandCountdownSec}s。",
            "状态测试", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ================= 整段演示 =================

    private void StartDemo()
    {
        if (_demoRunning) return;

        var advanceSec = ReadInt(_advanceBox, 0, 0, 60) * 60;
        var countdownSec = ReadInt(_countdownBox, 60, 5, 600);
        const int endHoldSec = 10;
        var speed = SpeedValue;

        _simAdvance = advanceSec / speed;
        _simCountdown = countdownSec / speed;
        _simHold = endHoldSec / speed;

        _demoRunning = true;
        _demoButton.IsEnabled = false;

        // 真实灵动岛同步跑一遍；岛不可用时只跑本窗口的预览
        var started = _controller.RunScenarioTest(advanceSec, countdownSec, endHoldSec, speed);
        if (!started)
        {
            MessageBox.Show(this,
                "灵动岛当前不可用，只在本窗口内演示。\n请在设置中打开「显示灵动岛」后重试。",
                "状态测试", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _simClock.Restart();
        _simTimer.Start();
        SimTick();
    }

    /// <summary>演示时间轴：与源项目 _sim_tick 的阶段划分完全一致。</summary>
    private void SimTick()
    {
        var elapsed = _simClock.Elapsed.TotalSeconds;
        var a = Math.Max(0.001, _simAdvance);
        var c = Math.Max(0.001, _simCountdown);

        if (elapsed < a)
        {
            var remain = a - elapsed;
            _preview.SetState($"距离上课 测试 · {(int)remain}s", "upcoming", remain / a, 0.6);
        }
        else if (elapsed < a + c)
        {
            var remain = a + c - elapsed;
            _preview.SetState($"下节 测试 · {(int)remain}s", "countdown", remain / c, 1.0);
        }
        else if (elapsed < a + c + SimAlert + _simHold)
        {
            _preview.SetState("上课了！", "alert", 0, 0.9, isEnd: false);
        }
        else if (elapsed < a + c + SimAlert + _simHold + SimAlert)
        {
            _preview.SetState("下课了！", "alert", 0, 0.9, isEnd: true);
        }
        else
        {
            _simTimer.Stop();
            _demoRunning = false;
            _demoButton.IsEnabled = true;
            Update();
        }
    }

    // ================= 静态预览 =================

    private void Update()
    {
        if (_demoRunning) return;

        var now = TestTime;
        var offset = ReadInt(_offsetBox, 0, -1800, 1800);
        var advance = ReadInt(_advanceBox, 0, 0, 60);
        var countdownSec = ReadInt(_countdownBox, 60, 5, 600);

        var status = _schedule.GetStatus(now, advance, offset);

        string islandState;
        string text;
        double ratio;
        double width;
        var untilSec = status.UntilSec;

        switch (status.Status)
        {
            case ScheduleStatusKind.Upcoming when untilSec is not null && untilSec <= countdownSec:
                islandState = "countdown";
                text = $"下节 {status.Course} · {untilSec}s";
                ratio = Math.Clamp((double)untilSec.Value / Math.Max(1, countdownSec), 0, 1);
                width = 1.0;
                break;

            case ScheduleStatusKind.Upcoming:
                islandState = "upcoming";
                text = $"下节 {status.Course} · {status.UntilMin}分钟后";
                ratio = 0;
                width = 0.6;
                break;

            case ScheduleStatusKind.Ongoing:
                islandState = "ongoing";
                text = $"本节：{status.Course} 还剩 {status.RemainMin}分钟";
                ratio = 1.0;
                width = 1.0;
                break;

            case ScheduleStatusKind.Holiday:
                islandState = "none";
                text = string.IsNullOrWhiteSpace(status.Name) ? "假期中" : $"假期中 · {status.Name}";
                ratio = 0;
                width = 0.6;
                break;

            case ScheduleStatusKind.None:
                islandState = "none";
                text = "今日无课";
                ratio = 0;
                width = 0.6;
                break;

            default:
                islandState = "done";
                text = "今日课程已结束";
                ratio = 0;
                width = 0.6;
                break;
        }

        _preview.SetState(text, islandState, ratio, width);

        BuildDetail(status, islandState, text, now, offset, advance, countdownSec);
    }

    private void BuildDetail(
        ScheduleStatus status,
        string islandState,
        string text,
        DateTime now,
        int offset,
        int advance,
        int countdownSec)
    {
        _detail.Inlines.Clear();

        Line("状态：", $"{islandState}（schedule={status.Status}）");
        Line("岛文案：", text);
        Line("测试时刻：", now.ToString("HH:mm:ss"));
        Line("参数：", $"偏移 {offset:+0;-0;0}s，提前 {advance} 分，倒计时窗口 {countdownSec}s，倍速 {(int)SpeedValue}×");

        var index = status.Index;
        var periods = _schedule.PeriodsFor(DateOnly.FromDateTime(now));

        if (index is not null && index >= 0 && index < periods.Count)
        {
            var period = periods[index.Value];
            if (TryParseHhmm(period.Start, out var h, out var m))
            {
                var printed = new DateTime(now.Year, now.Month, now.Day, h, m, 0);
                var effective = printed.AddSeconds(offset).AddMinutes(-advance);

                Line("课程：", $"{status.Course}（{period.Name}）");
                Line("课表时间：", $"{period.Start} - {period.End}");
                Line("有效上课：", $"{effective:HH:mm:ss}（提前 {advance} 分 / 偏移 {offset}s）");
                Line("进入倒计时：", $"{effective.AddSeconds(-countdownSec):HH:mm:ss}");
            }
        }

        if (status.Status == ScheduleStatusKind.Upcoming && status.UntilSec is not null)
            Line("距上课：", FormatSeconds(status.UntilSec.Value));
        else if (status.Status == ScheduleStatusKind.Ongoing)
            Line("距下课：", FormatSeconds(status.RemainSec ?? 0));

        void Line(string label, string value)
        {
            _detail.Inlines.Add(new System.Windows.Documents.Run(label) { FontWeight = FontWeight.FromOpenTypeWeight(600) });
            _detail.Inlines.Add(new System.Windows.Documents.Run(value));
            _detail.Inlines.Add(new System.Windows.Documents.LineBreak());
        }
    }

    private static string FormatSeconds(int sec)
    {
        var value = Math.Max(0, sec);
        return $"{value / 60}分{value % 60:00}秒";
    }

    private static bool TryParseHhmm(string text, out int hour, out int minute)
    {
        hour = minute = 0;

        var parts = (text ?? "").Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out hour)) return false;
        if (!int.TryParse(parts[1], out minute)) return false;

        return hour is >= 0 and < 24 && minute is >= 0 and < 60;
    }

    // ================= 入场动画 =================

    /// <summary>
    /// 跳过入场动画。仅在无人值守快照（--capture）时置位：
    /// 否则截图会落在动画中途，拿到半透明且错位的画面。
    /// </summary>
    internal bool SkipEntranceAnimation { get; init; }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (SkipEntranceAnimation) return;
        if (!_settings.Current.AnimFadeWindow) return;

        var duration = Math.Max(100, Math.Min(500, _settings.Current.AnimDuration));

        for (var i = 0; i < _animated.Count; i++)
        {
            var element = _animated[i];
            element.Opacity = 0;

            var translate = new TranslateTransform(0, 12);
            element.RenderTransform = translate;

            var delay = TimeSpan.FromMilliseconds(i * 40);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(duration))
            {
                BeginTime = delay,
                EasingFunction = ease,
            });

            translate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(duration))
                {
                    BeginTime = delay,
                    EasingFunction = ease,
                });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _simTimer.Stop();
        base.OnClosed(e);
    }
}
