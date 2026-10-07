namespace ClassDailyLand.App.Services;

/// <summary>
/// 灵动岛的几何与状态快照，用于副岛跟随定位与显隐。
/// 对应源项目 controller.on_island_geometry 接收的 geom，
/// 以及 sub_island.set_main_state 接收的主岛状态名。
/// </summary>
/// <param name="Left">主岛左边缘屏幕坐标。</param>
/// <param name="Top">主岛上边缘屏幕坐标。</param>
/// <param name="Width">主岛宽度。</param>
/// <param name="Height">主岛高度。</param>
/// <param name="MainState">
/// 主岛状态名：hidden / mini / compact / countdown。
/// 副岛仅在 compact / countdown / alert 时显示。
/// </param>
public readonly record struct IslandGeometry(
    double Left,
    double Top,
    double Width,
    double Height,
    string MainState = "hidden")
{
    /// <summary>垂直中心。</summary>
    public double CenterY => Top + Height / 2;

    /// <summary>右边缘，副岛据此贴靠。</summary>
    public double Right => Left + Width;
}
