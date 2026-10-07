using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ClassDailyLand.Win32;

/// <summary>
/// 前台窗口探测。对应源模块：dynamic_island.py 中的
/// _fg_hwnd / _class_name / _root_hwnd / _is_desktop / _process_name /
/// _is_office_foreground / _is_fullscreen 一组辅助函数。
///
/// 用途：灵动岛在有全屏应用或 WPS/Office 处于前台时自动隐藏，
/// 避免遮挡正在使用的窗口。
/// </summary>
public static class ForegroundMonitor
{
    private const uint GaRoot = 2;
    private const uint MonitorDefaultToNearest = 2;

    /// <summary>会被识别为「办公软件前台」的进程名（小写，不含扩展名）。</summary>
    private static readonly HashSet<string> OfficeProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Microsoft Office
        "winword", "excel", "powerpnt", "onenote", "outlook", "msaccess", "mspub",
        // WPS Office
        "wps", "et", "wpp", "wpsoffice",
    };

    /// <summary>桌面 / 任务栏相关窗口类名，这些不算「全屏应用」。</summary>
    private static readonly HashSet<string> ShellClassNames = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    public static IntPtr GetForegroundWindowHandle()
    {
        try
        {
            return GetForegroundWindow();
        }
        catch (DllNotFoundException)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>获取窗口类名。</summary>
    public static string? GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            var buffer = new StringBuilder(256);
            return GetClassNameNative(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    /// <summary>向上取到顶层窗口（对应源项目 _root_hwnd）。</summary>
    public static IntPtr GetRootWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            var root = GetAncestor(hwnd, GaRoot);
            return root == IntPtr.Zero ? hwnd : root;
        }
        catch (DllNotFoundException)
        {
            return hwnd;
        }
    }

    /// <summary>当前前台窗口的进程名（不含扩展名，小写）。</summary>
    public static string? GetForegroundProcessName()
    {
        var hwnd = GetRootWindow(GetForegroundWindowHandle());
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>前台是否为桌面 / 任务栏本体。</summary>
    public static bool IsDesktopForeground()
    {
        var className = GetClassName(GetForegroundWindowHandle());
        if (className is null) return false;
        if (ShellClassNames.Contains(className)) return true;

        // 桌面图标层
        return className.Equals("SHELLDLL_DefView", StringComparison.Ordinal);
    }

    /// <summary>前台是否为 WPS / Office（对应 _is_office_foreground）。</summary>
    public static bool IsOfficeForeground()
    {
        var processName = GetForegroundProcessName();
        return processName is not null && OfficeProcessNames.Contains(processName);
    }

    /// <summary>
    /// 前台窗口是否全屏（窗口矩形覆盖整个显示器，对应 _is_fullscreen）。
    /// 桌面与任务栏自身不算全屏，否则灵动岛会在桌面上被误隐藏。
    /// </summary>
    public static bool IsFullscreen()
    {
        var hwnd = GetRootWindow(GetForegroundWindowHandle());
        if (hwnd == IntPtr.Zero || IsDesktopForeground()) return false;

        try
        {
            if (!GetWindowRect(hwnd, out var windowRect)) return false;

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return false;

            var monitorInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref monitorInfo)) return false;

            var screen = monitorInfo.rcMonitor;

            // 允许 1 像素误差，规避边框计算差异
            return windowRect.Left <= screen.Left + 1
                   && windowRect.Top <= screen.Top + 1
                   && windowRect.Right >= screen.Right - 1
                   && windowRect.Bottom >= screen.Bottom - 1;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    // ================= P/Invoke =================

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameNative(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }
}
