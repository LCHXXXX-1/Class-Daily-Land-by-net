using System.Runtime.InteropServices;

namespace ClassDailyLand.Win32;

/// <summary>
/// 窗口视觉效果。对应源模块：ui_common.py 的 apply_mica / set_dark_titlebar 等。
///
/// Windows 10 / 11 能力差异（本层存在的核心理由）：
///   深色标题栏 —— Win10 1809（17763）+ 与 Win11 均支持；
///   Mica 云母   —— 仅 Win11 支持，Win10 上静默跳过；
///   圆角窗口     —— 仅 Win11 支持。
/// 所有方法在能力不足时返回 false 而不是抛异常，由调用方决定降级策略。
/// </summary>
public static class WindowEffects
{
    // ---- DWM 窗口属性编号 ----
    /// <summary>Win10 1809（17763）起的沉浸式深色模式；更早版本使用 19。</summary>
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;

    /// <summary>Win11（22000）起支持窗口圆角偏好。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>Win11 22H2（22621）起支持系统背景材质。</summary>
    private const int DwmwaSystemBackdropType = 38;

    // ---- 背景材质类型（DWM_SYSTEMBACKDROP_TYPE）----
    public const int BackdropAuto = 0;
    public const int BackdropNone = 1;
    public const int BackdropMica = 2;
    public const int BackdropAcrylic = 3;
    public const int BackdropTabbed = 4;

    // ---- 圆角偏好（DWM_WINDOW_CORNER_PREFERENCE）----
    public const int CornerDefault = 0;
    public const int CornerDoNotRound = 1;
    public const int CornerRound = 2;
    public const int CornerRoundSmall = 3;

    /// <summary>深色标题栏是否可用：Win10 1809 起支持。</summary>
    public static bool SupportsDarkTitleBar => WindowsVersion.Build >= 17763;

    /// <summary>Mica 云母是否可用：仅 Windows 11。</summary>
    public static bool SupportsMica => WindowsVersion.IsWindows11;

    /// <summary>圆角窗口是否可用：仅 Windows 11。</summary>
    public static bool SupportsRoundedCorners => WindowsVersion.IsWindows11;

    /// <summary>设置沉浸式深色标题栏。Win10 1809+ 与 Win11 均可用。</summary>
    public static bool TrySetDarkTitleBar(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero || !SupportsDarkTitleBar) return false;

        // 部分旧版本只认 19，需要按版本回退重试
        if (TrySetAttribute(hwnd, DwmwaUseImmersiveDarkMode, dark ? 1 : 0)) return true;
        return TrySetAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, dark ? 1 : 0);
    }

    /// <summary>
    /// 应用 Mica 云母材质。
    /// Windows 10 上直接返回 false —— 这正是源项目「Win10 自动忽略云母」的等价实现。
    /// </summary>
    public static bool TryApplyMica(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !SupportsMica) return false;

        // Mica 需要窗口框架延伸到工作区，否则见不到材质
        TryExtendFrameIntoClientArea(hwnd);
        return TrySetAttribute(hwnd, DwmwaSystemBackdropType, BackdropMica);
    }

    /// <summary>应用亚克力材质（Win11 有效；Win10 需另用未文档化 API，此处不实现）。</summary>
    public static bool TryApplyAcrylic(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !SupportsMica) return false;

        TryExtendFrameIntoClientArea(hwnd);
        return TrySetAttribute(hwnd, DwmwaSystemBackdropType, BackdropAcrylic);
    }

    /// <summary>移除背景材质。</summary>
    public static bool TryClearBackdrop(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !SupportsMica) return false;
        return TrySetAttribute(hwnd, DwmwaSystemBackdropType, BackdropNone);
    }

    /// <summary>设置窗口圆角（仅 Win11）。</summary>
    public static bool TrySetRoundedCorners(IntPtr hwnd, int preference = CornerRound)
    {
        if (hwnd == IntPtr.Zero || !SupportsRoundedCorners) return false;
        return TrySetAttribute(hwnd, DwmwaWindowCornerPreference, preference);
    }

    /// <summary>把窗口框架扩展到整个工作区（Mica / 亚克力的前置条件）。</summary>
    public static bool TryExtendFrameIntoClientArea(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var margins = new Margins { LeftWidth = -1, RightWidth = -1, TopHeight = -1, BottomHeight = -1 };
            return DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // ---- SetWindowPos 常量 ----
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private static readonly IntPtr HwndTopmost = new(-1);

    /// <summary>
    /// 强制把窗口压到最顶层且不夺焦点。
    /// 对应源项目 sub_island.py 的 _set_topmost —— 周期性调用可避免被其它置顶窗口盖住。
    /// </summary>
    public static bool TryPinTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            return SetWindowPos(
                hwnd, HwndTopmost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static bool TrySetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) == 0;
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 环境：静默降级，不影响调用方
            return false;
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int LeftWidth;
        public int RightWidth;
        public int TopHeight;
        public int BottomHeight;
    }
}
