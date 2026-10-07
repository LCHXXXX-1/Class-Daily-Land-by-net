using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>对话框公共构件。</summary>
internal static class DialogBuilder
{
    public static StackPanel VerticalRoot(Window window, double width, double height, string title)
    {
        window.Title = title;
        window.Width = width;
        window.Height = height;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ResizeMode = ResizeMode.CanResizeWithGrip;
        window.FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new StackPanel { Margin = new Thickness(16) };
        window.Content = root;
        return root;
    }

    /// <summary>「标签 + 输入框」一行。</summary>
    public static TextBox LabeledInput(Panel host, string label, string value, double inputWidth = double.NaN)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var caption = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(caption, Dock.Left);
        row.Children.Add(caption);

        var box = new TextBox
        {
            Text = value,
            Padding = new Thickness(6, 4, 6, 4),
        };
        if (!double.IsNaN(inputWidth)) box.Width = inputWidth;

        row.Children.Add(box);
        host.Children.Add(row);
        return box;
    }

    /// <summary>底部按钮行（默认右对齐）。</summary>
    public static StackPanel ButtonRow(Panel host, params (string Text, Action OnClick)[] buttons)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };

        foreach (var (text, onClick) in buttons)
        {
            var button = new Button
            {
                Content = text,
                MinWidth = 88,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(8, 0, 0, 0),
            };
            button.Click += (_, _) => onClick();
            row.Children.Add(button);
        }

        host.Children.Add(row);
        return row;
    }

    public static void Warn(Window owner, string title, string message)
        => MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
}

/// <summary>
/// 出勤人数编辑。对应源模块 menu.py 的 AttendanceDialog。
/// 只接受数字，非法输入给出提示（与源项目一致）。
/// </summary>
internal sealed class AttendanceDialog : Window
{
    private readonly IAttendanceService _attendance;
    private TextBox _shouldBox = null!;
    private TextBox _actualBox = null!;

    public AttendanceDialog(IAttendanceService attendance)
    {
        _attendance = attendance;

        var root = DialogBuilder.VerticalRoot(this, 350, 200, "出勤");
        _shouldBox = DialogBuilder.LabeledInput(root, "应到", attendance.Should);
        _actualBox = DialogBuilder.LabeledInput(root, "实到", attendance.Actual);

        DialogBuilder.ButtonRow(root,
            ("确认", OnConfirm),
            ("取消", Close));
    }

    private void OnConfirm()
    {
        var should = _shouldBox.Text.Trim();
        var actual = _actualBox.Text.Trim();

        if (!IsDigits(should) || !IsDigits(actual))
        {
            DialogBuilder.Warn(this, "格式错误", "请输入有效数字。");
            return;
        }

        _attendance.Set(should, actual);
        Close();
    }

    private static bool IsDigits(string text)
        => text.Length > 0 && text.All(char.IsDigit);
}

/// <summary>
/// 值日生名单管理。对应源模块 menu.py 的 DutyEditor。
/// 支持逐个添加、逗号批量添加、逐条删除。
/// </summary>
internal sealed class DutyEditorDialog : Window
{
    private readonly IDutyService _duty;
    private readonly StackPanel _listPanel;
    private TextBox _singleBox = null!;

    public DutyEditorDialog(IDutyService duty)
    {
        _duty = duty;

        var root = DialogBuilder.VerticalRoot(this, 500, 420, "值日生名单");

        // 逐个添加
        var addRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var caption = new TextBlock
        {
            Text = "逐个添加：",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(caption, Dock.Left);
        addRow.Children.Add(caption);

        var addButton = new Button
        {
            Content = "加上",
            MinWidth = 64,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(8, 0, 0, 0),
        };
        addButton.Click += (_, _) => AddSingle();
        DockPanel.SetDock(addButton, Dock.Right);
        addRow.Children.Add(addButton);

        _singleBox = new TextBox { Padding = new Thickness(6, 4, 6, 4) };
        addRow.Children.Add(_singleBox);
        root.Children.Add(addRow);

        // 批量添加
        var batchButton = new Button
        {
            Content = "一次加多几个（用逗号隔开）",
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8),
        };
        batchButton.Click += (_, _) => OpenBatchDialog();
        root.Children.Add(batchButton);

        // 名单
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 220,
            Padding = new Thickness(0),
        };
        _listPanel = new StackPanel();
        scroll.Content = _listPanel;
        root.Children.Add(scroll);

        DialogBuilder.ButtonRow(root, ("关闭", Close));

        Refresh();
    }

    private void Refresh()
    {
        _listPanel.Children.Clear();

        var names = _duty.DutyList;
        if (names.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock { Text = "暂无值日生", Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var index = i;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };

            var deleteButton = new Button
            {
                Content = "删除",
                MinWidth = 56,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
            };
            deleteButton.Click += (_, _) => DeleteAt(index);
            DockPanel.SetDock(deleteButton, Dock.Right);
            row.Children.Add(deleteButton);

            row.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {names[i]}",
                VerticalAlignment = VerticalAlignment.Center,
            });

            _listPanel.Children.Add(row);
        }
    }

    private void AddSingle()
    {
        var name = _singleBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            DialogBuilder.Warn(this, "提示", "姓名不能为空");
            return;
        }

        _duty.AddDuty(name);
        _singleBox.Clear();
        Refresh();
    }

    private void DeleteAt(int index)
    {
        _duty.RemoveDuty(index);
        Refresh();
    }

    private void OpenBatchDialog()
    {
        var dialog = new BatchDutyDialog(_duty) { Owner = this };
        dialog.ShowDialog();
        Refresh();
    }

    /// <summary>批量添加子弹窗（对应源项目 _open_batch）。</summary>
    private sealed class BatchDutyDialog : Window
    {
        public BatchDutyDialog(IDutyService duty)
        {
            var root = DialogBuilder.VerticalRoot(this, 450, 250, "批量添加值日生");
            root.Children.Add(new TextBlock { Text = "姓名（用逗号分隔）：", Margin = new Thickness(0, 0, 0, 6) });

            var textBox = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(6),
            };
            root.Children.Add(textBox);

            DialogBuilder.ButtonRow(root,
                ("确认", () =>
                {
                    var names = textBox.Text
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(n => n.Trim())
                        .Where(n => n.Length > 0)
                        .ToList();

                    if (names.Count == 0)
                    {
                        DialogBuilder.Warn(this, "提示", "姓名不能为空");
                        return;
                    }

                    // 与源项目一致：支持中英文逗号
                    if (names.Count == 1)
                    {
                        names = textBox.Text
                            .Split('，', StringSplitOptions.RemoveEmptyEntries)
                            .Select(n => n.Trim())
                            .Where(n => n.Length > 0)
                            .ToList();
                    }

                    if (names.Count == 0)
                    {
                        DialogBuilder.Warn(this, "提示", "姓名不能为空");
                        return;
                    }

                    duty.AddDuties(names);
                    Close();
                }),
                ("取消", Close));
        }
    }
}

/// <summary>
/// 作业编辑。对应源模块 menu.py 的 HomeworkEditor。
/// 上半部分为新增表单，下半部分按科目分组列出，可整组清除、逐条删除或修改。
/// </summary>
/// <summary>
/// 作业编辑器的主体（不含窗口外壳与底部按钮）。
/// 抽出来是为了两处复用：独立的编辑作业窗口，以及设置中心「作业」页的内嵌版本
/// （源项目用 embedded 参数达到同样效果）。
/// </summary>
internal sealed class HomeworkEditorPanel : StackPanel
{
    private readonly IHomeworkService _homework;
    private readonly Func<Window?> _owner;
    private readonly StackPanel _listPanel;

    private readonly TextBox _subjectBox;
    private readonly TextBox _dateBox;
    private readonly TextBox _contentBox;

    public HomeworkEditorPanel(IHomeworkService homework, Func<Window?> owner)
    {
        _homework = homework;
        _owner = owner;

        _subjectBox = DialogBuilder.LabeledInput(this, "科目：", "", 150);
        _dateBox = DialogBuilder.LabeledInput(this, "提交日期（可以自己填）：", "", 200);

        Children.Add(new TextBlock { Text = "作业内容：", Margin = new Thickness(0, 0, 0, 4) });
        _contentBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 70,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
        };
        Children.Add(_contentBox);

        var addButton = new Button
        {
            Content = "添加作业",
            Padding = new Thickness(16, 6, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 10),
        };
        addButton.Click += (_, _) => AddHomework();
        Children.Add(addButton);

        _listPanel = new StackPanel();
        Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Height = 280,
            Content = _listPanel,
        });

        Refresh();
    }

    /// <summary>把改动落盘（对应源项目的 save）。</summary>
    public void Save() => _homework.Save();

    public void Refresh()
    {
        _listPanel.Children.Clear();

        var groups = _homework.Grouped();
        if (groups.Count == 0)
        {
            _listPanel.Children.Add(new TextBlock { Text = "暂无作业", Margin = new Thickness(0, 4, 0, 0) });
            return;
        }

        foreach (var group in groups)
        {
            var subject = group.Subject;

            // 科目行：标题 + 「全部清除」
            var headerRow = new DockPanel { Margin = new Thickness(0, 6, 0, 2) };

            var clearButton = new Button
            {
                Content = "全部清除",
                MinWidth = 76,
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(8, 0, 0, 0),
            };
            clearButton.Click += (_, _) => ClearSubject(subject);
            DockPanel.SetDock(clearButton, Dock.Right);
            headerRow.Children.Add(clearButton);

            var title = group.Subject;
            if (!string.IsNullOrWhiteSpace(group.Date)) title += $" ({group.Date})";

            headerRow.Children.Add(new TextBlock
            {
                Text = title,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            _listPanel.Children.Add(headerRow);

            // 条目行：内容 + 「删除」「修改」
            for (var i = 0; i < group.Contents.Count; i++)
            {
                var realIndex = group.Indices[i];
                var content = group.Contents[i];

                var row = new DockPanel { Margin = new Thickness(20, 1, 0, 1) };

                var editButton = new Button
                {
                    Content = "修改",
                    MinWidth = 56,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                };
                editButton.Click += (_, _) => EditItem(realIndex);
                DockPanel.SetDock(editButton, Dock.Right);
                row.Children.Add(editButton);

                var deleteButton = new Button
                {
                    Content = "删除",
                    MinWidth = 56,
                    Padding = new Thickness(8, 2, 8, 2),
                    Margin = new Thickness(8, 0, 0, 0),
                };
                deleteButton.Click += (_, _) => DeleteItem(realIndex);
                DockPanel.SetDock(deleteButton, Dock.Right);
                row.Children.Add(deleteButton);

                row.Children.Add(new TextBlock
                {
                    Text = $"{realIndex + 1}. {content}",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                _listPanel.Children.Add(row);
            }
        }
    }

    private void AddHomework()
    {
        var subject = _subjectBox.Text.Trim();
        var content = _contentBox.Text.Trim();

        if (!_homework.Add(subject, content, _dateBox.Text.Trim()))
        {
            var owner = _owner();
            if (owner is null) MessageBox.Show("科目和作业内容都不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            else DialogBuilder.Warn(owner, "提示", "科目和作业内容都不能为空。");

            return;
        }

        _subjectBox.Clear();
        _dateBox.Clear();
        _contentBox.Clear();
        Refresh();
    }

    private void ClearSubject(string subject)
    {
        // 倒序删除，避免索引在删除过程中前移
        var targets = _homework.All
            .Select((item, index) => (item, index))
            .Where(x => string.Equals(
                string.IsNullOrWhiteSpace(x.item.Subject) ? "未分类" : x.item.Subject,
                subject,
                StringComparison.Ordinal))
            .Select(x => x.index)
            .OrderByDescending(i => i)
            .ToList();

        foreach (var index in targets) _homework.Remove(index);
        Refresh();
    }

    private void DeleteItem(int index)
    {
        _homework.Remove(index);
        Refresh();
    }

    private void EditItem(int index)
    {
        if (index < 0 || index >= _homework.All.Count) return;

        var item = _homework.All[index];
        var dialog = new HomeworkItemDialog(item);

        var owner = _owner();
        if (owner is not null) dialog.Owner = owner;

        if (dialog.ShowDialog() != true) return;

        _homework.Update(index, dialog.Subject, dialog.ContentText, dialog.Date);
        Refresh();
    }
}

/// <summary>编辑作业（独立窗口）。对应源模块 menu.py 的 HomeworkEditor。</summary>
internal sealed class HomeworkEditorDialog : Window
{
    public HomeworkEditorDialog(IHomeworkService homework)
    {
        var root = DialogBuilder.VerticalRoot(this, 700, 600, "编辑作业");

        var panel = new HomeworkEditorPanel(homework, () => this);
        root.Children.Add(panel);

        DialogBuilder.ButtonRow(root,
            ("取消", Close),
            ("确认", () =>
            {
                panel.Save();
                Close();
            }));
    }
}

/// <summary>单条作业的修改子弹窗。</summary>
internal sealed class HomeworkItemDialog : Window
{
    private readonly TextBox _subjectBox;
    private readonly TextBox _dateBox;
    private readonly TextBox _contentBox;

    public string Subject => _subjectBox.Text.Trim();
    public string Date => _dateBox.Text.Trim();

    /// <summary>作业内容（避开 ContentControl.Content，故命名为 ContentText）。</summary>
    public string ContentText => _contentBox.Text.Trim();

    public HomeworkItemDialog(HomeworkItem item)
    {
        var root = DialogBuilder.VerticalRoot(this, 460, 320, "修改作业");

        _subjectBox = DialogBuilder.LabeledInput(root, "科目：", item.Subject, 180);
        _dateBox = DialogBuilder.LabeledInput(root, "提交日期：", item.Date, 180);

        root.Children.Add(new TextBlock { Text = "作业内容：", Margin = new Thickness(0, 0, 0, 4) });
        _contentBox = new TextBox
        {
            Text = item.Content,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6),
        };
        root.Children.Add(_contentBox);

        DialogBuilder.ButtonRow(root,
            ("确认", () => DialogResult = true),
            ("取消", () => DialogResult = false));
    }
}
