using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;
using ClassDailyLand.App.Services;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Infrastructure.Schedule;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>课表类对话框的公共构件。</summary>
internal static class ScheduleDialogParts
{
    public static readonly string[] DayHeaders = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    /// <summary>分组卡片（对应源项目的 SectionCard）。</summary>
    public static Border Card(string title, string subtitle, out StackPanel body)
    {
        body = new StackPanel();

        var inner = new StackPanel();
        inner.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });

        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            inner.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        body.Margin = new Thickness(0, 10, 0, 0);
        inner.Children.Add(body);

        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = inner,
        };

        // 主题资源不存在时保持无边框，不影响使用
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    public static TextBox Input(string value = "", double width = double.NaN, string placeholder = "")
    {
        var box = new TextBox
        {
            Text = value,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 6, 0),
        };

        if (!double.IsNaN(width)) box.Width = width;
        if (!string.IsNullOrEmpty(placeholder)) box.ToolTip = placeholder;

        return box;
    }

    public static Button Action(string text, Action onClick, double minWidth = 0, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };

        if (minWidth > 0) button.MinWidth = minWidth;
        if (primary) button.FontWeight = FontWeight.FromOpenTypeWeight(600);

        button.Click += (_, _) => onClick();
        return button;
    }

    public static bool IsValidDate(string text)
        => DateOnly.TryParse(text, CultureInfo.InvariantCulture, out _);
}

/// <summary>
/// 课表管理。对应源模块 menu.py 的 TimetableManagerDialog。
/// 支持多课表的新建 / 复制 / 重命名 / 删除 / 设为当前 / 编辑，以及导入导出与恢复默认。
/// </summary>
internal sealed class TimetableManagerDialog : Window
{
    private readonly IScheduleService _schedule;
    private readonly ListBox _list;
    private readonly TextBox _nameBox;

    public TimetableManagerDialog(IScheduleService schedule)
    {
        _schedule = schedule;

        Title = "课表管理";
        Width = 620;
        Height = 620;
        MinWidth = 520;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "课表管理",
            FontSize = 20,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });

        root.Children.Add(new TextBlock
        {
            Text = "双击课表名可快速切换为当前课表",
            FontSize = 11.5,
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 12),
        });

        _list = new ListBox { Height = 180 };
        _list.MouseDoubleClick += (_, _) => SwitchToSelected();
        root.Children.Add(_list);

        var inputRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };
        inputRow.Children.Add(new TextBlock
        {
            Text = "课表名称：",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        _nameBox = ScheduleDialogParts.Input(width: 200);
        inputRow.Children.Add(_nameBox);

        inputRow.Children.Add(ScheduleDialogParts.Action("新建", CreateNew));
        inputRow.Children.Add(ScheduleDialogParts.Action("复制", CopySelected));
        inputRow.Children.Add(ScheduleDialogParts.Action("重命名", RenameSelected));
        root.Children.Add(inputRow);

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };
        actionRow.Children.Add(ScheduleDialogParts.Action("编辑选中的课表", EditSelected, primary: true));
        actionRow.Children.Add(ScheduleDialogParts.Action("设为当前", SwitchToSelected));
        actionRow.Children.Add(ScheduleDialogParts.Action("删除", DeleteSelected));
        root.Children.Add(actionRow);

        var ioRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };
        ioRow.Children.Add(ScheduleDialogParts.Action("导出选中课表…", ExportSelected));
        ioRow.Children.Add(ScheduleDialogParts.Action("导出完整备份…", ExportAll));
        ioRow.Children.Add(ScheduleDialogParts.Action("导入课表…", Import));
        root.Children.Add(ioRow);

        root.Children.Add(new TextBlock
        {
            Text = "恢复默认课表会删除全部自定义课表，请谨慎操作。",
            FontSize = 11.5,
            Opacity = 0.6,
            Margin = new Thickness(0, 16, 0, 0),
        });

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        footer.Children.Add(ScheduleDialogParts.Action("恢复默认课表（将删除自定义课表）", RestoreDefault));
        footer.Children.Add(ScheduleDialogParts.Action("关闭", Close));
        root.Children.Add(footer);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };

        Refresh();
    }

    private string? SelectedName => _list.SelectedItem as string;

    private void Refresh()
    {
        var selected = SelectedName;

        _list.Items.Clear();
        foreach (var name in _schedule.ListTimetables())
        {
            var suffix = name == _schedule.ActiveName ? "（当前）" : "";
            _list.Items.Add(name + suffix);
        }

        // 列表项带「（当前）」后缀，回填时按前缀匹配
        if (selected is not null)
        {
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i] is string text && text.StartsWith(selected, StringComparison.Ordinal))
                {
                    _list.SelectedIndex = i;
                    break;
                }
            }
        }

        if (_list.SelectedIndex < 0 && _list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private string? ResolveSelected()
    {
        if (_list.SelectedItem is not string text) return null;
        return text.EndsWith("（当前）", StringComparison.Ordinal)
            ? text[..^"（当前）".Length]
            : text;
    }

    private void CreateNew()
    {
        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Warn("请输入课表名称。");
            return;
        }

        if (!_schedule.CreateTimetable(name)) Warn("同名课表已存在，或名称无效。");
        else _nameBox.Clear();

        Refresh();
    }

    private void CopySelected()
    {
        var source = ResolveSelected();
        if (source is null) return;

        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Warn("请先在「课表名称」里填一个名字，再点复制。");
            return;
        }

        if (!_schedule.CreateTimetable(name, source)) Warn("同名课表已存在，或名称无效。");
        else _nameBox.Clear();

        Refresh();
    }

    private void RenameSelected()
    {
        var source = ResolveSelected();
        if (source is null) return;

        var name = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Warn("请先在「课表名称」里填新的名字。");
            return;
        }

        if (!_schedule.RenameTimetable(source, name)) Warn("重命名失败：名称可能已存在。");
        else _nameBox.Clear();

        Refresh();
    }

    private void DeleteSelected()
    {
        var name = ResolveSelected();
        if (name is null) return;

        if (_schedule.ListTimetables().Count <= 1)
        {
            Warn("至少要保留一套课表。");
            return;
        }

        var confirm = MessageBox.Show(this, $"确定删除课表「{name}」吗？", "确认删除",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _schedule.DeleteTimetable(name);
        Refresh();
    }

    private void SwitchToSelected()
    {
        var name = ResolveSelected();
        if (name is null) return;

        _schedule.SwitchTimetable(name);
        Refresh();
    }

    private void EditSelected()
    {
        var name = ResolveSelected();
        if (name is null) return;

        var editor = new CourseEditorDialog(_schedule, name) { Owner = this };
        editor.ShowDialog();
        Refresh();
    }

    // ================= 导入导出 =================

    private void ExportSelected()
    {
        var name = ResolveSelected();
        if (name is null) return;

        Export(_schedule.ExportJson(new[] { name }), _schedule.ExportFileName(ExportFormat.Json, new[] { name }));
    }

    private void ExportAll()
    {
        Export(_schedule.ExportJson(_schedule.ListTimetables(), includeExtras: true),
            _schedule.ExportFileName(ExportFormat.Json, includeExtras: true));
    }

    private void Export(string content, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出课表",
            FileName = suggestedName,
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, content, new System.Text.UTF8Encoding(true));
            MessageBox.Show(this, "导出成功。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"导出失败：{ex.Message}");
        }
    }

    private void Import()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入课表",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        string content;
        try
        {
            content = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"读取失败：{ex.Message}");
            return;
        }

        var result = _schedule.ImportJson(content, "merge");
        MessageBox.Show(this, result.Message, "导入",
            MessageBoxButton.OK,
            result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Refresh();
    }

    private void RestoreDefault()
    {
        var confirm = MessageBox.Show(this,
            "恢复默认会删除全部自定义课表与假期设置，且无法撤销。\n\n确定继续吗？",
            "恢复默认课表", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _schedule.RestoreDefault();
        Refresh();
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>
/// 课表编辑器。对应源模块 menu.py 的 CourseEditor。
///
/// 列：节次 / 开始 / 结束 + 周一…周日（源项目没有独立的「时间」列，
/// 起止时间就是相邻两列，不要再加一列重复展示）。
/// 顶部是标题与用法提示，中间是「课程」卡片包住的表格，
/// 底部一行是行操作、导入导出、节数统计与取消 / 保存。
/// </summary>
internal sealed class CourseEditorDialog : Window
{
    private const double LabelColumnWidth = 90;
    private const double DayColumnWidth = 96;

    private readonly IScheduleService _schedule;
    private readonly string _timetableName;

    private readonly List<Period> _periods;
    private readonly Dictionary<string, List<string>> _timetable;

    private readonly StackPanel _gridHost;
    private readonly TextBlock _countLabel;
    private readonly TextBlock _countHint;

    private readonly List<Border> _rowHighlights = new();
    private int _selectedRow = -1;

    public CourseEditorDialog(IScheduleService schedule, string timetableName)
    {
        _schedule = schedule;
        _timetableName = timetableName;

        // 深拷贝，取消时不影响原数据
        var definition = schedule.Document.Timetables.TryGetValue(timetableName, out var found)
            ? found
            : new TimetableDefinition();

        _periods = definition.Periods.Select(p => p.Clone()).ToList();
        _timetable = definition.Timetable.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));

        // 保证七个星期几都有列表，长度与节次对齐
        foreach (var day in Enumerable.Range(0, 7))
        {
            var key = day.ToString();

            if (!_timetable.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _timetable[key] = list;
            }

            while (list.Count < _periods.Count) list.Add("");
        }

        Title = $"课表编辑器 - {timetableName}";
        Width = 940;
        Height = 640;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 18) };

        // ---------- 顶部：标题 + 用法提示 ----------
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "课表编辑器",
            FontSize = 22,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"课表：{timetableName}　·　双击格子，时间写成 HH:MM（比如 08:00）",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ---------- 最底部：节数过多的提醒 ----------
        _countHint = new TextBlock
        {
            FontSize = 11.5,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };

        DockPanel.SetDock(_countHint, Dock.Bottom);
        root.Children.Add(_countHint);

        // ---------- 按钮行 ----------
        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ScheduleDialogParts.Action("＋ 加一节", AddPeriod));
        actions.Children.Add(ScheduleDialogParts.Action("删除选中课节", RemovePeriod));

        var exportButton = ScheduleDialogParts.Action("导出课表…", () => ShowExportMenu(actions));
        actions.Children.Add(exportButton);
        actions.Children.Add(ScheduleDialogParts.Action("导入课表…", ImportFromFile));

        _countLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            FontSize = 12,
            Opacity = 0.7,
        };
        actions.Children.Add(_countLabel);

        Grid.SetColumn(actions, 0);
        footer.Children.Add(actions);

        var confirm = new StackPanel { Orientation = Orientation.Horizontal };
        confirm.Children.Add(ScheduleDialogParts.Action("取消", () => DialogResult = false));
        confirm.Children.Add(ScheduleDialogParts.Action("保存", Save, primary: true));

        Grid.SetColumn(confirm, 2);
        footer.Children.Add(confirm);

        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ---------- 中间：课程卡片 ----------
        var card = ScheduleDialogParts.Card("课程", "", out var cardBody);

        _gridHost = new StackPanel();
        cardBody.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 320,
            Content = _gridHost,
        });

        root.Children.Add(card);
        Content = root;

        Rebuild();
    }

    // ================= 表格 =================

    /// <summary>重建编辑网格。</summary>
    private void Rebuild()
    {
        _gridHost.Children.Clear();
        _rowHighlights.Clear();
        _selectedRow = -1;

        var grid = new Grid();

        // 3 列固定（节次 / 开始 / 结束）+ 7 列等分（周一…周日）
        for (var i = 0; i < 3 + 7; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = i < 3
                    ? new GridLength(LabelColumnWidth)
                    : new GridLength(DayColumnWidth, GridUnitType.Star),
            });
        }

        for (var row = 0; row <= _periods.Count; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 表头
        AddHeader(grid, 0, "节次");
        AddHeader(grid, 1, "开始");
        AddHeader(grid, 2, "结束");

        for (var day = 0; day < 7; day++)
            AddHeader(grid, 3 + day, ScheduleDialogParts.DayHeaders[day]);

        // 数据行
        for (var row = 0; row < _periods.Count; row++)
        {
            var period = _periods[row];
            var capturedRow = row;

            // 行底色（被选中时高亮），必须在同一行的单元格之前加入，才会位于下层
            var highlight = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(1, 2, 1, 2),
            };

            Grid.SetRow(highlight, capturedRow + 1);
            Grid.SetColumn(highlight, 0);
            Grid.SetColumnSpan(highlight, 10);
            grid.Children.Add(highlight);
            _rowHighlights.Add(highlight);

            grid.Children.Add(Cell(ScheduleDialogParts.Input(period.Name), capturedRow + 1, 0,
                box =>
                {
                    box.TextChanged += (_, _) => period.Name = box.Text;
                    box.PreviewMouseLeftButtonDown += (_, _) => SelectRow(capturedRow);
                }));

            grid.Children.Add(Cell(ScheduleDialogParts.Input(period.Start), capturedRow + 1, 1,
                box => box.TextChanged += (_, _) => period.Start = box.Text.Trim()));

            grid.Children.Add(Cell(ScheduleDialogParts.Input(period.End), capturedRow + 1, 2,
                box => box.TextChanged += (_, _) => period.End = box.Text.Trim()));

            for (var day = 0; day < 7; day++)
            {
                var key = day.ToString();
                var courses = _timetable[key];
                while (courses.Count <= row) courses.Add("");

                var current = courses[row];
                var box = ScheduleDialogParts.Input(current);

                var capturedDay = key;
                var capturedIndex = row;
                box.TextChanged += (_, _) =>
                {
                    var list = _timetable[capturedDay];
                    while (list.Count <= capturedIndex) list.Add("");
                    list[capturedIndex] = box.Text.Trim();
                };

                grid.Children.Add(Cell(box, capturedRow + 1, 3 + day));
            }
        }

        _gridHost.Children.Add(grid);
        UpdateCount();
    }

    private static void AddHeader(Grid grid, int column, string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            Margin = new Thickness(0, 6, 8, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };

        Grid.SetColumn(block, column);
        Grid.SetRow(block, 0);
        grid.Children.Add(block);
    }

    private static FrameworkElement Cell(FrameworkElement child, int row, int column, Action<TextBox>? configure = null)
    {
        if (child is TextBox box)
        {
            box.Margin = new Thickness(0, 2, 6, 2);
            configure?.Invoke(box);
        }

        Grid.SetColumn(child, column);
        Grid.SetRow(child, row);
        return child;
    }

    /// <summary>选中某一行（删除操作的对象）。</summary>
    private void SelectRow(int row)
    {
        _selectedRow = row;

        var (_, _, accent, _) = ThemeBridge.NavigationPalette;
        var selected = new SolidColorBrush(accent) { Opacity = 0.12 };

        for (var i = 0; i < _rowHighlights.Count; i++)
            _rowHighlights[i].Background = i == row ? selected : Brushes.Transparent;
    }

    // ================= 行操作 =================

    private void AddPeriod()
    {
        _periods.Add(new Period
        {
            Name = $"第{_periods.Count + 1}节",
            Start = "08:00",
            End = "08:45",
        });

        foreach (var key in _timetable.Keys.ToList()) _timetable[key].Add("");

        Rebuild();
    }

    /// <summary>删除选中课节；没有选中就删最后一节（对应源项目 _del_row）。</summary>
    private void RemovePeriod()
    {
        var row = _selectedRow >= 0 ? _selectedRow : _periods.Count - 1;
        if (row < 0 || row >= _periods.Count) return;

        _periods.RemoveAt(row);

        foreach (var key in _timetable.Keys.ToList())
        {
            var list = _timetable[key];
            if (row < list.Count) list.RemoveAt(row);
        }

        Rebuild();
    }

    private void UpdateCount()
    {
        var count = _periods.Count;
        _countLabel.Text = $"共 {count} 节";

        // 节次多到离谱的时候，底部来一句
        _countHint.Text = count switch
        {
            >= 30 => "节次过多，请确认",
            >= 25 => "节次偏多，请确认",
            >= 20 => "节次较多，请确认",
            _ => "",
        };
    }

    // ================= 保存 =================

    private void Save()
    {
        if (_periods.Count == 0)
        {
            Warn("至少保留一节课");
            return;
        }

        for (var row = 0; row < _periods.Count; row++)
        {
            var period = _periods[row];

            if (!IsValidTime(period.Start))
            {
                Warn($"第 {row + 1} 行的开始时间格式无效：{period.Start}");
                return;
            }

            if (!IsValidTime(period.End))
            {
                Warn($"第 {row + 1} 行的结束时间格式无效：{period.End}");
                return;
            }
        }

        if (!_schedule.UpdateTimetable(_timetableName, _periods, _timetable))
        {
            Warn("保存失败：课表已不存在。");
            return;
        }

        MessageBox.Show(this, $"课表“{_timetableName}”已保存", "保存成功",
            MessageBoxButton.OK, MessageBoxImage.Information);

        DialogResult = true;
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static bool IsValidTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(':');
        if (parts.Length != 2) return false;

        return int.TryParse(parts[0], out var hour) && hour is >= 0 and <= 23
               && int.TryParse(parts[1], out var minute) && minute is >= 0 and <= 59;
    }

    // ================= 导出 / 导入 =================

    /// <summary>导出菜单（对应源项目 _export_menu 的三项）。</summary>
    private void ShowExportMenu(FrameworkElement anchor)
    {
        var menu = new ContextMenu();

        menu.Items.Add(new MenuItem { Header = "导出当前课表（JSON）" });
        menu.Items.Add(new MenuItem { Header = "导出为 CSV（Excel 可打开）" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "导出全部课表 + 假期 / 周末设置（完整备份）" });

        ((MenuItem)menu.Items[0]).Click += (_, _) => DoExport(ExportFormat.Json, new[] { _timetableName });
        ((MenuItem)menu.Items[1]).Click += (_, _) => DoExport(ExportFormat.Csv, new[] { _timetableName });
        ((MenuItem)menu.Items[3]).Click += (_, _) => DoExport(ExportFormat.Json, null, includeExtras: true);

        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void DoExport(ExportFormat format, IEnumerable<string>? names, bool includeExtras = false)
    {
        var suggested = _schedule.ExportFileName(format, names, includeExtras);
        var filter = format == ExportFormat.Csv ? "CSV 文件 (*.csv)|*.csv" : "JSON 文件 (*.json)|*.json";

        var dialog = new SaveFileDialog
        {
            Title = "导出课表",
            FileName = suggested,
            Filter = filter,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var content = format == ExportFormat.Csv
                ? _schedule.ExportCsv(names)
                : _schedule.ExportJson(names, includeExtras);

            File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));

            MessageBox.Show(this, $"文件已保存到：\n{dialog.FileName}", "导出完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>导入课表：先看预览与导入方式，再执行（对应 _import_file）。</summary>
    private void ImportFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入课表",
            Filter = "课表文件 (*.json *.csv)|*.json;*.csv|所有文件 (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        ScheduleImportPreview preview;

        try
        {
            preview = _schedule.PreviewImport(ScheduleService.ReadTextFile(dialog.FileName), dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"无法读取该文件：\n{ex.Message}", "导入失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var options = new ImportPreviewDialog(preview) { Owner = this };
        if (options.ShowDialog() != true) return;

        try
        {
            var outcome = _schedule.ImportFile(dialog.FileName, options.Mode);

            MessageBox.Show(this, DescribeOutcome(outcome), "导入完成",
                MessageBoxButton.OK, MessageBoxImage.Information);

            ReloadFromSchedule();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导入失败：{ex.Message}", "导入失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>导入结果摘要（对应 TimetableManagerDialog._import_summary）。</summary>
    internal static string DescribeOutcome(ImportOutcome outcome)
    {
        var parts = new List<string>();

        if (outcome.Added.Count > 0) parts.Add($"新增 {outcome.Added.Count} 套：{string.Join("、", outcome.Added)}");
        if (outcome.Replaced.Count > 0) parts.Add($"替换 {outcome.Replaced.Count} 套：{string.Join("、", outcome.Replaced)}");
        if (outcome.Renamed.Count > 0) parts.Add($"同名改名 {outcome.Renamed.Count} 处：{string.Join("、", outcome.Renamed)}");
        if (outcome.Removed.Count > 0) parts.Add($"移除 {outcome.Removed.Count} 套：{string.Join("、", outcome.Removed)}");

        if (outcome.Active.Length > 0) parts.Add($"当前课表：{outcome.Active}");

        if (outcome.Warnings.Count > 0) parts.Add("提示：" + string.Join("；", outcome.Warnings));

        return parts.Count > 0 ? string.Join("\n", parts) : "已导入";
    }

    /// <summary>重新从服务读一遍数据（导入之后刷新表格）。</summary>
    private void ReloadFromSchedule()
    {
        var definition = _schedule.Document.Timetables.TryGetValue(_timetableName, out var found)
            ? found
            : null;

        _periods.Clear();
        _timetable.Clear();

        if (definition is not null)
        {
            _periods.AddRange(definition.Periods.Select(p => p.Clone()));

            foreach (var (key, value) in definition.Timetable)
                _timetable[key] = new List<string>(value);
        }

        foreach (var day in Enumerable.Range(0, 7))
        {
            var key = day.ToString();

            if (!_timetable.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _timetable[key] = list;
            }

            while (list.Count < _periods.Count) list.Add("");
        }

        Rebuild();
    }
}

/// <summary>
/// 导入预览与导入方式选择。对应源模块 menu.py 的 _ImportPreviewDialog。
/// </summary>
internal sealed class ImportPreviewDialog : Window
{
    /// <summary>用户选择的导入方式：merge / replace。</summary>
    public string Mode { get; private set; } = "merge";

    public ImportPreviewDialog(ScheduleImportPreview preview)
    {
        Title = "导入预览";
        Width = 440;
        Height = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        SizeToContent = SizeToContent.Manual;

        var root = new StackPanel { Margin = new Thickness(18) };

        // ---- 标题：文件类型 ----
        var head = preview.Format switch
        {
            "csv" => "CSV 课表文件（单张课表，不含假期等设置）",
            _ when preview.Kind == "full" => "JSON 完整备份（课表 + 假期 / 调休 / 周末等设置）",
            _ => "JSON 课表文件",
        };

        root.Children.Add(new TextBlock
        {
            Text = head,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            TextWrapping = TextWrapping.Wrap,
        });

        // ---- 内容摘要 ----
        var lines = new List<string>();

        foreach (var timetable in preview.Timetables)
        {
            var mark = timetable.Exists ? "　⚠ 已有同名课表" : "";
            lines.Add($"· {timetable.Name}：{timetable.Periods} 节 / {timetable.Courses} 门课{mark}");
        }

        if (preview.Kind == "full")
        {
            var extras = preview.Extras;
            var bits = new List<string>();

            if (extras.Holidays > 0) bits.Add($"假期 {extras.Holidays} 天");
            if (extras.MakeupDays > 0) bits.Add($"调休 {extras.MakeupDays} 天");
            if (extras.Weekend) bits.Add("周末作息");
            if (extras.AdvanceMinutes is not null) bits.Add($"提前提醒 {extras.AdvanceMinutes} 分钟");
            if (extras.TimeOffsetSeconds is not null and not 0) bits.Add($"时间偏移 {extras.TimeOffsetSeconds} 秒");

            lines.Add("顺带带过来的设置：" + (bits.Count > 0 ? string.Join("、", bits) : "没有"));
        }

        root.Children.Add(new TextBlock
        {
            Text = lines.Count > 0 ? string.Join(Environment.NewLine, lines) : "（空的）",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        // ---- 警告 ----
        foreach (var warning in preview.Warnings)
        {
            root.Children.Add(new TextBlock
            {
                Text = $"⚠ {warning}",
                FontSize = 11.5,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        // ---- 导入方式 ----
        root.Children.Add(new TextBlock
        {
            Text = "如何导入：",
            Margin = new Thickness(0, 14, 0, 6),
        });

        var (options, defaultMode) = preview.Kind == "full"
            ? (new[]
                {
                    ("replace", "整体恢复：课表与假期安排全部替换为备份内容（将覆盖当前内容）"),
                    ("merge", "只并课表：都当成新课表加进来，其他设置不动"),
                }, "replace")
            : (new[]
                {
                    ("merge", "合并：作为新课表导入"
                              + (preview.HasSameName ? "（同名自动改名为「xx (导入)」）" : "")),
                    ("replace", "覆盖同名课表（不存在的则新建）"),
                }, preview.HasSameName ? "replace" : "merge");

        var radios = new List<(string Mode, RadioButton Button)>();

        foreach (var (mode, text) in options)
        {
            var radio = new RadioButton
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                IsChecked = mode == defaultMode,
                GroupName = "import-mode",
                Margin = new Thickness(0, 3, 0, 3),
            };

            radios.Add((mode, radio));
            root.Children.Add(radio);
        }

        // ---- 按钮 ----
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
        };

        var confirm = ScheduleDialogParts.Action("导入", () =>
        {
            Mode = radios.FirstOrDefault(r => r.Button.IsChecked == true).Mode ?? defaultMode;
            DialogResult = true;
        }, primary: true);

        buttons.Children.Add(confirm);
        buttons.Children.Add(ScheduleDialogParts.Action("取消", () => DialogResult = false));

        root.Children.Add(buttons);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };
    }
}

/// <summary>
/// 临时调课。对应源模块 menu.py 的 TempAdjustDialog。
/// 仅对选定日期生效，逐节编辑当天课程。
/// </summary>
internal sealed class TempAdjustDialog : Window
{
    private readonly IScheduleService _schedule;
    private readonly DatePicker _datePicker;
    private readonly TextBlock _weekLabel;
    private readonly StackPanel _courseHost;

    private readonly List<TextBox> _courseBoxes = new();

    public TempAdjustDialog(IScheduleService schedule)
    {
        _schedule = schedule;

        Title = "临时调课";
        Width = 620;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock
        {
            Text = "哪一天：",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        _datePicker = new DatePicker
        {
            SelectedDate = DateTime.Today,
            Width = 150,
        };
        _datePicker.SelectedDateChanged += (_, _) => LoadForDate();
        header.Children.Add(_datePicker);

        _weekLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Opacity = 0.65,
        };
        header.Children.Add(_weekLabel);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var tip = new TextBlock
        {
            Text = "修改仅对所选日期生效；点「保存」后写入临时调课。",
            FontSize = 11.5,
            Opacity = 0.6,
            Margin = new Thickness(0, 8, 0, 12),
        };
        DockPanel.SetDock(tip, Dock.Top);
        root.Children.Add(tip);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        footer.Children.Add(ScheduleDialogParts.Action("清除临时调课", ClearAdjust));
        footer.Children.Add(ScheduleDialogParts.Action("取消", () => DialogResult = false));
        footer.Children.Add(ScheduleDialogParts.Action("保存", Save, primary: true));
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        _courseHost = new StackPanel();
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _courseHost,
        });

        Content = root;

        LoadForDate();
    }

    private void LoadForDate()
    {
        _courseHost.Children.Clear();
        _courseBoxes.Clear();

        var date = _datePicker.SelectedDate ?? DateTime.Today;
        var today = DateOnly.FromDateTime(date);

        // 星期一 = 0，与项目内部约定一致
        var weekdayIndex = ((int)date.DayOfWeek + 6) % 7;
        _weekLabel.Text = $"（{ScheduleDialogParts.DayHeaders[weekdayIndex]}）";

        var periods = _schedule.PeriodsFor(today);
        var dateText = today.ToString("yyyy-MM-dd");

        var adjust = _schedule.TempAdjust;
        var courses = adjust.Date == dateText
            ? new List<string>(adjust.Courses)
            : new List<string>(_schedule.CoursesFor(today));

        for (var i = 0; i < periods.Count; i++)
        {
            var period = periods[i];
            var course = i < courses.Count ? courses[i] : "";

            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = $"{period.Name}  {period.Start}-{period.End}",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.75,
                FontSize = 12.5,
            };
            row.Children.Add(label);

            var box = ScheduleDialogParts.Input(course);
            Grid.SetColumn(box, 1);
            row.Children.Add(box);

            _courseBoxes.Add(box);
            _courseHost.Children.Add(row);
        }

        if (periods.Count == 0)
        {
            _courseHost.Children.Add(new TextBlock
            {
                Text = "这一天的课表为空（假期或无课）。",
                Opacity = 0.7,
                Margin = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private void ClearAdjust()
    {
        _schedule.ClearTempAdjust();
        LoadForDate();

        MessageBox.Show(this, "已清除临时调课。", "提示",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Save()
    {
        var date = _datePicker.SelectedDate ?? DateTime.Today;
        var courses = _courseBoxes.Select(b => b.Text.Trim()).ToList();

        _schedule.SetTempAdjust(date.ToString("yyyy-MM-dd"), courses);
        DialogResult = true;
    }
}

/// <summary>
/// 假期与调休。对应源模块 menu.py 的 HolidayDialog。
/// 上半部分维护假期区间，下半部分维护调休上课日。
/// </summary>
internal sealed class HolidayDialog : Window
{
    private readonly IScheduleService _schedule;

    private readonly ListBox _holidayList;
    private readonly DatePicker _holidayStart;
    private readonly DatePicker _holidayEnd;
    private readonly TextBox _holidayName;

    private readonly ListBox _makeupList;
    private readonly DatePicker _makeupDate;
    private readonly ComboBox _makeupWeekday;

    public HolidayDialog(IScheduleService schedule)
    {
        _schedule = schedule;

        Title = "假期与调休";
        Width = 780;
        Height = 680;
        MinWidth = 660;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new StackPanel { Margin = new Thickness(26, 22, 26, 18) };

        root.Children.Add(new TextBlock
        {
            Text = "假期与调休",
            FontSize = 20,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });
        root.Children.Add(new TextBlock
        {
            Text = "一个悲伤的故事",
            Opacity = 0.6,
            Margin = new Thickness(0, 4, 0, 12),
        });

        // ---------- 假期区间 ----------
        var holidayCard = ScheduleDialogParts.Card("放假的日期段", "可以加好几段", out var holidayBody);

        _holidayList = new ListBox { Height = 150 };
        holidayBody.Children.Add(_holidayList);

        var holidayRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        holidayRow.Children.Add(Caption("开始"));
        _holidayStart = new DatePicker { SelectedDate = DateTime.Today, Width = 130 };
        holidayRow.Children.Add(_holidayStart);

        holidayRow.Children.Add(Caption("结束"));
        _holidayEnd = new DatePicker { SelectedDate = DateTime.Today, Width = 130 };
        holidayRow.Children.Add(_holidayEnd);

        _holidayName = ScheduleDialogParts.Input(width: 150);
        _holidayName.ToolTip = "起个名（可以不填，比如：国庆）";
        holidayRow.Children.Add(_holidayName);

        holidayRow.Children.Add(ScheduleDialogParts.Action("加上", AddHoliday, primary: true));
        holidayRow.Children.Add(ScheduleDialogParts.Action("删除选中项", DeleteHoliday));
        holidayBody.Children.Add(holidayRow);

        root.Children.Add(holidayCard);

        // ---------- 调休上课日 ----------
        var makeupCard = ScheduleDialogParts.Card("要补课的日子", "周末和假期也得按“补周几”来上课", out var makeupBody);

        _makeupList = new ListBox { Height = 130 };
        makeupBody.Children.Add(_makeupList);

        var makeupRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        makeupRow.Children.Add(Caption("日期"));
        _makeupDate = new DatePicker { SelectedDate = DateTime.Today, Width = 130 };
        makeupRow.Children.Add(_makeupDate);

        _makeupWeekday = new ComboBox { Width = 110, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var day in ScheduleDialogParts.DayHeaders) _makeupWeekday.Items.Add("补" + day);
        _makeupWeekday.SelectedIndex = 0;
        makeupRow.Children.Add(_makeupWeekday);

        makeupRow.Children.Add(ScheduleDialogParts.Action("加上", AddMakeup, primary: true));
        makeupRow.Children.Add(ScheduleDialogParts.Action("删除选中项", DeleteMakeup));
        makeupBody.Children.Add(makeupRow);

        root.Children.Add(makeupCard);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        footer.Children.Add(ScheduleDialogParts.Action("关闭", Close, primary: true));
        root.Children.Add(footer);

        Content = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = root,
        };

        Refresh();
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private void Refresh()
    {
        _holidayList.Items.Clear();
        foreach (var holiday in _schedule.Holidays)
        {
            var name = string.IsNullOrWhiteSpace(holiday.Name) ? "" : $"（{holiday.Name}）";
            _holidayList.Items.Add($"{holiday.Start} ～ {holiday.End}{name}");
        }

        _makeupList.Items.Clear();
        foreach (var makeup in _schedule.MakeupDays)
        {
            var weekday = Math.Clamp(makeup.AsWeekday, 0, 6);
            _makeupList.Items.Add($"{makeup.Date} → 补{ScheduleDialogParts.DayHeaders[weekday]}");
        }
    }

    private void AddHoliday()
    {
        var start = (_holidayStart.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");
        var end = (_holidayEnd.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");

        _schedule.AddHoliday(start, end, _holidayName.Text.Trim());
        _holidayName.Clear();
        Refresh();
    }

    private void DeleteHoliday()
    {
        if (_holidayList.SelectedIndex < 0)
        {
            Warn("请先选中要删除的假期。");
            return;
        }

        _schedule.RemoveHoliday(_holidayList.SelectedIndex);
        Refresh();
    }

    private void AddMakeup()
    {
        var date = (_makeupDate.SelectedDate ?? DateTime.Today).ToString("yyyy-MM-dd");
        var weekday = Math.Max(0, _makeupWeekday.SelectedIndex);

        _schedule.AddMakeup(date, weekday);
        Refresh();
    }

    private void DeleteMakeup()
    {
        if (_makeupList.SelectedIndex < 0)
        {
            Warn("请先选中要删除的调休日。");
            return;
        }

        _schedule.RemoveMakeup(_makeupList.SelectedIndex);
        Refresh();
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>
/// 周末作息。对应源模块 menu.py 的 WeekendScheduleDialog。
/// 设置上午分界点、下午时间片，以及周末课程列表。
/// </summary>
internal sealed class WeekendDialog : Window
{
    private readonly IScheduleService _schedule;

    private readonly TextBox _splitBox;
    private readonly StackPanel _afternoonHost;
    private readonly TextBox _coursesBox;

    private readonly List<Period> _afternoon;

    public WeekendDialog(IScheduleService schedule)
    {
        _schedule = schedule;

        var weekend = schedule.Weekend;
        _afternoon = weekend.AfternoonPeriods.Select(p => p.Clone()).ToList();

        Title = "周末作息";
        Width = 640;
        Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 18) };

        var header = new StackPanel();
        header.Children.Add(new TextBlock
        {
            Text = "周末作息",
            FontSize = 20,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
        });
        header.Children.Add(new TextBlock
        {
            Text = "上午沿用平日课表中开始时间早于分界点的节次；下午使用下面单独配置的节次。",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        });

        var splitRow = new StackPanel { Orientation = Orientation.Horizontal };
        splitRow.Children.Add(new TextBlock
        {
            Text = "上午分界点：",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        _splitBox = ScheduleDialogParts.Input(weekend.MorningSplit, 100);
        splitRow.Children.Add(_splitBox);
        header.Children.Add(splitRow);

        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        footer.Children.Add(ScheduleDialogParts.Action("取消", () => DialogResult = false));
        footer.Children.Add(ScheduleDialogParts.Action("保存", Save, primary: true));
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new StackPanel();

        var afternoonCard = ScheduleDialogParts.Card("下午节次", "留空表示周末只有上午有课", out var afternoonBody);
        afternoonBody.Children.Add(ScheduleDialogParts.Action("＋ 加一节", () =>
        {
            _afternoon.Add(new Period { Name = $"午{_afternoon.Count + 1}", Start = "13:30", End = "14:15" });
            RebuildAfternoon();
        }));
        _afternoonHost = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        afternoonBody.Children.Add(_afternoonHost);
        body.Children.Add(afternoonCard);

        var courseCard = ScheduleDialogParts.Card("周末课程", "一行一节，与上面的节次顺序对应", out var courseBody);
        _coursesBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 160,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
            Text = string.Join(Environment.NewLine, weekend.Courses),
        };
        courseBody.Children.Add(_coursesBox);
        body.Children.Add(courseCard);

        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = body,
        });

        Content = root;

        RebuildAfternoon();
    }

    private void RebuildAfternoon()
    {
        _afternoonHost.Children.Clear();

        for (var i = 0; i < _afternoon.Count; i++)
        {
            var period = _afternoon[i];
            var index = i;

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 3, 0, 3),
            };

            var nameBox = ScheduleDialogParts.Input(period.Name, 100);
            nameBox.TextChanged += (_, _) => period.Name = nameBox.Text;
            row.Children.Add(nameBox);

            var startBox = ScheduleDialogParts.Input(period.Start, 80);
            startBox.TextChanged += (_, _) => period.Start = startBox.Text.Trim();
            row.Children.Add(startBox);

            var endBox = ScheduleDialogParts.Input(period.End, 80);
            endBox.TextChanged += (_, _) => period.End = endBox.Text.Trim();
            row.Children.Add(endBox);

            row.Children.Add(ScheduleDialogParts.Action("删除", () =>
            {
                _afternoon.RemoveAt(index);
                RebuildAfternoon();
            }));

            _afternoonHost.Children.Add(row);
        }
    }

    private void Save()
    {
        var split = _splitBox.Text.Trim();
        if (split.Length == 0) split = "12:00";

        var courses = _coursesBox.Text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .ToList();

        _schedule.SetWeekend(split, _afternoon, courses);
        DialogResult = true;
    }
}
