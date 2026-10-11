using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using ClassDailyLand.App.Views.Dialogs;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Market;
using ClassDailyLand.Infrastructure.Updates;
using ClassDailyLand.PluginHost;

namespace ClassDailyLand.App.Services;

/// <summary>
/// 设置中心 Web 层（仓库根的 ui/）在 .NET 侧的宿主桥。
///
/// 职责很窄：把前端 <c>host.invoke(method, params)</c> 翻译成对既有服务的调用，
/// 再把结果塞回 WebView2。<b>不碰任何界面渲染</b> —— 视图全在 HTML/CSS 里，
/// 因此灵动岛、班级日常主窗、下拉面板仍是原生 WPF，一行未动。
///
/// 方法清单与 <c>ui/README.md</c> 的「宿主需要实现的 RPC」逐条对应。
/// Python 侧的 <c>web_settings.py</c> 实现同一份清单，两边返回值形状必须一致。
/// </summary>
public sealed class WebUiBridge
{
    /// <summary>回包用的序列化选项：中文不转义，避免日志里出现 \uXXXX。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>市场与依赖包都只有 by-net 仓库一个来源。</summary>
    private const string MarketLabel = "GitHub · Class-Daily-Land-by-net（plugin 分支）";

    private const string MarketUrl = "https://github.com/LCHXXXX-1/Class-Daily-Land-by-net/tree/plugin";

    /// <summary>settings.patch 用：snake_case 键 → AppSettings 属性。</summary>
    private static readonly Dictionary<string, PropertyInfo> SettingsMap = BuildSettingsMap();

    private readonly ISettingsService _settings;
    private readonly IScheduleService _schedule;
    private readonly IHomeworkService _homework;
    private readonly IDutyService _duty;
    private readonly PluginManager _plugins;
    private readonly IPathService _paths;
    private readonly IAppController _controller;
    private readonly MarketService _market;
    private readonly PackageService _packages;
    private readonly UpdateLauncher _update;

    public WebUiBridge(
        ISettingsService settings,
        IScheduleService schedule,
        IHomeworkService homework,
        IDutyService duty,
        PluginManager plugins,
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
        _paths = paths;
        _controller = controller;
        _market = market;
        _packages = packages;
        _update = update;

        // ---- 市场 / 依赖包的事件直接推给页面 ----
        //
        // 订阅放在构造函数里（而不是某次 RPC 里）：安装、更新、后台补依赖都会走这些
        // 事件，页面上的进度条与运行日志就靠它们实时刷新。桥是单例，只会订阅一次；
        // 设置窗口没开时没人订阅 PushEvent，等于空转，没有副作用。
        _market.LogLine += (_, text) => Push("market.log", new { text });
        _market.TaskProgress += (_, e) => Push("market.progress", new { id = e.PluginId, percent = e.Percent });
        _market.TaskDone += (_, r) => Push("market.done", new
        {
            id = r.PluginId,
            ok = r.Success,
            message = r.Message,
            restartRequired = r.RestartRequired,
        });
        _market.BusyChanged += (_, busy) => Push("market.busy", new { busy });

        _packages.LogLine += (_, text) => Push("packages.log", new { text });
        _packages.TaskProgress += (_, e) => Push("packages.progress", new { name = e.Package, percent = e.Percent });
        _packages.TaskDone += (_, r) => Push("packages.done", new
        {
            name = r.PluginId,
            ok = r.Success,
            message = r.Message,
        });
        _packages.BusyChanged += (_, busy) => Push("packages.busy", new { busy });

        // 依赖下载进度：.NET 这个事件带的是**依赖包名**而不是插件 id（Python 那边的
        // dep 事件才有 plugin 字段），所以只把文案送进日志、顺带推一下包卡片自己的进度条，
        // 不硬往某张插件卡片上安。
        _packages.DependencyProgress += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text)) Push("market.log", new { text = e.Text });
            Push("packages.progress", new { name = e.Package, percent = e.Pct });
        };
    }

    /// <summary>二级对话框的宿主窗口，由设置窗口在打开时设置。</summary>
    public Window? Owner { get; set; }

    /// <summary>把一条事件推给页面（页面用 host.on 订阅）。</summary>
    public event EventHandler<string>? PushEvent;

    /// <summary>
    /// 把一条事件推给页面（页面用 host.on 订阅）。
    ///
    /// ★ 必须跳回 UI 线程：这些事件大多来自工作线程（索引同步、依赖下载都在后台
    ///   写日志），而 WebView2 的接口只能在 UI 线程碰。
    /// </summary>
    private void Push(string eventName, object? payload)
    {
        var json = JsonSerializer.Serialize(new { @event = eventName, payload }, JsonOptions);

        var dispatcher = Owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PushEvent?.Invoke(this, json);
            return;
        }

        dispatcher.InvokeAsync(() => PushEvent?.Invoke(this, json), DispatcherPriority.Background);
    }

    // ==================================================================
    //  分发
    // ==================================================================

    /// <summary>
    /// 执行一次 RPC。抛出的异常会被上层转成 <c>{id, error}</c> 回包，
    /// 页面侧表现为 Promise reject —— 与 native 一致，不吞错。
    /// </summary>
    public async Task<object?> DispatchAsync(string method, JsonElement prms, CancellationToken ct = default)
    {
        switch (method)
        {
            // ---------- 应用信息 ----------
            case "app.getVersion":
                return GetVersion();

            case "app.openFolder":
                return OpenFolder(prms);

            // ---------- 设置 ----------
            case "settings.getAll":
                return GetAllSettings();

            case "settings.patch":
                return PatchSettings(prms);

            case "settings.reset":
                return ResetSettings();

            // ---------- 插件 ----------
            case "plugins.list":
                return ListPlugins();

            case "plugins.setEnabled":
                return SetPluginEnabled(prms);

            // ---------- 插件市场 ----------
            case "market.status":
                return MarketStatus();

            case "market.sync":
                return await SyncMarketAsync(ct).ConfigureAwait(true);

            case "market.rescan":
                await _market.RescanAsync(ct).ConfigureAwait(true);
                return MarketStatus();

            case "market.install":
                return await InstallPluginAsync(prms, ct).ConfigureAwait(true);

            case "market.uninstall":
                return await UninstallPluginAsync(prms, ct).ConfigureAwait(true);

            case "market.setEnabled":
                return await SetPluginEnabledAsync(prms).ConfigureAwait(true);

            case "market.updateAll":
                return await UpdateAllPluginsAsync(ct).ConfigureAwait(true);

            case "market.setSource":
                // .NET 端索引固定走 by-net 仓库的 plugin 分支，没有可切换的来源。
                // 页面在 sourceOptions 为空时根本不会画那个下拉，正常走不到这里。
                return new { ok = true, source = _settings.Current.MarketSource, sourceLabel = MarketLabel };

            // ---------- 第三方依赖包 ----------
            case "packages.status":
                return PackagesStatus();

            case "packages.sync":
                return await SyncPackageAsync(prms, ct).ConfigureAwait(true);

            case "packages.install":
                return await InstallPackageAsync(prms, ct).ConfigureAwait(true);

            case "packages.uninstall":
                return await UninstallPackageAsync(prms, ct).ConfigureAwait(true);

            case "packages.setSource":
                return SetPackageSource(prms);

            // ---------- 二级对话框 ----------
            case "dialog.open":
                return OpenDialog(prms);

            // ---------- 假期与调休 ----------
            case "schedule.holidays.get":
                return HolidaysGet();

            case "schedule.holidays.add":
                return HolidayAdd(prms);

            case "schedule.holidays.remove":
                return HolidayRemove(prms);

            case "schedule.makeup.add":
                return MakeupAdd(prms);

            case "schedule.makeup.remove":
                return MakeupRemove(prms);

            // ---------- 周末作息 ----------
            case "schedule.weekend.get":
                return WeekendGet();

            case "schedule.weekend.set":
                return WeekendSet(prms);

            // ---------- 值日生 ----------
            case "duty.get":
                return DutyGet();

            case "duty.add":
                return DutyAdd(prms);

            case "duty.addMany":
                return DutyAddMany(prms);

            case "duty.remove":
                return DutyRemove(prms);

            // ---------- 作业编辑器 ----------
            case "homework.get":
                return HomeworkGet();

            case "homework.add":
                return HomeworkAdd(prms);

            case "homework.update":
                return HomeworkUpdate(prms);

            case "homework.remove":
                return HomeworkRemove(prms);

            case "homework.clearSubject":
                return HomeworkClearSubject(prms);

            case "homework.save":
                _homework.Save();
                return new { ok = true };

            // ---------- 更新 ----------
            case "update.check":
                return CheckUpdate();

            // ---------- 无边框窗口的窗口控制 ----------
            //  页面上的最小化 / 最大化 / 关闭按钮走这几条。
            case "window.minimize":
                return WindowStateChange(minimize: true);

            case "window.toggleMaximize":
                return WindowStateChange(toggleMaximize: true);

            case "window.close":
                return WindowClose();

            case "window.startMove":
                // WebView2 已经把页内标题栏当系统标题栏了（app-region: drag），
                // 拖拽由系统接管，正常走不到这里。留着是为了和 Qt 那条路对称。
                return new { ok = true };

            case "window.startResize":
                // 本窗口有系统自带的调整边框（WS_THICKFRAME），拖边缩放不用页面操心
                return new { ok = false, reason = "这一端由系统边框负责缩放" };

            default:
                throw new InvalidOperationException($"未知的宿主方法：{method}");
        }
    }

    // ==================================================================
    //  app.*
    // ==================================================================

    private object GetVersion() => new
    {
        version = AppVersionText(),
        platform = "win32",
        host = "webview2",
        system = SystemText(),
        configPath = _paths.ConfigDirectory,
        pluginsPath = _paths.PluginsDirectory,
        marketUrl = MarketUrl,
        // 窗口镶边归谁画 —— 设置窗口现在是无边框的，页内那条标题栏就是系统标题栏。
        // 页面据此决定画不画最小化 / 最大化 / 关闭，以及要不要自己处理拖边缩放。
        window = new
        {
            frame = "none",
            // 拖动交给 WebView2 的非客户区支持（app-region: drag），
            // 缩放交给窗口自带的 WS_THICKFRAME 调整边框 —— 都不需要页面插手。
            resize = "native",
            controls = new[] { "minimize", "maximize", "close" },
        },
    };

    private static string AppVersionText()
    {
        var assembly = typeof(App).Assembly;

        // Directory.Build.props 里的 Version 会带进 InformationalVersion；
        // 源链接等场景可能附带 "+<sha>"，截掉。
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info!.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        var v = assembly.GetName().Version;
        return v is null ? "4.3.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// 给「外观」页判断能否用云母用的：前端只认字符串里有没有 "Windows 11"，
    /// 所以这里必须把版本名带出来，不能只给 Build 号。
    /// </summary>
    private static string SystemText()
    {
        var build = Environment.OSVersion.Version.Build;
        var name = build switch
        {
            >= 22000 => "Windows 11",
            >= 10240 => "Windows 10",
            _ => RuntimeInformation.OSDescription,
        };
        return $"{name}（Build {build}）";
    }

    private object OpenFolder(JsonElement prms)
    {
        var id = GetString(prms, "id") ?? "";

        if (id == "marketUrl")
        {
            Process.Start(new ProcessStartInfo(MarketUrl) { UseShellExecute = true });
            return new { ok = true, path = MarketUrl };
        }

        var path = id switch
        {
            "plugins" => _paths.PluginsDirectory,
            "config" => _paths.ConfigDirectory,
            // 作业数据与 config.json 同放在数据根，没有单独的作业目录
            "homework" => _paths.AppRoot,
            _ => null,
        };

        if (path is null) throw new InvalidOperationException($"不认识的文件夹标识：{id}");

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        return new { ok = true, path };
    }

    // ==================================================================
    //  settings.*
    // ==================================================================

    /// <summary>
    /// 全部设置项。
    ///
    /// 「提前提醒 / 时间偏移」这两项存在课表服务上而不是 AppSettings 里
    /// （源项目也是这么分的，见 SettingsWindow.Pages.cs 的 Read/Write 委托），
    /// 所以这里合并进来 —— 页面才能像其它设置一样统一读写。
    /// </summary>
    private object GetAllSettings()
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            map[name] = property.GetValue(_settings.Current);
        }

        map["schedule_advance_minutes"] = _schedule.AdvanceMinutes;
        map["schedule_time_offset_seconds"] = _schedule.TimeOffsetSeconds;

        return map;
    }

    /// <summary>由课表服务承载的两个设置键。</summary>
    private static bool IsScheduleKey(string name)
        => name is "schedule_advance_minutes" or "schedule_time_offset_seconds";

    private object PatchSettings(JsonElement prms)
    {
        if (prms.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("settings.patch 需要一个对象参数");

        int? advance = null;
        int? offset = null;

        _settings.Update(settings =>
        {
            foreach (var item in prms.EnumerateObject())
            {
                if (IsScheduleKey(item.Name))
                {
                    // 攒起来最后一次性写，避免中途触发多次保存
                    if (item.Name == "schedule_advance_minutes") advance = ToInt(item.Value);
                    else offset = ToInt(item.Value);
                    continue;
                }

                if (!SettingsMap.TryGetValue(item.Name, out var property) || !property.CanWrite) continue;

                var value = ConvertJson(item.Value, property.PropertyType);
                if (value is not null) property.SetValue(settings, value);
            }
        });

        if (advance is not null || offset is not null)
        {
            if (advance is not null) _schedule.AdvanceMinutes = advance.Value;
            if (offset is not null) _schedule.TimeOffsetSeconds = offset.Value;
            _schedule.Save();
        }

        return new { ok = true };
    }

    private object ResetSettings()
    {
        var defaults = new AppSettings();

        _settings.Update(settings =>
        {
            foreach (var property in typeof(AppSettings).GetProperties())
            {
                if (!property.CanWrite) continue;
                property.SetValue(settings, property.GetValue(defaults));
            }
        });

        // 服务侧的两个值（提前提醒 / 时间偏移）也一并回位，
        // 否则「恢复全部默认」会漏掉两项 —— 与原生设置窗口的行为保持一致
        var defaultSchedule = new ScheduleDocument();
        _schedule.AdvanceMinutes = defaultSchedule.AdvanceMinutes;
        _schedule.TimeOffsetSeconds = defaultSchedule.TimeOffsetSeconds;
        _schedule.Save();

        return new { ok = true };
    }

    private static Dictionary<string, PropertyInfo> BuildSettingsMap()
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var property in typeof(AppSettings).GetProperties())
        {
            var name = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;
            map[name] = property;
        }

        return map;
    }

    /// <summary>
    /// JSON 值 → 目标类型。类型对不上返回 null（该项被忽略，不让一个坏字段带崩整次修改）。
    /// </summary>
    private static object? ConvertJson(JsonElement element, Type target)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;

            case JsonValueKind.True:
            case JsonValueKind.False:
                return target == typeof(bool) ? element.GetBoolean() : null;

            case JsonValueKind.String:
                return target == typeof(string) ? element.GetString() : null;

            case JsonValueKind.Array:
                return target == typeof(List<string>) ? element.Deserialize<List<string>>() : null;
        }

        if (target == typeof(int))
            return element.TryGetInt32(out var i) ? i : (int)Math.Round(element.GetDouble());

        if (target == typeof(double)) return element.GetDouble();

        return null;
    }

    /// <summary>取整数值；不是数字就抛，免得悄悄写进去一个 0。</summary>
    private static int ToInt(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("该设置项需要一个数字");

        return element.TryGetInt32(out var value) ? value : (int)Math.Round(element.GetDouble());
    }

    // ==================================================================
    //  plugins.*
    // ==================================================================

    private object ListPlugins()
    {
        var disabled = new HashSet<string>(_settings.Current.DisabledPlugins, StringComparer.OrdinalIgnoreCase);
        var root = _paths.PluginsDirectory;
        var list = new List<object>();

        if (Directory.Exists(root))
        {
            foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var id = Path.GetFileName(directory);

                // packages/ 是依赖包目录，不是插件
                if (string.Equals(id, "packages", StringComparison.OrdinalIgnoreCase)) continue;

                var loaded = _plugins.Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
                var failure = _plugins.Failures.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

                var requires = loaded?.Manifest.Requires ?? new List<string>();
                var reason = failure?.Reason ?? (loaded is null ? "未加载（可能缺少 plugin.json 或入口程序集）" : "");

                list.Add(new
                {
                    id,
                    name = loaded is not null && loaded.Manifest.Name.Length > 0 ? loaded.Manifest.Name : id,
                    version = loaded?.Manifest.Version ?? "",
                    desc = loaded?.Manifest.Description ?? "",
                    requires,
                    enabled = !disabled.Contains(id),
                    loaded = loaded is not null,
                    failure = reason,
                });
            }
        }

        return new { plugins = list };
    }

    private object SetPluginEnabled(JsonElement prms)
    {
        var id = GetString(prms, "id");
        if (string.IsNullOrWhiteSpace(id))
            throw new InvalidOperationException("plugins.setEnabled 缺少 id");

        var enabled = GetBool(prms, "enabled");

        _settings.Update(settings =>
        {
            var list = new List<string>(settings.DisabledPlugins);

            if (enabled) list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            else if (!list.Contains(id, StringComparer.OrdinalIgnoreCase)) list.Add(id);

            settings.DisabledPlugins = list;
        });

        // 与原生设置窗口一致：启用状态要下次启动才生效
        return new { ok = true, restartRequired = true };
    }

    // ==================================================================
    //  market.* / packages.*
    // ==================================================================

    private object MarketStatus()
    {
        var entries = _market.Catalog.Select(ToMarketEntry).ToList();

        return new
        {
            count = entries.Count,
            source = _settings.Current.MarketSource,
            sourceLabel = MarketLabel,
            // ★ 空的 sourceOptions 就是「这一端没有可选来源」——页面据此不画那个下拉。
            //   .NET 固定走 by-net 仓库的 plugin 分支，没有切换这回事。
            sourceOptions = Array.Empty<object>(),
            lastSync = _market.LastSyncText,
            busy = _market.IsBusy,
            entries,
        };
    }

    private static object ToMarketEntry(MarketEntry entry) => new
    {
        id = entry.Id,
        name = entry.Name.Length > 0 ? entry.Name : entry.Id,
        version = entry.Version,
        description = entry.Description,
        author = entry.Author,
        format = entry.Format,
        file = entry.File,
        installed = entry.Installed,
        installedVersion = entry.InstalledVersion.Length > 0 ? entry.InstalledVersion : "",
        disabled = entry.Disabled,
        updateAvailable = entry.UpdateAvailable,
        status = entry.Status,
        // 索引记录本身的问题（装不了）与插件加载失败的原因，都往这一个字段上带，
        // 页面统一渲染成「⚠ 原因」。
        problem = entry.Problem,
        loadError = entry.LoadError,
    };

    private async Task<object> SyncMarketAsync(CancellationToken ct)
    {
        var log = new List<string>();
        void OnLog(object? _, string text) { lock (log) log.Add(text); }

        _market.LogLine += OnLog;
        try
        {
            var result = await _market.CheckUpdatesAsync(ct).ConfigureAwait(true);

            lock (log)
            {
                if (result.Method.Length > 0) log.Add($"[来源] {result.Method}");
                log.Add($"[完成] 索引 {result.Entries.Count} 条");
            }

            // 同步完顺手刷一次本地状态：索引里带了新版本，比对的结论才算数
            try { await _market.RescanAsync(ct).ConfigureAwait(true); }
            catch (Exception) { /* 扫描失败不影响已经拿到的索引 */ }

            return new
            {
                count = result.Entries.Count,
                source = _settings.Current.MarketSource,
                sourceLabel = MarketLabel,
                sourceOptions = Array.Empty<object>(),
                lastSync = _market.LastSyncText,
                log = log.ToArray(),
                entries = _market.Catalog.Select(ToMarketEntry).ToList(),
            };
        }
        finally
        {
            _market.LogLine -= OnLog;
        }
    }

    private async Task<object> InstallPluginAsync(JsonElement prms, CancellationToken ct)
    {
        var id = GetString(prms, "id") ?? "";
        if (id.Length == 0) throw new InvalidOperationException("market.install 缺少 id");

        var result = await _market.InstallAsync(id, ct).ConfigureAwait(true);

        try { await _market.RescanAsync(ct).ConfigureAwait(true); }
        catch (Exception) { /* 同上 */ }

        return new
        {
            ok = result.Success,
            message = result.Message,
            restartRequired = result.RestartRequired,
        };
    }

    private async Task<object> UninstallPluginAsync(JsonElement prms, CancellationToken ct)
    {
        var id = GetString(prms, "id") ?? "";
        if (id.Length == 0) throw new InvalidOperationException("market.uninstall 缺少 id");

        var result = await _market.UninstallAsync(id, ct).ConfigureAwait(true);

        try { await _market.RescanAsync(ct).ConfigureAwait(true); }
        catch (Exception) { /* 同上 */ }

        return new
        {
            ok = result.Success,
            message = result.Message,
            restartRequired = result.RestartRequired,
        };
    }

    private async Task<object> SetPluginEnabledAsync(JsonElement prms)
    {
        var id = GetString(prms, "id") ?? "";
        if (id.Length == 0) throw new InvalidOperationException("market.setEnabled 缺少 id");

        var result = _market.SetEnabled(id, GetBool(prms, "enabled"));

        try { await _market.RescanAsync().ConfigureAwait(true); }
        catch (Exception) { /* 同上 */ }

        return new { ok = result.Success, message = result.Message, restartRequired = result.RestartRequired };
    }

    private async Task<object> UpdateAllPluginsAsync(CancellationToken ct)
    {
        var log = new List<string>();
        void OnLog(object? _, string text) { lock (log) log.Add(text); }

        // 批量更新前先记下 id→名字的对应，好让页面上的浮动提示定位到具体那张卡片
        var idByName = _market.Catalog.ToDictionary(e => e.Name, e => e.Id, StringComparer.Ordinal);

        _market.LogLine += OnLog;
        try
        {
            var items = await _market.UpdateAllAsync(ct).ConfigureAwait(true);

            try { await _market.RescanAsync(ct).ConfigureAwait(true); }
            catch (Exception) { /* 同上 */ }

            var payload = items.Select(item => new
            {
                id = idByName.TryGetValue(item.Name, out var id) ? id : "",
                name = item.Name,
                success = item.Success,
                message = item.Message,
            }).ToList();

            return new
            {
                items = payload,
                restartRequired = items.Any(i => i.Success),
                count = _market.Catalog.Count,
                lastSync = _market.LastSyncText,
                log = log.ToArray(),
                entries = _market.Catalog.Select(ToMarketEntry).ToList(),
            };
        }
        finally
        {
            _market.LogLine -= OnLog;
        }
    }

    private object PackagesStatus()
    {
        var entries = _packages.Catalog.Select(ToPackage).ToList();

        return new
        {
            source = _packages.SourceId,
            sourceLabel = PackageSourceLabel(_packages.SourceId),
            // 依赖包这边 .NET 确实有多个下载源可选，把选项交给页面去画
            sourceOptions = PackageService.Sources
                .Select(s => new { value = s.Id, label = s.Label })
                .ToList(),
            busy = _packages.IsBusy,
            entries,
        };
    }

    private static string PackageSourceLabel(string sourceId)
        => PackageService.Sources.FirstOrDefault(s => s.Id == sourceId).Label ?? sourceId;

    private static object ToPackage(PackageCatalogEntry entry) => new
    {
        name = entry.Name,
        version = entry.Version,
        installed = entry.Installed,
        installedVersion = entry.InstalledVersion.Length > 0 ? entry.InstalledVersion : "",
        manual = entry.Manual,
        usedBy = entry.UsedBy.ToList(),
        status = entry.Status,
        statusLabel = entry.StatusLabel,
    };

    private object SetPackageSource(JsonElement prms)
    {
        var source = GetString(prms, "source") ?? "";
        if (source.Length == 0) throw new InvalidOperationException("packages.setSource 缺少 source");

        if (!_packages.SetSource(source))
            throw new InvalidOperationException($"不认识的下载源：{source}");

        return new { ok = true, source = _packages.SourceId, sourceLabel = PackageSourceLabel(_packages.SourceId) };
    }

    private async Task<object> InstallPackageAsync(JsonElement prms, CancellationToken ct)
    {
        var name = GetString(prms, "name") ?? "";
        if (name.Length == 0) throw new InvalidOperationException("packages.install 缺少 name");

        var result = await _packages.InstallAsync(name, ct).ConfigureAwait(true);

        try { await _packages.RefreshAsync(ct).ConfigureAwait(true); }
        catch (Exception) { /* 清单拉不到不影响「已装好」这个事实 */ }

        return new { ok = result.Success, message = result.Message };
    }

    private async Task<object> UninstallPackageAsync(JsonElement prms, CancellationToken ct)
    {
        var name = GetString(prms, "name") ?? "";
        if (name.Length == 0) throw new InvalidOperationException("packages.uninstall 缺少 name");

        var result = await _packages.UninstallAsync(name, ct).ConfigureAwait(true);

        try { await _packages.RefreshAsync(ct).ConfigureAwait(true); }
        catch (Exception) { /* 同上 */ }

        return new { ok = result.Success, message = result.Message };
    }

    private async Task<object> SyncPackageAsync(JsonElement prms, CancellationToken ct)
    {
        var name = GetString(prms, "name");

        // 带 name 是「装这个包」，不带 name 才只是刷新目录 —— 两者页面都在用
        if (!string.IsNullOrWhiteSpace(name))
        {
            var result = await _packages.InstallAsync(name, ct).ConfigureAwait(true);
            if (!result.Success) throw new InvalidOperationException(result.Message);
        }

        try { await _packages.RefreshAsync(ct).ConfigureAwait(true); }
        catch (Exception) { /* 同上 */ }

        return PackagesStatus();
    }

    // ==================================================================
    //  dialog.*
    // ==================================================================

    /// <summary>
    /// 打开二级对话框。
    ///
    /// 关键：**先把回包发出去，再弹窗**。ShowDialog 会阻塞消息循环，
    /// 若在返回前弹，页面那边 15 秒就会判超时 —— 用户开着一个对话框停一会儿就报错。
    /// </summary>
    private object OpenDialog(JsonElement prms)
    {
        var id = GetString(prms, "id") ?? "";

        if (id.StartsWith("install-", StringComparison.Ordinal))
        {
            var pluginId = id["install-".Length..];
            RunLater(async () =>
            {
                var result = await _market.InstallAsync(pluginId).ConfigureAwait(true);

                MessageBox.Show(Owner!,
                    result.Message,
                    result.Success ? "安装完成" : "安装失败",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

                Push("market.changed", new { id = pluginId, success = result.Success, restartRequired = result.RestartRequired });
            });

            return new { opened = true };
        }

        Action? open = id switch
        {
            "timetable" => () => new TimetableManagerDialog(_schedule) { Owner = Owner }.ShowDialog(),
            "weekend" => () => new WeekendDialog(_schedule) { Owner = Owner }.ShowDialog(),
            "holidays" => () => new HolidayDialog(_schedule) { Owner = Owner }.ShowDialog(),
            "duty" => () => new DutyEditorDialog(_duty) { Owner = Owner }.ShowDialog(),
            "statustest" => () => new StatusTestDialog(_schedule, _settings, _controller) { Owner = Owner }.ShowDialog(),
            "homework-editor" => () => new HomeworkEditorDialog(_homework) { Owner = Owner }.ShowDialog(),
            "install-local" => () => InstallLocalPlugin(),
            _ => null,
        };

        if (open is null)
            throw new InvalidOperationException($"不认识的对话框：{id}");

        RunLater(() => { open(); return Task.CompletedTask; });
        return new { opened = true };
    }

    /// <summary>安装本地 .cblplugin 包（对应高级页的「安装本地插件…」）。</summary>
    private void InstallLocalPlugin()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择插件包",
            Filter = "插件包 (*.cblplugin)|*.cblplugin|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        if (dialog.ShowDialog(Owner) != true) return;

        var target = Path.Combine(_paths.PluginsDirectory, Path.GetFileName(dialog.FileName));
        Directory.CreateDirectory(_paths.PluginsDirectory);
        File.Copy(dialog.FileName, target, overwrite: true);

        MessageBox.Show(Owner!,
            $"{Path.GetFileName(dialog.FileName)} 已放入插件目录，重启后生效。",
            "安装本地插件", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================================================================
    //  update.*
    // ==================================================================

    /// <summary>
    /// 手动检查更新。
    ///
    /// 更新是独立进程（Launcher）干的活，本程序拿不到远端版本号 —— 所以这里
    /// 只负责把更新器拉起来，真实结果由更新器窗口给出。同样先把回包发出去。
    /// </summary>
    private object CheckUpdate()
    {
        var (allowed, hits) = _update.EvaluateSourceGuard();

        RunLater(() =>
        {
            if (!allowed)
            {
                var choice = MessageBox.Show(Owner!,
                    "程序目录下检测到未打包的主入口源码，继续更新会用发布版覆盖这些"
                    + "源文件，导致你本地的改动丢失。\n\n"
                    + UpdateLauncher.BackupTip(hits),
                    "检测到本地源码，未执行更新",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning,
                    MessageBoxResult.Cancel);

                if (choice != MessageBoxResult.OK) return Task.CompletedTask;
            }

            var result = _update.LaunchVisible(force: true);

            if (!result.Spawned)
            {
                MessageBox.Show(Owner!,
                    result.Message.Length > 0 ? result.Message : "未能启动更新器。",
                    "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return Task.CompletedTask;
        });

        return new
        {
            current = AppVersionText(),
            latest = "",
            hasUpdate = false,
            message = _update.IsUpdaterAvailable
                ? "已启动更新器，检查结果会在更新器窗口里给出。"
                : "程序目录下没有找到更新器（Launcher），无法在线检查更新。",
        };
    }

    // ==================================================================
    //  小工具
    // ==================================================================

    /// <summary>把一段可能阻塞的消息循环（弹窗）推迟到回包之后再跑。</summary>
    private void RunLater(Func<Task> action)
    {
        var dispatcher = Owner?.Dispatcher ?? Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Owner!,
                    ex.Message, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }, DispatcherPriority.Background);
    }

    // ==================================================================
    //  二级对话框：假期与调休
    //
    //  这一组是**即时写入**：加上 / 删除后页面立刻重新拉一次。
    //  （对应原生 HolidayDialog 的做法 —— 它连保存按钮都没有。）
    // ==================================================================

    private object HolidaysGet() => new
    {
        holidays = _schedule.Holidays
            .Select(h => new { start = h.Start, end = h.End, name = h.Name })
            .ToList(),
        makeupDays = _schedule.MakeupDays
            .Select(m => new { date = m.Date, asWeekday = m.AsWeekday })
            .ToList(),
    };

    private object HolidayAdd(JsonElement prms)
    {
        _schedule.AddHoliday(
            GetString(prms, "start") ?? "",
            GetString(prms, "end") ?? "",
            GetString(prms, "name") ?? "");

        return new { ok = true };
    }

    private object HolidayRemove(JsonElement prms)
    {
        if (!_schedule.RemoveHoliday(GetInt(prms, "index")))
            throw new InvalidOperationException("没有这一条假期（可能已经被删掉了）。");

        return new { ok = true };
    }

    private object MakeupAdd(JsonElement prms)
    {
        _schedule.AddMakeup(
            GetString(prms, "date") ?? "",
            Math.Clamp(GetInt(prms, "asWeekday"), 0, 6));

        return new { ok = true };
    }

    private object MakeupRemove(JsonElement prms)
    {
        if (!_schedule.RemoveMakeup(GetInt(prms, "index")))
            throw new InvalidOperationException("没有这一条调休（可能已经被删掉了）。");

        return new { ok = true };
    }

    // ==================================================================
    //  二级对话框：周末作息（点保存才写）
    // ==================================================================

    private object WeekendGet()
    {
        var weekend = _schedule.Weekend;

        return new
        {
            morningSplit = weekend.MorningSplit,
            afternoonPeriods = weekend.AfternoonPeriods
                .Select(p => new { name = p.Name, start = p.Start, end = p.End })
                .ToList(),
            courses = weekend.Courses.ToList(),
        };
    }

    private object WeekendSet(JsonElement prms)
    {
        // 分界点留空时与原生一致，回落到 12:00
        var split = GetString(prms, "morningSplit");
        if (string.IsNullOrWhiteSpace(split)) split = "12:00";

        _schedule.SetWeekend(split!, ReadPeriods(prms, "afternoonPeriods"), ReadStringList(prms, "courses"));

        return new { ok = true };
    }

    // ==================================================================
    //  二级对话框：值日生名单（即时写入）
    // ==================================================================

    private object DutyGet() => new
    {
        list = _duty.DutyList.ToList(),
        current = _duty.CurrentDuty,
        index = _duty.CurrentIndex,
    };

    private object DutyAdd(JsonElement prms)
    {
        var name = (GetString(prms, "name") ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("姓名不能为空");

        _duty.AddDuty(name);
        return new { ok = true };
    }

    private object DutyAddMany(JsonElement prms)
    {
        var names = ReadStringList(prms, "names")
            .Select(n => n.Trim())
            .Where(n => n.Length > 0)
            .ToList();

        if (names.Count == 0) throw new InvalidOperationException("姓名不能为空");

        _duty.AddDuties(names);
        return new { ok = true, added = names.Count };
    }

    private object DutyRemove(JsonElement prms)
    {
        if (!_duty.RemoveDuty(GetInt(prms, "index")))
            throw new InvalidOperationException("没有这一条值日生（可能已经被删掉了）。");

        return new { ok = true };
    }

    // ==================================================================
    //  二级对话框：作业编辑器
    //
    //  增删改都是即时落盘的（与原生 HomeworkService 一致），
    //  「确认」只是再强制存一次，「取消」不回溯 —— 原生就是这个行为，别改。
    // ==================================================================

    private object HomeworkGet()
    {
        var items = _homework.All
            .Select((item, index) => new
            {
                index,
                subject = item.Subject ?? "",
                content = item.Content ?? "",
                date = item.Date ?? "",
            })
            .ToList();

        // 分组由服务给（按科目，Date 取该科目第一个非空值），别在这里另搞一套
        var groups = _homework.Grouped()
            .Select(group => new
            {
                subject = group.Subject,
                date = group.Date ?? "",
                entries = group.Indices
                    .Select((realIndex, i) => new { index = realIndex, content = group.Contents[i] })
                    .ToList(),
            })
            .ToList();

        return new { items, groups, count = items.Count };
    }

    private object HomeworkAdd(JsonElement prms)
    {
        var subject = GetString(prms, "subject") ?? "";
        var content = GetString(prms, "content") ?? "";

        if (!_homework.Add(subject, content, GetString(prms, "date") ?? ""))
            throw new InvalidOperationException("科目和作业内容都不能为空。");

        return new { ok = true };
    }

    private object HomeworkUpdate(JsonElement prms)
    {
        if (!_homework.Update(
                GetInt(prms, "index"),
                GetString(prms, "subject") ?? "",
                GetString(prms, "content") ?? "",
                GetString(prms, "date") ?? ""))
        {
            throw new InvalidOperationException("这一条作业不在了（可能已经被删掉）。");
        }

        return new { ok = true };
    }

    private object HomeworkRemove(JsonElement prms)
    {
        if (!_homework.Remove(GetInt(prms, "index")))
            throw new InvalidOperationException("这一条作业不在了（可能已经被删掉）。");

        return new { ok = true };
    }

    private object HomeworkClearSubject(JsonElement prms)
    {
        var subject = GetString(prms, "subject") ?? "";

        // 倒序删，避免删除过程中索引前移
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

        return new { ok = true, removed = targets.Count };
    }

    // ==================================================================
    //  无边框窗口的窗口控制
    // ==================================================================

    /// <summary>最小化 / 最大化切换。这两个都是同步的，改完立刻能报回新状态。</summary>
    private object WindowStateChange(bool minimize = false, bool toggleMaximize = false)
    {
        var window = Owner;
        if (window is null) return new { ok = false, maximized = false };

        var maximized = false;

        window.Dispatcher.Invoke(() =>
        {
            if (minimize) window.WindowState = WindowState.Minimized;

            if (toggleMaximize)
            {
                window.WindowState = window.WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }

            maximized = window.WindowState == WindowState.Maximized;
        });

        return new { ok = true, maximized };
    }

    /// <summary>
    /// 关闭设置窗口。
    ///
    /// ★ 必须「先回包、后关窗」。现在这条栈还在 WebView2 的 WebMessageReceived 回调里，
    ///   而关窗会走 Closed 处理器去 Dispose 这个控件 —— 直接关等于把正在跑的栈拆掉。
    ///   和弹窗类 RPC 是同一个道理（见 OpenDialog 的注释）。
    /// </summary>
    private object WindowClose()
    {
        var window = Owner;
        if (window is null) return new { ok = false };

        RunLater(() =>
        {
            window.Dispatcher.Invoke(window.Close);
            return Task.CompletedTask;
        });

        return new { ok = true };
    }

    // ==================================================================
    //  参数读取
    // ==================================================================

    private static List<Period> ReadPeriods(JsonElement prms, string name)
    {
        var list = new List<Period>();
        if (!TryGetArray(prms, name, out var array)) return list;

        foreach (var item in array.EnumerateArray())
        {
            list.Add(new Period
            {
                Name = GetString(item, "name") ?? "",
                Start = GetString(item, "start") ?? "",
                End = GetString(item, "end") ?? "",
            });
        }

        return list;
    }

    private static List<string> ReadStringList(JsonElement prms, string name)
    {
        var list = new List<string>();
        if (!TryGetArray(prms, name, out var array)) return list;

        foreach (var item in array.EnumerateArray())
            list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());

        return list;
    }

    private static bool TryGetArray(JsonElement prms, string name, out JsonElement array)
    {
        array = default;
        if (prms.ValueKind != JsonValueKind.Object) return false;
        if (!prms.TryGetProperty(name, out var element)) return false;
        if (element.ValueKind != JsonValueKind.Array) return false;

        array = element;
        return true;
    }

    private static int GetInt(JsonElement obj, string name, int fallback = 0)
    {
        if (obj.ValueKind != JsonValueKind.Object) return fallback;
        if (!obj.TryGetProperty(name, out var element)) return fallback;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)) return value;

        // 页面偶尔会发字符串（文本框直接读出来的），别因此报错
        return element.ValueKind == JsonValueKind.String
               && int.TryParse(element.GetString(), out var parsed)
            ? parsed
            : fallback;
    }

    private static string? GetString(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var element)
           && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static bool GetBool(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var element)
           && (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
           && element.GetBoolean();
}
