using System.Runtime.InteropServices;

namespace ClassDailyLand.Win32;

/// <summary>
/// 操作系统版本探测。对应源模块：utils.py 的
/// is_windows_11 / is_windows_10 / windows_build / windows_display_name。
///
/// 用 RtlGetVersion 而非 Environment.OSVersion：
/// 未经应用程序清单声明的进程，GetVersionEx 会被系统「版本撒谎」，
/// RtlGetVersion 始终返回真实内核版本，是判断 Win10/Win11 最可靠的方式。
/// </summary>
public static class WindowsVersion
{
    /// <summary>Windows 11 起始内部版本号。</summary>
    public const int Windows11Build = 22000;

    /// <summary>Windows 10 起始内部版本号（1507）。</summary>
    public const int Windows10Build = 10240;

    private static readonly Lazy<(int Major, int Minor, int Build)> Cached = new(Query);

    public static int MajorVersion => Cached.Value.Major;
    public static int MinorVersion => Cached.Value.Minor;
    public static int Build => Cached.Value.Build;

    /// <summary>系统完整版本号，例如 10.0.22621。</summary>
    public static Version Version => new(MajorVersion, MinorVersion, Build);

    /// <summary>Windows 11（Build 22000 及以上）。</summary>
    public static bool IsWindows11 => Build >= Windows11Build;

    /// <summary>Windows 10（10240 ~ 21999）。</summary>
    public static bool IsWindows10 => Build is >= Windows10Build and < Windows11Build;

    /// <summary>面向用户展示的系统名称，对应源项目 windows_display_name()。</summary>
    public static string DisplayName => IsWindows11
        ? $"Windows 11 (Build {Build})"
        : IsWindows10
            ? $"Windows 10 (Build {Build})"
            : $"Windows (Build {Build})";

    private static (int, int, int) Query()
    {
        try
        {
            var info = new RtlOsVersionInfoEx
            {
                dwOSVersionInfoSize = (uint)Marshal.SizeOf<RtlOsVersionInfoEx>(),
            };

            if (RtlGetVersion(ref info) == 0)
                return ((int)info.dwMajorVersion, (int)info.dwMinorVersion, (int)info.dwBuildNumber);
        }
        catch (DllNotFoundException)
        {
            // 非 Windows 平台（单元测试在 Linux/macOS 上跑时）→ 走下面的回退
        }
        catch (EntryPointNotFoundException)
        {
        }

        var fallback = Environment.OSVersion.Version;
        return (fallback.Major, fallback.Minor, fallback.Build);
    }

    [DllImport("ntdll.dll", SetLastError = true)]
    private static extern int RtlGetVersion(ref RtlOsVersionInfoEx lpVersionInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RtlOsVersionInfoEx
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;

        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }
}
