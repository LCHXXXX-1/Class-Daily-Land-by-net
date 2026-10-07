using System.IO;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClassDailyLand.App.Services;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Market;
using ClassDailyLand.Infrastructure.Updates;
using ClassDailyLand.PluginHost;
using ClassDailyLand.Win32;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心。对应源模块：settings_dialog_v4.py 的 SettingsDialog。
///
/// 结构逐项对齐源项目：
///   顶部条（标题 + 居中「查找设置」搜索框）
///   左侧导航 —— Windows 11 设置外观：圆角选中块 + 3px 强调色条 + 图标
///   右侧内容 —— 页面标题 + 若干「卡片」，每张卡片是一组相关设置行
///
/// 页面与卡片划分、行标签、提示文案、取值范围都取自 v4 的定义，不自行增删
/// （此前自创的「今日课表」预览等内容已移除）。
///
/// 动画：导航切换做颜色与高度过渡；页面切换整体淡入上浮、逐行错峰出现。
/// 时长与开关读取「外观 → 淡入淡出」的设置。
///
/// 云母（Mica）材质作用于本窗口（与源项目一致），Windows 10 上自动跳过。
/// </summary>
public partial class SettingsWindow : Window
{
    private const int NavAnimMs = 180;
    private const int RowStaggerMs = 30;

    private readonly ISettingsService _settings;
    private readonly IScheduleService _schedule;
    private readonly IHomeworkService _homework;
    private readonly IDutyService _duty;
    private readonly PluginRuntime _plugins;
    private readonly PluginManager _pluginManager;
    private readonly IPathService _paths;
    private readonly IAppController _controller;
    private readonly MarketService _market;
    private readonly PackageService _packages;
    private readonly UpdateLauncher _update;

    private readonly List<NavItemView> _navItems = new();
    private readonly List<NavEntry> _entries = new();
    private readonly List<SearchEntry> _searchIndex = new();

    private int _selectedIndex = -1;
    private int _indexBeforeSearch = -1;
    private bool _suppressSearch;

    public SettingsWindow(
        ISettingsService settings,
        IScheduleService schedule,
        IHomeworkService homework,
        IDutyService duty,
        PluginRuntime plugins,
        PluginManager pluginManager,
        IPathService paths,
        IAppController controller,
        MarketService market,
        PackageService packages,
        UpdateLauncher update)
    {
        _settings = settings;
        _schedule = schedule;
        _homework = homework;
        _duty = duty;
        _plugins = plugins;
        _pluginManager = pluginManager;
        _paths = paths;
        _controller = controller;
        _market = market;
        _packages = packages;
        _update = update;

        InitializeComponent();

        // 与源项目一致：找不到 icon.ico 时用自绘图标兜底
        Icon = Controls.AppIconFactory.CreateImageSource();

        // 左侧导航支持手指 / 鼠标拖拽滚动（对应 settings_dialog.py 的
        // setup_touch_scroll(self.nav_scroll, mouse_drag=True)）。
        // 右侧内容只开触屏平移（全局样式），不加鼠标拖拽 —— 免得和
        // 滑块 / 分段按钮抢拖拽；触摸不受影响照样能滑。
        DragScroll.SetEnabled(NavScroll, true);

        SourceInitialized += (_, _) => ApplyWindowEffects();

        // 主题翻转时即时重排：Fluent 画刷会自动跟，但卡片底色等自绘配色
        // 取自 ThemeBridge.IsDark，标题栏明暗也是一次性设置 —— 都得重来
        _lastAppliedDark = ThemeBridge.IsDark;
        _settings.Changed += OnSettingsChangedForTheme;
        Closed += (_, _) => _settings.Changed -= OnSettingsChangedForTheme;

        BuildNavigation();
        BuildSearchIndex();
        UpdateSearchPlaceholder();

        if (_entries.Count > 0) SelectNav(0, animate: false);
    }

    private bool _lastAppliedDark;

    private void OnSettingsChangedForTheme(object? sender, SettingsChangedEventArgs e)
    {
        var dark = ThemeBridge.IsDark;
        if (dark == _lastAppliedDark) return;   // 其它设置项的变化不用重排

        _lastAppliedDark = dark;
        ApplyWindowEffects();
        RenderPage(_selectedIndex, animate: false);
    }

    // ================= Win10 / Win11 材质 =================

    /// <summary>云母仅 Windows 11 支持；Win10 上静默跳过（对应源项目「Win10 自动忽略云母」）。</summary>
    private void ApplyWindowEffects()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        WindowEffects.TrySetDarkTitleBar(hwnd, ThemeBridge.IsDark);

        if (!_settings.Current.SettingsMica) return;

        if (WindowEffects.TryApplyMica(hwnd))
        {
            if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;

            Background = Brushes.Transparent;
            WindowEffects.TrySetRoundedCorners(hwnd);
        }
    }

    // ================= 页面描述 =================

    private enum FieldKind
    {
        Toggle,
        Slider,
        Spin,
        Segmented,
        Text,
    }

    /// <summary>分段控件的一个选项（值 → 中文标签）。</summary>
    private sealed record Choice(string Value, string Label);

    /// <summary>
    /// 一个设置项。Key 对应 AppSettings 上的 snake_case 配置键；
    /// 少数不落在 AppSettings 上的（提前提醒 / 时间偏移）用 Read / Write 直接指向服务。
    /// </summary>
    private sealed record Field(
        string Key,
        string Label,
        FieldKind Kind,
        double Min = 0,
        double Max = 100,
        double Step = 1,
        string Unit = "",
        string Hint = "",
        Choice[]? Choices = null,
        double Scale = 1,
        Func<object?>? Read = null,
        Action<object>? Write = null);

    /// <summary>一张设置卡片（源项目的 SettingCard）。</summary>
    private sealed record Card(string Title, string Subtitle, Field[] Fields)
    {
        public static Card Of(string title, params Field[] fields) => new(title, "", fields);

        public static Card WithSubtitle(string title, string subtitle, params Field[] fields)
            => new(title, subtitle, fields);
    }

    /// <summary>导航条目。Special 非空时走自定义页面渲染。</summary>
    private sealed record NavEntry(string Title, string Glyph, Card[] Cards, string Special = "");

    private static List<NavEntry> BuildEntries() => new()
    {
        // ---------------- 通用 ----------------
        new NavEntry("通用", "\uE713", new[]
        {
            Card.Of("主窗口",
                new Field("show_main_window", "显示班级日常窗口", FieldKind.Toggle,
                    Hint: "关掉之后，托盘菜单里还能再打开"),
                new Field("main_width_ratio", "窗口宽度", FieldKind.Slider, 15, 50,
                    Unit: "%", Hint: "占屏幕宽度的百分之多少", Scale: 0.01),
                new Field("main_opacity", "窗口透明度", FieldKind.Slider, 0, 100,
                    Unit: "%", Hint: "设为 0 表示完全透明", Scale: 0.01),
                new Field("main_font_size", "正文字号", FieldKind.Spin, 8, 30, Unit: " px")),

            Card.Of("作业滚动",
                new Field("main_auto_scroll", "自动滚动作业", FieldKind.Toggle),
                new Field("main_scroll_interval", "滚动间隔", FieldKind.Spin, 20, 200, Unit: " ms"),
                new Field("main_scroll_step", "每步滚动距离", FieldKind.Spin, 1, 10, Unit: " px"),
                new Field("main_scroll_pause", "到底暂停", FieldKind.Spin, 0, 200, Unit: " 次")),

            Card.Of("启动与更新",
                new Field("check_update_on_start", "启动时检查更新", FieldKind.Toggle,
                    Hint: "由独立更新器处理，可随时手动检查"),
                new Field("block_update_on_source", "源码运行保护更新", FieldKind.Toggle,
                    Hint: "检测到本地主入口源码时不再自动更新，改前先提醒备份")),
        }),

        // ---------------- 外观 ----------------
        new NavEntry("外观", "\uE790", BuildAppearanceCards()),

        // ---------------- 灵动岛（主岛 + 副岛两张卡；副岛在源项目里不是独立页）----------------
        new NavEntry("灵动岛", "\uE7C4", new[]
        {
            Card.Of("主岛",
                new Field("show_island", "显示灵动岛", FieldKind.Toggle),
                new Field("island_top_margin", "距屏幕顶部距离", FieldKind.Spin, 0, 100, Unit: " px"),
                new Field("island_alert_width", "提醒条宽度", FieldKind.Spin, 200, 800, Unit: " px"),
                new Field("island_alert_hold_ms", "提醒条停留", FieldKind.Spin, 100, 5000, Step: 50, Unit: " ms"),
                new Field("island_hide_on_fullscreen", "仅全屏时隐藏", FieldKind.Toggle),
                new Field("island_show_wakeup_anim", "启用唤醒动画", FieldKind.Toggle,
                    Hint: "收起时为圆形，展开后为胶囊")),

            Card.Of("副岛",
                new Field("show_sub_island", "显示副岛", FieldKind.Toggle, Hint: "显示在主岛右侧"),
                new Field("sub_island_collapsed", "默认收成圆形", FieldKind.Toggle),
                new Field("sub_island_auto_collapse_sec", "无内容自动收起", FieldKind.Spin, 0, 120,
                    Unit: " 秒", Hint: "设为 0 表示不自动收起")),
        }),

        // ---------------- 课表与提醒 ----------------
        new NavEntry("课表与提醒", "\uE787", Array.Empty<Card>(), Special: "schedule"),

        // ---------------- 作业 ----------------
        new NavEntry("作业", "\uE7BC", Array.Empty<Card>(), Special: "homework"),

        // ---------------- 插件 ----------------
        new NavEntry("插件", "\uE8F1", Array.Empty<Card>(), Special: "plugins"),

        // ---------------- 高级 ----------------
        new NavEntry("高级", "\uE90F", Array.Empty<Card>(), Special: "advanced"),

        // ---------------- 关于 ----------------
        new NavEntry("关于", "\uE946", Array.Empty<Card>(), Special: "about"),
    };

    /// <summary>
    /// 外观页。第三张卡「窗口材质」在 Windows 10 上退化为说明文字
    /// （源项目同样只在 Win11 显示云母开关）。
    /// </summary>
    private static Card[] BuildAppearanceCards()
    {
        var material = WindowsVersion.IsWindows11
            ? Card.WithSubtitle("窗口材质", $"当前系统：{WindowsVersion.DisplayName}",
                new Field("settings_mica", "云母材质（Mica）", FieldKind.Toggle,
                    Hint: "Windows 11 的半透明云母背板，会跟着系统明暗变"))
            : Card.WithSubtitle("窗口材质", $"当前系统：{WindowsVersion.DisplayName}",
                new Field("", "云母材质需要 Windows 11，当前系统使用普通窗口。", FieldKind.Text));

        return new[]
        {
            Card.Of("颜色主题",
                new Field("theme_mode", "颜色模式", FieldKind.Segmented, Choices: new[]
                {
                    new Choice("system", "跟随系统"),
                    new Choice("light", "浅色"),
                    new Choice("dark", "深色"),
                })),

            Card.Of("淡入淡出",
                new Field("anim_fade_window", "窗口打开时渐显", FieldKind.Toggle),
                new Field("anim_fade_panel", "面板展开时渐显", FieldKind.Toggle),
                new Field("anim_fade_text", "文字变化时渐显", FieldKind.Toggle),
                new Field("anim_duration", "动画时长", FieldKind.Spin, 100, 2000, Step: 50, Unit: " ms")),

            material,
        };
    }

    // ================= 导航 =================

    /// <summary>一个导航项的可视元素集合（便于做选中动画）。</summary>
    private sealed class NavItemView
    {
        public required Border Root { get; init; }
        public required Border AccentBar { get; init; }
        public required TextBlock Icon { get; init; }
        public required TextBlock Label { get; init; }
        public required SolidColorBrush Background { get; init; }
        public required SolidColorBrush AccentBrush { get; init; }
        public required SolidColorBrush LabelBrush { get; init; }
        public bool IsSelected { get; set; }
    }

    private void BuildNavigation()
    {
        _entries.Clear();
        _entries.AddRange(BuildEntries());

        // 插件注册的设置页（对应源项目的插件设置二级页）
        foreach (var api in _plugins.Apis)
        {
            foreach (var page in api.SettingsPages)
                _entries.Add(new NavEntry(
                    page.Title, "\uE74C", Array.Empty<Card>(),
                    Special: $"plugin:{api.PluginId}:{page.Title}"));
        }

        for (var i = 0; i < _entries.Count; i++) AddNavItem(_entries[i], i);
    }

    private void AddNavItem(NavEntry entry, int index)
    {
        var (_, _, accent, normalText) = ThemeBridge.NavigationPalette;

        var background = new SolidColorBrush(Colors.Transparent);
        var accentBrush = new SolidColorBrush(accent);
        var labelBrush = new SolidColorBrush(normalText);

        // 左侧强调色条：选中时从 4px 长到 16px（Windows 11 设置的选中指示）
        var accentBar = new Border
        {
            Width = 3,
            Height = 4,
            CornerRadius = new CornerRadius(1.5),
            Background = accentBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 0, 0),
            Opacity = 0,
        };

        var icon = new TextBlock
        {
            Text = entry.Glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 16,
            Width = 24,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(15, 0, 0, 0),
            Foreground = labelBrush,
        };

        var label = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(15, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = labelBrush,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Grid.SetColumn(accentBar, 0);
        Grid.SetColumn(icon, 1);
        Grid.SetColumn(label, 2);

        grid.Children.Add(accentBar);
        grid.Children.Add(icon);
        grid.Children.Add(label);

        var root = new Border
        {
            Height = 40,
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 2, 0, 2),
            Background = background,
            Cursor = Cursors.Hand,
            Child = grid,
        };

        var view = new NavItemView
        {
            Root = root,
            AccentBar = accentBar,
            Icon = icon,
            Label = label,
            Background = background,
            AccentBrush = accentBrush,
            LabelBrush = labelBrush,
        };

        root.MouseLeftButtonUp += (_, _) => SelectNav(index, animate: true);
        root.MouseEnter += (_, _) => ApplyHover(view, true);
        root.MouseLeave += (_, _) => ApplyHover(view, false);

        _navItems.Add(view);
        NavPanel.Children.Add(root);
    }

    private void ApplyHover(NavItemView view, bool hovered)
    {
        if (view.IsSelected) return;

        var (_, hoverBackground, _, _) = ThemeBridge.NavigationPalette;
        AnimateBrush(view.Background, hovered ? hoverBackground : Colors.Transparent, NavAnimMs);
    }

    private void SelectNav(int index, bool animate)
    {
        if (index < 0 || index >= _entries.Count) return;
        if (index == _selectedIndex) return;

        _selectedIndex = index;

        var (selectedBackground, _, accent, normalText) = ThemeBridge.NavigationPalette;

        for (var i = 0; i < _navItems.Count; i++)
        {
            var view = _navItems[i];
            var selected = i == index;
            view.IsSelected = selected;

            var duration = animate ? NavAnimMs : 0;

            AnimateBrush(view.Background, selected ? selectedBackground : Colors.Transparent, duration);
            AnimateBrush(view.LabelBrush, selected ? accent : normalText, duration);
            AnimateBrush(view.Icon.Foreground as SolidColorBrush ?? view.LabelBrush,
                selected ? accent : normalText, duration);
            AnimateOpacity(view.AccentBar, selected ? 1 : 0, duration);
            AnimateHeight(view.AccentBar, selected ? 16 : 4, duration);
        }

        RenderPage(index, animate: animate);
    }

    /// <summary>
    /// 按标题切到指定页。仅供无人值守快照（--capture）点名要拍哪一页，
    /// 因为截图只能看到首屏，而多数入口在后面的页面里。
    /// </summary>
    internal bool SelectPageForCapture(string title)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Title, title, StringComparison.Ordinal));
        if (index < 0) return false;

        SelectNav(index, animate: false);
        return true;
    }

    /// <summary>滚到页面底部（仅供快照，用于核对首屏之外的内容）。</summary>
    internal void ScrollToEndForCapture() => ContentScroll.ScrollToEnd();

    // ================= 页面渲染 =================

    private void RenderPage(int index, bool animate)
    {
        ContentPanel.Children.Clear();
        ContentScroll.ScrollToTop();

        var entry = _entries[index];

        ContentPanel.Children.Add(new TextBlock
        {
            Text = entry.Title,
            FontSize = 22,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Margin = new Thickness(0, 0, 0, 18),
        });

        switch (entry.Special)
        {
            case "schedule":
                RenderScheduleHubPage();
                break;
            case "homework":
                RenderHomeworkPage();
                break;
            case "plugins":
                RenderPluginsPage();
                break;
            case "advanced":
                RenderAdvancedPage();
                break;
            case "about":
                RenderAboutPage();
                break;
            default:
                if (entry.Special.StartsWith("plugin:", StringComparison.Ordinal))
                    RenderPluginSettingsPage(entry.Special);
                else
                    foreach (var card in entry.Cards) ContentPanel.Children.Add(BuildCard(card));
                break;
        }

        if (animate) PlayPageEntrance();
    }

    /// <summary>
    /// 页面入场动画：整体淡入并上浮，逐行错峰出现。
    /// 时长与开关读取「外观 → 淡入淡出」的设置。
    /// </summary>
    private void PlayPageEntrance()
    {
        var settings = _settings.Current;
        if (!settings.AnimFadePanel) return;

        var duration = Math.Max(50, Math.Min(2000, settings.AnimDuration <= 0 ? 320 : settings.AnimDuration));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        for (var i = 0; i < ContentPanel.Children.Count; i++)
        {
            if (ContentPanel.Children[i] is not UIElement element) continue;

            // 错峰数量封顶，避免长列表末尾等待过久
            var delay = TimeSpan.FromMilliseconds(Math.Min(i, 14) * RowStaggerMs);

            element.Opacity = 0;

            var translate = new TranslateTransform(0, 12);
            element.RenderTransform = translate;

            element.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(duration)) { BeginTime = delay, EasingFunction = ease });

            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0,
                TimeSpan.FromMilliseconds(duration)) { BeginTime = delay, EasingFunction = ease });
        }
    }

    // ================= 卡片 =================

    /// <summary>主题相关的卡片配色（对应源项目 v3Card 的卡片底与发丝描边）。</summary>
    private static Brush CardBackgroundBrush => ThemeBridge.IsDark
        ? new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B))
        : Brushes.White;

    private static Brush CardHairlineBrush => ThemeBridge.IsDark
        ? new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF))
        : new SolidColorBrush(Color.FromArgb(0x0D, 0x00, 0x00, 0x00));

    /// <summary>渲染一张卡片：标题 + 可选副标题 + 若干设置行。</summary>
    private FrameworkElement BuildCard(Card card)
    {
        var body = new StackPanel();

        if (card.Title.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = card.Title,
                FontSize = 15,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
            });
        }

        if (card.Subtitle.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = card.Subtitle,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        if (card.Fields.Length > 0)
        {
            var rows = new StackPanel { Margin = new Thickness(0, card.Title.Length > 0 ? 6 : 0, 0, 0) };

            foreach (var field in card.Fields)
            {
                var row = BuildRow(field);
                if (row is not null) rows.Children.Add(row);
            }

            body.Children.Add(rows);
        }

        return new Border
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardHairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 15, 18, 15),
            Margin = new Thickness(0, 0, 0, 14),
            Child = body,
        };
    }

    /// <summary>设置行：左侧「标签 + 提示」，右侧控件。</summary>
    private FrameworkElement? BuildRow(Field field)
    {
        // 纯说明行：既没有配置键，也没有读写委托（例如 Win10 上的云母说明）。
        // 注意不能只看 Key 是否为空 —— 「提前提醒」这类落在服务上而不是
        // AppSettings 上的设置项同样没有 Key，但它们是需要控件的。
        if (field.Key.Length == 0 && field.Read is null && field.Write is null)
        {
            return new TextBlock
            {
                Text = field.Label,
                FontSize = 12,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            };
        }

        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelPanel.Children.Add(new TextBlock { Text = field.Label, TextWrapping = TextWrapping.Wrap });

        if (!string.IsNullOrWhiteSpace(field.Hint))
        {
            labelPanel.Children.Add(new TextBlock
            {
                Text = field.Hint,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        row.Children.Add(labelPanel);

        var control = BuildControl(field);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);

        return row;
    }

    /// <summary>按控件类型生成右侧控件。</summary>
    private FrameworkElement BuildControl(Field field)
    {
        switch (field.Kind)
        {
            case FieldKind.Toggle:
            {
                var check = new CheckBox
                {
                    IsChecked = ReadValue(field) is true,
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 44,
                };
                check.Checked += (_, _) => WriteValue(field, true);
                check.Unchecked += (_, _) => WriteValue(field, false);
                return check;
            }

            case FieldKind.Slider:
            {
                var stored = ToDouble(ReadValue(field), field.Min * field.Scale);
                var display = field.Scale == 0 ? stored : stored / field.Scale;

                var host = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var valueText = new TextBlock
                {
                    Text = FormatDisplay(display, field),
                    Width = 56,
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };

                var slider = new Slider
                {
                    Minimum = field.Min,
                    Maximum = field.Max,
                    Width = 200,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsSnapToTickEnabled = false,
                    Value = Math.Clamp(display, field.Min, field.Max),
                };

                slider.ValueChanged += (_, e) =>
                {
                    valueText.Text = FormatDisplay(e.NewValue, field);
                    WriteValue(field, e.NewValue * field.Scale);
                };

                host.Children.Add(slider);
                host.Children.Add(valueText);
                return host;
            }

            case FieldKind.Spin:
            {
                var host = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                // 控件自带范围（源项目 QSpinBox 的 setRange 同理）：
                // 配置里若有越界值，显示时夹回并把夹取后的值写回去，
                // 避免「界面显示一个数、程序内部按另一个数在跑」
                var stored = ToDouble(ReadValue(field), field.Min);
                var initial = Math.Clamp(stored, field.Min, field.Max);
                if (initial != stored) WriteValue(field, initial);

                var box = new TextBox
                {
                    Text = initial.ToString("0"),
                    Width = 96,
                    Padding = new Thickness(6, 4, 6, 4),
                    TextAlignment = TextAlignment.Right,
                };

                void Commit()
                {
                    if (!double.TryParse(box.Text.Trim(), out var parsed))
                    {
                        box.Text = ToDouble(ReadValue(field), field.Min).ToString("0");
                        return;
                    }

                    var clamped = Math.Clamp(parsed, field.Min, field.Max);
                    box.Text = clamped.ToString("0");
                    WriteValue(field, clamped);
                }

                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter) return;

                    Commit();
                    Keyboard.ClearFocus();
                };

                host.Children.Add(box);

                if (field.Unit.Length > 0)
                {
                    host.Children.Add(new TextBlock
                    {
                        Text = field.Unit.Trim(),
                        Opacity = 0.6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(8, 0, 0, 0),
                    });
                }

                return host;
            }

            case FieldKind.Segmented:
            {
                var current = ReadValue(field)?.ToString() ?? "";
                var host = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var buttons = new List<(Choice Choice, Border Host, TextBlock Text)>();
                var (_, _, accent, normalText) = ThemeBridge.NavigationPalette;

                foreach (var choice in field.Choices ?? Array.Empty<Choice>())
                {
                    var text = new TextBlock
                    {
                        Text = choice.Label,
                        Padding = new Thickness(14, 5, 14, 5),
                        FontSize = 13,
                    };

                    var border = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        Margin = new Thickness(0, 0, 6, 0),
                        Cursor = Cursors.Hand,
                        Child = text,
                    };

                    var captured = choice;
                    border.MouseLeftButtonUp += (_, _) =>
                    {
                        WriteValue(field, captured.Value);
                        Paint(captured.Value);
                    };

                    buttons.Add((choice, border, text));
                    host.Children.Add(border);
                }

                void Paint(string selected)
                {
                    foreach (var (choice, border, text) in buttons)
                    {
                        var isSelected = string.Equals(choice.Value, selected, StringComparison.Ordinal);

                        border.Background = isSelected
                            ? new SolidColorBrush(accent) { Opacity = 0.18 }
                            : Brushes.Transparent;

                        border.BorderBrush = isSelected
                            ? new SolidColorBrush(accent)
                            : new SolidColorBrush(ThemeBridge.IsDark
                                ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
                                : Color.FromArgb(0x1A, 0x00, 0x00, 0x00));

                        border.BorderThickness = new Thickness(1);
                        text.Foreground = isSelected
                            ? new SolidColorBrush(accent)
                            : new SolidColorBrush(normalText);

                        text.FontWeight = isSelected
                            ? FontWeight.FromOpenTypeWeight(600)
                            : FontWeights.Normal;
                    }
                }

                Paint(current);
                return host;
            }

            default:
            {
                var box = new TextBox
                {
                    Text = ReadValue(field)?.ToString() ?? "",
                    MinWidth = 220,
                    Padding = new Thickness(6, 4, 6, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };

                box.LostFocus += (_, _) => WriteValue(field, box.Text.Trim());
                return box;
            }
        }
    }

    private static string FormatDisplay(double display, Field field)
    {
        var text = field.Step < 1
            ? display.ToString("0.#")
            : Math.Round(display).ToString("0");

        return field.Unit.Length > 0 ? text + field.Unit : text;
    }

    private static double ToDouble(object? value, double fallback)
        => value switch
        {
            null => fallback,
            double d => d,
            int i => i,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => fallback,
        };

    // ================= 设置读写 =================

    private static readonly Dictionary<string, PropertyInfo> PropertyMap = BuildPropertyMap();

    private static Dictionary<string, PropertyInfo> BuildPropertyMap()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in typeof(AppSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;

            var key = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            map[key] = property;
        }

        return map;
    }

    private object? ReadValue(Field field)
    {
        if (field.Read is not null) return field.Read();
        if (field.Key.Length == 0) return null;

        return PropertyMap.TryGetValue(field.Key, out var property)
            ? property.GetValue(_settings.Current)
            : null;
    }

    private void WriteValue(Field field, object value)
    {
        if (field.Write is not null)
        {
            field.Write(value);
            return;
        }

        if (field.Key.Length == 0) return;
        if (!PropertyMap.TryGetValue(field.Key, out var property)) return;

        try
        {
            if (property.PropertyType == typeof(List<string>))
            {
                var items = value.ToString()!
                    .Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

                _settings.Update(settings => property.SetValue(settings, items));
                return;
            }

            var converted = Convert.ChangeType(value, property.PropertyType);
            _settings.Update(settings => property.SetValue(settings, converted));
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            // 类型不符时忽略本次修改，保持原值
        }
    }

    // ================= 搜索 =================

    /// <summary>搜索索引里的一项：展示文案 → 该设置所在的页面与行。</summary>
    private sealed record SearchEntry(string Text, int PageIndex, string Path, Field? Field);

    private void BuildSearchIndex()
    {
        _searchIndex.Clear();

        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];

            _searchIndex.Add(new SearchEntry(entry.Title.ToLowerInvariant(), i, entry.Title, null));

            foreach (var card in entry.Cards)
            {
                foreach (var field in card.Fields)
                {
                    if (field.Key.Length == 0) continue;

                    var text = $"{field.Label} {field.Hint}".Trim().ToLowerInvariant();
                    _searchIndex.Add(new SearchEntry(text, i, $"{entry.Title} · {card.Title}", field));
                }
            }
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearchPlaceholder();
        if (_suppressSearch) return;

        var query = SearchBox.Text.Trim();

        if (query.Length == 0)
        {
            if (_indexBeforeSearch >= 0)
            {
                var restore = _indexBeforeSearch;
                _indexBeforeSearch = -1;
                SelectNav(restore, animate: false);
            }

            return;
        }

        if (_indexBeforeSearch < 0) _indexBeforeSearch = _selectedIndex;
        RenderSearchResults(query);
    }

    private void UpdateSearchPlaceholder()
        => SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 && !SearchBox.IsKeyboardFocused
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void OnSearchFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
        => UpdateSearchPlaceholder();

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearSearch();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter) return;

        // 按 Enter 打开第一项（对应源项目结果页的提示文案）
        var matches = Match(SearchBox.Text.Trim());
        if (matches.Count == 0) return;

        OpenResult(matches[0]);
        e.Handled = true;
    }

    private List<SearchEntry> Match(string query)
    {
        var lowered = query.ToLowerInvariant();

        return _searchIndex
            .Where(entry => entry.Text.Contains(lowered, StringComparison.Ordinal))
            .OrderBy(entry => entry.Field is null ? 0 : 1)
            .ThenBy(entry => entry.Path, StringComparer.Ordinal)
            .Take(80)
            .ToList();
    }

    private void OpenResult(SearchEntry entry)
    {
        SelectNav(entry.PageIndex, animate: false);
        _indexBeforeSearch = entry.PageIndex;   // 清空搜索后停在结果所在页
    }

    private void ClearSearch()
    {
        _suppressSearch = true;
        SearchBox.Clear();
        _suppressSearch = false;

        UpdateSearchPlaceholder();

        if (_indexBeforeSearch >= 0)
        {
            var restore = _indexBeforeSearch;
            _indexBeforeSearch = -1;
            SelectNav(restore, animate: false);
        }
    }

    private void RenderSearchResults(string query)
    {
        ContentPanel.Children.Clear();
        ContentScroll.ScrollToTop();

        var matches = Match(query);

        ContentPanel.Children.Add(new TextBlock
        {
            Text = $"搜索结果（{matches.Count}）",
            FontSize = 22,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Margin = new Thickness(0, 0, 0, 18),
        });

        if (matches.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"没有找到与「{query}」相关的设置。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });

            return;
        }

        foreach (var entry in matches)
        {
            AddLinkRow(
                entry.Field?.Label ?? entry.Path,
                entry.Field is null ? "打开此页面" : entry.Path,
                () => OpenResult(entry));
        }

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "按 Enter 打开第一项，Esc 返回",
            FontSize = 11.5,
            Opacity = 0.6,
            Margin = new Thickness(0, 14, 0, 0),
        });
    }

    // ================= 链接行 / 链接卡 =================

    /// <summary>
    /// 可点击的操作行（对应源项目 LinkRow）。
    /// card 为 true 时是「独立大卡」样式（二级页索引用）：
    /// 卡片底 + 发丝描边 + 左侧彩色图标 + 右侧 ›。
    /// </summary>
    private void AddLinkRow(string title, string hint, Action onClick, bool card = false,
        string glyph = "", Color? tint = null, bool danger = false)
        => ContentPanel.Children.Add(BuildLinkRow(title, hint, onClick, card, glyph, tint, danger));

    /// <summary>构造一个链接行 / 链接卡但不加入页面（供放进卡片内部）。</summary>
    private FrameworkElement BuildLinkRow(string title, string hint, Action onClick, bool card = false,
        string glyph = "", Color? tint = null, bool danger = false)
    {
        var (_, _, accent, normalText) = ThemeBridge.NavigationPalette;

        var iconBrush = new SolidColorBrush(
            tint ?? (danger ? Color.FromRgb(0xC4, 0x2B, 0x1C) : accent));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (glyph.Length > 0)
        {
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = card ? 20 : 16,
                Width = card ? 24 : 20,
                TextAlignment = TextAlignment.Center,
                Foreground = iconBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = card ? new Thickness(0, 0, 16, 0) : new Thickness(0, 0, 12, 0),
            };

            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap,
            Foreground = danger ? iconBrush : new SolidColorBrush(normalText),
        });

        if (hint.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = hint,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var chevron = new TextBlock
        {
            Text = "›",
            FontSize = 16,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        };

        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var normalBackground = card ? CardBackgroundBrush : (Brush)new SolidColorBrush(Colors.Transparent);

        var border = new Border
        {
            Background = normalBackground,
            BorderBrush = card ? CardHairlineBrush : Brushes.Transparent,
            BorderThickness = card ? new Thickness(1) : new Thickness(0),
            CornerRadius = new CornerRadius(card ? 8 : 6),
            Padding = card ? new Thickness(18, 15, 16, 15) : new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, card ? 0 : 1, 0, card ? 4 : 1),
            Cursor = Cursors.Hand,
            Child = grid,
        };

        var hover = card
            ? new SolidColorBrush(ThemeBridge.IsDark
                ? Color.FromRgb(0x33, 0x33, 0x33)
                : Color.FromRgb(0xF7, 0xF7, 0xF7))
            : new SolidColorBrush(ThemeBridge.IsDark
                ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0x0A, 0x00, 0x00, 0x00));

        border.MouseEnter += (_, _) => border.Background = hover;
        border.MouseLeave += (_, _) => border.Background = normalBackground;
        border.MouseLeftButtonUp += (_, _) => onClick();

        return border;
    }

    /// <summary>分组小标题（对应源项目 _add_section_label）。</summary>
    private void AddSectionLabel(string text, double top = 18)
    {
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13.5,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Opacity = 0.85,
            Margin = new Thickness(0, top, 0, 8),
        });
    }

    // ================= 插件设置页 =================

    private void RenderPluginSettingsPage(string key)
    {
        // key 形如 plugin:<pluginId>:<pageTitle>
        var parts = key.Split(':', 3);
        if (parts.Length < 3) return;

        var pluginId = parts[1];
        var title = parts[2];

        var descriptor = _plugins.Apis
            .Where(a => string.Equals(a.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(a => a.SettingsPages)
            .FirstOrDefault(p => string.Equals(p.Title, title, StringComparison.Ordinal));

        if (descriptor is null)
        {
            ContentPanel.Children.Add(new TextBlock { Text = "该插件设置页已不可用。", Opacity = 0.7 });
            return;
        }

        try
        {
            var content = descriptor.Factory(descriptor.Owner);
            if (content is UIElement element) ContentPanel.Children.Add(element);
        }
        catch (Exception ex)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = $"插件设置页加载失败：{ex.Message}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
        }
    }

    // ================= 动画工具 =================

    private static void AnimateBrush(SolidColorBrush brush, Color target, int durationMs)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null);

        if (durationMs <= 0)
        {
            brush.Color = target;
            return;
        }

        brush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(target, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void AnimateOpacity(UIElement element, double target, int durationMs)
    {
        element.BeginAnimation(OpacityProperty, null);

        if (durationMs <= 0)
        {
            element.Opacity = target;
            return;
        }

        element.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private static void AnimateHeight(FrameworkElement element, double target, int durationMs)
    {
        element.BeginAnimation(HeightProperty, null);

        if (durationMs <= 0)
        {
            element.Height = target;
            return;
        }

        element.BeginAnimation(
            HeightProperty,
            new DoubleAnimation(target, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ================= 通用小工具 =================

    private static void OpenInExplorer(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "Class Daily Land",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
