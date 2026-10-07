namespace ClassDailyLand.Infrastructure.Market;

/// <summary>一个依赖清单源。</summary>
/// <param name="Id">源 id（写进 settings.packages_source）。</param>
/// <param name="Label">显示名。</param>
/// <param name="Base">主清单地址（raw 直链）。</param>
/// <param name="Mirrors">备用镜像，按顺序重试（GitHub raw 在国内常连不通，配 jsDelivr）。</param>
/// <param name="Api">Contents API 地址（返回文件列表 JSON），用于列出该源有哪些包。</param>
public sealed record PackageSource(
    string Id,
    string Label,
    string Base,
    IReadOnlyList<string> Mirrors,
    string Api);

/// <summary>
/// 依赖包下载源。对应源模块：plugin_deps.py 的 PACKAGE_SOURCES / resolve_bases / resolve_base。
///
/// 每个源指向一个「依赖清单目录」，目录里是 &lt;包名&gt;.json。
///
/// 关于内容形态的重要说明（移植决策）：
/// 源项目这套清单分发的是 **Python wheel**，配套的 ABI 自检（解释器标签、
/// 扫描 .so/.pyd）在 .NET 宿主上没有对应物，因此 .NET 版把「单文件归档」
/// 定义为 .nupkg / .zip 优先；遇到 .whl 会明确拒绝并说明原因，
/// 而不是装进去一堆宿主用不上的 .py 文件后假装成功。
/// </summary>
public static class PackageSources
{
    public const string DefaultSourceId = "gitee";

    private static readonly Dictionary<string, PackageSource> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gitee"] = new PackageSource(
            "gitee",
            "Gitee",
            "https://gitee.com/lchxxxx/class-daily-land/raw/plugins/packages",
            Array.Empty<string>(),
            "https://gitee.com/api/v5/repos/lchxxxx/class-daily-land/contents/packages?ref=plugins"),

        ["github"] = new PackageSource(
            "github",
            "GitHub",
            "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land/plugins/packages",
            new[] { "https://cdn.jsdelivr.net/gh/LCHXXXX-1/Class-Daily-Land@plugins/packages" },
            "https://api.github.com/repos/LCHXXXX-1/Class-Daily-Land/contents/packages?ref=plugins"),
    };

    /// <summary>清单服务器兜底地址（settings.packages_base_url 为空时按 packages_source 取）。</summary>
    public static string DefaultBase => All[DefaultSourceId].Base;

    /// <summary>[(显示名, 源 id), ...]，顺序即定义顺序。</summary>
    public static IReadOnlyList<(string Label, string Id)> Options
        => All.Values.Select(s => (s.Label, s.Id)).ToList();

    public static bool IsKnown(string? sourceId) => All.ContainsKey((sourceId ?? "").Trim());

    public static PackageSource Get(string? sourceId)
        => All.TryGetValue((sourceId ?? "").Trim(), out var source) ? source : All[DefaultSourceId];

    /// <summary>
    /// 解析清单 base 候选列表（主地址 + 镜像）。
    /// customUrl（packages_base_url）非空时**只认它**；未知 / 空的 sourceId 回退到默认源。
    /// </summary>
    public static IReadOnlyList<string> ResolveBases(string? sourceId = null, string? customUrl = null)
    {
        var custom = (customUrl ?? "").Trim().TrimEnd('/');
        if (custom.Length > 0) return new[] { custom };

        var source = Get(sourceId);

        var result = new List<string> { source.Base };
        result.AddRange(source.Mirrors);

        return result;
    }

    /// <summary>单个主清单地址（取 ResolveBases 的第一个）。</summary>
    public static string ResolveBase(string? sourceId = null, string? customUrl = null)
        => ResolveBases(sourceId, customUrl)[0];
}
