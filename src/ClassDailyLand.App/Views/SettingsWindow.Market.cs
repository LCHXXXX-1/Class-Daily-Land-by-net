using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Market;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心「插件」页的资料库部分：插件市场 + 依赖包。
/// 对应源模块：market_page.py 的 MarketPage 与 package_page.py 的 PackagePage。
/// </summary>
public partial class SettingsWindow
{
    private readonly List<string> _marketLog = new();

    private TextBlock? _marketStatus;
    private StackPanel? _marketList;
    private StackPanel? _packageList;
    private TextBox? _marketLogBox;
    private bool _marketWired;

    private static readonly Brush OkBrush = Frozen("#107C10");
    private static readonly Brush WarnBrush = Frozen("#C77700");
    private static readonly Brush ErrorBrush = Frozen("#C42B1C");
    private static readonly Brush MutedBrush = Frozen("#8A8A8E");
    private static readonly Brush AccentBrush = Frozen("#0078D4");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    // ================= 事件接线 =================

    /// <summary>
    /// 把市场 / 依赖包服务的事件接到界面上。只接一次。
    /// 服务是单例而设置窗口是每次新建的，所以事件要跟着窗口生命周期摘掉。
    /// </summary>
    private void WireMarketEvents()
    {
        if (_marketWired) return;
        _marketWired = true;

        _market.LogLine += OnMarketLog;
        _market.CatalogReady += OnMarketCatalogReady;
        _market.BusyChanged += OnMarketBusyChanged;
        _market.TaskDone += OnMarketTaskDone;

        _packages.LogLine += OnMarketLog;
        _packages.CatalogReady += OnPackageCatalogReady;
        _packages.TaskDone += OnPackageTaskDone;

        Closed += (_, _) =>
        {
            _market.LogLine -= OnMarketLog;
            _market.CatalogReady -= OnMarketCatalogReady;
            _market.BusyChanged -= OnMarketBusyChanged;
            _market.TaskDone -= OnMarketTaskDone;

            _packages.LogLine -= OnMarketLog;
            _packages.CatalogReady -= OnPackageCatalogReady;
            _packages.TaskDone -= OnPackageTaskDone;
        };
    }

    private void OnMarketLog(object? sender, string text) => AppendLog(text);

    private void OnMarketCatalogReady(object? sender, MarketSyncResult result)
        => RunOnUi(() => RenderMarketList(result.Entries));

    private void OnPackageCatalogReady(object? sender, IReadOnlyList<PackageCatalogEntry> entries)
        => RunOnUi(() => RenderPackageList(entries));

    private void OnMarketTaskDone(object? sender, MarketTaskResult result)
    {
        RunOnUi(() =>
        {
            if (result.PluginId.Length > 0 && result.Message.Length > 0)
                AppendLog($"{result.PluginId}：{result.Message}");

            if (result.RestartRequired)
                AppendLog("（重启程序后生效）");
        });
    }

    private void OnPackageTaskDone(object? sender, MarketTaskResult result)
    {
        RunOnUi(() =>
        {
            if (result.Message.Length > 0) AppendLog($"{result.PluginId}：{result.Message}");
        });
    }

    private void OnMarketBusyChanged(object? sender, bool busy) => RunOnUi(() =>
    {
        var service = sender as MarketService;
        if (_marketStatus is not null)
        {
            _marketStatus.Text = busy
                ? "正在处理…"
                : _market.LastSyncText.Length > 0
                    ? $"上次同步 {_market.LastSyncText} · 索引 {_market.Catalog.Count} 个插件"
                    : "尚未同步索引";
        }
    });

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private void AppendLog(string text)
    {
        RunOnUi(() =>
        {
            _marketLog.Add(text);
            if (_marketLog.Count > 400) _marketLog.RemoveRange(0, _marketLog.Count - 400);

            if (_marketLogBox is not null)
            {
                _marketLogBox.Text = string.Join(Environment.NewLine, _marketLog);
                _marketLogBox.ScrollToEnd();
            }
        });
    }

    // ================= 市场区 =================

    private void RenderMarketSection()
    {
        WireMarketEvents();

        AddSectionLabel("插件市场", top: 0);

        _marketStatus = new TextBlock
        {
            Text = _market.LastSyncText.Length > 0
                ? $"上次同步 {_market.LastSyncText} · 索引 {_market.Catalog.Count} 个插件"
                : "尚未同步索引",
            Opacity = 0.65,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };

        ContentPanel.Children.Add(_marketStatus);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

        buttons.Children.Add(AsyncAction("同步索引", async () =>
        {
            try
            {
                await _market.CheckUpdatesAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"同步失败：{ex.Message}");
            }
        }, primary: true));

        buttons.Children.Add(AsyncAction("刷新列表", async () =>
        {
            try
            {
                await _market.RescanAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"刷新失败：{ex.Message}");
            }
        }));

        buttons.Children.Add(AsyncAction("全部更新", async () =>
        {
            try
            {
                await _market.UpdateAllAsync();
            }
            catch (Exception ex)
            {
                AppendLog($"批量更新失败：{ex.Message}");
            }
        }));

        ContentPanel.Children.Add(buttons);

        _marketList = new StackPanel();
        ContentPanel.Children.Add(_marketList);

        RenderMarketList(_market.Catalog);

        AddSectionLabel("运行日志", top: 14);

        _marketLogBox = new TextBox
        {
            Text = string.Join(Environment.NewLine, _marketLog),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 110,
            MaxHeight = 180,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            Padding = new Thickness(8),
        };

        ContentPanel.Children.Add(_marketLogBox);
    }

    private void RenderMarketList(IReadOnlyList<MarketEntry> entries)
    {
        if (_marketList is null) return;

        _marketList.Children.Clear();

        if (entries.Count == 0)
        {
            _marketList.Children.Add(new TextBlock
            {
                Text = "索引尚未同步，点「同步索引」从插件服务器获取列表。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            return;
        }

        foreach (var entry in entries) _marketList.Children.Add(BuildMarketRow(entry));
    }

    private FrameworkElement BuildMarketRow(MarketEntry entry)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 2),
            BorderThickness = new Thickness(1),
        };

        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // ---------- 左：名称 / 版本 / 作者 / 说明 ----------
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontWeight = FontWeight.FromOpenTypeWeight(600),
            VerticalAlignment = VerticalAlignment.Center,
        });

        titleRow.Children.Add(new TextBlock
        {
            Text = entry.Version,
            Opacity = 0.55,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        info.Children.Add(titleRow);

        var meta = entry.Author.Length > 0 ? $"{entry.Id} · {entry.Author}" : entry.Id;

        if (entry.InstalledVersion.Length > 0 && entry.UpdateAvailable)
            meta += $" · 已装 {entry.InstalledVersion}";

        info.Children.Add(new TextBlock
        {
            Text = meta,
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        if (entry.Description.Length > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = entry.Description,
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        if (entry.Problem.Length > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = $"⚠ {entry.Problem}",
                FontSize = 11.5,
                Foreground = ErrorBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        else if (entry.LoadError.Length > 0)
        {
            info.Children.Add(new TextBlock
            {
                Text = $"加载失败：{entry.LoadError}",
                FontSize = 11.5,
                Foreground = ErrorBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        grid.Children.Add(info);

        // ---------- 中：状态徽章 ----------
        var badge = new TextBlock
        {
            Text = StatusText(entry.Status),
            Foreground = StatusBrush(entry.Status),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
        };

        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        // ---------- 右：操作 ----------
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var id = entry.Id;

        switch (entry.Status)
        {
            case MarketEntryStatus.Invalid:
                actions.Children.Add(Action("不可安装", () => { }, enabled: false));
                break;

            case MarketEntryStatus.NotInstalled:
                actions.Children.Add(Action("安装", () => InstallPlugin(id), primary: true));
                break;

            case MarketEntryStatus.Update:
                actions.Children.Add(Action("更新", () => InstallPlugin(id), primary: true));
                actions.Children.Add(Action("卸载", () => UninstallPlugin(id)));
                break;

            case MarketEntryStatus.Disabled:
                actions.Children.Add(Action("启用", () => _market.SetEnabled(id, true)));
                actions.Children.Add(Action("卸载", () => UninstallPlugin(id)));
                break;

            default:
                actions.Children.Add(Action("卸载", () => UninstallPlugin(id)));
                break;
        }

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        card.Child = grid;
        return card;
    }

    private async void InstallPlugin(string pluginId)
    {
        try
        {
            var result = await _market.InstallAsync(pluginId);
            if (!result.Success) AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            AppendLog($"安装失败：{ex.Message}");
        }
    }

    private async void UninstallPlugin(string pluginId)
    {
        try
        {
            var released = _packages.ReleasePluginDependencies(pluginId);
            var result = await _market.UninstallAsync(pluginId);

            if (released.Count > 0) AppendLog($"顺带回收依赖 {string.Join("、", released)}");
            if (!result.Success) AppendLog(result.Message);
        }
        catch (Exception ex)
        {
            AppendLog($"卸载失败：{ex.Message}");
        }
    }

    private static string StatusText(string status) => status switch
    {
        MarketEntryStatus.Installed => "已安装",
        MarketEntryStatus.Update => "可更新",
        MarketEntryStatus.Disabled => "已禁用",
        MarketEntryStatus.Invalid => "索引异常",
        MarketEntryStatus.Failed => "加载失败",
        _ => "未安装",
    };

    private static Brush StatusBrush(string status) => status switch
    {
        MarketEntryStatus.Installed => OkBrush,
        MarketEntryStatus.Update => AccentBrush,
        MarketEntryStatus.Invalid or MarketEntryStatus.Failed => ErrorBrush,
        MarketEntryStatus.Disabled => WarnBrush,
        _ => MutedBrush,
    };

    // ================= 依赖包区 =================

    private void RenderPackagesSection()
    {
        AddSectionLabel("第三方包管理", top: 16);

        ContentPanel.Children.Add(new TextBlock
        {
            Text = "第三方依赖由插件在 plugin.json 的 requires 中声明，安装插件时会自动准备。"
                   + "此处也可以手动预装或清理。",
            FontSize = 11.5,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // ---- 下载源 ----
        var sourceRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 8),
        };

        sourceRow.Children.Add(new TextBlock
        {
            Text = "下载源",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        var sourceBox = new ComboBox { MinWidth = 130, Margin = new Thickness(0, 0, 8, 0) };

        foreach (var (label, id) in PackageService.Sources) sourceBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });

        var currentSource = _packages.SourceId;
        for (var i = 0; i < sourceBox.Items.Count; i++)
        {
            if (sourceBox.Items[i] is ComboBoxItem { Tag: string id } && id == currentSource)
            {
                sourceBox.SelectedIndex = i;
                break;
            }
        }

        sourceBox.SelectionChanged += (_, _) =>
        {
            if (sourceBox.SelectedItem is not ComboBoxItem { Tag: string id }) return;
            if (!_packages.SetSource(id)) return;

            AppendLog($"下载源已切换为 {id}");
            _ = RefreshPackagesAsync();
        };

        sourceRow.Children.Add(sourceBox);

        sourceRow.Children.Add(Action("刷新", () => _ = RefreshPackagesAsync()));

        ContentPanel.Children.Add(sourceRow);

        // ---- 手动安装 ----
        var installRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 10),
        };

        var nameBox = new TextBox
        {
            MinWidth = 180,
            Padding = new Thickness(6, 4, 6, 4),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "小写字母开头，可含数字 / 点 / 下划线 / 短横线",
        };

        installRow.Children.Add(nameBox);

        installRow.Children.Add(AsyncAction("安装指定包", async () =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0) return;

            var result = await _packages.InstallAsync(name);
            AppendLog($"{name}：{result.Message}");
        }, primary: true));

        ContentPanel.Children.Add(installRow);

        _packageList = new StackPanel();
        ContentPanel.Children.Add(_packageList);

        RenderPackageList(_packages.Catalog);

        // 首次进入才联网拉一次；之后由「刷新」按钮驱动，
        // 避免每次切到本页都打一轮远端请求
        if (_packages.Catalog.Count == 0) _ = RefreshPackagesAsync();
    }

    private async Task RefreshPackagesAsync()
    {
        try
        {
            await _packages.RefreshAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"依赖包列表刷新失败：{ex.Message}");
        }
    }

    private void RenderPackageList(IReadOnlyList<PackageCatalogEntry> entries)
    {
        if (_packageList is null) return;

        _packageList.Children.Clear();

        if (entries.Count == 0)
        {
            _packageList.Children.Add(new TextBlock
            {
                Text = "还没有依赖包。插件声明的依赖会在安装时自动准备。",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 4),
            });

            return;
        }

        foreach (var entry in entries)
        {
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();

            var title = entry.Installed
                ? $"{entry.Name} {entry.InstalledVersion}"
                : entry.Version.Length > 0 ? $"{entry.Name} {entry.Version}" : entry.Name;

            info.Children.Add(new TextBlock { Text = title });

            var detail = entry.UsedBy.Count > 0
                ? "被以下插件使用：" + string.Join("、", entry.UsedBy)
                : entry.Manual ? "手动安装" : "暂无插件使用";

            if (entry.Version.Length > 0 && entry.InstalledVersion.Length > 0
                && !string.Equals(entry.Version, entry.InstalledVersion, StringComparison.Ordinal))
            {
                detail += $" · 远端 {entry.Version}";
            }

            info.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Opacity = 0.6,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
            });

            row.Children.Add(info);

            var badge = new TextBlock
            {
                Text = entry.StatusLabel,
                Foreground = entry.Status switch
                {
                    MarketEntryStatus.Installed => OkBrush,
                    MarketEntryStatus.Update => AccentBrush,
                    _ => MutedBrush,
                },
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
            };

            Grid.SetColumn(badge, 1);
            row.Children.Add(badge);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var name = entry.Name;

            if (!entry.Installed)
            {
                actions.Children.Add(AsyncAction("安装", async () =>
                {
                    var result = await _packages.InstallAsync(name);
                    AppendLog($"{name}：{result.Message}");
                }, primary: true));
            }
            else
            {
                actions.Children.Add(AsyncAction("重装", async () =>
                {
                    var result = await _packages.InstallAsync(name);
                    AppendLog($"{name}：{result.Message}");
                }));

                actions.Children.Add(AsyncAction("删除", async () =>
                {
                    if (entry.UsedBy.Count > 0)
                    {
                        MessageBox.Show(this,
                            $"以下插件仍在使用它：{string.Join("、", entry.UsedBy)}\n"
                            + "删除后这些插件可能无法正常工作。",
                            "依赖包", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    var result = await _packages.UninstallAsync(name);
                    AppendLog($"{name}：{result.Message}");
                }));
            }

            Grid.SetColumn(actions, 2);
            row.Children.Add(actions);

            _packageList.Children.Add(row);
        }
    }

    // ================= 小工具 =================

    private static Button Action(string text, Action onClick, bool primary = false, bool enabled = true)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = enabled,
        };

        if (primary) button.FontWeight = FontWeight.FromOpenTypeWeight(600);

        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>异步按钮：点击后立刻返回，不阻塞界面线程。</summary>
    private static Button AsyncAction(string text, Func<Task> onClick, bool primary = false)
        => Action(text, () => _ = onClick(), primary);
}
