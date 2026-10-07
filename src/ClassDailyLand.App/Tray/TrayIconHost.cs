using System.IO;
using ClassDailyLand.Core.Abstractions;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 系统托盘。对应源模块：tray_icon.py 的 AppTray。
///
/// 实现说明：使用 WinForms 的 NotifyIcon，避免为了一个托盘图标引入第三方依赖。
/// WPF 与 WinForms 在同一进程中混用是官方支持的做法（csproj 已开启 UseWindowsForms）。
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    /// <summary>可切换的可见性目标。</summary>
    private enum ToggleTarget
    {
        Main,
        Island,
        SubIsland,
    }

    private readonly AppController _controller;
    private readonly ISettingsService _settings;
    private readonly IPathService _paths;

    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _mainItem;
    private readonly WinForms.ToolStripMenuItem _islandItem;
    private readonly WinForms.ToolStripMenuItem _subIslandItem;

    private bool _disposed;

    public TrayIconHost(
        AppController controller,
        ISettingsService settings,
        IPathService paths,
        Action openSettings,
        Action addPlugin,
        Action exit,
        Action? openHolidays = null,
        Action? openWeekend = null)
    {
        _controller = controller;
        _settings = settings;
        _paths = paths;

        _mainItem = new WinForms.ToolStripMenuItem("显示班级日常");
        _mainItem.Click += (_, _) => Toggle(ToggleTarget.Main);

        _islandItem = new WinForms.ToolStripMenuItem("显示灵动岛");
        _islandItem.Click += (_, _) => Toggle(ToggleTarget.Island);

        _subIslandItem = new WinForms.ToolStripMenuItem("显示副岛");
        _subIslandItem.Click += (_, _) => Toggle(ToggleTarget.SubIsland);

        var menu = new WinForms.ContextMenuStrip();

        menu.Items.Add(_mainItem);
        menu.Items.Add(_islandItem);
        menu.Items.Add(_subIslandItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 假期与周末入口（对应源项目 on_holidays / on_weekend）
        if (openHolidays is not null)
            menu.Items.Add(new WinForms.ToolStripMenuItem("假期与调休", null, (_, _) => openHolidays()));

        if (openWeekend is not null)
            menu.Items.Add(new WinForms.ToolStripMenuItem("周末作息", null, (_, _) => openWeekend()));

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("设置", null, (_, _) => openSettings()));
        menu.Items.Add(new WinForms.ToolStripMenuItem("安装插件", null, (_, _) => addPlugin()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(new WinForms.ToolStripMenuItem("退出", null, (_, _) => exit()));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Class Daily Land",
            ContextMenuStrip = menu,
            Visible = false,
        };

        // 单击或双击托盘图标 → 打开设置（与源项目 _on_activated 行为一致）
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) openSettings();
        };

        SyncState();
    }

    public void Show()
    {
        if (_disposed) return;
        _notifyIcon.Visible = true;
    }

    /// <summary>同步菜单勾选状态（对应源项目 tray.sync_state）。</summary>
    public void SyncState()
    {
        if (_disposed) return;

        var settings = _settings.Current;
        _mainItem.Checked = settings.ShowMainWindow;
        _islandItem.Checked = settings.ShowIsland;
        _subIslandItem.Checked = settings.ShowSubIsland;
    }

    /// <summary>
    /// 切换某一项可见性。
    /// 只改设置并落盘，界面更新交给设置变更订阅统一处理 ——
    /// 这样托盘、菜单、设置面板三条入口的行为完全一致。
    /// </summary>
    private void Toggle(ToggleTarget target)
    {
        _settings.Update(settings =>
        {
            switch (target)
            {
                case ToggleTarget.Main:
                    settings.ShowMainWindow = !settings.ShowMainWindow;
                    break;
                case ToggleTarget.Island:
                    settings.ShowIsland = !settings.ShowIsland;
                    break;
                case ToggleTarget.SubIsland:
                    settings.ShowSubIsland = !settings.ShowSubIsland;
                    break;
            }
        });
    }

    /// <summary>
    /// 加载托盘图标。
    /// 找不到 icon.ico 或文件损坏时，自绘一个（#0067c0 圆角方块 + 白字「班」），
    /// 对应源项目 utils.py 的 _fallback_icon —— 保证托盘图标任何情况下都不丢失。
    /// </summary>
    private Drawing.Icon LoadIcon()
    {
        try
        {
            if (File.Exists(_paths.IconPath)) return new Drawing.Icon(_paths.IconPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            // 图标文件损坏 → 走自绘回退
        }

        return ClassDailyLand.App.Views.Controls.AppIconFactory.CreateTrayIcon();
    }

    public void ShowBalloon(string title, string text)
    {
        if (_disposed) return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = text;
        _notifyIcon.ShowBalloonTip(3000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
