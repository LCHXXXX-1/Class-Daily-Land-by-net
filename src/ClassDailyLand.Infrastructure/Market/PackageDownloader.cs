using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 依赖包清单与文件的获取。对应源模块：plugin_deps.py 的
/// safe_join / fetch_manifest / _fetch_manifest_one / download_file / fmt_mb / fmt_speed。
/// </summary>
public static class PackageDownloader
{
    private const string UserAgent = "ClassDailyLand-DepInstaller/1.0";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>单文件 300MB 封顶，防清单写飞。</summary>
    private const long MaxFileBytes = 300L * 1024 * 1024;

    /// <summary>
    /// 防目录穿越：只允许落在 base 内部，非法返回 null。
    ///
    /// 与 <see cref="PluginPackage.SafeJoin"/> 的差异（忠实于源实现）：
    /// 这里含 ".." 段的一律**拒绝**，不给「静默清洗」留余地；绝对路径同样拒绝。
    /// </summary>
    public static string? SafeJoin(string basePath, string? relative)
    {
        var rel = (relative ?? "").Replace('\\', '/');

        if (rel.StartsWith('/')) return null;

        var parts = rel.Split('/').Where(p => p is not ("" or ".")).ToArray();

        if (parts.Length == 0) return null;
        if (parts.Any(p => p == "..")) return null;

        var destination = Path.Combine(new[] { basePath }.Concat(parts).ToArray());

        var baseAbsolute = Path.GetFullPath(basePath);
        var destinationAbsolute = Path.GetFullPath(destination);

        var inside = string.Equals(destinationAbsolute, baseAbsolute, StringComparison.OrdinalIgnoreCase)
                     || destinationAbsolute.StartsWith(
                         baseAbsolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        return inside ? destination : null;
    }

    /// <summary>字节数 → 展示字符串（自动 B / KB / MB）。</summary>
    public static string FormatBytes(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "?";

        if (value >= 1048576) return (value / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (value >= 1024) return (value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

        return ((long)value).ToString(CultureInfo.InvariantCulture) + " B";
    }

    /// <summary>字节/秒 → 实时速度展示字符串。</summary>
    public static string FormatSpeed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || double.IsInfinity(bytesPerSecond)) return "? MB/s";

        return bytesPerSecond >= 1048576
            ? (bytesPerSecond / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB/s"
            : (bytesPerSecond / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB/s";
    }

    /// <summary>
    /// 从 {base}/&lt;pkg&gt;.json 拉清单并校验；失败抛 <see cref="DepException"/>。
    ///
    /// baseUrl 可以是单个字符串，也可以是候选列表（主地址 + 镜像），按顺序重试。
    /// 地址指向依赖清单目录（默认 .../packages）；为兼容只给站点根的旧写法，
    /// 会自动补上 /packages 再拼包名。
    /// </summary>
    public static async Task<DependencyManifest> FetchManifestAsync(
        IEnumerable<string> baseUrls, string package, HttpClient http, CancellationToken ct = default)
    {
        var bases = baseUrls.Where(b => !string.IsNullOrWhiteSpace(b)).ToList();
        if (bases.Count == 0) bases.Add(PackageSources.DefaultBase);

        DepException? last = null;

        foreach (var baseUrl in bases)
        {
            try
            {
                return await FetchManifestOneAsync(baseUrl, package, http, ct).ConfigureAwait(false);
            }
            catch (DepException ex)
            {
                last = ex;
            }
        }

        throw last ?? new DepException(DepErrorKind.DownloadFailed, "没有可用的清单地址");
    }

    /// <summary>
    /// 单个地址拉清单：服务器明确说没有这个包（HTTP 404）→ MISSING_DEP，
    /// 其它网络问题 → DOWNLOAD_FAILED。
    /// </summary>
    private static async Task<DependencyManifest> FetchManifestOneAsync(
        string baseUrl, string package, HttpClient http, CancellationToken ct)
    {
        var baseAddress = (string.IsNullOrWhiteSpace(baseUrl) ? PackageSources.DefaultBase : baseUrl)
            .Trim().TrimEnd('/');

        if (baseAddress.Length > 0 && !baseAddress.EndsWith("/packages", StringComparison.OrdinalIgnoreCase))
            baseAddress = baseAddress + "/packages";

        var url = $"{baseAddress}/{package}.json";

        string json;
        try
        {
            json = await GetStringAsync(http, url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new DepException(DepErrorKind.MissingDep, $"服务器上没有 {package} 的清单");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is not null)
        {
            throw new DepException(
                DepErrorKind.DownloadFailed,
                $"拉取 {package} 清单失败: HTTP {(int)ex.StatusCode.Value}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DepException(DepErrorKind.DownloadFailed, $"拉取 {package} 清单失败: {ex.Message}");
        }

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new DepException(DepErrorKind.DownloadFailed, $"清单 {package} 不是有效 JSON: {ex.Message}");
        }

        var manifest = PackageManifest.TryParse(root, package, out var error);
        if (manifest is null)
            throw new DepException(DepErrorKind.DownloadFailed, $"清单 {package} 不合法: {error}");

        return manifest;
    }

    /// <summary>拉一个 JSON 文本（清单 / 远端包列表）。</summary>
    public static async Task<string> GetStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// 按多镜像顺序下载一个文件；sha256 **边下边算**，齐了才改名落盘。
    ///
    /// 返回命中的镜像 url。sha 不匹配换下一个镜像重试，
    /// 所有镜像都失败抛 <see cref="DepException"/>。
    /// <paramref name="onProgress"/>(已下字节, 总字节) 在下载循环里被调用。
    /// </summary>
    public static async Task<string> DownloadFileAsync(
        ManifestFile file,
        string destination,
        HttpClient http,
        Action<long, long>? onProgress = null,
        Func<bool>? shouldAbort = null,
        CancellationToken ct = default)
    {
        var part = destination + ".part";

        var parent = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        var lastError = "";

        try
        {
            foreach (var url in file.Urls)
            {
                try
                {
                    var (hash, total) = await DownloadOnceAsync(url, part, file, http, onProgress, shouldAbort, ct)
                        .ConfigureAwait(false);

                    var actual = Convert.ToHexString(hash).ToLowerInvariant();

                    if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        lastError = $"sha256 不符（实际 {actual[..12]}…，期望 {file.Sha256[..12]}…）";
                    }
                    else
                    {
                        // 齐了才改名落盘：中途断掉不会留下半个「看起来装好了」的文件
                        File.Move(part, destination, overwrite: true);
                        return url;
                    }
                }
                catch (InstallAbortedException)
                {
                    throw;
                }
                catch (DepException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                TryDelete(part);
            }

            var fileName = file.Rel.Split('/').Last();
            throw new DepException(
                DepErrorKind.DownloadFailed,
                $"文件 {fileName} 所有镜像都没成功: {lastError}");
        }
        finally
        {
            TryDelete(part);
        }

        static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 残留 .part 不影响正确性
            }
        }
    }

    private static async Task<(byte[] Hash, long Total)> DownloadOnceAsync(
        string url,
        string partPath,
        ManifestFile file,
        HttpClient http,
        Action<long, long>? onProgress,
        Func<bool>? shouldAbort,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(Timeout);

        using var response = await http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? 0;
        if (total <= 0) total = file.Size;

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        await using var source = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            partPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var buffer = new byte[65536];
        long done = 0;

        while (true)
        {
            if (shouldAbort?.Invoke() == true) throw new InstallAbortedException();

            var read = await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            if (read <= 0) break;

            await target.WriteAsync(buffer.AsMemory(0, read), linked.Token).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            done += read;

            if (done > MaxFileBytes)
                throw new DepException(
                    DepErrorKind.DownloadFailed,
                    $"文件超过 {FormatBytes(MaxFileBytes)} 上限");

            onProgress?.Invoke(done, total);
        }

        return (hasher.GetHashAndReset(), total);
    }
}
