using System.IO;
using System.Text.Json.Serialization;
using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>登记表里的单个包条目。</summary>
public sealed class PackageRecord
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    /// <summary>使用它的插件目录名列表。手动安装的包此列表为空。</summary>
    [JsonPropertyName("used_by")]
    public List<string> UsedBy { get; set; } = new();

    /// <summary>文件数。</summary>
    [JsonPropertyName("files")]
    public int Files { get; set; }

    [JsonPropertyName("installed_at")]
    public string InstalledAt { get; set; } = "";

    /// <summary>手动安装：不随插件卸载回收。</summary>
    [JsonPropertyName("manual")]
    public bool Manual { get; set; }

    public PackageRecord Clone() => new()
    {
        Version = Version,
        UsedBy = new List<string>(UsedBy),
        Files = Files,
        InstalledAt = InstalledAt,
        Manual = Manual,
    };
}

/// <summary>packages.json 的顶层结构。</summary>
public sealed class PackageDatabaseDocument
{
    [JsonPropertyName("packages")]
    public Dictionary<string, PackageRecord> Packages { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("updated_at")]
    public string UpdatedAt { get; set; } = "";
}

/// <summary>
/// 依赖包登记表 &lt;packages&gt;/packages.json 的读写。
/// 对应源模块：plugin_deps.py 的 PackageDB。
///
/// 职责：记录「哪个包、什么版本、被哪些插件在用」。
/// used_by 空了且非手动安装的包，会在插件卸载时连带删除 —— 引用计数回收。
/// </summary>
public sealed class PackageDatabase
{
    public const string FileName = "packages.json";

    private readonly string _root;
    private readonly IJsonStore _store;
    private readonly object _lock = new();

    public PackageDatabase(string root, IJsonStore store)
    {
        _root = root;
        _store = store;
    }

    private string FilePath => Path.Combine(_root, FileName);

    private Dictionary<string, PackageRecord> Read()
    {
        var document = _store.ReadAt<PackageDatabaseDocument>(FilePath);
        var packages = document?.Packages;
        if (packages is null) return new Dictionary<string, PackageRecord>(StringComparer.Ordinal);

        // 统一大小写无关的比较器，避免 Gitee / GitHub 大小写差异造成重复登记
        return new Dictionary<string, PackageRecord>(packages, StringComparer.OrdinalIgnoreCase);
    }

    private void Write(Dictionary<string, PackageRecord> packages)
        => _store.WriteAt(FilePath, new PackageDatabaseDocument
        {
            Packages = packages,
            UpdatedAt = NowIso(),
        });

    /// <summary>某包的登记；未登记返回 null。</summary>
    public PackageRecord? Entry(string package)
    {
        lock (_lock)
        {
            return Read().TryGetValue(package, out var record) ? record.Clone() : null;
        }
    }

    /// <summary>已安装版本号；目录实际不存在也给空串（与磁盘不一致视为未装）。</summary>
    public string InstalledVersion(string package)
    {
        var record = Entry(package);
        if (record is null) return "";

        if (!Directory.Exists(Path.Combine(_root, package))) return "";

        return record.Version;
    }

    /// <summary>登记「pkg version 被 plugin 在用」；同版本重复只合并 used_by。</summary>
    public void RecordUse(string package, string version, string plugin, int files = 0)
    {
        lock (_lock)
        {
            try
            {
                var packages = Read();

                if (!packages.TryGetValue(package, out var record))
                    record = new PackageRecord();

                if (version.Length > 0) record.Version = version;
                else if (record.Version.Length == 0) record.Version = "unknown";

                if (plugin.Length > 0)
                {
                    if (!record.UsedBy.Contains(plugin, StringComparer.OrdinalIgnoreCase))
                        record.UsedBy.Add(plugin);
                }
                else
                {
                    // 手动安装：不随插件卸载回收
                    record.Manual = true;
                }

                if (files > 0) record.Files = files;
                if (record.InstalledAt.Length == 0) record.InstalledAt = NowIso();

                packages[package] = record;
                Write(packages);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 登记失败不阻断安装：包已落盘，下次扫描会重新登记
            }
        }
    }

    /// <summary>
    /// 插件卸载：把自己从所有包的 used_by 里摘掉，used_by 空了且非手动安装才删目录 + 销登记。
    /// 返回被删除目录的包名列表。
    /// </summary>
    public IReadOnlyList<string> Release(string plugin)
    {
        var removed = new List<string>();

        lock (_lock)
        {
            try
            {
                var packages = Read();
                var dirty = false;

                foreach (var name in packages.Keys.ToList())
                {
                    var record = packages[name];

                    if (record.UsedBy.RemoveAll(u => string.Equals(u, plugin, StringComparison.OrdinalIgnoreCase)) > 0)
                        dirty = true;

                    if (record.UsedBy.Count > 0 || record.Manual) continue;

                    // used_by 空了且非手动安装 → 删目录 + 销登记
                    var directory = PackageDownloader.SafeJoin(_root, name);
                    if (directory is not null && Directory.Exists(directory))
                        DeleteDirectoryQuietly(directory);

                    packages.Remove(name);
                    removed.Add(name);
                    dirty = true;
                }

                if (dirty) Write(packages);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 回收失败不影响卸载本身
            }
        }

        return removed;
    }

    /// <summary>全部登记（包名 → 条目）。</summary>
    public IReadOnlyDictionary<string, PackageRecord> AllEntries()
    {
        lock (_lock)
        {
            return Read().ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>彻底删除一个包（目录 + 登记）；返回是否删掉了目录。</summary>
    public bool Remove(string package)
    {
        lock (_lock)
        {
            try
            {
                var packages = Read();
                packages.Remove(package);
                Write(packages);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 继续尝试删目录
            }
        }

        var directory = PackageDownloader.SafeJoin(_root, package);
        if (directory is null || !Directory.Exists(directory)) return false;

        DeleteDirectoryQuietly(directory);
        return true;
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录被占用时留到下次
        }
    }

    private static string NowIso() => DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
}
