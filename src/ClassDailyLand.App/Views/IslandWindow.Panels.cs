using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Win32;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 灵动岛下拉面板的展开 / 收起管理。
/// 对应源模块：dynamic_island.py 的 _collect_specs / _layout / _pull_down /
/// _stagger_panels_in / _push_up / _poll_outside_click。
///
/// 展开：面板从高度 0 长出，彼此错峰 70ms 出现，全部就位后停留 3 秒自动收回。
/// 收起：从当前实际高度收拢，避免展开途中被打断时跳变。
/// 外部点击：轮询全局左键状态，点击落在主岛与面板之外即收回。
/// </summary>
public partial class IslandWindow
{
    // ---------- 面板布局常量（对应源项目类属性）----------
    private const double PanelGap = 4;
    private const double PanelMinWidth = 120;
    private const double PanelMaxWidth = 320;
    private const double ScheduleDefaultWidth = 240;
    private const double ScheduleMinWidth = 180;
    private const double MaxTotalRatio = 0.80;
    private const int PanelStaggerMs = 70;
    private const int PanelHoldMs = 3000;
    private const int OutsideClickPollMs = 100;

    private readonly List<IslandPanelWindow> _panels = new();

    private bool _pulled;
    private int _pullGeneration;
    private DispatcherTimer? _pullHoldTimer;
    private DispatcherTimer? _outsideClickTimer;
    private bool _leftButtonWasDown;

    /// <summary>一个待展示的面板条目。</summary>
    private sealed record PanelItem(string Text, double Width, double MinWidth, double Height);

    /// <summary>计算后的面板落位。</summary>
    private sealed record PanelPlacement(PanelItem Item, double X, double Y, double Width);

    // ================= 诊断辅助 =================

    /// <summary>诊断用：立即展开下拉面板（供 --capture 渲染快照）。</summary>
    internal void OpenPanelsForCapture() => PullDownPanels();

    /// <summary>诊断用：当前已创建的面板窗口。</summary>
    internal IReadOnlyList<IslandPanelWindow> CapturePanels => _panels;

    /// <summary>诊断用：面板没展开时用来交代为什么（状态机此刻停在哪）。</summary>
    internal string CaptureDiagnostics
        => $"state={_state} pulled={_pulled} enabled={_enabled} testing={_testing} "
           + $"compactWidth={CompactWidth} actual={ActualWidth:0} panels={_panels.Count}\n"
           + $"geometry: left={Left:0} top={Top:0} width={Width:0} height={Height:0} "
           + $"visualWidth={Visual.Width:0} screenWidth={SystemParameters.PrimaryScreenWidth:0} "
           + $"screenCenterOffset={(Left + Width / 2 - SystemParameters.PrimaryScreenWidth / 2):0.0}";

    // ================= 开关 =================

    private void TogglePanels()
    {
        if (_pulled) PushUpPanels();
        else PullDownPanels();
    }

    /// <summary>展开面板（对应 _pull_down）。</summary>
    private void PullDownPanels()
    {
        if (_pulled || _state != IslandState.Compact) return;

        var items = CollectPanelItems();
        if (items.Count == 0) return;

        _pulled = true;
        _pullGeneration++;

        var placements = LayoutPanels(items);
        var fade = _current.AnimFadePanel;
        var duration = PanelDuration();

        foreach (var placement in placements)
        {
            var panel = new IslandPanelWindow();
            panel.Configure(placement.Width, placement.Item.Height, placement.Item.Text);
            panel.Place(placement.X, placement.Y);
            _panels.Add(panel);
        }

        // ---- 错峰展开：每个面板延迟 i × 70ms ----
        var generation = _pullGeneration;
        for (var i = 0; i < _panels.Count; i++)
        {
            var index = i;
            var delay = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(index * PanelStaggerMs),
            };

            delay.Tick += (_, _) =>
            {
                delay.Stop();

                // 快速收起后作废延迟回调，防止幽灵面板残留
                if (generation != _pullGeneration || !_pulled) return;
                _panels[index].AnimateIn(duration, fade);
            };

            delay.Start();
        }

        // ---- 全部就位后启动停留计时与外部点击检测 ----
        var settle = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds((_panels.Count - 1) * PanelStaggerMs + 240),
        };

        settle.Tick += (_, _) =>
        {
            settle.Stop();
            if (generation != _pullGeneration || !_pulled) return;

            StartPanelHold();
            StartOutsideClickWatch();
        };

        settle.Start();
    }

    /// <summary>收起面板（对应 _push_up）。</summary>
    private void PushUpPanels(bool immediate = false)
    {
        StopPanelHold();
        StopOutsideClickWatch();

        _pullGeneration++;   // 作废所有延迟展开回调

        if (!_pulled)
        {
            if (immediate) ClosePanelsImmediately();
            return;
        }

        _pulled = false;

        if (immediate || _panels.Count == 0)
        {
            ClosePanelsImmediately();
            return;
        }

        var duration = PanelDuration();
        var fade = _current.AnimFadePanel;

        // 先复制一份：收起完成回调里会清空原列表
        var closing = _panels.ToList();
        var remaining = closing.Count;

        foreach (var panel in closing)
        {
            panel.AnimateOut(duration, fade, () =>
            {
                remaining--;
                if (remaining <= 0) ClosePanelsImmediately();
            });
        }
    }

    private void ClosePanelsImmediately()
    {
        foreach (var panel in _panels)
        {
            try
            {
                panel.CloseImmediately();
                panel.Close();
            }
            catch (InvalidOperationException)
            {
                // 窗口已在关闭流程中，忽略
            }
        }

        _panels.Clear();
    }

    // ================= 内容收集 =================

    /// <summary>
    /// 收集全部面板：插件面板按 position 分列左右，课表预览固定居中
    /// （对应 _collect_specs）。
    /// </summary>
    private List<PanelItem> CollectPanelItems()
    {
        var left = new List<PanelItem>();
        var right = new List<PanelItem>();

        // 插件面板：order 小的排前面，默认 order = 100
        var specs = _plugins.GetPanelSpecs()
            .OrderBy(s => s.Order)
            .ToList();

        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Text)) continue;

            var item = new PanelItem(
                spec.Text,
                spec.Width > 0 ? spec.Width : ScheduleDefaultWidth,
                spec.MinWidth > 0 ? spec.MinWidth : PanelMinWidth,
                spec.Height > 0 ? spec.Height : 20 + 22 + 14);

            if (string.Equals(spec.Position, "right", StringComparison.OrdinalIgnoreCase))
                right.Add(item);
            else
                left.Add(item);
        }

        var result = new List<PanelItem>();
        result.AddRange(left);
        result.Add(MakeSchedulePanel());
        result.AddRange(right);

        return result;
    }

    /// <summary>课表预览面板（对应 _make_schedule_spec）。</summary>
    private PanelItem MakeSchedulePanel()
    {
        var today = _schedule.TodayCourses();
        var periods = _schedule.PeriodsFor();
        var status = _schedule.GetStatus();
        var current = status.Index ?? -1;

        var lines = new List<string>();

        for (var i = 0; i < periods.Count; i++)
        {
            var course = i < today.Count ? today[i] : "";
            if (string.IsNullOrWhiteSpace(course)) continue;

            // ▶ 标记当前节次
            var mark = i == current ? "▶ " : "  ";
            lines.Add($"{mark}{course}  {_schedule.OffsetTimeStr(periods[i].Start)}");
        }

        string text;
        if (status.Status == ScheduleStatusKind.Holiday)
        {
            text = string.IsNullOrWhiteSpace(status.Name) ? "假期中" : $"假期中 · {status.Name}";
        }
        else
        {
            text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "今天没有课";
        }

        var lineCount = Math.Max(1, lines.Count);
        var height = 20 + lineCount * 22 + 14;

        return new PanelItem(text, ScheduleDefaultWidth, ScheduleMinWidth, height);
    }

    // ================= 布局 =================

    /// <summary>计算各面板的位置与宽度（对应 _layout）。</summary>
    private List<PanelPlacement> LayoutPanels(List<PanelItem> items)
    {
        var screenWidth = ScreenWidth;
        var maxTotal = screenWidth * MaxTotalRatio;

        // 宽度先取「声明宽度」与「下限」的较大者，再夹到面板允许区间
        var widths = items
            .Select(i => Math.Clamp(Math.Max(i.MinWidth, i.Width), PanelMinWidth, PanelMaxWidth))
            .ToList();

        var total = widths.Sum() + PanelGap * Math.Max(0, items.Count - 1);

        // 总宽超出屏幕可用宽度时按比例压缩
        if (total > maxTotal && total > 0)
        {
            var scale = maxTotal / total;
            for (var i = 0; i < widths.Count; i++)
                widths[i] = Math.Max(PanelMinWidth, widths[i] * scale);

            total = widths.Sum() + PanelGap * Math.Max(0, items.Count - 1);
        }

        var placements = new List<PanelPlacement>(items.Count);
        var x = Math.Max(0, (screenWidth - total) / 2);
        var y = Math.Max(0, _current.IslandTopMargin) + CompactHeight + PanelGap;

        for (var i = 0; i < items.Count; i++)
        {
            placements.Add(new PanelPlacement(items[i], x, y, widths[i]));
            x += widths[i] + PanelGap;
        }

        return placements;
    }

    private int PanelDuration()
    {
        var duration = _current.AnimDuration;
        if (duration <= 0) duration = 320;
        return Math.Max(100, Math.Min(500, duration));
    }

    // ================= 自动收回 =================

    private void StartPanelHold()
    {
        StopPanelHold();

        _pullHoldTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(PanelHoldMs),
        };

        _pullHoldTimer.Tick += (_, _) =>
        {
            StopPanelHold();
            if (_pulled) PushUpPanels();
        };

        _pullHoldTimer.Start();
    }

    private void StopPanelHold()
    {
        _pullHoldTimer?.Stop();
        _pullHoldTimer = null;
    }

    // ================= 外部点击检测 =================

    private void StartOutsideClickWatch()
    {
        StopOutsideClickWatch();

        // 记录初始状态，避免展开时正在按下的鼠标被误判为新点击
        _leftButtonWasDown = MouseHelper.IsLeftButtonDown;

        _outsideClickTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(OutsideClickPollMs),
        };
        _outsideClickTimer.Tick += (_, _) => OutsideClickTick();
        _outsideClickTimer.Start();
    }

    private void StopOutsideClickWatch()
    {
        _outsideClickTimer?.Stop();
        _outsideClickTimer = null;
    }

    /// <summary>轮询全局左键，检测是否在主岛与面板之外点击（对应 _poll_outside_click）。</summary>
    private void OutsideClickTick()
    {
        var isDown = MouseHelper.IsLeftButtonDown;
        var justPressed = isDown && !_leftButtonWasDown;
        _leftButtonWasDown = isDown;

        if (!justPressed || !_pulled) return;

        var cursor = MouseHelper.GetCursorPosition();
        if (cursor is not { } point) return;

        if (IsPointOverUi(point.X, point.Y)) return;

        PushUpPanels();
    }

    /// <summary>判断屏幕物理坐标是否落在主岛或任一已展开面板之内。</summary>
    private bool IsPointOverUi(int screenX, int screenY)
    {
        var dpi = VisualTreeHelper.GetDpi(this);

        if (ContainsDip(Left, Top, ActualWidth, ActualHeight, dpi))
            return true;

        foreach (var panel in _panels)
        {
            if (!panel.IsOpen) continue;
            if (ContainsDip(panel.Left, panel.Top, panel.Width, panel.TargetHeight, dpi))
                return true;
        }

        return false;

        bool ContainsDip(double left, double top, double width, double height, DpiScale scale)
        {
            var physicalLeft = left * scale.DpiScaleX;
            var physicalTop = top * scale.DpiScaleY;
            var physicalRight = physicalLeft + width * scale.DpiScaleX;
            var physicalBottom = physicalTop + height * scale.DpiScaleY;

            return screenX >= physicalLeft && screenX <= physicalRight
                   && screenY >= physicalTop && screenY <= physicalBottom;
        }
    }
}
