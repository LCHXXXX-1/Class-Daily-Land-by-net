using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>依赖安装错误类别（对应源项目 DepError.kind）。</summary>
public static class DepErrorKind
{
    /// <summary>服务器上没有这个包（HTTP 404）。</summary>
    public const string MissingDep = "MISSING_DEP";

    public const string DownloadFailed = "DOWNLOAD_FAILED";
    public const string LoadError = "LOAD_ERROR";
}

/// <summary>依赖安装错误。</summary>
public sealed class DepException : Exception
{
    public DepException(string kind, string message) : base(message) => Kind = kind;

    public string Kind { get; }
}

/// <summary>宿主退出等原因中止安装循环。</summary>
public sealed class InstallAbortedException : Exception
{
    public InstallAbortedException(string message = "安装已中止") : base(message) { }
}

/// <summary>清单里的一个文件条目。</summary>
public sealed class ManifestFile
{
    /// <summary>相对包根目录的路径。</summary>
    public string Rel { get; init; } = "";

    public string Sha256 { get; init; } = "";

    /// <summary>https 镜像候选（已剔除注定请求不出去的地址）。</summary>
    public IReadOnlyList<string> Urls { get; init; } = Array.Empty<string>();

    public long Size { get; init; }

    /// <summary>是否是可解压的单文件归档（.nupkg / .zip / .whl）。</summary>
    public bool IsArchive => ArchiveExtensions.Any(
        e => Rel.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ArchiveExtensions = { ".zip", ".nupkg", ".whl" };
}

/// <summary>校验通过的依赖清单。</summary>
public sealed class DependencyManifest
{
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ManifestFile> Files { get; init; } = Array.Empty<ManifestFile>();

    /// <summary>
    /// 单文件归档包（对应源项目的 wheel 标志）。
    /// 源项目此处专指 Python 轮子；.NET 版扩展为 .nupkg / .zip / .whl 三种归档。
    /// </summary>
    public bool IsArchive { get; init; }
}

/// <summary>
/// 依赖清单的解析与获取。对应源模块：plugin_deps.py 的
/// resolve_bases / normalize_requires / parse_manifest / fetch_manifest 系列。
/// </summary>
public static partial class PackageManifest
{
    /// <summary>包名：小写字母开头，允许小写字母 / 数字 / 点 / 下划线 / 短横线。</summary>
    [GeneratedRegex(@"^[a-z][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    public static partial Regex PackageNamePattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ShaPattern();

    [GeneratedRegex(@"^[A-Za-z]:", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsDrivePattern();

    /// <summary>
    /// requires 字段 → 合法小写包名列表（去重、去自环）。
    /// 不合法的条目直接忽略：宁可放过作者拼写手滑，也别把整个插件卡死。
    /// </summary>
    public static List<string> NormalizeRequires(IEnumerable<string>? raw, string selfName = "")
    {
        var result = new List<string>();

        foreach (var item in raw ?? Enumerable.Empty<string>())
        {
            var name = (item ?? "").Trim().ToLowerInvariant();

            if (name.Length == 0) continue;
            if (!PackageNamePattern().IsMatch(name)) continue;
            if (string.Equals(name, selfName, StringComparison.Ordinal)) continue;
            if (result.Contains(name, StringComparer.Ordinal)) continue;

            result.Add(name);
        }

        return result;
    }

    /// <summary>
    /// 校验清单结构。返回错误说明（null 表示通过）。
    /// 对应 parse_manifest。
    /// </summary>
    public static DependencyManifest? TryParse(
        JsonElement root, string packageName, out string error)
    {
        error = "";

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "清单不是 JSON 对象";
            return null;
        }

        var name = (ReadString(root, "name")).Trim().ToLowerInvariant();
        if (name != packageName)
        {
            error = $"清单里的包名 '{name}' 与请求的 '{packageName}' 不一致";
            return null;
        }

        var version = ReadString(root, "version").Trim();
        if (version.Length == 0 || version.Length > 64)
        {
            error = "version 为空或过长";
            return null;
        }

        if (version.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            error = $"version 含非法字符: '{version}'";
            return null;
        }

        var requires = NormalizeRequires(ReadStringArray(root, "requires"), packageName);

        if (!root.TryGetProperty("files", out var rawFiles) || rawFiles.ValueKind != JsonValueKind.Array)
        {
            error = "files 缺失或为空";
            return null;
        }

        var files = new List<ManifestFile>();

        foreach (var item in rawFiles.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            // 兼容两种清单写法：path（逐文件清单）与 name（归档文件名）
            var rel = ReadString(item, "path");
            if (rel.Length == 0) rel = ReadString(item, "name");

            rel = rel.Trim().Replace('\\', '/');

            if (rel.Length == 0 || rel is "." or "..") continue;

            if (rel.StartsWith('/') || WindowsDrivePattern().IsMatch(rel))
            {
                error = $"文件路径不合法: {rel}";
                return null;
            }

            if (PackageDownloader.SafeJoin(Path.DirectorySeparatorChar + "base", rel) is null)
            {
                error = $"文件路径不合法: {rel}";
                return null;
            }

            var sha = ReadString(item, "sha256").Trim().ToLowerInvariant();
            if (!ShaPattern().IsMatch(sha))
            {
                error = $"文件 {rel} 的 sha256 非法";
                return null;
            }

            var (urls, bad) = CollectUrls(item);

            if (urls.Count == 0)
            {
                if (bad.Count > 0)
                {
                    // 典型场景：清单生成时把路径截断成了省略号 …
                    error = $"文件 {rel} 的下载地址全都不合法（{string.Join("；", bad.Select(b => b.Reason))}，"
                            + $"例如 {Truncate(bad[0].Url, 90)}）—— 这类地址一请求就失败，请重新生成依赖清单，别手写 URL";
                }
                else
                {
                    error = $"文件 {rel} 没有可用的 https 镜像";
                }

                return null;
            }

            long size = 0;
            if (item.TryGetProperty("size", out var sizeValue) && sizeValue.ValueKind == JsonValueKind.Number)
                sizeValue.TryGetInt64(out size);

            files.Add(new ManifestFile
            {
                Rel = rel,
                Sha256 = sha,
                Urls = urls,
                Size = Math.Max(0, size),
            });
        }

        if (files.Count == 0)
        {
            error = "files 里没有任何合法条目";
            return null;
        }

        return new DependencyManifest
        {
            Name = packageName,
            Version = version,
            Requires = requires,
            Files = files,
            IsArchive = files.Count == 1 && files[0].IsArchive,
        };
    }

    /// <summary>
    /// 地址「注定请求不出去」时返回中文原因，否则返回空串。
    ///
    /// 重点是非 ASCII：省略号 …、全角标点、中文、空格 都会让请求行编码失败。
    /// 这不是网络问题 —— 换镜像、重试都没用，只能改清单。
    /// </summary>
    public static string UrlRejectReason(string url)
    {
        if (url.Any(c => c > 0x7e))
        {
            var bad = new string(url.Where(c => c > 0x7e).Distinct().OrderBy(c => c).ToArray());
            return $"含非 ASCII 字符 '{bad}'";
        }

        if (url.Any(char.IsWhiteSpace)) return "含空白字符";

        return "";
    }

    /// <summary>
    /// 从清单条目提取 https 镜像列表（urls 列表或 url 单值都收）。
    /// 返回 (可用地址, 被剔除的地址及原因)。
    /// </summary>
    private static (List<string> Urls, List<(string Url, string Reason)> Bad) CollectUrls(JsonElement item)
    {
        var urls = new List<string>();
        var bad = new List<(string, string)>();

        var candidates = new List<string>();

        if (item.TryGetProperty("urls", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String) candidates.Add(entry.GetString() ?? "");
        }
        else
        {
            candidates.Add(ReadString(item, "url"));
        }

        foreach (var candidate in candidates)
        {
            var url = candidate.Trim();

            if (url.Length == 0) continue;
            if (urls.Contains(url, StringComparer.Ordinal)) continue;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;

            var reason = UrlRejectReason(url);
            if (reason.Length > 0) bad.Add((url, reason));
            else urls.Add(url);
        }

        return (urls, bad);
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static List<string> ReadStringArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return new List<string>();

        if (value.ValueKind == JsonValueKind.String)
            return new List<string> { value.GetString() ?? "" };

        if (value.ValueKind != JsonValueKind.Array) return new List<string>();

        return value.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")
            .ToList();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];
}
