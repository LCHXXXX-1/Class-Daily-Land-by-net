using System.Windows;
using System.Windows.Media;

namespace ClassDailyLand.Plugin.Abstractions;

/// <summary>
/// 主岛右侧「加长片段」的描述。
/// 对应源项目 add_island_extra 返回的 {width, draw, on_click, priority}。
/// </summary>
public sealed class IslandExtraSpec
{
    /// <summary>占用宽度（像素）。</summary>
    public double Width { get; init; }

    /// <summary>绘制回调：在给定的矩形区域内自绘内容。</summary>
    public Action<DrawingContext, Rect>? Draw { get; init; }

    /// <summary>点击回调（可选）。</summary>
    public Action? OnClick { get; init; }

    /// <summary>多个片段时的排序权重，越大越靠后。</summary>
    public int Priority { get; init; }
}

/// <summary>
/// 主岛横幅片段。对应源项目 add_island_banner。
/// </summary>
public sealed class IslandBannerSpec
{
    public string Text { get; init; } = "";
    public Action<DrawingContext, Rect>? Draw { get; init; }
    public int Priority { get; init; }
}

/// <summary>
/// 下拉面板条目。对应源项目 add_panel 返回的
/// {text, width, min_width, height, position, order}。
/// </summary>
public sealed class PanelSpec
{
    public string Text { get; init; } = "";
    public double Width { get; init; }
    public double MinWidth { get; init; }
    public double Height { get; init; }

    /// <summary>展开方向，例如 "below" / "above"。</summary>
    public string Position { get; init; } = "below";

    /// <summary>面板内排序。</summary>
    public int Order { get; init; }
}

/// <summary>
/// 副岛内容描述。对应源项目 add_subisland 返回的
/// {text 或 icon 或 draw, length, priority, auto_collapse}。
/// </summary>
public sealed class SubIslandSpec
{
    /// <summary>纯文本内容（与 <see cref="Draw"/> 二选一）。</summary>
    public string? Text { get; init; }

    /// <summary>图标字形（与 <see cref="Draw"/> 二选一）。</summary>
    public string? Icon { get; init; }

    /// <summary>自绘内容（优先级高于 Text / Icon）。</summary>
    public Action<DrawingContext, Rect>? Draw { get; init; }

    /// <summary>副岛展开长度（像素）。</summary>
    public double Length { get; init; }

    public int Priority { get; init; }

    /// <summary>是否允许自动收起。</summary>
    public bool AutoCollapse { get; init; } = true;
}
