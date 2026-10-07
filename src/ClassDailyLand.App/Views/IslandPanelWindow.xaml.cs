using System.Windows;
using System.Windows.Media.Animation;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 灵动岛下拉面板。对应源模块：panel_window.py 的 PanelWindow。
///
/// 行为：
///   展开 —— 从高度 0 长到目标高度，起始位置下沉 6px 后上浮（弹性感），
///           可选透明度淡入（anim_fade_panel）
///   收起 —— 从当前实际几何收到高度 0（中途被打断也不会跳变）
///   时长 —— 读取「动画时长」设置（anim_duration，限制在 100–500ms）
/// </summary>
public partial class IslandPanelWindow : Window
{
    /// <summary>面板底色的不透明度（源项目为纯色，这里略留一点通透感）。</summary>
    private const double ExpanderOffset = 6;

    private double _baseX;
    private double _baseY;
    private double _targetHeight;

    private bool _isOpen;

    public IslandPanelWindow()
    {
        InitializeComponent();
    }

    /// <summary>目标高度（收起后恢复用）。</summary>
    public double TargetHeight => _targetHeight;

    /// <summary>设置内容与尺寸。</summary>
    public void Configure(double width, double height, string text)
    {
        Width = Math.Max(1, width);
        _targetHeight = Math.Max(1, height);
        Height = 0;

        ContentText.Text = text ?? "";
        Root.UpdateLayout();
    }

    /// <summary>设定基准位置（展开后的最终位置）。</summary>
    public void Place(double x, double y)
    {
        _baseX = x;
        _baseY = y;
        Left = x;
        Top = y + ExpanderOffset;
    }

    /// <summary>当前是否处于展开状态。</summary>
    public bool IsOpen => _isOpen;

    /// <summary>展开动画。</summary>
    public void AnimateIn(int durationMs, bool fade)
    {
        _isOpen = true;

        BeginAnimation(TopProperty, null);
        BeginAnimation(HeightProperty, null);

        if (!IsVisible) Show();

        // 高度：0 → 目标高度；位置：下沉 6px → 基准位（对应源项目的轻微上浮）
        var height = new DoubleAnimation(0, _targetHeight, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var top = new DoubleAnimation(_baseY + ExpanderOffset, _baseY,
            TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(HeightProperty, height);
        BeginAnimation(TopProperty, top);

        if (!fade)
        {
            Opacity = 1;
            return;
        }

        StartOpacity(0, 1);
    }

    /// <summary>收起动画，结束后回调。</summary>
    public void AnimateOut(int durationMs, bool fade, Action? onCompleted)
    {
        _isOpen = false;

        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);

        // 从「当前实际高度」收起，避免展开途中被打断时先跳到满高
        var from = ActualHeight > 0 ? ActualHeight : Height;

        var height = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        height.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = 0;

            Hide();
            onCompleted?.Invoke();
        };

        BeginAnimation(HeightProperty, height);

        if (fade) StartOpacity(1, 0);
    }

    /// <summary>立即收起（不播动画，用于强制清理）。</summary>
    public void CloseImmediately()
    {
        _isOpen = false;

        BeginAnimation(HeightProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);

        Height = 0;
        Hide();
    }

    private void StartOpacity(double from, double to)
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = from;

        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(OpacityProperty, animation);
    }
}
