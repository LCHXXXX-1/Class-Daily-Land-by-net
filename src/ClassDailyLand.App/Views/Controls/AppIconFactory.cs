using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace ClassDailyLand.App.Views.Controls;

/// <summary>
/// 应用图标工厂。
/// 对应源模块：utils.py 的 app_icon() / _fallback_icon()。
///
/// 找不到 icon.ico 时，源项目会自绘一个图标：
///   32×32 圆角方块（半径 8）、底色 #0067c0、居中白色「班」字（19px 粗体）。
/// 这里用同一套参数生成，保证托盘与窗口图标在任何情况下都不丢失。
/// </summary>
internal static class AppIconFactory
{
    private const int Size = 32;
    private const int CornerRadius = 8;
    private static readonly Drawing.Color AccentColor = Drawing.Color.FromArgb(0x00, 0x67, 0xC0);

    /// <summary>为托盘生成 System.Drawing 图标。</summary>
    public static Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = RenderBitmap();
        var handle = bitmap.GetHicon();

        // FromHandle 不接管句柄所有权，需克隆后由调用方释放
        using var temporary = Drawing.Icon.FromHandle(handle);
        return (Drawing.Icon)temporary.Clone();
    }

    /// <summary>为 WPF 窗口生成 ImageSource。</summary>
    public static ImageSource CreateImageSource()
    {
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var radius = CornerRadius;
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x00, 0x67, 0xC0)),
                null,
                new Rect(1, 1, Size - 2, Size - 2),
                radius,
                radius);

            var text = new FormattedText(
                "班",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal,
                    FontWeights.Bold, FontStretches.Normal),
                19,
                Brushes.White,
                96);

            dc.DrawText(text, new Point((Size - text.Width) / 2, (Size - text.Height) / 2));
        }

        var target = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>用 System.Drawing 绘制同款图标位图。</summary>
    private static Drawing.Bitmap RenderBitmap()
    {
        var bitmap = new Drawing.Bitmap(Size, Size);

        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);

        using var path = RoundedRect(new Drawing.Rectangle(1, 1, Size - 2, Size - 2), CornerRadius);
        using var fill = new Drawing.SolidBrush(AccentColor);
        graphics.FillPath(fill, path);

        using var font = new Drawing.Font(
            "Microsoft YaHei UI", 19, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);

        var format = new Drawing.StringFormat
        {
            Alignment = Drawing.StringAlignment.Center,
            LineAlignment = Drawing.StringAlignment.Center,
        };

        graphics.DrawString("班", font, textBrush,
            new Drawing.RectangleF(0, 0, Size, Size), format);

        return bitmap;
    }

    private static Drawing2D.GraphicsPath RoundedRect(Drawing.Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new Drawing2D.GraphicsPath();

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
