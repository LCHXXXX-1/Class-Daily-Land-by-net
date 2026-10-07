using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClassDailyLand.App.Views.Controls;

/// <summary>
/// 灵动岛自绘控件。逐项对应源模块 dynamic_island.py 的 paintEvent 绘制逻辑。
///
/// 关键尺寸与配色（与源项目完全一致）：
///   紧凑态 240 × 40，圆角 = 高度一半（胶囊）
///   迷你态  48 × 40（无内容时只显示一个状态点）
///   背景 #1c1c1e
///   左侧进度圆环：位置 x=6, y=6，直径 28，环宽 2
///       · 未到上课 → #4da3ff
///       · 上课中   → #30d158
///       · 其它     → #8a8a8e
///   文字起点 x=42，14px 粗体白字，字体 Microsoft YaHei UI
///   倒计时态：整条胶囊填充蓝色进度（#0a84ff）+ 流光 + 前缘光点
/// </summary>
public sealed class IslandVisual : FrameworkElement
{
    // ---------- 尺寸常量（对应 DynamicIsland 的类属性） ----------
    public const double CompactWidth = 240;
    public const double CompactHeight = 40;
    public const double MiniWidth = 48;
    public const double AlertHeight = 40;

    private const double RingSize = 28;
    private const double RingX = 6;
    private const double RingStroke = 2;
    private const double TextX = RingX + RingSize + 8;   // = 42
    private const double TextRightMargin = 10;
    private const double BarInset = 1;
    private const double MarqueeGap = 40;

    // ---------- 配色 ----------
    private static readonly Brush CapsuleBrush = Frozen("#1c1c1e");
    private static readonly Brush RingTrackBrush = Frozen("#3a3a3c");
    private static readonly Brush TextBrush = Brushes.White;
    private static readonly Brush CountdownFillBrush = Frozen("#0a84ff");
    private static readonly Brush CometHeadBrush = Frozen("#EBF5FF");
    private static readonly Brush AlertEndBrush = Brushes.White;
    private static readonly Brush AlertStartBrush = Frozen("#30d158");
    private static readonly Brush MiniIdleBrush = Frozen("#8a8a8e");

    private static readonly Typeface CapsuleTypeface = new(
        new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Typeface AlertTypeface = new(
        new FontFamily("Microsoft YaHei UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    // ---------- 绘制状态 ----------
    private string _text = "";
    private Brush _ringBrush = MiniIdleBrush;
    private double _ringRatio;
    private double _fillRatio = -1;          // < 0 表示不绘制填充
    private bool _isMini;
    private string? _alertText;
    private bool _alertIsEnd;
    private double _marqueeOffset;

    // 文字切换的滚动动画（对应 _draw_rolling_text）
    private string? _rollOldText;
    private double _rollProgress = 1.0;

    // 插件加长片段（对应 _paint_extra）
    private Action<DrawingContext, Rect>? _extraDraw;

    /// <summary>加长片段左侧分隔线颜色：rgba(255,255,255,45)。</summary>
    private static readonly Brush SeparatorBrush = Frozen("#2DFFFFFF");

    public IslandVisual()
    {
        Width = CompactWidth;
        Height = CompactHeight;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    /// <summary>当前是否处于迷你态。</summary>
    public bool IsMini => _isMini;

    /// <summary>当前文字是否溢出（供跑马灯定时器判断是否启动）。</summary>
    public bool IsTextOverflowing { get; private set; }

    /// <summary>更新紧凑态状态。</summary>
    public void SetCompactState(
        string text,
        Brush ringBrush,
        double ringRatio,
        double fillRatio = 0,
        double extraWidth = 0)
    {
        _isMini = false;
        _alertText = null;   // 进入紧凑态即提醒结束；不清除会永远画在提醒态（对应源项目按 _state 分支绘制）
        _text = text ?? "";
        _ringBrush = ringBrush;
        _ringRatio = Clamp01(ringRatio);
        _fillRatio = fillRatio;
        ExtraWidth = Math.Max(0, extraWidth);

        Height = CompactHeight;
        InvalidateVisual();
    }

    /// <summary>
    /// 设置胶囊宽度。
    /// 紧凑态为 240，倒计时态会展开到 600（对应源项目 COMPACT_W / COUNTDOWN_W）。
    /// </summary>
    public void SetCapsuleWidth(double width)
    {
        var target = Math.Max(MiniWidth, width);
        if (Math.Abs(Width - target) < 0.5) return;

        Width = target;
        InvalidateVisual();
    }

    /// <summary>仅更新倒计时填充比例（由高频填充时钟调用，避免重算其它状态）。</summary>
    public void SetFillRatio(double ratio)
    {
        _fillRatio = ratio;
        InvalidateVisual();
    }

    /// <summary>更新迷你态（对应 _paint_mini）。</summary>
    public void SetMiniState(Brush dotBrush)
    {
        _isMini = true;
        _alertText = null;
        _ringBrush = dotBrush;
        _fillRatio = -1;
        Width = MiniWidth;
        Height = CompactHeight;
        InvalidateVisual();
    }

    /// <summary>更新提醒态（对应 _paint_alert）。</summary>
    public void SetAlertState(string text, bool isEnd)
    {
        _alertText = text;
        _alertIsEnd = isEnd;
        _isMini = false;
        _fillRatio = -1;
        Height = AlertHeight;
        InvalidateVisual();
    }

    /// <summary>清除提醒态。</summary>
    public void ClearAlert() => _alertText = null;

    /// <summary>插件加长片段占用宽度。</summary>
    public double ExtraWidth { get; private set; }

    /// <summary>
    /// 设置插件的加长片段绘制回调与占用宽度（对应 _paint_extra）。
    /// 回调异常会被吞掉，单个插件的绘制问题不影响主岛。
    /// </summary>
    public void SetExtraSegment(double width, Action<DrawingContext, Rect>? draw)
    {
        ExtraWidth = Math.Max(0, width);
        _extraDraw = draw;
        InvalidateVisual();
    }

    /// <summary>开始一次文字切换动画；progress 从 0 到 1。</summary>
    public void StartTextRoll(string? oldText)
    {
        _rollOldText = oldText;
        _rollProgress = 0;
    }

    /// <summary>推进文字切换动画（由外部定时器按帧调用）。</summary>
    public void SetRollProgress(double progress)
    {
        _rollProgress = progress;

        if (progress >= 1)
        {
            _rollOldText = null;
            _rollProgress = 1;
        }

        InvalidateVisual();
    }

    /// <summary>跑马灯位移（由外部定时器推进）。</summary>
    public void SetMarqueeOffset(double offset)
    {
        if (Math.Abs(offset - _marqueeOffset) < 0.01) return;
        _marqueeOffset = offset;
        InvalidateVisual();
    }

    /// <summary>返回文字实际测量宽度（用于判断是否需要跑马灯）。</summary>
    public double MeasureTextWidth()
    {
        if (string.IsNullOrEmpty(_text)) return 0;
        return BuildText(_text, CapsuleTypeface, 14, TextBrush).Width;
    }

    /// <summary>文字可用宽度。</summary>
    public double TextAreaWidth
    {
        get
        {
            var right = (ExtraWidth > 0 ? ExtraWidth + TextRightMargin : TextRightMargin);
            return Math.Max(0, Width - TextX - right);
        }
    }

    // ================= 绘制 =================

    protected override void OnRender(DrawingContext dc)
    {
        var w = Width;
        var h = Height;
        if (w <= 0 || h <= 0) return;

        // 胶囊底：圆角半径 = 高度一半
        var radius = h / 2;
        dc.DrawRoundedRectangle(CapsuleBrush, null, new Rect(0, 0, w, h), radius, radius);

        // 提醒态独占显示
        if (_alertText is not null)
        {
            DrawAlert(dc, w, h);
            return;
        }

        if (_isMini)
        {
            DrawMini(dc, w, h);
            return;
        }

        // 倒计时态：整条蓝色填充
        if (_fillRatio >= 0) DrawCountdownFill(dc, w, h, _fillRatio);

        DrawRing(dc, _ringBrush, _ringRatio);
        DrawCapsuleText(dc, h);
        DrawExtraSegment(dc, w, h);
    }

    /// <summary>绘制插件加长片段与其左侧分隔线（对应 _paint_extra）。</summary>
    private void DrawExtraSegment(DrawingContext dc, double w, double h)
    {
        if (ExtraWidth <= 0) return;

        var lineX = w - ExtraWidth;

        // 分隔线：源项目为 drawLine(left, 7, left, height-8)
        dc.DrawLine(new Pen(SeparatorBrush, 1), new Point(lineX, 7), new Point(lineX, h - 8));

        if (_extraDraw is null) return;

        var rect = new Rect(lineX, 0, ExtraWidth, h);
        dc.PushClip(new RectangleGeometry(rect));

        try
        {
            _extraDraw(dc, rect);
        }
        catch (Exception ex)
        {
            // 插件自绘异常不能影响主岛渲染
            System.Diagnostics.Debug.WriteLine($"[plugin] 加长片段绘制异常: {ex.Message}");
        }

        dc.Pop();
    }

    /// <summary>绘制左侧进度圆环（对应 _draw_ring）。</summary>
    private static void DrawRing(DrawingContext dc, Brush brush, double ratio)
    {
        var y = (CompactHeight - RingSize) / 2;
        var center = new Point(RingX + RingSize / 2.0, y + RingSize / 2.0);
        var radius = (RingSize - 4) / 2.0;

        dc.DrawEllipse(null, new Pen(RingTrackBrush, RingStroke), center, radius, radius);

        if (ratio <= 0) return;

        // Qt 从 90°（12 点）顺时针扫过 -360°·ratio；
        // 换算到 WPF 屏幕坐标（Y 向下）：-90° 起，顺时针扫 +360°·ratio
        var geometry = BuildArc(center, radius, -90, 360 * ratio);
        dc.DrawGeometry(null, new Pen(brush, RingStroke), geometry);
    }

    /// <summary>绘制倒计时蓝色填充条（对应 _paint_countdown_fill）。</summary>
    private static void DrawCountdownFill(DrawingContext dc, double w, double h, double ratio)
    {
        var shown = Clamp01(ratio);
        if (shown <= 0) return;

        var barHeight = Math.Max(2, h - BarInset * 2);
        var r = barHeight / 2;
        var fillWidth = Math.Max(0, w * shown) - BarInset;
        if (fillWidth <= 0) return;

        var barRect = new Rect(BarInset, BarInset, fillWidth, Math.Min(r, fillWidth / 2));

        // 底色蓝条
        dc.DrawRoundedRectangle(CountdownFillBrush, null, barRect, r, r);

        // 流光扫过：中间亮、两侧透明的横向渐变
        var sheenWidth = Math.Max(40, fillWidth * 0.5);
        var sheen = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(70, 255, 255, 255), 0.5),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        sheen.Freeze();

        var sheenRect = new Rect(
            Math.Max(BarInset, BarInset + fillWidth - sheenWidth),
            BarInset,
            Math.Min(sheenWidth, fillWidth),
            barHeight);

        dc.DrawRoundedRectangle(sheen, null, sheenRect, r, r);

        // 前缘亮点
        var headCenter = new Point(BarInset + fillWidth - r, h / 2);
        dc.DrawEllipse(CometHeadBrush, null, headCenter, r * 0.6, r * 0.6);
    }

    /// <summary>绘制迷你态（对应 _paint_mini）。</summary>
    private void DrawMini(DrawingContext dc, double w, double h)
        => dc.DrawEllipse(_ringBrush, null, new Point(w / 2, h / 2), 6, 6);

    /// <summary>绘制提醒文字（对应 _paint_alert）。</summary>
    private void DrawAlert(DrawingContext dc, double w, double h)
    {
        var brush = _alertIsEnd ? AlertEndBrush : AlertStartBrush;
        var text = BuildText(_alertText ?? "", AlertTypeface, 16, brush);
        dc.DrawText(text, new Point(Math.Max(0, (w - text.Width) / 2), Math.Max(0, (h - text.Height) / 2)));
    }

    /// <summary>绘制胶囊文字：含切换滚动与溢出跑马灯（对应 _draw_rolling_text / _draw_marquee_text）。</summary>
    private void DrawCapsuleText(DrawingContext dc, double h)
    {
        if (string.IsNullOrEmpty(_text)) return;

        var available = TextAreaWidth;
        var text = BuildText(_text, CapsuleTypeface, 14, TextBrush);
        IsTextOverflowing = text.Width > available;

        var top = Math.Max(0, (h - text.Height) / 2);

        // ---- 文字切换：旧文字上移淡出，新文字自下淡入（140ms，OutCubic）----
        if (_rollProgress < 1 && !string.IsNullOrEmpty(_rollOldText))
        {
            var t = Clamp01(_rollProgress);
            var eased = 1 - Math.Pow(1 - t, 3);
            var offset = eased * h;

            var oldText = BuildText(_rollOldText, CapsuleTypeface, 14, TextBrush);

            dc.PushOpacity(Math.Max(0, 1 - 1.5 * t));
            dc.PushTransform(new TranslateTransform(0, -offset));
            dc.DrawText(oldText, new Point(TextX, top));
            dc.Pop();
            dc.Pop();

            dc.PushOpacity(Math.Max(0, 1.5 * t - 0.5));
            dc.PushTransform(new TranslateTransform(0, h - offset));
            dc.DrawText(text, new Point(TextX, top));
            dc.Pop();
            dc.Pop();
            return;
        }

        if (!IsTextOverflowing || available <= 4)
        {
            dc.DrawText(text, new Point(TextX, top));
            return;
        }

        // ---- 文字放不下 → 左右无缝循环滚动 ----
        var span = text.Width + MarqueeGap;
        dc.PushClip(new RectangleGeometry(new Rect(TextX, 0, available, h)));

        for (var k = 0; k < 2; k++)
        {
            var x = TextX - _marqueeOffset + k * span;
            if (x > TextX + available) break;
            dc.DrawText(text, new Point(x, top));
        }

        dc.Pop();
    }

    // ================= 工具 =================

    private FormattedText BuildText(string content, Typeface typeface, double size, Brush brush)
        => new(
            content,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>构造圆弧几何（WPF 无 DrawArc，需要手工拼 ArcSegment）。</summary>
    private static Geometry BuildArc(Point center, double radius, double startAngle, double sweepAngle)
    {
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, startAngle + sweepAngle);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(
                end,
                new Size(radius, radius),
                rotationAngle: 0,
                isLargeArc: Math.Abs(sweepAngle) > 180,
                sweepDirection: sweepAngle >= 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                isStroked: true,
                isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>按颜色值取状态色画刷（供外部构造环色）。</summary>
    public static Brush Accent(IslandAccent accent) => accent switch
    {
        IslandAccent.Blue => Frozen("#4da3ff"),
        IslandAccent.Green => Frozen("#30d158"),
        _ => MiniIdleBrush,
    };
}

/// <summary>灵动岛状态配色（对应源项目的 #4da3ff / #30d158 / #8a8a8e）。</summary>
public enum IslandAccent
{
    Gray,
    Blue,
    Green,
}
