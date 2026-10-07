using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 依赖包管理服务。对应源模块：package_store.py 的 PackageStore。
///
/// 职责：
/// 1. 按当前源列出远端包（index.json 或 Contents API）并合并本地登记，构成目录；
/// 2. 安装 / 卸载单个包（走 <see cref="PackageInstaller"/> 的完整校验链路）；
/// 3. 给「依赖包」设置页提供数据。
/// </summary>
public sealed class PackageService
{
    private readonly IPathService _paths;
    private readonly IJsonStore _store;
    private readonly ISettingsService _settings;
    private readonly HttpClient _http;
    private readonly PackageDatabase _database;

    private readonly List<PackageCatalogEntry> _catalog = new();

    public PackageService(IPathService paths, IJsonStore store, ISettingsService settings, HttpClient? http = null)
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
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _database = new PackageDatabase(paths.PackagesDirectory, store);
    }

    public event EventHandler<string>? LogLine;
    public event EventHandler<bool>? BusyChanged;
    public event EventHandler<IReadOnlyList<PackageCatalogEntry>>? CatalogReady;
    public event EventHandler<DependencyProgress>? DependencyProgress;
    public event EventHandler<(string Package, int Percent)>? TaskProgress;
    public event EventHandler<MarketTaskResult>? TaskDone;

    public IReadOnlyList<PackageCatalogEntry> Catalog => _catalog;
    public PackageDatabase Database => _database;
    public bool IsBusy { get; private set; }

    // ================= 下载源 =================

    /// <summary>当前生效的源 id；设置里写了未知值时回落到默认源。</summary>
    public string SourceId
    {
        get
        {
            var configured = (_settings.Current.PackagesSource ?? "").Trim();
            return PackageSources.IsKnown(configured) ? configured : PackageSources.DefaultSourceId;
        }
    }

    /// <summary>列出全部可选源（供设置页下拉）。</summary>
    public static IReadOnlyList<(string Label, string Id)> Sources => PackageSources.Options;

    /// <summary>切换下载源；返回是否设置成功。</summary>
    public bool SetSource(string sourceId)
    {
        if (!PackageSources.IsKnown(sourceId)) return false;

        _settings.Update(settings => settings.PackagesSource = sourceId);
        return true;
    }

    /// <summary>当前下载源的清单地址候选（主地址 + 镜像）。settings.packages_base_url 非空时只认它。</summary>
    public IReadOnlyList<string> BaseUrls()
        => PackageSources.ResolveBases(SourceId, _settings.Current.PackagesBaseUrl);

    // ================= 本地状态 =================

    /// <summary>已登记安装的包。</summary>
    public IReadOnlyDictionary<string, PackageRecord> Installed() => _database.AllEntries();

    /// <summary>
    /// 包名 → [使用它的插件目录名]，来源：本地插件 manifest + 登记表。
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> UsedByMap()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void Add(string package, string user)
        {
            if (!result.TryGetValue(package, out var list))
            {
                list = new List<string>();
                result[package] = list;
            }

            if (!list.Contains(user, StringComparer.OrdinalIgnoreCase)) list.Add(user);
        }

        if (Directory.Exists(_paths.PluginsDirectory))
        {
            foreach (var name in Directory.EnumerateDirectories(_paths.PluginsDirectory)
                         .Select(Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n))
                         .Select(n => n!)
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                if (string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase)) continue;

                var manifest = _store.ReadObjectAt(Path.Combine(_paths.PluginsDirectory, name, "plugin.json"));
                if (manifest is null) continue;

                foreach (var package in ReadRequires(manifest, name))
                    Add(package, name);
            }
        }

        foreach (var (package, record) in Installed())
            foreach (var user in record.UsedBy)
                Add(package, user);

        return result;
    }

    internal static IReadOnlyList<string> ReadRequires(
        IReadOnlyDictionary<string, JsonElement> manifest, string selfName)
    {
        if (!manifest.TryGetValue("requires", out var value)) return Array.Empty<string>();

        var raw = new List<string>();

        if (value.ValueKind == JsonValueKind.String)
        {
            raw.Add(value.GetString() ?? "");
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            raw.AddRange(value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? ""));
        }

        return PackageManifest.NormalizeRequires(raw, selfName);
    }

    // ================= 目录构建 =================

    /// <summary>刷新包列表（按当前源拉远端 + 合并本地）。</summary>
    public async Task<IReadOnlyList<PackageCatalogEntry>> RefreshAsync(CancellationToken ct = default)
    {
        using var busy = BeginTask();

        var entries = await BuildCatalogAsync(ct).ConfigureAwait(false);

        _catalog.Clear();
        _catalog.AddRange(entries);

        CatalogReady?.Invoke(this, _catalog);
        return _catalog;
    }

    private async Task<List<PackageCatalogEntry>> BuildCatalogAsync(CancellationToken ct)
    {
        var used = UsedByMap();
        var installed = Installed();

        HashSet<string>? remoteNames = null;

        try
        {
            var listed = await ListRemoteNamesAsync(ct).ConfigureAwait(false);
            if (listed is not null) remoteNames = new HashSet<string>(listed, StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"列出远端包失败：{ex.Message}");
        }

        if (remoteNames is null)
        {
            Log("当前下载源不支持自动列出，已改为显示：本地已装 + 插件声明的包（也可手动输入包名安装）");
        }
        else
        {
            Log($"远端发现 {remoteNames.Count} 个包");
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in remoteNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase)) names.Add(name);
        foreach (var name in installed.Keys) names.Add(name);
        foreach (var name in used.Keys) names.Add(name);

        var bases = BaseUrls();
        var entries = new List<PackageCatalogEntry>();

        foreach (var name in names)
        {
            var record = installed.GetValueOrDefault(name);
            var localVersion = "";

            if (record is not null && Directory.Exists(Path.Combine(_paths.PackagesDirectory, name)))
                localVersion = record.Version;

            var remoteVersion = "";

            if (remoteNames is not null && remoteNames.Contains(name))
            {
                try
                {
                    var manifest = await PackageDownloader
                        .FetchManifestAsync(bases, name, _http, ct).ConfigureAwait(false);

                    remoteVersion = manifest.Version;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    remoteVersion = "";     // 单个包拉清单失败不影响整表
                }
            }

            var status = localVersion.Length == 0 ? MarketEntryStatus.NotInstalled
                : remoteVersion.Length > 0 && !string.Equals(remoteVersion, localVersion, StringComparison.Ordinal)
                    ? MarketEntryStatus.Update
                    : MarketEntryStatus.Installed;

            entries.Add(new PackageCatalogEntry
            {
                Name = name,
                Version = remoteVersion,
                Installed = localVersion.Length > 0,
                InstalledVersion = localVersion,
                Manual = record?.Manual ?? false,
                UsedBy = used.GetValueOrDefault(name, new List<string>()),
                Status = status,
            });
        }

        return entries;
    }

    // ---------- 远端包列表 ----------

    /// <summary>按当前源的能力列出远端包名；不支持列出时返回 null。</summary>
    private async Task<List<string>?> ListRemoteNamesAsync(CancellationToken ct)
    {
        var source = PackageSources.Get(SourceId);

        if (source.Api.Length > 0) return await ListViaApiAsync(source, ct).ConfigureAwait(false);

        return await ListViaIndexAsync(BaseUrls()[0], ct).ConfigureAwait(false);
    }

    /// <summary>{base}/index.json：返回包名列表；没有 index（404）返回 null。</summary>
    private async Task<List<string>?> ListViaIndexAsync(string baseUrl, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/index.json";

        JsonElement root;
        try
        {
            var json = await PackageDownloader.GetStringAsync(_http, url, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return root.ValueKind switch
        {
            JsonValueKind.Object when root.TryGetProperty("packages", out var packages) => NamesFromItems(packages),
            JsonValueKind.Array => NamesFromItems(root),
            _ => new List<string>(),
        };
    }

    /// <summary>Contents API（Gitee / GitHub 通用）：列出 packages 目录的 &lt;包名&gt;.json。</summary>
    private async Task<List<string>?> ListViaApiAsync(PackageSource source, CancellationToken ct)
    {
        JsonElement root;
        try
        {
            var json = await PackageDownloader.GetStringAsync(_http, source.Api, ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        var names = new List<string>();
        if (root.ValueKind != JsonValueKind.Array) return names;

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var type = item.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;

            var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() : null;

            if (type != "file" || name is null) continue;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            names.Add(name[..^5].ToLowerInvariant());
        }

        return names;
    }

    private static List<string> NamesFromItems(JsonElement items)
    {
        var names = new List<string>();
        if (items.ValueKind != JsonValueKind.Array) return names;

        foreach (var item in items.EnumerateArray())
        {
            var name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    => n.GetString(),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
        }

        return names;
    }

    // ================= 安装 / 卸载 =================

    /// <summary>安装单个包（含递归依赖）。</summary>
    public async Task<MarketTaskResult> InstallAsync(string packageName, CancellationToken ct = default)
    {
        var name = (packageName ?? "").Trim().ToLowerInvariant();

        if (name.Length == 0)
            return new MarketTaskResult("", false, "包名为空", false);

        if (!PackageManifest.PackageNamePattern().IsMatch(name))
            return Report(new MarketTaskResult(name, false, "包名不合法（小写字母开头）", false));

        using var busy = BeginTask();

        Log($"开始安装 {name} …");

        var (_, errors) = await PackageInstaller.EnsureRequirementsAsync(
            _database,
            _paths.PackagesDirectory,
            BaseUrls(),
            plugin: "",
            new[] { name },
            _http,
            progress =>
            {
                if (progress.Text.Length > 0) Log($"[{name}] {progress.Text}");
                DependencyProgress?.Invoke(this, progress);
                TaskProgress?.Invoke(this, (name, progress.Pct));
            },
            () => ct.IsCancellationRequested,
            ct).ConfigureAwait(false);

        if (errors.Count > 0)
            return Report(new MarketTaskResult(name, false, string.Join("；", errors.Values), false));

        await RefreshQuietlyAsync(ct).ConfigureAwait(false);
        return Report(new MarketTaskResult(name, true, "安装完成", true));
    }

    /// <summary>彻底删除一个包（目录 + 登记）。</summary>
    public async Task<MarketTaskResult> UninstallAsync(string packageName, CancellationToken ct = default)
    {
        var name = (packageName ?? "").Trim();
        if (name.Length == 0) return new MarketTaskResult("", false, "包名为空", false);

        using var busy = BeginTask();

        var removed = await Task.Run(() => _database.Remove(name), ct).ConfigureAwait(false);

        await RefreshQuietlyAsync(ct).ConfigureAwait(false);

        return Report(new MarketTaskResult(name, true, removed ? "已删除" : "已从登记表移除（目录本就不存在）", true));
    }

    /// <summary>插件卸载时回收它引用的依赖。</summary>
    public IReadOnlyList<string> ReleasePluginDependencies(string pluginId)
    {
        try
        {
            return _database.Release(pluginId);
        }
        catch (Exception ex)
        {
            Log($"依赖回收出错（不影响卸载）: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private async Task RefreshQuietlyAsync(CancellationToken ct)
    {
        try
        {
            var entries = await BuildCatalogAsync(ct).ConfigureAwait(false);

            _catalog.Clear();
            _catalog.AddRange(entries);

            CatalogReady?.Invoke(this, _catalog);
        }
        catch (Exception ex)
        {
            Log($"刷新列表失败: {ex.Message}");
        }
    }

    // ================= 工具 =================

    private void Log(string text) => LogLine?.Invoke(this, text);

    private MarketTaskResult Report(MarketTaskResult result)
    {
        TaskDone?.Invoke(this, result);
        return result;
    }

    private IDisposable BeginTask()
    {
        IsBusy = true;
        BusyChanged?.Invoke(this, true);

        return new TaskScope(this);
    }

    private sealed class TaskScope : IDisposable
    {
        private readonly PackageService _owner;
        private bool _disposed;

        public TaskScope(PackageService owner) => _owner = owner;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _owner.IsBusy = false;
            _owner.BusyChanged?.Invoke(_owner, false);
        }
    }
}
