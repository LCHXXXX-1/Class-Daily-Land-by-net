using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.App.Views;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Duty;
using ClassDailyLand.Infrastructure.Homework;
using ClassDailyLand.Infrastructure.Market;
using ClassDailyLand.Infrastructure.Paths;
using ClassDailyLand.Infrastructure.Schedule;
using ClassDailyLand.Infrastructure.Settings;
using ClassDailyLand.Infrastructure.State;
using ClassDailyLand.Infrastructure.Storage;
using ClassDailyLand.Infrastructure.Updates;
using ClassDailyLand.PluginHost;
using Microsoft.Extensions.DependencyInjection;

// 本项目同时启用 WPF 与 WinForms（托盘图标），两者都有 Application / MessageBox，
// 显式指定用 WPF 版本，避免二义性。
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ClassDailyLand.App;

/// <summary>
/// 组合根。对应源模块：Class Daily Land.py 的 __main__ 段。
///
/// 装配顺序刻意与源项目保持一致：
/// 单实例锁 → 建目录 → 协议门禁 → 设置 → 主题 → 课表 → 运行时状态 →
/// 插件运行时 → 三窗口 → 控制器接线 → 托盘 → 加载插件 → 应用设置 → 进入事件循环。
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _provider;
    private AppController? _controller;
    private PluginManager? _pluginManager;
    private PluginRuntime? _pluginRuntime;
    private MainWindow? _main;
    private IslandWindow? _island;
    private SubIslandWindow? _subIsland;

    /// <summary>当前是否处于 --capture 无人值守诊断模式。</summary>
    private static bool _capturing;

    /// <summary>启动参数要求了诊断捕获（--capture &lt;目录&gt;）。</summary>
    private static bool _captureRequested;

    /// <summary>--capture 的输出目录。</summary>
    private static string _captureDirectory = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 全局异常落盘：静默崩溃时至少留下现场 ----
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("UI线程", e.Exception);

            // 无人值守捕获时不许挂住：记下现场直接退出
            if (_capturing)
            {
                e.Handled = true;
                Shutdown(1);
                return;
            }

            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("后台线程", e.ExceptionObject as Exception);

        // 诊断捕获是无人值守运行：已有实例在跑时静默退出，
        // 不弹窗等人工确认（弹窗曾把后台捕获挂住好几分钟）
        if (TryGetCaptureDirectory(out _captureDirectory)) _captureRequested = true;

        if (!SingleInstanceGuard.TryAcquire(out var message))
        {
            if (!_captureRequested)
            {
                MessageBox.Show(message, "Class Daily Land", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown(0);
            return;
        }

        try
        {
            Compose();
        }
        catch (Exception ex)
        {
            WriteCrashLog("启动", ex);
            MessageBox.Show($"启动失败：{ex.Message}", "Class Daily Land",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>把未处理异常追加到 exe 旁的 crash-log.txt。</summary>
    private static void WriteCrashLog(string source, Exception? exception)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "crash-log.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}]\n{exception}\n\n");
        }
        catch
        {
            // 落盘失败只能放弃，不能再抛
        }
    }

    // ================= 装配 =================

    private void Compose()
    {
        _provider = BuildContainer();

        var paths = Require<IPathService>();
        paths.EnsureDirectories();

        // ---- 用户协议门禁（未同意直接退出）----
        if (!EnsureAgreementAccepted(paths))
        {
            Shutdown(0);
            return;
        }

        // ---- 数据层：注意加载顺序，DutyService 构造时依赖已加载的 RuntimeConfig ----
        var settings = Require<ISettingsService>();
        settings.Load();

        Require<IRuntimeConfigService>().Load();
        Require<IScheduleService>().Load();

        Require<IDutyService>();          // 构造即加载名单
        Require<IHomeworkService>();      // 构造即加载作业
        Require<IAttendanceService>();

        // ---- 主题 ----
        ThemeBridge.Apply(settings.Current.ThemeMode);

        // ---- UI ----
        _controller = Require<AppController>();

        var main = Require<MainWindow>();
        _main = main;
        _island = Require<IslandWindow>();
        var subIsland = Require<SubIslandWindow>();
        _subIsland = subIsland;

        _controller.Attach(main, _island, subIsland);

        // 主窗口右键菜单的「设置」入口
        main.SettingsRequested += (_, _) => ShowSettings();

        var bridge = Require<UiBridge>();
        bridge.Island = _island;
        bridge.RequestRefresh = _controller.NotifyIslandDirty;

        var tray = new TrayIconHost(
            _controller,
            settings,
            paths,
            openSettings: ShowSettings,
            addPlugin: OpenAddPluginDialog,
            exit: ExitApplication,
            openHolidays: ShowHolidays,
            openWeekend: ShowWeekend);

        _controller.AttachTray(tray);

        // ---- 设置变更统一分发 ----
        settings.Changed += (_, _) =>
        {
            ThemeBridge.Apply(settings.Current.ThemeMode);
            _controller.ApplySettings();
        };

        // ---- 副岛折叠控制权交给插件运行时（对应 set_subisland_control）----
        _pluginRuntime = Require<PluginRuntime>();
        _pluginRuntime.SetSubIslandControl(collapsed =>
        {
            if (collapsed) subIsland.Collapse();
            else subIsland.Expand();
        });

        // ---- 插件加载 ----
        _pluginManager = Require<PluginManager>();
        _pluginManager.PluginStopping += (_, plugin) => _pluginRuntime.NotifyStopping(plugin.Id);

        // 先解压 plugins/ 下待安装的 .cblplugin 包，再统一扫描目录
        // （对应源项目 PluginManager._install_packages 的执行时机）
        MarketService? market = null;
        try
        {
            market = Require<MarketService>();
            market.InstallPendingPackages();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[market] 预装插件包失败: {ex.Message}");
        }

        _pluginManager.LoadAll(settings.Current.DisabledPlugins, _pluginRuntime.CreateApi);

        // 把加载失败信息交给市场，让「插件」页能直接点名原因
        if (market is not null)
        {
            market.LoadFailures = _pluginManager.Failures
                .GroupBy(f => f.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Reason, StringComparer.OrdinalIgnoreCase);
        }

        // ---- 首次渲染与应用设置 ----
        _controller.ApplySettings();

        main.Show();
        _island.SetEnabled(settings.Current.ShowIsland);
        _island.StartTicking();
        subIsland.SetEnabled(settings.Current.ShowSubIsland);

        tray.Show();

        // ---- 插件生命周期回调 ----
        _pluginRuntime.NotifyLoaded();
        _pluginRuntime.NotifyStarted();

        // ---- 更新链路 ----
        SetupQuitWatcher();
        ScheduleAutoUpdateCheck();

        // ---- 诊断模式：把三个窗口渲染成 PNG 后退出（--capture <目录>）----
        if (_captureRequested)
        {
            _capturing = true;
            ScheduleCapture(_captureDirectory);
        }
    }

    // ================= 更新链路 =================

    /// <summary>
    /// 轮询更新器的退出信号（对应 gui.py 的 _setup_quit_watcher）。
    /// 更新器替换文件前会写一个标志文件，主程序看到就自行退出。
    /// </summary>
    private void SetupQuitWatcher()
    {
        UpdateLauncher.ClearQuitFlag();

        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(500) };

        timer.Tick += (_, _) =>
        {
            if (!UpdateLauncher.QuitRequested()) return;

            timer.Stop();
            UpdateLauncher.ClearQuitFlag();

            ExitApplication();
        };

        timer.Start();
    }

    /// <summary>
    /// 启动 2 秒后后台静默检查更新（对应 _auto_check_update）。
    /// 延迟是为了先让主窗口露出来，别一开机就被框住。
    /// </summary>
    private void ScheduleAutoUpdateCheck()
    {
        var settings = Require<ISettingsService>();
        if (!settings.Current.CheckUpdateOnStart) return;

        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle)
        {
            Interval = TimeSpan.FromSeconds(2),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                var launcher = Require<UpdateLauncher>();
                var result = launcher.LaunchHidden();

                // 被源码保护拦下：弹一次说明为什么没更新（只有一个「知道了」，不要求做决定）
                if (!result.Spawned && result.Hits is { Count: > 0 } hits)
                {
                    MessageBox.Show(
                        "检测到程序目录下有未打包的主入口源码，已跳过自动更新。\n\n"
                        + UpdateLauncher.BackupTip(hits) + "\n\n"
                        + "（想更新可以在「设置 → 关于 → 检查更新」里手动触发，届时会再确认一次）",
                        "源码运行 · 已跳过自动更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[update] 自动检查更新失败: {ex.Message}");
            }
        };

        timer.Start();
    }

    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();

        // ---- 基础设施 ----
        services.AddSingleton<IPathService, AppPathService>();
        services.AddSingleton<IJsonStore, JsonStore>();

        // ---- 数据与配置 ----
        services.AddSingleton<IRuntimeConfigService, RuntimeConfigService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IScheduleService, ScheduleService>();
        services.AddSingleton<IDutyService, DutyService>();
        services.AddSingleton<IAttendanceService, AttendanceService>();
        services.AddSingleton<IHomeworkService, HomeworkService>();

        // ---- 插件 ----
        services.AddSingleton<PluginManager>();

        // ---- 插件市场与依赖包 ----
        services.AddSingleton<MarketService>();
        services.AddSingleton<PackageService>();

        // ---- 更新链路 ----
        services.AddSingleton<UpdateLauncher>();
        services.AddSingleton<UiBridge>();
        services.AddSingleton(sp => new PluginRuntime(
            sp.GetRequiredService<IJsonStore>(),
            () => sp.GetRequiredService<IScheduleService>().GetStatus(),
            (text, isEnd, force) => sp.GetRequiredService<UiBridge>().Island?.Notify(text, isEnd, force),
            () => sp.GetRequiredService<UiBridge>().RequestRefresh?.Invoke()));

        // ---- UI ----
        services.AddSingleton<AppController>();
        services.AddSingleton<IAppController>(sp => sp.GetRequiredService<AppController>());

        services.AddSingleton<MainWindow>();
        services.AddSingleton<IslandWindow>();
        services.AddSingleton<SubIslandWindow>();
        services.AddTransient<SettingsWindow>();

        return services.BuildServiceProvider();
    }

    private T Require<T>() where T : notnull => _provider!.GetRequiredService<T>();

    // ================= 用户协议 =================

    private static bool EnsureAgreementAccepted(IPathService paths)
    {
        var store = new JsonStore(paths);

        var state = store.Read<AgreementState>("agreement.json");
        if (state?.Agreed == true) return true;

        var dialog = new AgreementWindow();
        if (dialog.ShowDialog() != true) return false;

        store.Write("agreement.json", new AgreementState { Agreed = true });
        return true;
    }

    // ================= 托盘的入口 =================

    /// <summary>打开设置中心（对应源项目 tray / 主窗口菜单的「设置」）。</summary>
    private void ShowSettings()
    {
        var window = Require<SettingsWindow>();
        if (_main is not null && _main.IsVisible) window.Owner = _main;

        window.ShowDialog();
    }

    /// <summary>假期与调休（对应托盘 on_holidays）。</summary>
    private void ShowHolidays()
    {
        var dialog = new HolidayDialog(Require<IScheduleService>());
        if (_main is not null && _main.IsVisible) dialog.Owner = _main;

        dialog.ShowDialog();
        _controller?.NotifyIslandDirty();
    }

    /// <summary>周末作息（对应托盘 on_weekend）。</summary>
    private void ShowWeekend()
    {
        var dialog = new WeekendDialog(Require<IScheduleService>());
        if (_main is not null && _main.IsVisible) dialog.Owner = _main;

        dialog.ShowDialog();
        _controller?.NotifyIslandDirty();
    }

    /// <summary>把插件文件夹复制进 plugins/ 目录（对应源项目的「添加插件」）。</summary>
    private static void OpenAddPluginDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件文件夹中的入口程序集（.dll）",
            Filter = "插件程序集 (*.dll)|*.dll|所有文件 (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        var paths = new AppPathService();
        paths.EnsureDirectories();

        var copied = 0;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                File.Copy(file, Path.Combine(paths.PluginsDirectory, Path.GetFileName(file)), overwrite: true);
                copied++;
            }
            catch (IOException)
            {
                // 单个文件失败不中断其余文件
            }
        }

        MessageBox.Show(
            copied == 0
                ? "没有文件被复制。"
                : $"已复制 {copied} 个文件到 plugins/ 目录。\n请把插件的 plugin.json 也放入同一目录，然后重启程序。",
            "Class Daily Land",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // ================= 诊断：窗口渲染快照 =================

    /// <summary>解析 <c>--capture &lt;目录&gt;</c> 参数。</summary>
    private static bool TryGetCaptureDirectory(out string directory)
    {
        directory = "";

        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--capture", StringComparison.OrdinalIgnoreCase)) continue;

            directory = args[i + 1];
            return !string.IsNullOrWhiteSpace(directory);
        }

        return false;
    }

    /// <summary>可选的捕获延迟秒数（--capture-delay &lt;秒&gt;）。用于等真实倒计时 / 提醒走完再截图。</summary>
    private static int TryGetCaptureDelaySeconds()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--capture-delay", StringComparison.OrdinalIgnoreCase)) continue;

            return int.TryParse(args[i + 1], out var seconds) && seconds > 0 ? seconds : 0;
        }

        return 0;
    }

    /// <summary>
    /// 等待窗口动画结束后把各窗口渲染为 PNG 并退出。
    /// 分两阶段：先基础界面，再展开灵动岛下拉面板后捕获面板。
    /// </summary>
    /// <summary>--capture-delay 只消费一次：延迟回调里再进 ScheduleCapture 时不再重读命令行
    /// （否则会无限给自己排延迟，一张图也拍不出来）。</summary>
    private bool _captureDelayConsumed;

    private void ScheduleCapture(string directory)
    {
        // --capture-delay：先等真实状态机走完一段（例如倒计时 → 上课提醒 → 恢复），再截图
        var delaySeconds = _captureDelayConsumed ? 0 : TryGetCaptureDelaySeconds();
        _captureDelayConsumed = true;

        if (delaySeconds > 0)
        {
            var waiter = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(delaySeconds),
            };

            waiter.Tick += (_, _) =>
            {
                waiter.Stop();
                ScheduleCapture(directory);
            };

            waiter.Start();
            return;
        }

        var first = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1800),   // 需覆盖灵动岛唤醒动画时长
        };

        first.Tick += (_, _) =>
        {
            first.Stop();

            try
            {
                Directory.CreateDirectory(directory);
                CaptureWindow(_main, Path.Combine(directory, "01-main-window.png"), null);
                CaptureWindow(_island, Path.Combine(directory, "02-island.png"), CanvasBrush);

                // 岛的状态快照必须趁现在写：后面的状态测试捕获会跑情景演示，
                // 把岛重置回 Compact，到时就看不出倒计时 / 提醒的真实几何了
                File.WriteAllText(
                    Path.Combine(directory, "capture-island-state.txt"),
                    _island.CaptureDiagnostics);

                CaptureWindow(_subIsland, Path.Combine(directory, "03-sub-island.png"), CanvasBrush);
                CaptureSettings(directory);
                CaptureStatusTest(directory);
                CaptureCourseEditor(directory);
                CaptureScenario(directory);
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "capture-error.txt"), ex.ToString());
                Shutdown(0);
                return;
            }

            // 第二阶段：展开下拉面板，等错峰动画结束后逐个捕获
            try
            {
                _island?.OpenPanelsForCapture();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "capture-panel-error.txt"), ex.ToString());
                Shutdown(0);
                return;
            }

            var second = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                // 覆盖错峰展开，但要赶在自动收起之前
                Interval = TimeSpan.FromMilliseconds(1200),
            };

            second.Tick += (_, _) =>
            {
                second.Stop();

                try
                {
                    var panels = _island?.CapturePanels ?? Array.Empty<IslandPanelWindow>();

                    if (_island is not null)
                    {
                        // 总是写：面板没展开时交代原因；展开了就记录窗口几何
                        // （屏幕中线偏移量，用来验证倒计时 / 提醒条居中）
                        File.WriteAllText(
                            Path.Combine(directory, "capture-panel-state.txt"),
                            _island.CaptureDiagnostics);
                    }

                    for (var i = 0; i < panels.Count; i++)
                    {
                        CaptureWindow(
                            panels[i],
                            Path.Combine(directory, $"05-panel-{i + 1}.png"),
                            CanvasBrush);
                    }
                }
                catch (Exception ex)
                {
                    File.WriteAllText(Path.Combine(directory, "capture-panel-error.txt"), ex.ToString());
                }
                finally
                {
                    // 情景演示的两张验证图还没拍完就先别退出
                    if (_scenarioRendersPending <= 0) Shutdown(0);
                }
            };

            second.Start();
        };

        first.Start();
    }

    /// <summary>自绘窗口背景透明，渲染时铺一层浅灰便于查看。</summary>
    private static readonly Brush CanvasBrush =
        new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

    /// <summary>情景演示还没拍完的验证图数量；归零后捕获流程才允许退出。</summary>
    private int _scenarioRendersPending;

    /// <summary>
    /// 情景演示捕获：8 倍速跑一轮「提前 → 倒计时 → 上课提醒 → 下课提醒」，
    /// 在倒计时相与收尾恢复后各渲染一次岛并记录窗口几何。
    /// 相比墙钟播种（改配置文件等真实时刻到来），这条路径是确定性的。
    /// </summary>
    private void CaptureScenario(string directory)
    {
        try
        {
            var controller = Require<IAppController>();
            if (_island is null) return;

            // advanceSec=120、countdownSec=60、endHoldSec=60，8 倍速：
            // T1=15s 倒计时开始，T2=22.5s 上课了，T3≈30.7s 下课了，T4≈31.3s 收尾
            if (!controller.RunScenarioTest(120, 60, 60, 8.0))
            {
                _scenarioRendersPending = 0;
                return;
            }

            _scenarioRendersPending = 2;
            RenderIslandAt(directory, TimeSpan.FromSeconds(19), "13-scenario-countdown.png");
            RenderIslandAt(directory, TimeSpan.FromSeconds(34.5), "14-scenario-after-alert.png");

            // 兜底：渲染定时器万一没触发，也不能让捕获流程永远不退出
            var failsafe = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromSeconds(75),
            };
            failsafe.Tick += (_, _) =>
            {
                failsafe.Stop();
                Shutdown(0);
            };
            failsafe.Start();
        }
        catch (Exception ex)
        {
            _scenarioRendersPending = 0;
            File.WriteAllText(Path.Combine(directory, "capture-scenario-error.txt"), ex.ToString());
        }
    }

    /// <summary>延时渲染一次灵动岛并记录几何。</summary>
    private void RenderIslandAt(string directory, TimeSpan delay, string fileName)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = delay,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            try
            {
                if (_island is not null)
                {
                    CaptureWindow(_island, Path.Combine(directory, fileName), CanvasBrush);
                    File.WriteAllText(
                        Path.Combine(directory, Path.ChangeExtension(fileName, ".txt")),
                        _island.CaptureDiagnostics);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(directory, "capture-scenario-error.txt"), ex.ToString());
            }
            finally
            {
                _scenarioRendersPending--;
                if (_scenarioRendersPending <= 0) Shutdown(0);
            }
        };

        timer.Start();
    }

    /// <summary>设置中心为按需创建的模态窗口，捕获时临时以非模态方式显示一次。</summary>
    private void CaptureSettings(string directory)
    {
        SettingsWindow? window = null;

        try
        {
            window = Require<SettingsWindow>();
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 40;
            window.Top = 40;
            window.Show();
            window.UpdateLayout();

            CaptureWindow(window, Path.Combine(directory, "04-settings.png"), null);

            // 课表与提醒页带二级页索引大卡，滚到底再拍一张
            if (window.SelectPageForCapture("课表与提醒"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "04b-settings-schedule.png"), null);

                window.ScrollToEndForCapture();
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "04c-settings-schedule-links.png"), null);
            }

            // 插件页：市场 + 已安装 + 依赖包
            if (window.SelectPageForCapture("插件"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "07-settings-plugins.png"), null);
            }

            // 高级页与关于页
            if (window.SelectPageForCapture("高级"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "08-settings-advanced.png"), null);
            }

            if (window.SelectPageForCapture("关于"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "09-settings-about.png"), null);
            }

            // 作业页与课表与提醒页
            if (window.SelectPageForCapture("作业"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "10-settings-homework.png"), null);
            }

            if (window.SelectPageForCapture("课表与提醒"))
            {
                window.UpdateLayout();
                CaptureWindow(window, Path.Combine(directory, "11-settings-schedule-hub.png"), null);
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "capture-settings-error.txt"), ex.ToString());
        }
        finally
        {
            window?.Close();
        }
    }

    /// <summary>状态测试同样是按需打开的模态窗口，捕获时临时显示一次。</summary>
    private void CaptureStatusTest(string directory)
    {
        Window? window = null;

        try
        {
            window = new StatusTestDialog(
                Require<IScheduleService>(),
                Require<ISettingsService>(),
                Require<IAppController>())
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 60,
                Top = 60,
                SkipEntranceAnimation = true,
            };

            window.Show();
            window.UpdateLayout();

            CaptureWindow(window, Path.Combine(directory, "06-status-test.png"), null);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "capture-status-test-error.txt"), ex.ToString());
        }
        finally
        {
            window?.Close();
        }
    }

    /// <summary>课表编辑器同样按需打开，捕获时临时显示当前课表。</summary>
    private void CaptureCourseEditor(string directory)
    {
        Window? window = null;

        try
        {
            var schedule = Require<IScheduleService>();

            window = new CourseEditorDialog(schedule, schedule.ActiveName)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 40,
                Top = 40,
            };

            window.Show();
            window.UpdateLayout();

            CaptureWindow(window, Path.Combine(directory, "12-course-editor.png"), null);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "capture-course-editor-error.txt"), ex.ToString());
        }
        finally
        {
            window?.Close();
        }
    }

    /// <summary>把窗口内容渲染为 PNG。</summary>
    private static void CaptureWindow(Window? window, string path, Brush? canvas)
    {
        if (window?.Content is not FrameworkElement content) return;

        content.UpdateLayout();

        var width = (int)Math.Ceiling(window.ActualWidth > 0 ? window.ActualWidth : window.Width);
        var height = (int)Math.Ceiling(window.ActualHeight > 0 ? window.ActualHeight : window.Height);
        if (width <= 0 || height <= 0) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(canvas ?? window.Background ?? Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(
                new VisualBrush(content)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top,
                },
                null,
                new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ================= 退出 =================

    private void ExitApplication()
    {
        _island?.StopTicking();
        _pluginManager?.StopAll();
        _controller?.Shutdown();

        SingleInstanceGuard.Release();

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _island?.StopTicking();
        _controller?.Shutdown();
        _provider?.Dispose();
        SingleInstanceGuard.Release();

        base.OnExit(e);
    }
}
// 才不想被看到呢～