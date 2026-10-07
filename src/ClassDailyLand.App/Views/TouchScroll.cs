using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 触屏 / 鼠标拖拽滚动适配。对应源模块 ui_common.py 的
/// setup_touch_scroll / install_touch_scrolling。
///
/// - 触屏平移：由 App.xaml 里 ScrollViewer 的全局样式统一打开
///   （PanningMode = VerticalBoth，对应 QScroller 抓 TouchGesture +
///   install_touch_scrolling 的「应用级兜底」）；
/// - 鼠标拖拽（源项目 mouse_drag=True 的对应物）：按住左键拖动即可滚动，
///   触屏被系统转成鼠标事件时也有兜底；松手带一段惯性衰减
///   （参数对齐 _SCROLL_METRICS：速度平滑 0.6、低于 50 px/s 视为停下、
///   DecelerationFactor 0.12）；
/// - 拖动超过阈值才算拖：未超过时事件照常放行，按钮 / 输入框照常能点
///   （对应源项目「位移超过阈值才算拖动」的 panel_window 语义）。
/// </summary>
internal static class DragScroll
{
    /// <summary>开启「按住左键拖动滚动 + 松手惯性」。</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(DragScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>
    /// 用户开始手动滚动（按下并拖动 / 转动滚轮）时的回调。
    /// 主窗口用它暂停自动滚动，别和手指抢画面（对应 gui.py 的 _pause_auto_scroll）。
    /// </summary>
    public static readonly DependencyProperty InteractionProperty =
        DependencyProperty.RegisterAttached(
            "Interaction", typeof(Action), typeof(DragScroll),
            new PropertyMetadata(null));

    public static void SetEnabled(DependencyObject obj, bool value)
        => obj.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject obj)
        => (bool)obj.GetValue(EnabledProperty);

    public static void SetInteraction(DependencyObject obj, Action? value)
        => obj.SetValue(InteractionProperty, value);

    public static Action? GetInteraction(DependencyObject obj)
        => (Action?)obj.GetValue(InteractionProperty);

    // ---- 惯性参数（对应 _SCROLL_METRICS） ----
    private const double VelocitySmoothing = 0.6;      // DragVelocitySmoothingFactor
    private const double MinVelocity = 50;             // MinimumVelocity = 0.05（px/s）
    private const double DecelerationFactor = 0.12;    // DecelerationFactor：每秒保留 12% 速度的指数衰减
    private const double DragThreshold = 6;            // 拖动判定阈值（px）

    private const int InertiaTickMs = 16;

    private sealed class State
    {
        public bool Pressed;
        public bool Dragging;
        public Point Anchor;
        public double StartOffsetV;
        public double LastDy;                    // 上一次采样时手指相对锚点的位移
        public DateTime LastMove = DateTime.UtcNow;
        public double Velocity;                  // 内容偏移的变化速率，px/s
        public DispatcherTimer? Inertia;
    }

    private static readonly Dictionary<ScrollViewer, State> States = new();

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;

        if ((bool)e.NewValue) Attach(viewer);
        else Detach(viewer);
    }

    private static void Attach(ScrollViewer viewer)
    {
        if (States.ContainsKey(viewer)) return;   // 幂等（对应 _TOUCH_SCROLL_PROP）

        var state = new State();
        States[viewer] = state;

        viewer.PreviewMouseLeftButtonDown += (_, e) =>
        {
            state.Pressed = true;
            state.Dragging = false;
            state.Anchor = e.GetPosition(viewer);
            state.StartOffsetV = viewer.VerticalOffset;
            state.LastDy = 0;
            state.Velocity = 0;
            StopInertia(state);
        };

        viewer.PreviewMouseMove += (_, e) =>
        {
            if (!state.Pressed || e.LeftButton != MouseButtonState.Pressed) return;

            var point = e.GetPosition(viewer);
            var dy = point.Y - state.Anchor.Y;

            if (!state.Dragging)
            {
                // 未超过阈值前不算拖：事件放行，按钮 / 输入框照常响应
                if (Math.Abs(dy) < DragThreshold) return;

                state.Dragging = true;
                state.LastMove = DateTime.UtcNow;
                state.LastDy = dy;
                GetInteraction(viewer)?.Invoke();      // 通知宿主：用户开始手动滚动
                viewer.CaptureMouse();
            }

            var now = DateTime.UtcNow;
            var dt = (now - state.LastMove).TotalSeconds;
            state.LastMove = now;

            if (dt > 0)
            {
                // 手指这一帧的位移换算成内容偏移的变化速率（手指向下 → 内容上滚 → 负值）
                var target = -(dy - state.LastDy) / dt;
                state.Velocity = state.Velocity * (1 - VelocitySmoothing) + target * VelocitySmoothing;
            }

            state.LastDy = dy;
            viewer.ScrollToVerticalOffset(state.StartOffsetV - dy);
            e.Handled = true;
        };

        viewer.PreviewMouseLeftButtonUp += (_, e) =>
        {
            state.Pressed = false;

            if (!state.Dragging) return;

            state.Dragging = false;
            viewer.ReleaseMouseCapture();
            e.Handled = true;                          // 拖动结束时不要触发子控件点击

            if (Math.Abs(state.Velocity) >= MinVelocity) StartInertia(viewer, state);
        };

        viewer.PreviewMouseWheel += (_, _) =>
        {
            StopInertia(state);
            GetInteraction(viewer)?.Invoke();
        };

        viewer.Unloaded += (_, _) => Detach(viewer);
    }

    private static void Detach(ScrollViewer viewer)
    {
        if (!States.Remove(viewer, out var state)) return;

        StopInertia(state);
        viewer.ReleaseMouseCapture();
    }

    // ================= 惯性 =================

    private static void StartInertia(ScrollViewer viewer, State state)
    {
        StopInertia(state);

        state.Inertia = new DispatcherTimer(DispatcherPriority.Render, viewer.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(InertiaTickMs),
        };

        var last = DateTime.UtcNow;

        state.Inertia.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var dt = (now - last).TotalSeconds;
            last = now;

            if (dt <= 0) return;

            var next = viewer.VerticalOffset + state.Velocity * dt;
            state.Velocity *= Math.Pow(DecelerationFactor, dt);

            var stopped = Math.Abs(state.Velocity) < MinVelocity;
            var atEdge = next <= 0 || next >= viewer.ScrollableHeight;

            viewer.ScrollToVerticalOffset(Math.Clamp(next, 0, viewer.ScrollableHeight));

            if (stopped || atEdge) StopInertia(state);
        };

        state.Inertia.Start();
    }

    private static void StopInertia(State state)
    {
        state.Inertia?.Stop();
        state.Inertia = null;
    }
}
