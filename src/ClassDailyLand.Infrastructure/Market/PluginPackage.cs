using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 插件包（.cblplugin / .cbplugin，本质是 ZIP）的读写工具。
/// 对应源模块：plugin_manager.py 的 _package_root / _safe_join / _safe_dir_name /
/// _dir_name_for / _resolve_entry / _install_packages，
/// 以及 plugin_market.py 的 download_candidates / _sha256 / _safe_extract / _finalize_meta。
///
/// 放在 Infrastructure 而非 PluginHost 的原因：这里的路径穿越防护与包结构
/// 判定属于安全关键逻辑，需要能被单元测试直接覆盖。
/// </summary>
public static partial class PluginPackage
{
    /// <summary>插件包后缀（两种都伺候，与源项目一致）。</summary>
    public static readonly string[] Extensions = { ".cblplugin", ".cbplugin" };

    /// <summary>托管插件标记文件：记录安装来源，用来判断包有没有更新过。</summary>
    public const string MarkerFileName = ".cblplugin.json";

    private const string ManifestFileName = "plugin.json";
    private const string DefaultEntry = "main.py";

    /// <summary>目录名净化后的兜底值。</summary>
    private const string FallbackDirectoryName = "plugin";

    /// <summary>GitHub 直链会 302 跳到 raw.githubusercontent.com —— 国内常年连不上，认出来换镜像。</summary>
    [GeneratedRegex(@"^https://github\.com/([^/]+/[^/]+)/raw/([^/]+)/(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubBlobPattern();

    [GeneratedRegex(@"^https://raw\.githubusercontent\.com/([^/]+/[^/]+)/([^/]+)/(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubRawPattern();

    // ================= 下载地址 =================

    /// <summary>
    /// 把一个下载地址展开成候选镜像（按优先级排好队）。
    /// GitHub 直链换成 jsDelivr CDN 打头 —— 同一个文件、同一份 sha256，国内连得上；
    /// 后面再排两种 GitHub 原形态。非 GitHub 地址原样退回，只有一个。
    /// </summary>
    public static IReadOnlyList<string> DownloadCandidates(string? url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0) return Array.Empty<string>();

        var match = GitHubBlobPattern().Match(raw);
        if (!match.Success) match = GitHubRawPattern().Match(raw);

        var ordered = new List<string>();

        if (match.Success)
        {
            var repo = match.Groups[1].Value;
            var branch = match.Groups[2].Value;
            var path = match.Groups[3].Value;

            ordered.Add($"https://cdn.jsdelivr.net/gh/{repo}@{branch}/{path}");
            ordered.Add($"https://raw.githubusercontent.com/{repo}/{branch}/{path}");
            ordered.Add($"https://github.com/{repo}/raw/{branch}/{path}");
        }

        ordered.Add(raw);

        var result = new List<string>();
        foreach (var candidate in ordered)
        {
            if (!candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Contains(candidate, StringComparer.Ordinal)) continue;

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>计算文件的 SHA-256（小写十六进制）。</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>文件是否是合法 ZIP（后缀对不代表内容对）。</summary>
    public static bool IsZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return false;
        }
    }

    /// <summary>返回文件名匹配到的插件包后缀（小写）；不匹配返回 null。</summary>
    public static string? MatchExtension(string? fileName)
    {
        var lower = (fileName ?? "").ToLowerInvariant();

        foreach (var extension in Extensions)
            if (lower.EndsWith(extension, StringComparison.Ordinal))
                return extension;

        return null;
    }

    // ================= 包结构 =================

    /// <summary>
    /// 返回包内根前缀（"" 或 "子目录/"）；找不到 plugin.json 返回 null。
    /// 对应 _package_root。
    /// </summary>
    public static string? PackageRoot(IReadOnlyCollection<string> memberNames)
    {
        if (memberNames.Contains("plugin.json")) return "";

        var tops = memberNames
            .Where(n => n.Contains('/'))
            .Select(n => n.Split('/', 2)[0])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal);

        foreach (var top in tops)
            if (memberNames.Contains($"{top}/plugin.json"))
                return top + "/";

        return null;
    }

    /// <summary>
    /// 防目录穿越：只允许解压到 base 内部。
    /// 「..」段被过滤掉而不是报错（与源实现一致），
    /// 过滤后落点若仍在 base 之外则返回 null。
    /// </summary>
    public static string? SafeJoin(string basePath, string relative)
    {
        var normalized = (relative ?? "").Replace('\\', '/').TrimStart('/');

        var parts = normalized
            .Split('/')
            .Where(p => p is not ("" or "." or ".."))
            .ToArray();

        if (parts.Length == 0) return null;

        var destination = Path.Combine(new[] { basePath }.Concat(parts).ToArray());

        var baseAbsolute = Path.GetFullPath(basePath);
        var destinationAbsolute = Path.GetFullPath(destination);

        var inside = string.Equals(destinationAbsolute, baseAbsolute, StringComparison.OrdinalIgnoreCase)
                     || destinationAbsolute.StartsWith(baseAbsolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        return inside ? destination : null;
    }

    /// <summary>
    /// 把任意名字洗成合法目录名。
    /// allowDot 为 true 时保留点号 —— 插件 id 可能带命名空间
    /// （class.作者.名字），洗掉点号就跟市场索引对不上了。
    /// 首尾的点一律去掉，避免生成 "." / ".." 这种危险名字。
    /// </summary>
    public static string SafeDirectoryName(string? name, bool allowDot = false)
    {
        var extra = allowDot ? new[] { '_', '-', '.' } : new[] { '_', '-' };

        var kept = new string((name ?? "")
            .Where(c => char.IsLetterOrDigit(c) || extra.Contains(c))
            .ToArray());

        // 中日韩文字属于 LetterOrDigit，会被保留；这里只管首尾点号
        kept = kept.Trim('.');

        return kept.Length > 0 ? kept : FallbackDirectoryName;
    }

    /// <summary>包内 plugin.json 自己声明的目录名候选：id 优先，其次 name。</summary>
    public static IReadOnlyList<string> DeclaredDirectoryNames(JsonElement? manifest)
    {
        var result = new List<string>();
        if (manifest is not { ValueKind: JsonValueKind.Object } meta) return result;

        foreach (var key in new[] { "id", "name" })
        {
            if (!meta.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;

            var raw = value.GetString();
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var candidate = SafeDirectoryName(raw, allowDot: true);
            if (candidate == FallbackDirectoryName) continue;
            if (result.Contains(candidate, StringComparer.Ordinal)) continue;

            result.Add(candidate);
        }

        return result;
    }

    /// <summary>
    /// 决定插件包解压到 plugins/ 下用哪个目录名。顺序：
    /// <list type="number">
    /// <item>已经装过这个包（标记文件里 package 字段就是该文件名，
    ///       或那个目录声明的名字与包内声明的名字有交集）→ 沿用旧目录名；</item>
    /// <item>包内 plugin.json 的 id → name；</item>
    /// <item>兜底：文件名词干。</item>
    /// </list>
    /// 目录名稳定才能和市场的 package_name 对上，
    /// 否则会出现「明明装了却显示未安装」。
    /// </summary>
    public static string DirectoryNameFor(
        string pluginsDirectory,
        string fileName,
        string stem,
        JsonElement? manifest,
        IJsonStore store)
    {
        var wanted = DeclaredDirectoryNames(manifest);

        if (Directory.Exists(pluginsDirectory))
        {
            foreach (var name in Directory.EnumerateDirectories(pluginsDirectory)
                         .Select(Path.GetFileName)
                         .Where(n => !string.IsNullOrEmpty(n))
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                var directory = Path.Combine(pluginsDirectory, name!);

                var marker = store.ReadObjectAt(Path.Combine(directory, MarkerFileName));
                if (marker is not null
                    && marker.TryGetValue("package", out var package)
                    && package.ValueKind == JsonValueKind.String
                    && string.Equals(package.GetString(), fileName, StringComparison.Ordinal))
                {
                    return name!;
                }

                if (wanted.Count == 0) continue;

                var oldManifest = store.ReadObjectAt(Path.Combine(directory, ManifestFileName));
                if (oldManifest is null) continue;

                var oldNames = DeclaredDirectoryNames(ToElement(oldManifest));
                if (oldNames.Intersect(wanted, StringComparer.Ordinal).Any()) return name!;
            }
        }

        return wanted.Count > 0 ? wanted[0] : SafeDirectoryName(stem);
    }

    /// <summary>
    /// 把 plugin.json 里的 entry 解析成真实文件路径。
    /// 兼容「带扩展名」与「不带扩展名」两种写法；找不到返回按 entry 拼出的路径（供报错）。
    /// </summary>
    public static string ResolveEntry(string pluginDirectory, string? entry)
    {
        var name = (entry ?? "").Trim();
        if (name.Length == 0) name = DefaultEntry;

        var candidates = new List<string> { name };

        if (!Path.HasExtension(name))
        {
            candidates.Add(name + ".dll");
            candidates.Add(name + ".py");
        }

        candidates.Add("main.dll");
        candidates.Add("main.py");

        foreach (var candidate in candidates)
        {
            var path = SafeJoin(pluginDirectory, candidate);
            if (path is not null && File.Exists(path)) return path;
        }

        return SafeJoin(pluginDirectory, name) ?? Path.Combine(pluginDirectory, name);
    }

    // ================= 解压与安装 =================

    /// <summary>
    /// 解压插件包到目标目录：防目录穿越 + 支持包内唯一顶层目录。
    /// 返回解压出的文件数。
    /// </summary>
    /// <exception cref="InvalidDataException">包内未找到 plugin.json，或含非法路径。</exception>
    public static int Extract(string packagePath, string destination)
    {
        using var archive = ZipFile.OpenRead(packagePath);

        var members = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();

        var root = PackageRoot(members);
        if (root is null) throw new InvalidDataException("插件包内未找到 plugin.json");

        Directory.CreateDirectory(destination);

        var count = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var name = entry.FullName.Replace('\\', '/');
            var relative = root.Length > 0 && name.StartsWith(root, StringComparison.Ordinal)
                ? name[root.Length..]
                : name;

            var target = SafeJoin(destination, relative);
            if (target is null) throw new InvalidDataException($"插件包含非法路径: {name}");

            var parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            entry.ExtractToFile(target, overwrite: true);
            count++;
        }

        return count;
    }

    /// <summary>
    /// 合并市场元数据进包内 plugin.json；entry 等加载器字段以**包内**为准。
    /// 返回 false 表示包内缺少 plugin.json 或入口文件（安装应判为失败）。
    /// </summary>
    public static bool FinalizeManifest(string destination, MarketEntry entry, IJsonStore store)
    {
        var manifestPath = Path.Combine(destination, ManifestFileName);

        var raw = store.ReadObjectAt(manifestPath);
        if (raw is null) return false;

        var dictionary = raw.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);

        var declaredEntry = dictionary.TryGetValue("entry", out var existing) && existing.ValueKind == JsonValueKind.String
            ? existing.GetString()
            : null;

        var entryPath = ResolveEntry(destination, declaredEntry);
        if (!File.Exists(entryPath)) return false;

        // 把 entry 归一化为真实存在的相对路径（兼容 "main" / "main.dll"）
        dictionary["entry"] = JsonSerializer.SerializeToElement(
            Path.GetRelativePath(destination, entryPath).Replace('\\', '/'));

        foreach (var (key, value) in new (string Key, string Value)[]
                 {
                     ("id", entry.Id),
                     ("format", entry.Format),
                     ("file", entry.File),
                     ("download_url", entry.DownloadUrl),
                     ("sha256", entry.Sha256),
                     ("author", entry.Author),
                 })
        {
            if (value.Length > 0) dictionary[key] = JsonSerializer.SerializeToElement(value);
        }

        dictionary["version"] = JsonSerializer.SerializeToElement(entry.Version);

        if (!HasText(dictionary, "name")) dictionary["name"] = JsonSerializer.SerializeToElement(entry.Name);
        if (!HasText(dictionary, "description") && entry.Description.Length > 0)
            dictionary["description"] = JsonSerializer.SerializeToElement(entry.Description);

        store.WriteAt(manifestPath, dictionary);
        return true;
    }

    /// <summary>
    /// 把 plugins/ 下的 .cblplugin 包自动解压安装成插件目录。
    /// 包没变化就跳过；包更新了（大小或修改时间变了）就覆盖重装。
    /// 对应 PluginManager._install_packages / _install_one_package。
    /// </summary>
    /// <returns>本次实际安装 / 重装的插件目录名。</returns>
    public static IReadOnlyList<string> InstallPendingPackages(
        string pluginsDirectory,
        IJsonStore store,
        Action<string>? log = null)
    {
        var installed = new List<string>();
        if (!Directory.Exists(pluginsDirectory)) return installed;

        IEnumerable<string> fileNames;
        try
        {
            fileNames = Directory.EnumerateFiles(pluginsDirectory)
                .Select(Path.GetFileName)
                .Where(n => n is not null && MatchExtension(n) is not null)
                .Select(n => n!)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return installed;
        }

        foreach (var fileName in fileNames)
        {
            try
            {
                var name = InstallOnePackage(pluginsDirectory, fileName, store, log);
                if (name is not null) installed.Add(name);
            }
            catch (Exception ex)
            {
                log?.Invoke($"安装 {fileName} 失败: {ex.Message}");
            }
        }

        return installed;
    }

    /// <summary>安装单个包；已是最新则返回 null。</summary>
    private static string? InstallOnePackage(
        string pluginsDirectory,
        string fileName,
        IJsonStore store,
        Action<string>? log)
    {
        var path = Path.Combine(pluginsDirectory, fileName);

        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!IsZip(path))
        {
            log?.Invoke($"{fileName} 不是有效的插件包");
            return null;
        }

        var extension = MatchExtension(fileName) ?? Extensions[0];
        var stem = fileName[..^extension.Length];

        using var archive = ZipFile.OpenRead(path);

        // 包内清单：优先根目录，其次唯一顶层目录
        var members = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName.Replace('\\', '/'))
            .ToList();

        var root = PackageRoot(members);
        if (root is null)
        {
            log?.Invoke($"{fileName} 内未找到 plugin.json，已跳过");
            return null;
        }

        var manifestElement = ReadManifestElement(archive, root + ManifestFileName);
        var directoryName = DirectoryNameFor(pluginsDirectory, fileName, stem, manifestElement, store);
        var target = Path.Combine(pluginsDirectory, directoryName);
        var markerPath = Path.Combine(target, MarkerFileName);

        var staticStamp = new PackageStamp
        {
            Package = fileName,
            Size = info.Length,
            Mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
        };

        // 已装且包未变化 → 跳过
        var existingMarker = store.ReadObjectAt(markerPath);
        if (existingMarker is not null
            && File.Exists(Path.Combine(target, ManifestFileName))
            && ReadLong(existingMarker, "size") == staticStamp.Size
            && ReadLong(existingMarker, "mtime") == staticStamp.Mtime)
        {
            return null;
        }

        Directory.CreateDirectory(target);
        var count = Extract(path, target);

        if (!File.Exists(Path.Combine(target, ManifestFileName)))
        {
            log?.Invoke($"{fileName} 解压后缺少 plugin.json");
            return null;
        }

        staticStamp.ExtractedAt = DateTimeOffset.Now.ToUnixTimeSeconds();
        staticStamp.Files = count;

        store.WriteAt(markerPath, staticStamp);
        log?.Invoke($"{fileName} 已安装到 plugins/{directoryName}/（{count} 个文件）");

        return directoryName;
    }

    private static JsonElement? ReadManifestElement(ZipArchive archive, string memberName)
    {
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), memberName, StringComparison.Ordinal));

        if (entry is null) return null;

        try
        {
            using var stream = entry.Open();
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static JsonElement ToElement(IReadOnlyDictionary<string, JsonElement> dictionary)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in dictionary)
            {
                writer.WritePropertyName(key);
                value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var document = JsonDocument.Parse(buffer);
        return document.RootElement.Clone();
    }

    private static long ReadLong(IReadOnlyDictionary<string, JsonElement> dictionary, string key)
        => dictionary.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : -1;

    private static bool HasText(IReadOnlyDictionary<string, JsonElement> dictionary, string key)
        => dictionary.TryGetValue(key, out var value)
           && value.ValueKind == JsonValueKind.String
           && !string.IsNullOrWhiteSpace(value.GetString());

    /// <summary>
    /// 包安装标记（.cblplugin.json）。字段名与源项目保持一致，
    /// 便于用户在两版之间来回切换时不会互相判为「包已变化」。
    /// </summary>
    internal sealed class PackageStamp
    {
        [System.Text.Json.Serialization.JsonPropertyName("package")]
        public string Package { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("size")]
        public long Size { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("mtime")]
        public long Mtime { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("extracted_at")]
        public long ExtractedAt { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("files")]
        public int Files { get; set; }
    }
}
