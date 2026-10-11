using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClassDailyLand.App.Services;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Win32;
using Microsoft.Web.WebView2.Core;
using MediaBrush = System.Windows.Media.SolidColorBrush;
using MediaColor = System.Windows.Media.Color;

namespace ClassDailyLand.App.Views;

/// <summary>
/// 设置中心（WebView2 外壳）。对应源模块：settings_dialog_v4.py 的 SettingsDialog。
///
/// 与原 <see cref="SettingsWindow"/> 的关系：
///   两者提供同一套设置项，区别只在「谁画界面」。原来的 WPF 版本整窗重画了一遍
///   控件树（约 3,000 行），这里只做一个 WebView2 容器（约 100 行）——
///   界面本体是仓库根的 <c>ui/</c>，和 Python 版共用同一份 HTML/CSS。
///
/// 保持原生的部分（本次未动一行）：灵动岛、副岛、班级日常主窗、下拉面板、
/// 托盘、全局热键、插件宿主。托盘 / 主窗菜单打开设置时走这里，失败自动回退原生窗口。
/// </summary>
public partial class SettingsWebWindow : Window
{
    /// <summary>虚拟主机名（等价于一个只在进程内可见的域名，避免 file:// 的权限问题）。</summary>
    private const string VirtualHost = "cdl.ui";

    /// <summary>页面多久没来报到就判定「起不来」。冷启动 + 首次建用户数据目录最多两三秒，
    /// 给到 10 秒足够，也不会让用户对着空窗口干等。</summary>
    private const int BootTimeoutSeconds = 10;

    /// <summary>浅/深两套底色。与 ui/ 的 --solid-background-fill-color-base 一致，
    /// 免得加载过程中闪一下刺白。</summary>
    private static readonly MediaColor LightBackdrop = MediaColor.FromRgb(0xF3, 0xF3, 0xF3);
    private static readonly MediaColor DarkBackdrop = MediaColor.FromRgb(0x20, 0x20, 0x20);

    private readonly WebUiBridge _bridge;
    private readonly ISettingsService _settings;
    private readonly IPathService _paths;

    private bool _booted;

    /// <summary>
    /// 上一次真正写进 WebView2 的深/浅色。
    ///
    /// Web 版必须自己记住这个值：<c>Profile.PreferredColorScheme</c> 与
    /// <c>DefaultBackgroundColor</c> 都是「写一次就让整块绘制表面重画」的 API ——
    /// 哪怕写进去的值和原来一模一样。而 <c>_settings.Changed</c> 在**任何**设置项变化时
    /// 都会触发（拖滑块时约 150ms 一次），照原样写就成了「整个窗口一闪一闪」。
    /// 原生 SettingsWindow 早就用同样的办法挡掉了这一层（见它的 _lastAppliedDark）。
    /// </summary>
    private bool? _appliedDark;

    /// <summary>浏览内核是否已就绪（EnsureCoreWebView2Async 成功返回）。</summary>
    private bool _coreReady;

    /// <summary>页面是否已报到（收到 ui.ready 通知）。</summary>
    private bool _pageReady;

    /// <summary>当前卡在哪一步，超时提示里要写清楚。</summary>
    private string _step = "创建浏览内核";

    /// <summary>实际用的 WebView2 用户数据目录（诊断日志里要写清楚）。</summary>
    private string? _userDataFolder;

    /// <summary>试过但失败的用户数据目录，失败时一并写进诊断日志。</summary>
    private readonly List<string> _userDataAttempts = new();

    /// <summary>页面自己报上来的错误，超时兜底时一并显示。</summary>
    private readonly List<string> _pageErrors = new();

    /// <summary>收到的页面消息条数。用来区分「页面没跑起来」和「跑了但消息没收懂」。</summary>
    private int _messageCount;

    /// <summary>消息解析失败的记录（去重）。曾经有一版就是败在这里，还一点痕迹都不留。</summary>
    private readonly List<string> _messageErrors = new();

    private DispatcherTimer? _watchdog;

    public SettingsWebWindow(WebUiBridge bridge, ISettingsService settings, IPathService paths)
    {
        _bridge = bridge;
        _settings = settings;
        _paths = paths;

        InitializeComponent();

        Icon = Controls.AppIconFactory.CreateImageSource();

        // ★ 这一行是「深色/浅色切换时页面不闪」的解药 —— 别删。
        //
        //   本窗口的界面 100% 由网页画（无边框窗口，连标题栏都是页内的），
        //   它根本不需要 WPF 的主题资源。可 App.xaml.cs 切主题时写的是
        //   Application.ThemeMode，而**没显式设过 ThemeMode 的窗口会被它改到** ——
        //   离屏 WPF 探针实测：只有设过 ThemeMode 的窗口完全不受影响（Loaded /
        //   Unloaded 增量为 0），没设过的每切一次就被卸下再装上（约 53ms）。
        //   对 WebView2 宿主就是控件被摘下再挂回 —— 视觉上正是「跟刷新了一样」。
        //
        //   所以这里在构造时显式钉住一次（取当前应用主题的深浅），此后
        //   Application.ThemeMode 再怎么变都碰不到本窗口；页面配色本来就由
        //   页内的 CSS 令牌自己管，窗口那块兜底背板由 ApplyBackdrop 手动跟。
        //   注意：ThemeMode 是结构体、写相同的值也会执行，所以只写这一次。
        ThemeMode = ThemeBridge.IsDark ? ThemeMode.Dark : ThemeMode.Light;

        _bridge.Owner = this;
        _bridge.PushEvent += OnBridgeEvent;

        // 先把底色铺上再等内核 —— 否则窗口会白一下
        ApplyBackdrop();

        SourceInitialized += (_, _) => ApplyWindowChrome();
        Loaded += OnLoadedAsync;
        Closed += OnClosed;

        // 主题切换时同步网页的 prefers-color-scheme，让「跟随系统」即时生效
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>要直接打开哪一页（如托盘菜单点「灵动岛设置」→ "island"）。</summary>
    public string? StartPage { get; set; }

    /// <summary>
    /// 用户在兜底面板上点了「改用原生设置窗口」。
    /// 由 <c>App.ShowSettings()</c> 订阅：收到就把原生窗口开出来。
    /// </summary>
    public event EventHandler? NativeFallbackRequested;

    /// <summary>
    /// Web 版设置中心是否可用：界面文件在、且机器上有 WebView2 运行时。
    /// 任一不满足就让调用方回退原生窗口 —— 设置页不该因为一个可选组件就打不开。
    /// </summary>
    internal static bool IsAvailable()
    {
        if (ResolveUiDirectory() is null) return false;

        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch (Exception)
        {
            // 未安装运行时：旧版 API 直接抛 WebView2RuntimeNotFoundException
            return false;
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        // 深/浅色没变就直接返回。
        //
        // ★ 这一行是「整个窗口一闪一闪」的解药。以前这里无条件调下面两个方法，
        //   而它们都会让 WebView2 重画整块表面；拖动滑块会连续改设置，
        //   于是每一拍都重画一次。
        if (_appliedDark == ThemeBridge.IsDark) return;

        ApplyBackdrop();
        ApplyColorScheme();
    }

    // ================= 启动 =================

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        if (_booted) return;
        _booted = true;

        // 计时从「窗口出现」就开始，而不是等内核建好 ——
        // 创建内核本身也可能卡住，那同样是一片白。
        StartWatchdog();

        var uiDirectory = ResolveUiDirectory();
        if (uiDirectory is null)
        {
            Fail("没有找到界面文件 ui\\index.html。");
            return;
        }

        // ---- 创建浏览内核 ----
        // 用户数据目录要挨个试：首选跟应用走（绿色版一个文件夹带走），但那个位置
        // 有两种常见情况下根本不可用 —— 程序装在 Program Files（普通用户不可写），
        // 以及应用放在 OneDrive / 网盘里（同步进程会去动 Chromium 的锁文件与缓存，
        // 本机实测就是在这里回 0x80004004 E_ABORT，界面直接打不开）。
        var environment = (CoreWebView2Environment?)null;

        foreach (var folder in UserDataFolderCandidates(_paths))
        {
            try
            {
                Directory.CreateDirectory(folder);
                environment = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
                _userDataFolder = folder;
                break;
            }
            catch (Exception ex)
            {
                _userDataAttempts.Add("· " + folder + " → " + ex.Message);
            }
        }

        if (environment is null)
        {
            Fail("WebView2 未能创建浏览内核，以下用户数据目录都不可用：\n"
                 + string.Join("\n", _userDataAttempts));
            return;
        }

        try
        {
            await Web.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex)
        {
            Fail("WebView2 运行时不可用，未能挂上浏览内核。\n" + ex.Message);
            return;
        }

        var core = Web.CoreWebView2;
        _coreReady = true;
        _step = "加载界面";

        core.SetVirtualHostNameToFolderMapping(VirtualHost, uiDirectory, CoreWebView2HostResourceAccessKind.Allow);

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;       // 与系统设置一致，不允许 Ctrl+滚轮缩放
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;

        // ★ 让 CSS 的 app-region: drag 生效 —— 本窗口是无边框的，页内那条标题栏就是
        //   系统标题栏。开了这个之后，标题栏上的拖动由 WebView2 当非客户区处理，
        //   页面收不到 pointerdown，也就不会再去调 window.startMove。
        try
        {
            core.Settings.IsNonClientRegionSupportEnabled = true;
        }
        catch (Exception)
        {
            // 老运行时没有这个属性：标题栏拖不动，但按钮还能点，不算致命
        }

        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        core.NavigationCompleted += OnNavigationCompleted;

        ApplyColorScheme();
        ApplyBackdrop();

        // 不再带 chrome=native：本窗口无边框，页内标题栏就是唯一的窗口镶边
        var query = "?page=" + Uri.EscapeDataString(StartPage ?? "general");

        Web.Source = new Uri($"https://{VirtualHost}/index.html{query}");
    }

    /// <summary>
    /// 找 ui/ 目录：先看程序目录（发布版随包带上），再逐级向上找（开发态在 bin/… 深处）。
    /// </summary>
    private static string? ResolveUiDirectory()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ui") };

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
            candidates.Add(Path.Combine(directory.FullName, "ui"));

        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "index.html")));
    }

    /// <summary>
    /// WebView2 用户数据目录的候选，按优先级排：
    ///
    /// 1. <c>{配置目录}/webview2</c> —— 跟应用走，绿色版直接拷文件夹也不丢。
    /// 2. <c>%LOCALAPPDATA%\ClassDailyLand\webview2</c> —— 程序装在 Program Files
    ///    这类只读位置时的正解，也是浏览器数据本来就该待的地方。
    /// 3. 临时目录 —— 前两个都不行时的最后一站，至少能开。
    ///
    /// 单独做一个方法是为了能直接对着它写测试（见 tests）。
    /// </summary>
    internal static IReadOnlyList<string> UserDataFolderCandidates(IPathService paths)
    {
        var list = new List<string> { Path.Combine(paths.ConfigDirectory, "webview2") };

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
            list.Add(Path.Combine(local, "ClassDailyLand", "webview2"));

        list.Add(Path.Combine(Path.GetTempPath(), "ClassDailyLand", "webview2"));

        // 路径可能重复（比如配置目录本来就在 %LOCALAPPDATA% 下），去个重
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ================= 启动看门狗 =================

    private void StartWatchdog()
    {
        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(BootTimeoutSeconds) };
        _watchdog.Tick += OnWatchdogTick;
        _watchdog.Start();
    }

    private void StopWatchdog()
    {
        _watchdog?.Stop();
        _watchdog = null;
    }

    /// <summary>
    /// 页面一直没来报到。
    ///
    /// 这一步是「白屏」的第二道防线：前面的失败能直接拿到异常，而这里覆盖的是
    /// 「什么都没抛，但界面就是没出来」—— 以前这种情况只会留一个空窗口，
    /// 用户完全不知道发生了什么。现在至少说出卡在哪一步、页面报了什么错。
    /// </summary>
    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        StopWatchdog();
        if (_pageReady) return;

        var detail = _pageErrors.Count > 0
            ? "\n\n页面报错：\n· " + string.Join("\n· ", _pageErrors.Take(3))
            : "";

        // 消息收到了但一条都没解析成功 —— 页面其实跑起来了，问题在本侧。
        // 这种情况以前不留任何痕迹，所以专门指出来。
        if (_messageCount > 0 && _messageErrors.Count > 0)
            detail += $"\n\n已收到页面消息 {_messageCount} 条，但没有一条能解析。";

        Fail($"设置界面在 {BootTimeoutSeconds} 秒内没有就绪（卡在：{_step}）。{detail}");
    }

    // ================= RPC =================

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id = 0;
        _messageCount++;

        try
        {
            using var document = JsonDocument.Parse(ReadMessage(e));
            var root = document.RootElement;

            // 页面发来的通知（ui.ready / ui.error）是单程的，没有 id、不需要回包
            if (root.TryGetProperty("event", out var eventElement))
            {
                HandlePageEvent(eventElement.GetString() ?? "", root);
                return;
            }

            id = root.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsed) ? parsed : 0;

            var method = root.TryGetProperty("method", out var methodElement)
                ? methodElement.GetString() ?? ""
                : "";

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : default;

            var result = await _bridge.DispatchAsync(method, parameters).ConfigureAwait(true);

            Post(JsonSerializer.Serialize(new { id, result }, WebUiBridge.JsonOptions));
        }
        catch (Exception ex)
        {
            // 解析失败曾经是「静默」的：异常被当成回包发出去（id=0），页面对不上号直接丢弃，
            // 结果是页面永远等不到回包、宿主永远等不到 ui.ready，只剩一片白。
            // 现在留个记录，诊断日志里能看见。
            RecordMessageError(ex);

            Post(JsonSerializer.Serialize(new { id, error = ex.Message }, WebUiBridge.JsonOptions));
        }
    }

    /// <summary>
    /// 取出页面发来的原文（一段 JSON 文本）。
    ///
    /// 页面统一用 <c>postMessage(JSON.stringify(x))</c> 发消息 —— 也就是发一个**字符串**
    /// （Python 侧 QWebChannel 传的同样也是字符串，两端行为一致）。而 WebView2 对
    /// 「字符串」这类消息的处理很容易踩坑：
    ///
    ///   postMessage({'a':'b'})  →  WebMessageAsJson = {"a": "b"}
    ///   postMessage('example')  →  WebMessageAsJson = "example"   ← 外面多一层引号
    ///
    /// 后者是一段「JSON 字符串字面量」，JsonDocument.Parse 之后根节点是 String 类型，
    /// 对它调 TryGetProperty 会直接抛 InvalidOperationException —— 于是每条消息都解析失败。
    /// 这正是之前 .NET 端白屏的真正原因（Python 端不过这一层，所以没事）。
    ///
    /// 所以：字符串消息走 TryGetWebMessageAsString 拿原文；万一拿不到，再把
    /// WebMessageAsJson 外面的引号剥掉。
    /// </summary>
    private static string ReadMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var text = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(text)) return text;
        }
        catch (Exception)
        {
            // 非字符串消息（比如宿主自己发的对象）会抛 E_INVALIDARG，走下面的常规路径
        }

        return UnwrapJsonString(e.WebMessageAsJson);
    }

    /// <summary>
    /// 若 <paramref name="raw"/> 是一段 JSON 字符串字面量（整体被引号包着），
    /// 就解出里面的原文；否则原样返回。单独拆出来是为了能直接对着它写测试。
    /// </summary>
    internal static string UnwrapJsonString(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw.Length < 2 || raw[0] != '"') return raw;

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw;
        }
        catch (JsonException)
        {
            return raw;   // 只是恰好以引号开头，不是字符串字面量
        }
    }

    private void RecordMessageError(Exception ex)
    {
        var text = ex.GetType().Name + "：" + ex.Message;
        if (!_messageErrors.Contains(text)) _messageErrors.Add(text);
    }

    private void HandlePageEvent(string name, JsonElement root)
    {
        if (name == "ui.ready")
        {
            _pageReady = true;
            StopWatchdog();
            LoadingText.Visibility = Visibility.Collapsed;
            return;
        }

        if (name != "ui.error") return;

        // 页面自己报的错：攒着，万一启动超时就把它们一并写进兜底面板。
        // 光有错不一定代表界面废了（某一页的小功能挂了也会报），所以不直接判死。
        if (!root.TryGetProperty("payload", out var payload)) return;

        var message = payload.TryGetProperty("message", out var m) ? m.GetString() : null;
        if (string.IsNullOrWhiteSpace(message)) return;

        var file = payload.TryGetProperty("file", out var f) ? f.GetString() : null;
        var line = payload.TryGetProperty("line", out var l) && l.TryGetInt32(out var n) ? n : 0;

        var text = file is { Length: > 0 }
            ? $"{message}（{Path.GetFileName(file)}:{line}）"
            : message!;

        if (!_pageErrors.Contains(text)) _pageErrors.Add(text);
    }

    private void OnBridgeEvent(object? sender, string json) => Post(json);

    private void Post(string json) => Web.CoreWebView2?.PostWebMessageAsString(json);

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess) return;

        // 导航都失败了，页面脚本一行都没跑，不会有人来报到 —— 直接判死。
        Fail($"界面没能加载（{e.WebErrorStatus}）。\n地址：{Web.Source}");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        // 渲染进程崩了就直说，别留一个空白窗口让人以为是程序卡死
        Dispatcher.Invoke(() => Fail($"界面渲染进程异常退出（{e.ProcessFailedKind}）。重开设置窗口即可恢复。"));
    }

    // ================= 外观 =================

    /// <summary>
    /// 只同步标题栏与圆角。
    /// 云母（Mica）不在本窗口用：WebView2 的绘制表面是不透明的，材质透不过来，
    /// 强行打开只会多一次透明闪烁 —— 界面的观感由 ui/ 自己那套 WinUI 3 色板负责。
    /// </summary>
    private void ApplyWindowChrome()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        WindowEffects.TrySetDarkTitleBar(hwnd, ThemeBridge.IsDark);

        if (WindowEffects.SupportsRoundedCorners) WindowEffects.TrySetRoundedCorners(hwnd);
    }

    /// <summary>
    /// 窗口底色（WPF 侧）。
    ///
    /// 内核还没建好的那一两秒，窗口里只有 WPF 的 RootGrid，得先把底色铺上，
    /// 否则会白一下 —— 深色主题下尤其明显。
    /// </summary>
    private void ApplyBackdrop()
    {
        var dark = ThemeBridge.IsDark;

        RootGrid.Background = new MediaBrush(dark ? DarkBackdrop : LightBackdrop);
        LoadingText.Foreground = new MediaBrush(dark
            ? MediaColor.FromRgb(0xFF, 0xFF, 0xFF)
            : MediaColor.FromRgb(0x1A, 0x1A, 0x1A));
    }

    /// <summary>
    /// 把主题推给浏览内核：网页的 <c>prefers-color-scheme</c> + 表面底色。
    ///
    /// 只在深/浅色**真的变了**的时候才写（原因见 <see cref="_appliedDark"/>）。
    /// 内核还没就绪时直接返回，<see cref="OnLoadedAsync"/> 建好内核后会再调一次。
    /// </summary>
    private void ApplyColorScheme()
    {
        var dark = ThemeBridge.IsDark;
        if (_appliedDark == dark) return;

        CoreWebView2? core;
        try { core = Web.CoreWebView2; }
        catch (Exception)
        {
            return;   // 控件已经释放（兜底面板接管了），不用再同步主题
        }

        if (core is null) return;

        try
        {
            // 注意：WebView2.Wpf 的 DefaultBackgroundColor 用的是 System.Drawing.Color，
            // 不是 WPF 的 Media.Color —— 别被同名类型骗了。
            Web.DefaultBackgroundColor = dark
                ? System.Drawing.Color.FromArgb(0xFF, 0x20, 0x20, 0x20)
                : System.Drawing.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        }
        catch (Exception)
        {
            // 极个别版本在内核就绪前不让写；下次还会再来
        }

        try
        {
            core.Profile.PreferredColorScheme = dark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception)
        {
            // 旧版运行时没有这个属性：网页仍可从设置里读到 theme_mode，只是「跟随系统」不准
        }

        _appliedDark = dark;
    }

    // ================= 收尾 =================

    private void OnNativeFallbackClick(object sender, RoutedEventArgs e)
    {
        NativeFallbackRequested?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopWatchdog();

        _settings.Changed -= OnSettingsChanged;
        _bridge.PushEvent -= OnBridgeEvent;
        _bridge.Owner = null;

        try
        {
            Web.Dispose();
        }
        catch (Exception)
        {
            // 窗口已在关闭流程里，释放失败无可挽回也无需挽回
        }
    }

    private void Fail(string message)
    {
        ErrorTitle.Text = message.StartsWith("界面渲染进程", StringComparison.Ordinal)
            ? "界面已停止"
            : "设置界面加载失败";

        ErrorMessage.Text = message;
        ErrorLayer.Visibility = Visibility.Visible;
        LoadingText.Visibility = Visibility.Collapsed;

        // WebView2 是 HWND 托管的，光设 Visibility=Collapsed 不够保险：
        // 控件自己的子窗口有可能在内核初始化「失败之后」才被创建出来，铺一层空白的
        // 绘制表面把整个面板盖住 —— 表现出来就是一直白屏，而程序以为自己已经报错了。
        // 直接从可视化树里摘掉，HwndHost 会随之销毁那个子窗口，面板才真的看得见。
        if (RootGrid.Children.Contains(Web))
        {
            RootGrid.Children.Remove(Web);
            try { Web.Dispose(); } catch (Exception) { /* 没建起来过，没什么可释放的 */ }
        }

        // 调用方接了事件才给这个出口（没人接的话点了只会关窗口，不如不给）
        if (NativeFallbackRequested is not null)
            NativeFallbackButton.Visibility = Visibility.Visible;

        WriteDiagnostics(message);
    }

    /// <summary>
    /// 把失败原因落到配置目录的 webui-error.log。
    ///
    /// 白屏这种东西光看窗口看不出所以然，用户也没法复述 —— 留一份可以整段贴过来的
    /// 证据（运行时版本、界面目录、页面报错都在里面）。
    /// </summary>
    private void WriteDiagnostics(string message)
    {
        try
        {
            var path = Path.Combine(_paths.ConfigDirectory, "webui-error.log");
            Directory.CreateDirectory(_paths.ConfigDirectory);

            var lines = new[]
            {
                "时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                "错误：" + message,
                "卡在：" + _step + "（内核就绪=" + _coreReady + "，页面报到=" + _pageReady + "）",
                "界面目录：" + (ResolveUiDirectory() ?? "(未找到)"),
                "用户数据目录：" + (_userDataFolder ?? "(未建成)")
                    + (_userDataAttempts.Count == 0 ? "" : "\n  试过但失败：\n  " + string.Join("\n  ", _userDataAttempts)),
                "WebView2 运行时：" + RuntimeVersionText(),
                "操作系统：" + Environment.OSVersion.VersionString,
                "页面消息：收到 " + _messageCount + " 条"
                    + (_messageErrors.Count == 0 ? "" : "，解析失败：" + string.Join(" | ", _messageErrors)),
                "页面报错：" + (_pageErrors.Count == 0 ? "(无)" : string.Join(" | ", _pageErrors)),
                new string('-', 64),
                "",
            };

            File.AppendAllLines(path, lines, new UTF8Encoding(false));

            ErrorHint.Text = "诊断信息已写入：" + path
                + "\n把上面这段错误文字（或这个文件）发过来就能定位原因。";
        }
        catch (Exception)
        {
            // 连日志都写不了，就只能靠窗口上这句话了
        }
    }

    private static string RuntimeVersionText()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Exception ex) { return "(不可用：" + ex.Message + ")"; }
    }
}
