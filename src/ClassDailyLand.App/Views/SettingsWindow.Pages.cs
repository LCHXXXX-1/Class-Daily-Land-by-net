using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Market;
using ClassDailyLand.Infrastructure.Updates;
using Microsoft.Win32;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心里几个有专属布局的页面。
/// 逐项对应源项目 settings_dialog_v4.py 的
/// _page_schedule_hub / _page_homework / _page_advanced / _page_about。
/// </summary>
public partial class SettingsWindow
{
    // ================= 课表与提醒 =================

    /// <summary>
    /// 课表与提醒 Hub 页（对应 _page_schedule_hub）。
    ///
    /// 一张「上课提醒」卡 + 一组二级页索引大卡。
    /// 注意：源项目这一页**没有**今日课表预览，也不列课表套数等统计 ——
    /// 那些是本工程前几轮自创的，已移除。课表原始时间只在课表编辑器里出现，
    /// 叠加时间偏移后的时刻只在灵动岛下拉面板里出现（源项目 offset_time_str
    /// 的唯一调用点），两处本就不该相同。
    /// </summary>
    private void RenderScheduleHubPage()
    {
        ContentPanel.Children.Add(BuildCard(Card.Of("上课提醒",
            new Field("island_countdown_sec", "倒计时时长", FieldKind.Spin, 5, 600,
                Unit: " 秒", Hint: "上课前显示蓝色倒计时"),
            new Field("", "提前提醒", FieldKind.Spin, 0, 60,
                Unit: " 分钟", Hint: "整体提前几分钟进上课状态（下课时间不变）",
                Read: () => _schedule.AdvanceMinutes,
                Write: v =>
                {
                    _schedule.AdvanceMinutes = (int)ToDouble(v, 2);
                    _schedule.Save();
                }),
            new Field("", "时间偏移", FieldKind.Spin, -1800, 1800,
                Unit: " 秒", Hint: "负数为提前，正数为延后",
                Read: () => _schedule.TimeOffsetSeconds,
                Write: v =>
                {
                    _schedule.TimeOffsetSeconds = (int)ToDouble(v, 0);
                    _schedule.Save();
                }))));

        AddSectionLabel("课表与值日", top: 6);

        AddLinkRow("课表管理", "切换、备份与导入导出课表", OpenTimetableManager,
            card: true, glyph: "\uE8FD", tint: Color.FromRgb(0x10, 0x7C, 0x10));

        AddLinkRow("周末作息", "单独设置周末上课时间", OpenWeekendSchedule,
            card: true, glyph: "\uE823", tint: Color.FromRgb(0x86, 0x61, 0xC5));

        AddLinkRow("假期与调休", "设置假期与调休日", OpenHolidaysDialog,
            card: true, glyph: "\uE706", tint: Color.FromRgb(0xF7, 0x63, 0x0C));

        AddLinkRow("值日生名单", "设置值日轮流安排", OpenDutyEditor,
            card: true, glyph: "\uE716", tint: Color.FromRgb(0x03, 0x83, 0x87));

        AddLinkRow("状态测试", "完整演示一节课的状态变化", OpenStatusTest,
            card: true, glyph: "\uE768", tint: Color.FromRgb(0x5B, 0x5F, 0xC7));
    }

    private void OpenTimetableManager()
    {
        new TimetableManagerDialog(_schedule) { Owner = this }.ShowDialog();
        RebuildCurrentPage();
    }

    private void OpenWeekendSchedule()
    {
        new WeekendDialog(_schedule) { Owner = this }.ShowDialog();
        _controller.NotifyIslandDirty();
    }

    private void OpenHolidaysDialog()
    {
        new HolidayDialog(_schedule) { Owner = this }.ShowDialog();
        _controller.NotifyIslandDirty();
    }

    private void OpenDutyEditor()
        => new DutyEditorDialog(_duty) { Owner = this }.ShowDialog();

    private void OpenStatusTest()
        => new StatusTestDialog(_schedule, _settings, _controller) { Owner = this }.ShowDialog();

    /// <summary>重画当前页（数据被对话框改过之后）。</summary>
    private void RebuildCurrentPage()
    {
        if (_selectedIndex < 0) return;

        // SelectNav 对同一索引会直接返回，所以先清掉选中态再重画
        var index = _selectedIndex;
        _selectedIndex = -1;
        SelectNav(index, animate: false);
    }

    // ================= 作业 =================

    /// <summary>
    /// 作业页：把作业编辑器整块内嵌进来（对应 _page_homework 的 _embed_dialog）。
    /// 源项目会隐藏「取消 / 确认」按钮，因为编辑是即时的。
    /// </summary>
    private void RenderHomeworkPage()
    {
        var panel = new HomeworkEditorPanel(_homework, () => this);
        panel.Save();

        ContentPanel.Children.Add(new Border
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardHairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 15, 18, 15),
            Child = panel,
        });
    }

    // ================= 插件 =================

    /// <summary>
    /// 插件 Hub 页（对应 _page_plugins_hub）。
    ///
    /// 源项目把「插件市场 / 第三方包管理」做成二级页，用索引大卡进入；
    /// 本工程的导航是平铺的，因此改为在同一页按源项目的分区标题依次展开，
    /// 标签与分区保持与源项目一致。
    /// </summary>
    private void RenderPluginsPage()
    {
        AddSectionLabel("插件中心", top: 0);

        RenderMarketSection();
        RenderPackagesSection();

        AddSectionLabel("已安装的插件", top: 6);
        RenderInstalledPlugins();
    }

    private void RenderInstalledPlugins()
    {
        var pluginRoot = _paths.PluginsDirectory;
        var disabled = new HashSet<string>(_settings.Current.DisabledPlugins, StringComparer.OrdinalIgnoreCase);

        var directories = Directory.Exists(pluginRoot)
            ? Directory.EnumerateDirectories(pluginRoot)
                .Where(d => !string.Equals(Path.GetFileName(d), "packages", StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        if (directories.Count == 0)
        {
            ContentPanel.Children.Add(new TextBlock
            {
                Text = "还没有安装任何插件。把插件文件夹放进 plugins/ 目录即可。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            });
        }

        foreach (var directory in directories)
        {
            var id = Path.GetFileName(directory);
            var loaded = _pluginManager.Plugins.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

            var row = new Grid { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = loaded?.Manifest.Name is { Length: > 0 } n ? n : id,
            });

            var detail = loaded is null
                ? "未加载（可能缺少 plugin.json 或入口程序集）"
                : $"{id} · {loaded.Manifest.Version}".TrimEnd(' ', '·');

            if (loaded is not null && loaded.Manifest.Requires.Count > 0)
                detail += $" · 依赖 {string.Join(", ", loaded.Manifest.Requires)}";

            info.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });

            row.Children.Add(info);

            var toggle = new CheckBox
            {
                IsChecked = !disabled.Contains(id),
                VerticalAlignment = VerticalAlignment.Center,
                Content = "启用",
                MinWidth = 64,
            };

            var capturedId = id;
            toggle.Checked += (_, _) => SetPluginEnabled(capturedId, true);
            toggle.Unchecked += (_, _) => SetPluginEnabled(capturedId, false);

            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);

            ContentPanel.Children.Add(row);
        }

        if (_pluginManager.Failures.Count > 0)
        {
            AddSectionLabel("加载失败", top: 14);

            foreach (var failure in _pluginManager.Failures)
            {
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = $"{failure.Id}：{failure.Reason}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B)),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12.5,
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
        }

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "启用状态变更后需要重启程序才会生效。",
            Opacity = 0.6,
            FontSize = 12,
            Margin = new Thickness(0, 12, 0, 0),
        });
    }

    private void SetPluginEnabled(string pluginId, bool enabled)
    {
        _settings.Update(settings =>
        {
            var list = new List<string>(settings.DisabledPlugins);

            if (enabled) list.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));
            else if (!list.Contains(pluginId, StringComparer.OrdinalIgnoreCase)) list.Add(pluginId);

            settings.DisabledPlugins = list;
        });
    }

    // ================= 高级 =================

    /// <summary>高级页（对应 _page_advanced）。</summary>
    private void RenderAdvancedPage()
    {
        var localPlugins = new StackPanel();
        localPlugins.Children.Add(new TextBlock
        {
            Text = LocalPluginsText(),
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        localPlugins.Children.Add(BuildLinkRow("安装本地插件…", "从 .cblplugin 文件安装",
            ImportLocalPlugin, glyph: "\uE7B8"));

        localPlugins.Children.Add(BuildLinkRow("打开插件文件夹", "在文件资源管理器中打开",
            () => OpenInExplorer(_paths.PluginsDirectory), glyph: "\uE90F"));

        ContentPanel.Children.Add(WrapInCard("本地插件", "程序启动时会加载这些插件", localPlugins));

        var maintenance = new StackPanel();

        maintenance.Children.Add(BuildLinkRow("打开配置文件夹", "查看 settings.json 等配置文件",
            () => OpenInExplorer(_paths.ConfigDirectory), glyph: "\uE713"));

        maintenance.Children.Add(BuildLinkRow("恢复全部默认", "将所有设置恢复为默认值",
            ResetAllDefaults, glyph: "\uE7A7", danger: true));

        ContentPanel.Children.Add(WrapInCard("日常维护", "", maintenance));

        ContentPanel.Children.Add(new TextBlock
        {
            Text = $"配置文件位置：{_paths.ConfigDirectory}",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });
    }

    /// <summary>把一段自定义内容包进带标题的卡片。</summary>
    private FrameworkElement WrapInCard(string title, string subtitle, UIElement content)
    {
        var body = new StackPanel();

        if (title.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeight.FromOpenTypeWeight(600),
            });
        }

        if (subtitle.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Opacity = 0.6,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        body.Children.Add(content);

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

    /// <summary>本地插件清单文案（对应 _local_plugins_text）。</summary>
    private string LocalPluginsText()
    {
        var lines = new List<string>();

        foreach (var plugin in _pluginManager.Plugins)
            lines.Add($"✓  {plugin.Id}    OK");

        foreach (var failure in _pluginManager.Failures)
            lines.Add($"✗  {failure.Id}    {failure.Reason}");

        if (lines.Count == 0) return "（还没有插件）";

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>安装本地插件包（对应 _import_plugin）。</summary>
    private void ImportLocalPlugin()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择插件文件",
            Filter = "Class Daily Land 插件 (*.cblplugin)|*.cblplugin|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        var target = Path.Combine(_paths.PluginsDirectory, Path.GetFileName(dialog.FileName));

        try
        {
            Directory.CreateDirectory(_paths.PluginsDirectory);
            File.Copy(dialog.FileName, target, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制文件失败：{ex.Message}", "安装失败",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(this, $"{Path.GetFileName(dialog.FileName)} 已安装，重启后生效。",
            "安装完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// 恢复全部默认（对应 _reset_all）。
    /// 只重置设置页里暴露出来的那些项 —— 与源项目逐行 load_default 的语义一致，
    /// 不碰插件市场源等没有对应设置行的字段。
    /// </summary>
    private void ResetAllDefaults()
    {
        var answer = MessageBox.Show(this,
            "确定要将所有设置恢复为默认值吗？",
            "恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        var keys = _entries
            .SelectMany(e => e.Cards)
            .SelectMany(c => c.Fields)
            .Where(f => f.Key.Length > 0 && f.Read is null && f.Write is null)
            .Select(f => f.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var defaults = new AppSettings();

        _settings.Update(settings =>
        {
            foreach (var key in keys)
            {
                if (!PropertyMap.TryGetValue(key, out var property)) continue;
                property.SetValue(settings, property.GetValue(defaults));
            }
        });

        // 服务侧的两个值（提前提醒 / 时间偏移）按其默认回位
        var defaultSchedule = new ScheduleDocument();
        _schedule.AdvanceMinutes = defaultSchedule.AdvanceMinutes;
        _schedule.TimeOffsetSeconds = defaultSchedule.TimeOffsetSeconds;
        _schedule.Save();

        MessageBox.Show(this, "所有设置已恢复默认。", "恢复默认",
            MessageBoxButton.OK, MessageBoxImage.Information);

        RebuildCurrentPage();
    }

    // ================= 关于 =================

    /// <summary>关于页（对应 _page_about）。</summary>
    private void RenderAboutPage()
    {
        var body = new StackPanel();

        // ---- 头部：图标 + 名称 + 版本 ----
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };

        var logo = new Image
        {
            Source = Controls.AppIconFactory.CreateImageSource(),
            Width = 56,
            Height = 56,
            Margin = new Thickness(0, 0, 14, 0),
        };

        head.Children.Add(logo);

        var titleColumn = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleColumn.Children.Add(new TextBlock
        {
            Text = AppDisplayName,
            FontSize = 20,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });
        titleColumn.Children.Add(new TextBlock
        {
            Text = $"版本 {ResolveAppVersion()}",
            FontSize = 12,
            Opacity = 0.7,
            Margin = new Thickness(0, 3, 0, 0),
        });

        head.Children.Add(titleColumn);
        body.Children.Add(head);

        // ---- 固定信息 ----
        foreach (var line in new[]
                 {
                     "一个轻量级的班级日常管理小工具",
                     "作者：LCHXXXX、hexwisp72",
                     "反馈：2352240265@qq.com",
                     $"系统：{Win32.WindowsVersion.DisplayName}",
                     $"窗口材质：{(_settings.Current.SettingsMica && Win32.WindowEffects.SupportsMica ? "Windows 11 云母（Mica）" : "普通")}",
                 })
        {
            body.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 1),
            });
        }

        // ---- 源码运行保护状态：一眼看出当前为什么没自动更新 ----
        var (allowed, hits) = _update.EvaluateSourceGuard();
        if (!allowed && hits.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = "源码运行 · 自动更新已跳过（更新前会先提醒备份）",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // ---- 检查更新 ----
        var updateButton = new Button
        {
            Content = "检查更新",
            MinWidth = 110,
            Padding = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 12, 0, 2),
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        };

        updateButton.Click += (_, _) => CheckForUpdates();
        body.Children.Add(updateButton);

        ContentPanel.Children.Add(new Border
        {
            Background = CardBackgroundBrush,
            BorderBrush = CardHairlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 15, 18, 15),
            Child = body,
        });
    }

    private const string AppDisplayName = "Class Daily Land";

    /// <summary>
    /// 版本号：优先读程序目录下的 config.json（源项目的清单），
    /// 读不到再退回程序集版本，保证发布版也有值可显示。
    /// </summary>
    private string ResolveAppVersion()
    {
        try
        {
            var path = Path.Combine(_paths.AppRoot, "config.json");
            if (File.Exists(path))
            {
                var manifest = JsonSerializer.Deserialize<AppManifest>(File.ReadAllText(path));
                if (!string.IsNullOrWhiteSpace(manifest?.Version)) return manifest!.Version;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // 清单不可读时退回程序集版本
        }

        var assembly = Assembly.GetEntryAssembly() ?? typeof(SettingsWindow).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+')[0];

        return assembly.GetName().Version?.ToString() ?? "未知";
    }

    /// <summary>
    /// 手动检查更新。检测到本地源码时先弹窗提醒备份并请求确认（对应 launch_visible）。
    /// </summary>
    private void CheckForUpdates()
    {
        var (allowed, hits) = _update.EvaluateSourceGuard();

        if (!allowed)
        {
            var choice = MessageBox.Show(this,
                "程序目录下检测到未打包的主入口源码，继续更新会用发布版覆盖这些"
                + "源文件，导致你本地的改动丢失。\n\n"
                + UpdateLauncher.BackupTip(hits),
                "检测到本地源码，未执行更新",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (choice != MessageBoxResult.OK) return;   // 取消 / 关窗 → 一律不拉起
        }

        var result = _update.LaunchVisible(force: true);

        if (result.Spawned)
        {
            MessageBox.Show(this,
                "更新器已启动。它会在本程序退出后替换文件，请让程序保持关闭直到替换完成。",
                "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        MessageBox.Show(this,
            result.Message.Length > 0 ? result.Message : "未能启动更新器。",
            "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
