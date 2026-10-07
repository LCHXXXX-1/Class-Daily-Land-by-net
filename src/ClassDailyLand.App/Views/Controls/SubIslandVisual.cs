using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ClassDailyLand.App.Views.Controls;

/// <summary>
/// 副岛自绘控件。逐项对应源模块 sub_island.py 的 paintEvent。
///
/// 三种形态（尺寸与源项目一致）：
///   空内容  宽 40 —— 居中灰色「—」（16px 粗体，#8a8a8e）
///   收起态  宽 40 —— 居中显示图标，或文案首字（18px 粗体，白色，Segoe UI Emoji）
///   展开态  宽 = 长度（夹在 44–380）—— 图标在 x=10（占 28px）+ 文案（13px，#e6e6e6）
///
/// 背景统一为 #1c1c1e，圆角 = 高度一半（胶囊）。
/// </summary>
public sealed class SubIslandVisual : FrameworkElement
{
    // ---------- 尺寸常量（对应 SubIsland 的类属性）----------
    public const double CapsuleHeight = 40;
    public const double CollapsedWidth = 40;
    public const double Gap = 8;
    public const double DefaultLength = 158;
    public const double MinLength = 44;
    public const double MaxLength = 380;

    private const double IconX = 10;
    private const double IconSlot = 28;
    private const double TextInset = 10;

    private static readonly Brush CapsuleBrush = Frozen("#1c1c1e");
    private static readonly Brush EmptyTextBrush = Frozen("#8a8a8e");
    private static readonly Brush ContentTextBrush = Frozen("#e6e6e6");
    private static readonly Brush IconBrush = Brushes.White;

    private static readonly Typeface TextTypeface = new(
        new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface BoldTypeface = new(
        new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private static readonly Typeface EmojiTypeface = new(
        new FontFamily("Segoe UI Emoji"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    private string _text = "";
    private string? _icon;
    private Action<DrawingContext, Rect>? _draw;
    private bool _hasContent;
    private bool _collapsed = true;

    /// <summary>当前是否处于收起态。</summary>
    public bool IsCollapsed => _collapsed;

    /// <summary>当前是否展示插件内容（无内容时显示占位符）。</summary>
    public bool HasContent => _hasContent;

    /// <summary>
    /// 更新内容。
    /// <paramref name="length"/> 为插件声明的展开长度，会被夹到 44–380。
    /// </summary>
    public void SetContent(
        string? text,
        string? icon,
        Action<DrawingContext, Rect>? draw,
        double length,
        bool collapsed)
    {
        _text = text ?? "";
        _icon = icon;
        _draw = draw;
        _hasContent = !string.IsNullOrWhiteSpace(_text) || draw is not null;
        _collapsed = collapsed;

        Width = _hasContent ? (_collapsed ? CollapsedWidth : ClampLength(length)) : CollapsedWidth;
        Height = CapsuleHeight;

        InvalidateVisual();
    }

    private static double ClampLength(double value)
    {
        if (double.IsNaN(value) || value <= 0) return DefaultLength;
        return Math.Clamp(value, MinLength, MaxLength);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = Width;
        var height = Height;
        if (width <= 0 || height <= 0) return;

        var radius = height / 2;
        dc.DrawRoundedRectangle(CapsuleBrush, null, new Rect(0, 0, width, height), radius, radius);

        // ---- 空内容：居中占位符 ----
        if (!_hasContent)
        {
            DrawCentered(dc, "—", BoldTypeface, 16, EmptyTextBrush, width, height);
            return;
        }

        // ---- 插件自绘优先 ----
        if (_draw is not null)
        {
            try
            {
                _draw(dc, new Rect(0, 0, width, height));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[plugin] 副岛绘制异常: {ex.Message}");
            }
            return;
        }

        // ---- 收起态：只显示图标或文案首字 ----
        if (_collapsed)
        {
            var label = !string.IsNullOrWhiteSpace(_icon)
                ? _icon!
                : (_text.Length > 0 ? _text[..1] : "•");

            DrawCentered(dc, label, EmojiTypeface, 18, IconBrush, width, height);
            return;
        }

        // ---- 展开态：图标 + 文案 ----
        var x = IconX;

        if (!string.IsNullOrWhiteSpace(_icon))
        {
            var iconText = BuildText(_icon!, EmojiTypeface, 18, IconBrush);
            dc.DrawText(iconText, new Point(x, (height - iconText.Height) / 2));
            x += IconSlot;
        }

        var available = width - x - TextInset;
        if (available <= 0) return;

        var text = BuildText(_text, TextTypeface, 13, ContentTextBrush);
        dc.PushClip(new RectangleGeometry(new Rect(x, 0, available, height)));
        dc.DrawText(text, new Point(x, (height - text.Height) / 2));
        dc.Pop();
    }

    private static void DrawCentered(
        DrawingContext dc, string label, Typeface typeface, double size, Brush brush,
        double width, double height)
    {
        var text = BuildText(label, typeface, size, brush);
        dc.DrawText(text, new Point(
            Math.Max(0, (width - text.Width) / 2),
            Math.Max(0, (height - text.Height) / 2)));
    }

    private static FormattedText BuildText(string content, Typeface typeface, double size, Brush brush)
        => new(
            content,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1.0);

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
