using System.Windows;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 主题桥接：把设置里的 theme_mode 字符串映射到 WPF 的 Fluent ThemeMode。
///
/// 说明：ThemeMode 是 .NET 9 引入的实验性 API（项目已在 csproj 中抑制 WPF0001）。
/// 设为 Light/Dark/System 即启用官方 Fluent 主题，
/// 浅色/深色切换、系统强调色、高对比度适配均由框架负责。
/// </summary>
internal static class ThemeBridge
{
    public const string ModeSystem = "system";
    public const string ModeLight = "light";
    public const string ModeDark = "dark";

    /// <summary>把设置值应用到全局。</summary>
    public static void Apply(string? mode)
    {
        var current = Application.Current;
        if (current is null) return;

        current.ThemeMode = mode?.Trim().ToLowerInvariant() switch
        {
            ModeLight => ThemeMode.Light,
            ModeDark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    /// <summary>当前是否处于深色模式（用于给自绘窗口决定配色）。</summary>
    public static bool IsDark
    {
        get
        {
            var mode = Application.Current?.ThemeMode;
            if (mode == ThemeMode.Dark) return true;
            if (mode == ThemeMode.Light) return false;

            // System：读取注册表中的应用主题设置
            return ReadSystemUsesDarkTheme();
        }
    }

    /// <summary>
    /// 主窗口配色。取值对齐源项目 theme.py 的 LIGHT / DARK 色板：
    /// 浅色 window_bg=#ffffff / text=#1b1b1b，深色 window_bg=#202020 / text=#e8e8e8。
    /// </summary>
    public static (System.Windows.Media.Color Background, System.Windows.Media.Color Text) MainWindowPalette
        => IsDark
            ? (System.Windows.Media.Color.FromRgb(0x20, 0x20, 0x20),
               System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xE8))
            : (System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF),
               System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x1B));

    /// <summary>
    /// 设置中心左侧导航配色。
    /// 同样取自源项目 theme.py 的色板：
    ///   nav_checked_bg   浅色 #e2e2e2 / 深色 #3a3a3a
    ///   nav_hover        浅色 #e9e9e9 / 深色 #383838
    ///   nav_checked_text 浅色 #0067c0 / 深色 #4da3e8（同时作为左侧强调色条）
    /// </summary>
    public static (
        System.Windows.Media.Color SelectedBackground,
        System.Windows.Media.Color HoverBackground,
        System.Windows.Media.Color Accent,
        System.Windows.Media.Color NormalText) NavigationPalette
        => IsDark
            ? (System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A),
               System.Windows.Media.Color.FromRgb(0x38, 0x38, 0x38),
               System.Windows.Media.Color.FromRgb(0x4D, 0xA3, 0xE8),
               System.Windows.Media.Color.FromRgb(0xE8, 0xE8, 0xE8))
            : (System.Windows.Media.Color.FromRgb(0xE2, 0xE2, 0xE2),
               System.Windows.Media.Color.FromRgb(0xE9, 0xE9, 0xE9),
               System.Windows.Media.Color.FromRgb(0x00, 0x67, 0xC0),
               System.Windows.Media.Color.FromRgb(0x1B, 0x1B, 0x1B));

    private static bool ReadSystemUsesDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme == 0 → 深色
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
