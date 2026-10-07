using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.App.Views.Dialogs;

/// <summary>
/// 随机点名器。对应源模块：randoms.py 的 RollCallDialog。
///
/// 行为与源项目一致：
///   · 启动时自动尝试读取 settings/name.json（值日生名单同款格式）
///   · 支持 { "names": [...] } 与纯数组两种文件格式
///   · 抽取带冷却：最近 COOLDOWN(10) 个抽过的人不再重复，
///     名单被抽空后仅保留最后一人并重新可选，避免死循环
/// </summary>
internal sealed class RollCallDialog : Window
{
    private const int Cooldown = 10;

    private readonly TextBlock _nameLabel;
    private readonly TextBlock _statusLabel;

    private readonly List<string> _recent = new();
    private List<string> _names = new();

    public RollCallDialog(IPathService paths)
    {
        Title = "随机点名器";
        Width = 520;
        Height = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        FontFamily = new FontFamily("Microsoft YaHei UI");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _nameLabel = new TextBlock
        {
            Text = "随机点名",
            FontSize = 48,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        root.Children.Add(_nameLabel);

        _statusLabel = new TextBlock
        {
            Text = "猜猜会是谁呢~",
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
            Margin = new Thickness(0, 0, 0, 16),
        };
        Grid.SetRow(_statusLabel, 1);
        root.Children.Add(_statusLabel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var openButton = new Button
        {
            Content = "选个名单",
            Width = 120,
            Height = 44,
            Margin = new Thickness(0, 0, 12, 0),
        };
        openButton.Click += (_, _) => OpenFile();
        buttons.Children.Add(openButton);

        var pickButton = new Button
        {
            Content = "随  机",
            Width = 120,
            Height = 44,
        };
        pickButton.Click += (_, _) => Pick();
        buttons.Children.Add(pickButton);

        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        Content = root;

        AutoLoad(paths);
    }

    // ================= 名单加载 =================

    /// <summary>自动加载默认名单（对应 _auto_load）。</summary>
    private void AutoLoad(IPathService paths)
    {
        var path = paths.ConfigFile("name.json");
        if (!File.Exists(path)) return;

        if (!TryLoad(path)) return;

        _statusLabel.Text = $"自动找到 name.json 啦（{_names.Count} 人）";
    }

    private void OpenFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "挑一个名单文件",
            Filter = "JSON 文件 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        if (TryLoad(dialog.FileName))
            _statusLabel.Text = $"名单来啦：{Path.GetFileName(dialog.FileName)}（{_names.Count} 人）";
    }

    private bool TryLoad(string path)
    {
        List<string>? names = null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("names", out var nested) && nested.ValueKind == JsonValueKind.Array)
                    names = ReadArray(nested);
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                names = ReadArray(root);
            }
            else
            {
                MessageBox.Show(this, "这个文件得是个数组才行", "格式不太对",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            MessageBox.Show(this, $"这个文件读不出来：\n{ex.Message}", "读不出来",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        if (names is null)
        {
            MessageBox.Show(this, "这个文件得是个数组才行", "格式不太对",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        if (names.Count == 0)
        {
            MessageBox.Show(this, "文件里一个能用的名字都没有。", "名单是空的",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        _names = names;
        _recent.Clear();
        _nameLabel.Text = "随机点名";
        return true;
    }

    private static List<string>? ReadArray(JsonElement element)
    {
        var list = new List<string>();

        foreach (var item in element.EnumerateArray())
        {
            var text = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : item.ToString();

            if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
        }

        return list;
    }

    // ================= 抽取 =================

    /// <summary>抽取一个名字（对应 pick）。</summary>
    private void Pick()
    {
        if (_names.Count == 0)
        {
            MessageBox.Show(this, "先选个名单文件吧。", "提醒一下",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var candidates = _names.Where(n => !_recent.Contains(n)).ToList();

        if (candidates.Count == 0)
        {
            // 名单被抽空：只保留最后一人，其余重新可选，避免无限循环
            var last = _recent.Count > 0 ? _recent[^1] : null;
            _recent.Clear();
            if (last is not null) _recent.Add(last);

            candidates = _names.Where(n => n != last).ToList();
            if (candidates.Count == 0) candidates = _names.ToList();
        }

        var chosen = candidates[Random.Shared.Next(candidates.Count)];

        _recent.Add(chosen);
        while (_recent.Count > Cooldown) _recent.RemoveAt(0);

        _nameLabel.Text = chosen;
        _statusLabel.Text = $"已抽 {_recent.Count} 人（不重复窗口 {Cooldown} 人）";
    }
}
