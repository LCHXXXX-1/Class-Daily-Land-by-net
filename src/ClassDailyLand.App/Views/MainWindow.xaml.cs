using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.App.Views.Controls;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 主窗口。对应源模块：gui.py 的 ClassBoardApp。
///
/// 与源项目对齐的关键点：
///   · 无边框、不占任务栏，贴屏幕右边缘、顶部对齐（setGeometry(x, 0, w, h)）
///   · 宽度 = 屏幕宽 × main_width_ratio；高度随内容自适应，上限为屏高 - 50
///   · 头部三行固定，作业区独立滚动，溢出时自动滚动
///   · 文案逐字对齐：标题「班级日常」、值日生「今日值日生：X」、
///     出勤「应到 X 人 · 实到 X 人」、作业「科目 (日期)」+「  1. 内容」
///   · 配色取自源项目 theme.py 色板（窗口背景 / 正文颜色）
///
/// 注意：源项目的云母（Mica）材质作用于<b>设置窗口</b>而非主窗口，
/// 因此这里不应用任何背景材质，保持纯色背景。
/// </summary>
public partial class MainWindow : Window
{
    private readonly IDutyService _duty;
    private readonly IAttendanceService _attendance;
    private readonly IHomeworkService _homework;
    private readonly IScheduleService _schedule;
    private readonly ISettingsService _settings;
    private readonly IPathService _paths;
    private readonly PluginRuntime _plugins;

    private readonly DispatcherTimer _autoScrollTimer;
    private int _scrollDirection = 1;
    private int _scrollPauseLeft;
    private double _scrollAccumulator;

    private AppSettings _current = new();
    private double _fontSize = 14;
    private Color _textColor;

    /// <summary>请求打开设置中心（由 App 订阅）。</summary>
    public event EventHandler? SettingsRequested;

    public MainWindow(
        IDutyService duty,
        IAttendanceService attendance,
        IHomeworkService homework,
        IScheduleService schedule,
        ISettingsService settings,
        IPathService paths,
        PluginRuntime plugins)
    {
        _duty = duty;
        _attendance = attendance;
        _homework = homework;
        _schedule = schedule;
        _settings = settings;
        _paths = paths;
        _plugins = plugins;

        InitializeComponent();

        // 与源项目一致：找不到 icon.ico 时用自绘图标兜底
        Icon = AppIconFactory.CreateImageSource();

        _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _autoScrollTimer.Tick += (_, _) => AutoScrollTick();

        // 触屏 / 鼠标拖拽滚动 + 手动滚动时暂停自动滚动（对应 gui.py 的
        // setup_touch_scroll(self.scroll, mouse_drag=True) 与 _pause_auto_scroll：
        // 触屏设备上没有滚轮，拖是唯一的滚动方式，别和手指抢画面）
        DragScroll.SetEnabled(HomeworkScroll, true);
        DragScroll.SetInteraction(HomeworkScroll, PauseAutoScrollForManual);

        _duty.Changed += (_, _) => RefreshContent();
        _attendance.Changed += (_, _) => RefreshContent();
        _homework.Changed += (_, _) => RefreshContent();
        _schedule.Changed += (_, _) => RefreshContent();

        Closing += OnClosing;
        Loaded += (_, _) => RefreshContent();

        // F1 退出（对应源项目 QShortcut(F1)）
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F1)
            {
                e.Handled = true;
                ExitApplication();
            }
        };

        ApplyThemeColors();
        RefreshContent();
    }

    // ================= 关闭行为 =================

    /// <summary>
    /// 主窗口无标题栏，正常不存在「用户点关闭」的入口。
    ///
    /// 这里一律取消关闭并改为隐藏，且**绝不改动 show_main_window**：
    /// 应用退出时 WPF 会关闭所有窗口并触发本事件，
    /// 若在此写入 show_main_window=false，下次启动主窗口就会消失 ——
    /// 这是必须避免的配置污染。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    // ================= 主题 =================

    /// <summary>按当前主题刷新窗口背景与文字颜色（对应 _apply_theme）。</summary>
    private void ApplyThemeColors()
    {
        var (background, text) = ThemeBridge.MainWindowPalette;

        _textColor = text;
        Background = new SolidColorBrush(background);

        TitleText.Foreground = new SolidColorBrush(text);
        DutyText.Foreground = new SolidColorBrush(text);
        AttendanceText.Foreground = new SolidColorBrush(text);

        // 标题字号 = 正文 + 4（对应 fs + 4）
        TitleText.FontSize = _fontSize + 4;
        TitleText.FontWeight = FontWeights.Bold;
        DutyText.FontSize = _fontSize;
        AttendanceText.FontSize = _fontSize;
    }

    // ================= 内容刷新 =================

    public void RefreshContent()
    {
        ApplyThemeColors();

        DutyText.Text = $"今日值日生：{_duty.CurrentDuty}";
        AttendanceText.Text = _attendance.Summary;

        RenderHomework();
        AdjustWindowGeometry();
    }

    /// <summary>渲染作业列表：科目标题（粗体）+ 带序号的条目。</summary>
    private void RenderHomework()
    {
        HomeworkPanel.Children.Clear();

        var groups = _homework.Grouped();

        if (groups.Count == 0)
        {
            HomeworkPanel.Children.Add(MakeLine("暂无作业", bold: false));
            return;
        }

        foreach (var group in groups)
        {
            var title = group.Subject;
            if (!string.IsNullOrWhiteSpace(group.Date)) title += $" ({group.Date.Trim()})";

            HomeworkPanel.Children.Add(MakeLine(title, bold: true));

            for (var i = 0; i < group.Contents.Count; i++)
                HomeworkPanel.Children.Add(MakeLine($"  {i + 1}. {group.Contents[i]}", bold: false));
        }
    }

    private TextBlock MakeLine(string text, bool bold) => new()
    {
        Text = text,
        FontSize = _fontSize,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Foreground = new SolidColorBrush(_textColor),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 2),   // 对应 homework_layout.setSpacing(2)
    };

    // ================= 几何与自适应高度 =================

    /// <summary>贴到屏幕右边缘并计算高度（对应 _adjust_window）。</summary>
    private void AdjustWindowGeometry()
    {
        var screenWidth = SystemParameters.PrimaryScreenWidth;
        var screenHeight = SystemParameters.PrimaryScreenHeight;

        var ratio = _current.MainWidthRatio is > 0.05 and < 1 ? _current.MainWidthRatio : 0.25;
        var width = Math.Max(120, screenWidth * ratio);

        Width = width;
        Left = Math.Max(0, screenWidth - width);
        Top = 0;

        var available = Math.Max(50, width - 24);

        // 头部高度：外边距 8(上) + 三行高度
        HeaderPanel.Measure(new Size(available, double.PositiveInfinity));
        var headerHeight = HeaderPanel.DesiredSize.Height + 8;

        // 作业区高度：上下边距 + 内容实测高度
        HomeworkPanel.Measure(new Size(available, double.PositiveInfinity));
        var contentHeight = Math.Max(40, HomeworkPanel.DesiredSize.Height + 12);

        var maxHeight = Math.Max(120, screenHeight - 50);
        var total = Math.Min(headerHeight + contentHeight, maxHeight);

        Height = total;
        HomeworkScroll.Height = Math.Max(40, total - headerHeight);

        UpdateAutoScroll();
    }

    // ================= 自动滚动 =================

    /// <summary>按内容是否溢出启停自动滚动（对应 _apply_scroll）。</summary>
    private void UpdateAutoScroll()
    {
        if (!_current.MainAutoScroll)
        {
            _autoScrollTimer.Stop();
            ResetScroll();
            return;
        }

        // 布局完成后 ScrollableHeight 才有效，故延后一帧判断
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (HomeworkScroll.ScrollableHeight > 0)
            {
                if (!_autoScrollTimer.IsEnabled)
                {
                    ResetScroll();
                    _autoScrollTimer.Interval =
                        TimeSpan.FromMilliseconds(Math.Max(10, _current.MainScrollInterval));
                    _autoScrollTimer.Start();
                }
            }
            else
            {
                _autoScrollTimer.Stop();
                ResetScroll();
            }
        }));
    }

    private void ResetScroll()
    {
        _scrollDirection = 1;
        _scrollPauseLeft = 0;
        _scrollAccumulator = 0;
        HomeworkScroll.ScrollToVerticalOffset(0);
    }

    /// <summary>
    /// 用户自己滑过之后，自动滚动先停几秒，别和手指抢画面
    /// （对应 gui.py 的 _pause_auto_scroll，MANUAL_SCROLL_HOLD_MS = 4000）。
    /// </summary>
    private void PauseAutoScrollForManual()
    {
        if (!_autoScrollTimer.IsEnabled) return;

        var interval = Math.Max(1, _current.MainScrollInterval);
        var ticks = (int)(4000.0 / interval);
        _scrollPauseLeft = Math.Max(_scrollPauseLeft, Math.Max(1, ticks));
    }

    /// <summary>自动滚动一帧（对应 _auto_scroll_tick）。</summary>
    private void AutoScrollTick()
    {
        var max = HomeworkScroll.ScrollableHeight;
        if (max <= 0)
        {
            _autoScrollTimer.Stop();
            return;
        }

        // 滚到两端时按配置停顿若干帧
        if (_scrollPauseLeft > 0)
        {
            _scrollPauseLeft--;
            return;
        }

        _scrollAccumulator += Math.Max(1, _current.MainScrollStep);
        var step = (int)_scrollAccumulator;
        if (step == 0) return;
        _scrollAccumulator -= step;

        var next = HomeworkScroll.VerticalOffset + _scrollDirection * step;

        if (next >= max)
        {
            next = max;
            _scrollDirection = -1;
            _scrollPauseLeft = Math.Max(0, _current.MainScrollPause);
        }
        else if (next <= 0)
        {
            next = 0;
            _scrollDirection = 1;
            _scrollPauseLeft = Math.Max(0, _current.MainScrollPause);
        }

        HomeworkScroll.ScrollToVerticalOffset(next);
    }

    // ================= 设置应用 =================

    /// <summary>应用主窗口相关设置（对应 apply_main_settings）。</summary>
    public void ApplyMainSettings(AppSettings settings)
    {
        _current = settings;
        _fontSize = settings.MainFontSize <= 0 ? 14 : settings.MainFontSize;

        ApplyOpacity(settings.MainOpacity);
        RefreshContent();
    }

    public void ApplyOpacity(double opacity) => Opacity = Math.Clamp(opacity, 0.1, 1.0);

    /// <summary>显示 / 隐藏主窗口（对应 controller.show_main）。</summary>
    public void SetVisible(bool visible)
    {
        if (visible)
        {
            Show();
            RefreshContent();
        }
        else
        {
            Hide();
        }
    }

    /// <summary>由托盘或菜单触发的真正退出。</summary>
    public void ExitApplication() => Application.Current.Shutdown();

    // ================= 右键菜单 =================

    private void OnPreviousDuty(object sender, RoutedEventArgs e) => _duty.PreviousDuty();

    private void OnNextDuty(object sender, RoutedEventArgs e) => _duty.NextDuty();

    private void OnEditAttendance(object sender, RoutedEventArgs e)
    {
        var dialog = new AttendanceDialog(_attendance) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnEditHomework(object sender, RoutedEventArgs e)
    {
        var dialog = new HomeworkEditorDialog(_homework) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnManageTimetables(object sender, RoutedEventArgs e)
    {
        var dialog = new TimetableManagerDialog(_schedule) { Owner = this };
        dialog.ShowDialog();
        RefreshContent();
    }

    private void OnTempAdjust(object sender, RoutedEventArgs e)
    {
        var dialog = new TempAdjustDialog(_schedule) { Owner = this };
        dialog.ShowDialog();
        RefreshContent();
        _plugins.RequestRefresh();
    }

    private void OnHolidays(object sender, RoutedEventArgs e)
    {
        var dialog = new HolidayDialog(_schedule) { Owner = this };
        dialog.ShowDialog();
        RefreshContent();
    }

    private void OnWeekend(object sender, RoutedEventArgs e)
    {
        var dialog = new WeekendDialog(_schedule) { Owner = this };
        dialog.ShowDialog();
        RefreshContent();
    }

    private void OnRollCall(object sender, RoutedEventArgs e)
    {
        var dialog = new RollCallDialog(_paths);
        dialog.Show();
    }

    private void OnExit(object sender, RoutedEventArgs e) => ExitApplication();
}
