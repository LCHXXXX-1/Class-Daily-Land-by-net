using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 依赖安装的一步事件。对应源项目 emit 出去的事件字典
/// （stage / package / label / file / pct / done / total / speed / index / count / text）。
/// </summary>
public sealed record DependencyProgress(
    string Stage,
    string Package,
    string Label,
    string File = "",
    int Pct = 0,
    long Done = 0,
    long Total = 0,
    string Speed = "",
    int Index = 1,
    int Count = 1,
    string Text = "")
{
    /// <summary>附带产生该事件的插件名（源实现用 setdefault 补上）。</summary>
    public string Plugin { get; init; } = "";
}

/// <summary>
/// 依赖包安装器。对应源模块：plugin_deps.py 的
/// _plan_closure / label_for / _install_one / ensure_requirements。
///
/// 移植决策（重要）：
/// 源项目这里分发的是 **Python wheel**，配套的 ABI 自检（解释器标签、
/// 扫描 .so/.pyd）在 .NET 宿主上没有对应物。因此 .NET 版把「单文件归档」
/// 定义为 .nupkg / .zip 优先；遇到 .whl 会明确拒绝并说明原因 ——
/// 装进去一堆宿主用不上的 .py 文件再报「成功」比直接失败更糟。
/// </summary>
public static class PackageInstaller
{
    private const string TemporaryPrefix = ".tmp_";

    /// <summary>
    /// 确保 plugin 声明的依赖全部就绪（含递归依赖闭包）。
    /// </summary>
    /// <param name="database">packages.json 登记表。</param>
    /// <param name="root">packages 根目录。</param>
    /// <param name="baseUrls">依赖清单目录候选（空集合用默认地址）。</param>
    /// <param name="plugin">当前插件名（写进各包 used_by，也用于文案）。</param>
    /// <param name="names">该插件声明（已规范化）的包名列表。</param>
    /// <returns>(就绪包名列表, 错误：包名 → 说明)。错误不抛出而是收集，方便上层归到具体插件头上。</returns>
    public static async Task<(IReadOnlyList<string> Ready, IReadOnlyDictionary<string, string> Errors)>
        EnsureRequirementsAsync(
            PackageDatabase database,
            string root,
            IEnumerable<string> baseUrls,
            string plugin,
            IEnumerable<string> names,
            HttpClient http,
            Action<DependencyProgress>? emit = null,
            Func<bool>? shouldAbort = null,
            CancellationToken ct = default)
    {
        var wanted = PackageManifest.NormalizeRequires(names, plugin);
        if (wanted.Count == 0) return (Array.Empty<string>(), new Dictionary<string, string>());

        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Array.Empty<string>(),
                new Dictionary<string, string> { [plugin] = $"[{DepErrorKind.LoadError}] 依赖目录建不出来: {ex.Message}" });
        }

        void Emit(DependencyProgress progress)
            => emit?.Invoke(progress with { Plugin = plugin });

        List<string> order;
        Dictionary<string, DependencyManifest> manifests;
        Dictionary<string, string> reasons;

        try
        {
            (order, manifests, reasons) = await PlanClosureAsync(wanted, baseUrls, http, Emit, shouldAbort, ct)
                .ConfigureAwait(false);
        }
        catch (InstallAbortedException)
        {
            return (Array.Empty<string>(),
                new Dictionary<string, string> { [plugin] = $"[{DepErrorKind.MissingDep}] 安装被中止" });
        }
        catch (DepException ex)
        {
            Emit(new DependencyProgress("failed", "", plugin, Text: $"依赖准备失败 [{ex.Kind}] {ex.Message}"));
            return (Array.Empty<string>(),
                new Dictionary<string, string> { [plugin] = $"[{ex.Kind}] {ex.Message}" });
        }

        var ready = new List<string>();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var count = order.Count;

        for (var index = 1; index <= order.Count; index++)
        {
            var package = order[index - 1];
            var manifest = manifests[package];
            var version = manifest.Version;
            var label = LabelFor(package, reasons.GetValueOrDefault(package, ""));

            try
            {
                var have = database.InstalledVersion(package);

                if (string.Equals(have, version, StringComparison.Ordinal))
                {
                    // 同版本已在：不重复下载，只登记 used_by
                    database.RecordUse(package, version, plugin, manifest.Files.Count);
                    ready.Add(package);

                    Emit(new DependencyProgress("done", package, label, Pct: 100,
                        Index: index, Count: count,
                        Text: $"{label} {version} 已就位（{index}/{count}）"));

                    continue;
                }

                if (have.Length > 0)
                {
                    Emit(new DependencyProgress("extract", package, label, Index: index, Count: count,
                        Text: $"{label} 将从 {have} 覆盖为 {version}（{index}/{count}）"));
                }

                await InstallOneAsync(manifest, root, label, http, Emit, shouldAbort, index, count, ct)
                    .ConfigureAwait(false);

                database.RecordUse(package, version, plugin, manifest.Files.Count);
                ready.Add(package);

                Emit(new DependencyProgress("done", package, label, Pct: 100,
                    Index: index, Count: count,
                    Text: $"已安装 {label} {version}（{index}/{count}）"));
            }
            catch (InstallAbortedException)
            {
                errors[package] = $"[{DepErrorKind.MissingDep}] 安装被中止";
            }
            catch (DepException ex)
            {
                errors[package] = $"[{ex.Kind}] {ex.Message}";
                Emit(new DependencyProgress("failed", package, label, Index: index, Count: count,
                    Text: $"依赖安装失败 {label} [{ex.Kind}] {ex.Message}"));
            }
            catch (Exception ex)
            {
                errors[package] = $"[{DepErrorKind.DownloadFailed}] {ex.Message}";
                Emit(new DependencyProgress("failed", package, label, Index: index, Count: count,
                    Text: $"依赖安装失败 {label} {ex.Message}"));
            }

            // 一个包失败，后面的多半也白搭 → 止损
            if (errors.Count > 0) break;
        }

        return (ready, errors);
    }

    /// <summary>依赖的展示名：「requests 的依赖 urllib3」/「urllib3」。</summary>
    public static string LabelFor(string package, string reason)
        => reason.Length > 0 ? $"{reason} 的依赖 {package}" : package;

    /// <summary>
    /// 递归拉全部清单 → (安装顺序, 清单表, 引入原因表)。
    ///
    /// 安装顺序为后序（依赖排在前面）；reasons 记录「是谁把包装进来的」，
    /// 供文案「正在安装 requests 的依赖 urllib3…」。环依赖自动剪掉。
    /// </summary>
    private static async Task<(
        List<string> Order,
        Dictionary<string, DependencyManifest> Manifests,
        Dictionary<string, string> Reasons)> PlanClosureAsync(
            IEnumerable<string> names,
            IEnumerable<string> baseUrls,
            HttpClient http,
            Action<DependencyProgress>? emit,
            Func<bool>? shouldAbort,
            CancellationToken ct)
    {
        var bases = baseUrls.ToList();

        var manifests = new Dictionary<string, DependencyManifest>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        async Task VisitAsync(string package, string reason)
        {
            if (manifests.ContainsKey(package)) return;
            if (!visiting.Add(package)) return;      // 环：谁先来谁先装，剪掉即可

            emit?.Invoke(new DependencyProgress("manifest", package, package,
                Text: $"正在拉取清单：{package}（已发现 {manifests.Count + 1} 个包）"));

            var manifest = await PackageDownloader.FetchManifestAsync(bases, package, http, ct)
                .ConfigureAwait(false);

            manifests[package] = manifest;
            reasons[package] = reason;

            foreach (var dependency in manifest.Requires)
                await VisitAsync(dependency, package).ConfigureAwait(false);

            visiting.Remove(package);
            order.Add(package);
        }

        foreach (var name in names)
        {
            if (shouldAbort?.Invoke() == true) throw new InstallAbortedException();
            await VisitAsync(name, "").ConfigureAwait(false);
        }

        return (order, manifests, reasons);
    }

    /// <summary>
    /// 全部文件下到临时目录，齐了再原子换入 packages/&lt;pkg&gt;/。
    /// 对应 _install_one。
    /// </summary>
    private static async Task InstallOneAsync(
        DependencyManifest manifest,
        string root,
        string label,
        HttpClient http,
        Action<DependencyProgress>? emit,
        Func<bool>? shouldAbort,
        int index,
        int count,
        CancellationToken ct)
    {
        var package = manifest.Name;
        var target = Path.Combine(root, package);

        // 同包残留的旧临时目录（上次崩在半路）先清了
        try
        {
            foreach (var leftover in Directory.EnumerateDirectories(root)
                         .Where(d => Path.GetFileName(d).StartsWith(TemporaryPrefix + package, StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteDirectory(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清不掉就让后面的覆盖逻辑处理
        }

        var temporary = Path.Combine(
            root, $"{TemporaryPrefix}{package}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);

        try
        {
            if (manifest.IsArchive)
                await UnpackArchiveAsync(manifest, temporary, label, http, emit, shouldAbort, index, count, ct)
                    .ConfigureAwait(false);
            else
                await DownloadFilesAsync(manifest, temporary, label, http, emit, shouldAbort, index, count, ct)
                    .ConfigureAwait(false);

            // 全部落盘 → 换入正式目录（旧版本目录直接覆盖重装）
            var lastError = "";
            var moved = false;

            for (var attempt = 0; attempt < 2 && !moved; attempt++)
            {
                try
                {
                    if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                    Directory.Move(temporary, target);
                    moved = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lastError = ex.Message;
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
            }

            if (!moved)
                throw new DepException(DepErrorKind.LoadError, $"{package} 落盘失败（文件可能被占用）: {lastError}");

            if (!Directory.Exists(target))
                throw new DepException(DepErrorKind.LoadError, $"{package} 安装后目录不存在");
        }
        finally
        {
            TryDeleteDirectory(temporary);
        }
    }

    /// <summary>逐文件下载（对应 _install_one 的非 wheel 分支）。</summary>
    private static async Task DownloadFilesAsync(
        DependencyManifest manifest,
        string temporary,
        string label,
        HttpClient http,
        Action<DependencyProgress>? emit,
        Func<bool>? shouldAbort,
        int index,
        int count,
        CancellationToken ct)
    {
        var package = manifest.Name;
        var totalFiles = manifest.Files.Count;

        for (var i = 0; i < manifest.Files.Count; i++)
        {
            if (shouldAbort?.Invoke() == true) throw new InstallAbortedException();

            // 清单路径若自带 "<包名>/" 前缀，剥掉再落盘：
            // 目标是 packages/<pkg>/ 就是包的根目录，不能再套一层。
            var rel = manifest.Files[i].Rel;
            var prefix = package + "/";
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) rel = rel[prefix.Length..];
            if (rel.Length == 0) continue;

            var destination = PackageDownloader.SafeJoin(temporary, rel)
                ?? throw new DepException(DepErrorKind.DownloadFailed, $"清单里有非法路径: {rel}");

            var fileName = rel.Split('/').Last();
            var fileIndex = i + 1;
            var filePercent = (int)(fileIndex * 100L / totalFiles);

            // 车速统计：每攒够 0.3 秒刷一次
            var stopwatch = Stopwatch.StartNew();
            long lastDone = 0;
            var speed = 0.0;

            void OnProgress(long done, long total)
            {
                if (stopwatch.Elapsed.TotalSeconds >= 0.3)
                {
                    var elapsed = stopwatch.Elapsed.TotalSeconds;
                    speed = Math.Max(0, (done - lastDone) / elapsed);
                    stopwatch.Restart();
                    lastDone = done;
                }

                var percent = total > 0 ? (int)(done * 100 / total) : -1;

                emit?.Invoke(new DependencyProgress(
                    "download", package, label, fileName,
                    Pct: Math.Max(0, percent), Done: done, Total: total,
                    Speed: PackageDownloader.FormatSpeed(speed),
                    Index: index, Count: count,
                    Text: $"正在安装 {label}…（{index}/{count}）{fileName} "
                          + (total > 0 ? $"{percent}%" : PackageDownloader.FormatBytes(done))
                          + $" · {PackageDownloader.FormatBytes(done)}/"
                          + (total > 0 ? PackageDownloader.FormatBytes(total) : "?")
                          + $" · {PackageDownloader.FormatSpeed(speed)}"));
            }

            // 落盘路径是剥过前缀的，下载器只关心镜像与校验值
            var fileToFetch = new ManifestFile
            {
                Rel = rel,
                Sha256 = manifest.Files[i].Sha256,
                Urls = manifest.Files[i].Urls,
                Size = manifest.Files[i].Size,
            };

            await PackageDownloader.DownloadFileAsync(
                fileToFetch, destination, http, OnProgress, shouldAbort, ct).ConfigureAwait(false);

            emit?.Invoke(new DependencyProgress(
                "verify", package, label, fileName, Pct: 100, Index: index, Count: count,
                Text: $"正在安装 {label}…（{index}/{count}）校验通过 {fileName}（已完成 {filePercent}%）"));
        }
    }

    /// <summary>
    /// 单文件归档包：下载后解压。
    ///
    /// 与源实现（_unpack_wheel）的差异：
    /// .whl 是 CPython 专用的二进制分发格式，.NET 宿主加载不了其中的 .py / .pyd，
    /// 与其装完报「成功」，不如明确拒绝并给出可执行的建议。
    /// </summary>
    private static async Task UnpackArchiveAsync(
        DependencyManifest manifest,
        string temporary,
        string label,
        HttpClient http,
        Action<DependencyProgress>? emit,
        Func<bool>? shouldAbort,
        int index,
        int count,
        CancellationToken ct)
    {
        var file = manifest.Files[0];

        if (file.Rel.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
        {
            throw new DepException(
                DepErrorKind.MissingDep,
                $"{manifest.Name} 只有 Python wheel（{file.Rel}），.NET 宿主无法加载其中的 .py / .pyd；"
                + "如需为 .NET 插件提供依赖，请改用 .nupkg 或 .zip 形式的清单");
        }

        var archivePath = Path.Combine(temporary, "__package" + Path.GetExtension(file.Rel));

        emit?.Invoke(new DependencyProgress("download", manifest.Name, label, file.Rel,
            Index: index, Count: count,
            Text: $"正在安装 {label}…（{index}/{count}）{file.Rel}"));

        await PackageDownloader.DownloadFileAsync(file, archivePath, http, null, shouldAbort, ct)
            .ConfigureAwait(false);

        var extractRoot = Path.Combine(temporary, "__extract");
        Directory.CreateDirectory(extractRoot);

        ZipFile.ExtractToDirectory(archivePath, extractRoot, overwriteFiles: true);
        File.Delete(archivePath);

        // 归档内若只有一个顶层目录，把它的内容提到包根，避免多套一层
        var entries = Directory.EnumerateFileSystemEntries(extractRoot).ToList();
        var source = extractRoot;

        if (entries.Count == 1 && Directory.Exists(entries[0]))
        {
            var single = Directory.EnumerateFileSystemEntries(entries[0]).ToList();
            var onlyDirectories = single.Count == 0 || single.All(Directory.Exists);

            // Wheel / nupkg 常见布局：<pkg>-<version>/… 或 lib/… 或 tools/…
            source = entries[0];

            if (!onlyDirectories) source = extractRoot;
        }

        foreach (var item in Directory.EnumerateFileSystemEntries(source))
        {
            var destination = Path.Combine(temporary, Path.GetFileName(item));

            if (Directory.Exists(item)) Directory.Move(item, destination);
            else File.Move(item, destination, overwrite: true);
        }

        TryDeleteDirectory(extractRoot);

        emit?.Invoke(new DependencyProgress("verify", manifest.Name, label, file.Rel,
            Pct: 100, Index: index, Count: count,
            Text: $"正在安装 {label}…（{index}/{count}）校验通过 {file.Rel}"));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 残留目录留到下次启动清理
        }
    }
}
