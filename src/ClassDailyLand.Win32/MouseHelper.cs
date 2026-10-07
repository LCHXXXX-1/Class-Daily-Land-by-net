using System.Runtime.InteropServices;

namespace ClassDailyLand.Win32;

/// <summary>
/// 鼠标状态探测。
/// 用途：灵动岛下拉面板展开后，检测「在面板之外点击」以便自动收回
/// （对应源项目 dynamic_island.py 的 _poll_outside_click）。
///
/// 说明：不用 WPF 的鼠标事件是因为面板是独立窗口，
/// 在其它窗口上的点击不会路由进来，只能主动查询全局状态。
/// </summary>
public static class MouseHelper
{
    private const int VkLeftButton = 0x01;

    /// <summary>鼠标左键当前是否按下。</summary>
    public static bool IsLeftButtonDown
    {
        get
        {
            try
            {
                // 高位为 1 表示当前按下；GetAsyncKeyState 不改变按键状态
                return (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }
    }

    /// <summary>获取屏幕坐标下的鼠标位置；失败返回 null。</summary>
    public static (int X, int Y)? GetCursorPosition()
    {
        try
        {
            if (!GetCursorPos(out var point)) return null;
            return (point.X, point.Y);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
