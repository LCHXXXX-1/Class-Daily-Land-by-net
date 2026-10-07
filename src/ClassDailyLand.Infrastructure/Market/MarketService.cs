using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 插件市场。对应源模块：plugin_market.py 的 PluginMarket。
///
/// 职责：
/// 1. 从三个来源（官方服务器 / GitHub / Gitee）同步索引，全挂时退回本地缓存；
/// 2. 扫描本地 plugins/ 目录，给索引条目附加安装状态；
/// 3. 下载 → SHA-256 校验 → 备份 → 解压 → 写元数据的原子安装；
/// 4. 卸载与启用 / 禁用。
///
/// 与源实现的差异：Python 用 QThread 做后台任务并靠「代号」作废旧结果，
/// C# 用 async/await + CancellationToken，语义等价且不需要手工等待线程收尾。
/// </summary>
public sealed class MarketService
{
    // ---------- 索引来源（与源项目常量逐字对齐）----------
    private const string GitHubRepo = "LCHXXXX-1/Class-Daily-Land";
    private const string GitHubBranch = "plugins";

    /// <summary>
    /// raw 打头、jsDelivr 殿后：jsDelivr 会缓存 GitHub 文件（分支引用能滞后
    /// 好几个小时），刚发布的插件可能半天刷不出来，所以先去 raw 拿新鲜的。
    /// </summary>
    private static readonly string[] GitHubListUrls =
    {
        $"https://raw.githubusercontent.com/{GitHubRepo}/{GitHubBranch}/list.json",
        $"https://cdn.jsdelivr.net/gh/{GitHubRepo}@{GitHubBranch}/list.json",
    };

    private const string RemoteListUrl = "https://plugins.class-daily-land.de5.net/list.json";

    private const string GiteeRepo = "lchxxxx/class-daily-land";
    private const string GiteeBranch = "plugins";

    private static readonly string[] GiteeListUrls =
    {
        $"https://gitee.com/{GiteeRepo}/raw/{GiteeBranch}/list.json",
    };

    /// <summary>各来源列表的本地缓存文件名（下划线开头，避免与插件实体文件混淆）。</summary>
    private const string RemoteCacheName = "_remote_list.json";
    private const string GitHubCacheName = "_github_list.json";
    private const string GiteeCacheName = "_gitee_list.json";

    private const string UserAgent = "ClassDailyLand-PluginMarket/4.2";

    /// <summary>索引同步超时（秒）。</summary>
    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    /// <summary>索引首地址专享的短上限：它内容最新但不一定通，8 秒没动静就换镜像。</summary>
    private static readonly TimeSpan FastListTimeout = TimeSpan.FromSeconds(8);

    /// <summary>插件包下载超时。慢网下 90KB 的包也得让人家爬完，别半路掐断。</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(60);

    private readonly IPathService _paths;
    private readonly IJsonStore _store;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;

    private readonly List<MarketEntry> _catalog = new();

    /// <summary>当前正在处理的插件 id（进度事件带上它，界面才知道是哪一个）。</summary>
    private string _currentId = "";

    public MarketService(IPathService paths, IJsonStore store, ISettingsService settings, HttpClient? http = null)
    {
        _paths = paths;
        _store = store;
        _settings = settings;

        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,   // 超时逐请求指定
        };
    }

    // ================= 事件 =================

    /// <summary>日志文本。</summary>
    public event EventHandler<string>? LogLine;

    /// <summary>是否有任务在跑。</summary>
    public event EventHandler<bool>? BusyChanged;

    /// <summary>索引刷新完成，携带新目录。</summary>
    public event EventHandler<MarketSyncResult>? CatalogReady;

    /// <summary>单个插件的安装进度：(插件 id, 0-100；-1 表示结束)。</summary>
    public event EventHandler<(string PluginId, int Percent)>? TaskProgress;

    /// <summary>单个插件任务完成。</summary>
    public event EventHandler<MarketTaskResult>? TaskDone;

    public IReadOnlyList<MarketEntry> Catalog => _catalog;

    /// <summary>上次同步成功的时刻文本（HH:mm:ss）；未同步过为空串。</summary>
    public string LastSyncText { get; private set; } = "";

    public bool IsBusy { get; private set; }

    /// <summary>加载失败的插件：插件 id → 原因。由宿主的加载报告填入。</summary>
    public IReadOnlyDictionary<string, string> LoadFailures { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // ================= 同步 =================

    /// <summary>
    /// 按设置挑索引来源 → 拉索引 → 刷新目录（对应 check_updates）。
    ///
    /// 联网全挂时**退回本地缓存**（上次同步成功的那份），
    /// 别让网络一抽风就把列表清空、只剩一句「同步没成功」。连缓存都没有，那才算真失败。
    /// </summary>
    public async Task<MarketSyncResult> CheckUpdatesAsync(CancellationToken ct = default)
    {
        using var busy = BeginTask();

        var source = _settings.Current.MarketSource;
        Log($"开始同步插件索引（来源：{source}）…");

        var errors = new List<string>();

        async Task<MarketSyncResult?> AttemptAsync(string label, Func<CancellationToken, Task<string>> sync, string cacheName)
        {
            try
            {
                var method = await sync(ct).ConfigureAwait(false);
                return new MarketSyncResult(ScanIndex(cacheName), method);
            }
            catch (OperationCanceledException)
            {
                throw;   // 主动中止（切源 / 退出）：不兜底
            }
            catch (Exception ex)
            {
                errors.Add($"{label}：{ex.Message}");
                Log($"{label} 同步失败（{ex.Message}）");
                return null;
            }
        }

        MarketSyncResult? got;

        switch (source)
        {
            case "github":
                got = await AttemptAsync("GitHub", SyncGitHubListAsync, GitHubCacheName).ConfigureAwait(false);
                break;

            case "gitee":
                got = await AttemptAsync("Gitee", SyncGiteeListAsync, GiteeCacheName).ConfigureAwait(false);
                break;

            default:
                got = await AttemptAsync("官方服务器", SyncRemoteListAsync, RemoteCacheName).ConfigureAwait(false);

                if (got is null)
                {
                    Log("换 GitHub 顶上…");
                    got = await AttemptAsync("GitHub", SyncGitHubListAsync, GitHubCacheName).ConfigureAwait(false);
                }

                break;
        }

        if (got is null)
        {
            // 所有来源都没连上：退回本地缓存，别把列表清空
            var cached = ScanIndex();
            if (cached.Count > 0)
            {
                Log($"联网同步全失败（{string.Join("；", errors)}），改用本地缓存的索引");
                got = new MarketSyncResult(cached, "本地缓存");
            }
            else
            {
                throw new InvalidOperationException(
                    errors.Count > 0 ? string.Join("；", errors) : "索引同步失败");
            }
        }

        _catalog.Clear();
        _catalog.AddRange(got.Entries);
        LastSyncText = DateTime.Now.ToString("HH:mm:ss");

        PublishCatalog(got);
        return got;
    }

    /// <summary>不联网，只重新扫描缓存 + 本地插件目录（对应 rescan）。</summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        using var busy = BeginTask();

        var entries = await Task.Run(() => ScanIndex(), ct).ConfigureAwait(false);

        _catalog.Clear();
        _catalog.AddRange(entries);

        PublishCatalog(new MarketSyncResult(entries, "本地扫描"));
    }

    private void PublishCatalog(MarketSyncResult result)
    {
        Log($"已通过 {result.Method} 同步 · 发现 {result.Entries.Count} 个插件");
        CatalogReady?.Invoke(this, result);
    }

    // ---------- 三个来源 ----------

    /// <summary>下载官方聚合列表写进本地缓存，返回来源标识 remote-list。</summary>
    private async Task<string> SyncRemoteListAsync(CancellationToken ct)
    {
        var json = await FetchAsync(RemoteListUrl, ListTimeout, ct).ConfigureAwait(false);

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"远程列表不是有效 JSON：{ex.Message}");
        }

        var result = RemoteListParser.Parse(root);

        if (result.ProblemCount > 0)
            Log($"远程列表里 {result.ProblemCount} 条记录有问题，仍会显示在列表中并标注原因");

        // 空列表按「不可用」处理，交给别的来源兜底，避免页面空白
        if (result.IsEmpty) throw new InvalidDataException("远程列表里没有任何可用插件");

        WriteListCache(RemoteCacheName, json);
        Log($"远程列表同步成功（{result.Entries.Count} 个插件）");
        return "remote-list";
    }

    private Task<string> SyncGitHubListAsync(CancellationToken ct)
        => SyncMirrorListAsync(GitHubListUrls, GitHubCacheName, "github-list", "GitHub", ct);

    private Task<string> SyncGiteeListAsync(CancellationToken ct)
        => SyncMirrorListAsync(GiteeListUrls, GiteeCacheName, "gitee-list", "Gitee", ct);

    /// <summary>
    /// 从多个镜像 URL 拉取聚合列表写进本地缓存。
    /// 格式与官方列表一致，复用 <see cref="RemoteListParser"/>；多个镜像按顺序试，全失败才报错。
    /// </summary>
    private async Task<string> SyncMirrorListAsync(
        string[] urls, string cacheName, string sourceId, string label, CancellationToken ct)
    {
        string? json = null;
        var lastError = "";

        for (var i = 0; i < urls.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            // 头一个地址最新但不一定通，给它短上限；换镜像再放宽
            var timeout = i == 0 ? FastListTimeout : ListTimeout;

            try
            {
                json = await FetchAsync(urls[i], timeout, ct).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "请求超时";
                Log($"{label} 列表源不通（{HostOf(urls[i])}）：{lastError}");
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Log($"{label} 列表源不通（{HostOf(urls[i])}）：{lastError}");
            }
        }

        if (json is null) throw new IOException($"{label} 列表拉取失败：{lastError}");

        var result = RemoteListParser.Parse(json);

        if (result.ProblemCount > 0)
            Log($"{label} 列表里 {result.ProblemCount} 条记录有问题，仍会显示在列表中并标注原因");

        if (result.IsEmpty) throw new InvalidDataException($"{label} 列表里没有任何可用插件");

        WriteListCache(cacheName, json);
        Log($"{label} 列表同步成功（{result.Entries.Count} 个插件）");
        return sourceId;
    }

    private async Task<string> FetchAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false);
    }

    private static string HostOf(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private void WriteListCache(string name, string json)
    {
        try
        {
            Directory.CreateDirectory(_paths.MarketCacheDirectory);

            // 原样落盘（不重新序列化），保证缓存与线上内容一致
            File.WriteAllText(
                Path.Combine(_paths.MarketCacheDirectory, name),
                json,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"索引缓存写入失败（不影响本次使用）：{ex.Message}");
        }
    }

    // ================= 索引扫描 + 本地状态 =================

    /// <summary>
    /// 读索引条目（优先 preferred 缓存，其次官方列表，再退 GitHub / Gitee），
    /// 并给每条附加本地安装状态。
    /// </summary>
    public List<MarketEntry> ScanIndex(string? preferred = null)
    {
        var names = new List<string>();
        if (!string.IsNullOrEmpty(preferred)) names.Add(preferred);

        names.Add(RemoteCacheName);
        names.Add(GitHubCacheName);
        names.Add(GiteeCacheName);

        foreach (var name in names)
        {
            var entries = ReadListCache(name);
            if (entries is null || entries.Count == 0) continue;

            var installed = ScanInstalledPlugins();
            return entries.Select(e => ApplyLocalStatus(e, installed)).ToList();
        }

        Log("索引尚未同步");
        return new List<MarketEntry>();
    }

    private List<MarketEntry>? ReadListCache(string name)
    {
        var path = Path.Combine(_paths.MarketCacheDirectory, name);
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var result = RemoteListParser.Parse(json);
            return result.Entries.Count > 0 ? result.Entries.ToList() : null;
        }
        catch (Exception ex)
        {
            Log($"列表缓存 {name} 罢工了（{ex.Message}）");
            return null;
        }
    }

    /// <summary>
    /// 扫一遍 plugins/ 下已安装的插件目录。
    /// 返回 [(目录名, plugin.json 的字段表或 null)]；整批条目复用同一次扫描。
    /// </summary>
    public IReadOnlyList<(string Directory, IReadOnlyDictionary<string, JsonElement>? Manifest)> ScanInstalledPlugins()
    {
        var items = new List<(string, IReadOnlyDictionary<string, JsonElement>?)>();

        var root = _paths.PluginsDirectory;
        if (!Directory.Exists(root)) return items;

        foreach (var directory in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);

            // 依赖包目录不是插件，跳过
            if (string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase)) continue;

            var manifest = _store.ReadObjectAt(Path.Combine(directory, "plugin.json"));
            items.Add((name, manifest));
        }

        return items;
    }

    /// <summary>目录名 / 插件 id 归一化：忽略大小写，下划线当连字符。</summary>
    private static string NormalizeName(string? name)
        => (name ?? "").Trim().ToLowerInvariant().Replace('_', '-');

    /// <summary>
    /// 在已安装目录里找 id 对应的那一个，返回 (目录名, 清单)。
    ///
    /// 匹配顺序：
    /// 1. 目录名 == 索引 id（从市场装的插件就是这样）；
    /// 2. 包内 plugin.json 的 id == 索引 id；
    /// 3. 归一化后相等（忽略大小写、下划线 / 连字符，以及命名空间前缀）。
    ///
    /// 第 3 步是模糊匹配，只在候选**唯一**时采用 —— 两个作者的同名插件
    /// （各自都叫 weather）就不会互相串台，宁可显示「未安装」。
    /// </summary>
    public (string? Directory, IReadOnlyDictionary<string, JsonElement>? Manifest) MatchInstalled(
        string pluginId,
        IReadOnlyList<(string Directory, IReadOnlyDictionary<string, JsonElement>? Manifest)> installed)
    {
        foreach (var item in installed)
            if (string.Equals(item.Directory, pluginId, StringComparison.Ordinal)) return item;

        foreach (var item in installed)
            if (item.Manifest is not null && ReadText(item.Manifest, "id") == pluginId) return item;

        var want = NormalizeName(pluginId);
        var shortName = NormalizeName(pluginId.Split('.').LastOrDefault());

        var loose = new List<(string, IReadOnlyDictionary<string, JsonElement>?)>();

        foreach (var item in installed)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal) { NormalizeName(item.Directory) };

            if (item.Manifest is not null)
            {
                keys.Add(NormalizeName(ReadText(item.Manifest, "id")));
                keys.Add(NormalizeName(ReadText(item.Manifest, "name")));
            }

            keys.Remove("");

            if (want.Length > 0 && keys.Contains(want)) loose.Add(item);
            else if (!string.IsNullOrEmpty(shortName) && keys.Contains(shortName)) loose.Add(item);
        }

        return loose.Count == 1 ? loose[0] : (null, null);
    }

    private MarketEntry ApplyLocalStatus(
        MarketEntry entry,
        IReadOnlyList<(string Directory, IReadOnlyDictionary<string, JsonElement>? Manifest)> scanned)
    {
        var (directory, manifest) = MatchInstalled(entry.Id, scanned);

        var loadError = "";
        if (directory is not null && LoadFailures.TryGetValue(directory, out var reason))
            loadError = reason;

        var disabledIds = DisabledIds();
        var disabled = disabledIds.Contains(entry.Id)
                       || (directory is not null && disabledIds.Contains(directory));

        var installed = manifest is not null;
        var localVersion = installed ? ReadText(manifest!, "version") : "";

        entry.Installed = installed;
        entry.InstalledDir = directory ?? "";
        entry.InstalledVersion = localVersion;
        entry.Disabled = disabled;
        entry.LoadError = loadError;
        entry.UpdateAvailable = installed && SemVersion.IsNewer(entry.Version, localVersion);

        entry.Status = entry.Problem.Length > 0 ? MarketEntryStatus.Invalid
            : !installed ? MarketEntryStatus.NotInstalled
            : disabled ? MarketEntryStatus.Disabled
            : entry.UpdateAvailable ? MarketEntryStatus.Update
            : loadError.Length > 0 ? MarketEntryStatus.Failed
            : MarketEntryStatus.Installed;

        return entry;
    }

    private HashSet<string> DisabledIds()
        => new(_settings.Current.DisabledPlugins, StringComparer.OrdinalIgnoreCase);

    private static string ReadText(IReadOnlyDictionary<string, JsonElement> dictionary, string key)
        => dictionary.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // ================= 安装 =================

    /// <summary>安装（或升级）索引里的某个插件。</summary>
    public async Task<MarketTaskResult> InstallAsync(string pluginId, CancellationToken ct = default)
    {
        var entry = _catalog.FirstOrDefault(e => e.Id == pluginId);

        if (entry is null)
            return Report(new MarketTaskResult(pluginId, false, "插件不在当前索引中", false));

        if (entry.Problem.Length > 0)
            return Report(new MarketTaskResult(pluginId, false, $"索引里这条记录有问题，装不了：{entry.Problem}", false));

        if (IsBusy)
            return Report(new MarketTaskResult(pluginId, false, "上一个任务仍在进行中，请稍候…", false));

        using var busy = BeginTask();

        var (success, message, restart) = await InstallCoreAsync(entry.Clone(), ct).ConfigureAwait(false);

        await RescanCatalogQuietlyAsync(ct).ConfigureAwait(false);
        return Report(new MarketTaskResult(pluginId, success, message, restart));
    }

    /// <summary>批量更新所有「有新版本且未禁用」的插件。</summary>
    public async Task<IReadOnlyList<MarketBatchItem>> UpdateAllAsync(CancellationToken ct = default)
    {
        var targets = _catalog
            .Where(e => e.UpdateAvailable && !e.Disabled)
            .Select(e => e.Clone())
            .ToList();

        if (targets.Count == 0)
        {
            Log("没有可更新的插件");
            return Array.Empty<MarketBatchItem>();
        }

        if (IsBusy)
        {
            Log("上一个任务仍在进行中，请稍候…");
            return Array.Empty<MarketBatchItem>();
        }

        using var busy = BeginTask();

        var results = new List<MarketBatchItem>();

        foreach (var entry in targets)
        {
            if (ct.IsCancellationRequested) break;

            Log($"更新 {entry.Name} → {entry.Version} …");

            var (success, message, _) = await InstallCoreAsync(entry, ct).ConfigureAwait(false);
            results.Add(new MarketBatchItem(entry.Name, success, message));
        }

        var done = results.Count(r => r.Success);
        Log($"批量更新完成：成功 {done} / {results.Count}");

        foreach (var item in results.Where(r => !r.Success))
            Log($"  ✗ {item.Name}: {item.Message}");

        await RescanCatalogQuietlyAsync(ct).ConfigureAwait(false);
        return results;
    }

    /// <summary>
    /// 安装核心：下载 → 校验 → 备份 → 解压 → 写元数据。
    /// 返回 (成功, 消息, 是否需重启)。
    /// </summary>
    private async Task<(bool Success, string Message, bool Restart)> InstallCoreAsync(
        MarketEntry entry, CancellationToken ct)
    {
        var pluginId = entry.Id;
        _currentId = pluginId;

        try
        {
            // 已装目录不一定叫索引 id（手动丢进来的包按包内 id / 文件名词干落盘），
            // 这种就走原地升级，别另起一个目录 —— 否则同一个插件会被加载两遍。
            var (existing, _) = MatchInstalled(pluginId, ScanInstalledPlugins());
            var destination = Path.Combine(_paths.PluginsDirectory, existing ?? pluginId);
            var wasInstalled = Directory.Exists(destination);

            var temporaryDirectory = Path.Combine(Path.GetTempPath(), "cdl_plugin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);

            var packagePath = Path.Combine(temporaryDirectory, "package.cblplugin");

            try
            {
                ct.ThrowIfCancellationRequested();

                Log($"下载 {entry.Name} v{entry.Version} …");
                RaiseProgress(pluginId, 0);

                // GitHub 来源的包地址是国内连不上的 github.com/…/raw/…，
                // 这里展开成候选镜像（jsDelivr 优先），逐个试到 sha256 对上为止。
                var candidates = PluginPackage.DownloadCandidates(entry.DownloadUrl);
                var used = await DownloadWithFallbackAsync(candidates, packagePath, entry.Sha256, ct)
                    .ConfigureAwait(false);

                if (candidates.Count > 1) Log($"{entry.Name} 实际下载源：{HostOf(used)}");

                if (!PluginPackage.IsZip(packagePath))
                    throw new InvalidDataException("插件包不是有效的 ZIP（.cblplugin）");

                var backup = destination + ".bak";
                if (Directory.Exists(backup)) DeleteDirectoryQuietly(backup);

                var moved = false;

                if (wasInstalled)
                {
                    Directory.Move(destination, backup);
                    moved = true;
                }

                try
                {
                    PluginPackage.Extract(packagePath, destination);

                    if (!PluginPackage.FinalizeManifest(destination, entry, _store))
                        throw new InvalidDataException("插件包缺少 plugin.json 或入口文件");
                }
                catch
                {
                    // 失败回滚：清掉半成品，把备份换回来
                    if (Directory.Exists(destination)) DeleteDirectoryQuietly(destination);

                    if (moved && Directory.Exists(backup))
                    {
                        if (Directory.Exists(destination)) DeleteDirectoryQuietly(destination);
                        Directory.Move(backup, destination);
                    }

                    throw;
                }

                if (moved) DeleteDirectoryQuietly(backup);

                var verb = wasInstalled ? "更新" : "装上";
                Log($"{entry.Name} {verb}成功");

                return (true, $"{verb}成功", wasInstalled);
            }
            finally
            {
                RaiseProgress(pluginId, -1);
                DeleteDirectoryQuietly(temporaryDirectory);
            }
        }
        catch (Exception ex)
        {
            Log($"{entry.Name} 安装失败: {ex.Message}");
            return (false, ex.Message, false);
        }
    }

    /// <summary>
    /// 下载插件包：候选镜像挨个试，每下一次就验一次 sha256。
    /// 哪个镜像连不上、被截断、校验不过，就换下一个；全军覆没才抛异常，
    /// 异常里写明每个源的原因。返回命中的地址。
    /// </summary>
    private async Task<string> DownloadWithFallbackAsync(
        IReadOnlyList<string> urls, string destination, string expectSha, CancellationToken ct)
    {
        var errors = new List<string>();

        for (var i = 0; i < urls.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var url = urls[i];
            var host = HostOf(url);

            try
            {
                if (i > 0) Log($"换下载源重试（{host}）…");

                RaiseProgress(_currentId, 0);
                await DownloadOnceAsync(url, destination, ct).ConfigureAwait(false);

                if (expectSha.Length > 0)
                {
                    var actual = await PluginPackage.ComputeSha256Async(destination, ct).ConfigureAwait(false);

                    if (!string.Equals(actual, expectSha, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException(
                            $"SHA-256 校验失败（实际 {actual[..Math.Min(12, actual.Length)]}…，"
                            + $"期望 {expectSha[..Math.Min(12, expectSha.Length)]}…）");
                    }
                }

                return url;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{host}: {ex.Message}");
                Log($"下载源 {host} 没成功（{ex.Message}）");
            }
        }

        throw new IOException("所有下载源都失败 → " + string.Join("；", errors));
    }

    /// <summary>单次下载，写满目标文件；返回落盘字节数。</summary>
    private async Task<long> DownloadOnceAsync(string url, string destination, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载地址不是 https");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(DownloadTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        long done = 0;
        var lastPercent = -1;

        await using var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[65536];

        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();

            var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
            done += read;

            if (total > 0)
            {
                var percent = (int)(done * 100 / total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    RaiseProgress(_currentId, percent);
                }
            }
        }

        if (total > 0 && done != total)
            throw new IOException($"只下到 {done}/{total} 字节，连接被截断");

        return done;
    }

    // ================= 卸载 / 启用禁用 =================

    /// <summary>卸载由市场安装的插件。</summary>
    public async Task<MarketTaskResult> UninstallAsync(string pluginId, CancellationToken ct = default)
    {
        using var busy = BeginTask();

        var result = await Task.Run(() =>
        {
            var (directory, manifest) = MatchInstalled(pluginId, ScanInstalledPlugins());

            if (directory is null)
                return new MarketTaskResult(pluginId, false, "本地没找到这个插件的目录", false);

            var pluginDirectory = Path.Combine(_paths.PluginsDirectory, directory);

            var markerPath = Path.Combine(pluginDirectory, PluginPackage.MarkerFileName);
            var managed = (manifest is not null && ReadText(manifest, "id").Length > 0) || File.Exists(markerPath);

            if (!managed)
                return new MarketTaskResult(pluginId, false, "内置/本地插件不能从市场卸载", false);

            try
            {
                if (Directory.Exists(pluginDirectory)) Directory.Delete(pluginDirectory, recursive: true);

                var entry = _catalog.FirstOrDefault(e => e.Id == pluginId);
                var leftovers = new List<string> { pluginId + ".cblplugin", directory + ".cblplugin" };
                if (entry is not null && entry.File.Length > 0) leftovers.Add(entry.File);

                foreach (var name in leftovers)
                {
                    var file = Path.Combine(_paths.PluginsDirectory, name);
                    if (!File.Exists(file)) continue;

                    try
                    {
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 删不掉就算了，不影响卸载结果
                    }
                }

                Log($"已卸载 {pluginId}");
                return new MarketTaskResult(pluginId, true, "已卸载，重启后彻底生效", true);
            }
            catch (Exception ex)
            {
                return new MarketTaskResult(pluginId, false, $"卸载失败: {ex.Message}", true);
            }
        }, ct).ConfigureAwait(false);

        await RescanCatalogQuietlyAsync(ct).ConfigureAwait(false);
        return Report(result);
    }

    /// <summary>启用 / 禁用插件（重启后生效）。</summary>
    public MarketTaskResult SetEnabled(string pluginId, bool enabled)
    {
        _settings.Update(settings =>
        {
            var list = new List<string>(settings.DisabledPlugins);

            if (enabled) list.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));
            else if (!list.Contains(pluginId, StringComparer.OrdinalIgnoreCase)) list.Add(pluginId);

            settings.DisabledPlugins = list;
        });

        var verb = enabled ? "启用" : "禁用";
        Log($"已{verb} {pluginId}，重启后生效");

        RefreshCatalogQuietly();

        var result = new MarketTaskResult(pluginId, true, $"已{verb}，重启后生效", true);
        TaskDone?.Invoke(this, result);
        return result;
    }

    /// <summary>把 plugins/ 下的 .cblplugin 包解压安装（启动时先于插件加载执行）。</summary>
    public IReadOnlyList<string> InstallPendingPackages()
        => PluginPackage.InstallPendingPackages(_paths.PluginsDirectory, _store, Log);

    private async Task RescanCatalogQuietlyAsync(CancellationToken ct)
    {
        try
        {
            var entries = await Task.Run(() => ScanIndex(), ct).ConfigureAwait(false);

            _catalog.Clear();
            _catalog.AddRange(entries);

            CatalogReady?.Invoke(this, new MarketSyncResult(entries, "刷新"));
        }
        catch (Exception ex)
        {
            Log($"刷新列表失败: {ex.Message}");
        }
    }

    private void RefreshCatalogQuietly()
    {
        try
        {
            var entries = ScanIndex();

            _catalog.Clear();
            _catalog.AddRange(entries);

            CatalogReady?.Invoke(this, new MarketSyncResult(entries, "刷新"));
        }
        catch (Exception ex)
        {
            Log($"刷新列表失败: {ex.Message}");
        }
    }

    // ================= 工具 =================

    private void Log(string text) => LogLine?.Invoke(this, text);

    private void RaiseProgress(string pluginId, int percent)
        => TaskProgress?.Invoke(this, (pluginId, percent));

    private MarketTaskResult Report(MarketTaskResult result)
    {
        TaskDone?.Invoke(this, result);
        return result;
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录残留不影响正确性
        }
    }

    private IDisposable BeginTask()
    {
        IsBusy = true;
        BusyChanged?.Invoke(this, true);

        return new TaskScope(this);
    }

    private sealed class TaskScope : IDisposable
    {
        private readonly MarketService _owner;
        private bool _disposed;

        public TaskScope(MarketService owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _owner.IsBusy = false;
            _owner.BusyChanged?.Invoke(_owner, false);
        }
    }
}
