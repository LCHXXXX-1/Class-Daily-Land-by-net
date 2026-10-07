using System.Text.Json;
using System.Text.RegularExpressions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 远程聚合索引的解析。对应源模块：plugin_market.py 的 parse_remote_list。
///
/// 在线索引格式（官方服务器 / GitHub / Gitee 三处一致）：
/// <code>
/// {
///   "plugins": [
///     {
///       "package_name": "class.lchx.clock",
///       "display_name": "时钟",
///       "author_name": "LCHXXXX",
///       "version": "1.0.0",
///       "description": "在灵动岛下拉面板显示当前时间和日期",
///       "file": "plugins/class.lchx.clock.cblplugin",
///       "sha256": "de39…5e1",
///       "url": "https://plugin.lchxxxx.de5.net/plugins/….cblplugin"
///     }
///   ]
/// }
/// </code>
/// </summary>
public static class RemoteListParser
{
    private static readonly Regex ShaPattern = new(
        @"^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 索引 id 允许带点号（如 class.lchx.clock），不是「大惊小怪」的非法字符。
    /// </summary>
    private static readonly Regex IdPattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>解析结果：条目列表 + 其中有多少条记录带问题。</summary>
    public readonly record struct Result(IReadOnlyList<MarketEntry> Entries, int ProblemCount)
    {
        /// <summary>远程列表里没有任何可用插件。</summary>
        public bool IsEmpty => Entries.Count == 0;
    }

    /// <summary>
    /// 解析远程聚合索引。
    ///
    /// 条目不合法也**不丢**：以前一条脏数据就把插件悄悄抹掉，用户只能看到
    /// 「这插件哪去了」，查都没处查。现在照样生成条目、把原因写进
    /// <see cref="MarketEntry.Problem"/>，由市场渲染成「⚠ + 原因」并禁用安装。
    /// 整体结构不合法（不是对象 / 缺 plugins 数组）才抛异常。
    /// </summary>
    /// <exception cref="InvalidDataException">整体结构不合法。</exception>
    public static Result Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("远程列表不是 JSON 对象");

        if (!root.TryGetProperty("plugins", out var plugins) || plugins.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("远程列表缺少 plugins 数组");

        var entries = new List<MarketEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var problems = 0;

        foreach (var item in plugins.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = ReadString(item, "package_name");
            var version = ReadString(item, "version");
            var sha = ReadString(item, "sha256").ToLowerInvariant();
            var url = ReadString(item, "url");

            // file 在索引里形如 "plugins/xxx.cblplugin"，只取纯文件名
            var fileName = Path.GetFileName(ReadString(item, "file"));
            if (string.IsNullOrEmpty(fileName) || fileName is "." or "..")
                fileName = id.Length > 0 ? id + ".cblplugin" : "";

            // ---- 逐项体检：只记原因，不丢条目 ----
            var problem = "";
            if (id.Length == 0) problem = "索引里缺少 package_name";
            else if (!IdPattern.IsMatch(id)) problem = $"package_name 不合法（只许字母数字和 . _ -）：'{id}'";
            else if (seen.Contains(id)) problem = $"package_name 在索引里重复：{id}";
            else if (SemVersion.Parse(version) is null) problem = $"版本号不是 SemVer（x.y.z）：'{version}'";
            else if (!ShaPattern.IsMatch(sha)) problem = $"sha256 不是 64 位十六进制：'{Truncate(sha, 20)}'";
            else if (url.Length == 0) problem = "索引里没有下载地址（url）";
            else if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                problem = $"下载地址必须是 https：'{Truncate(url, 40)}'";

            if (id.Length > 0) seen.Add(id);
            if (problem.Length > 0) problems++;

            var displayName = ReadString(item, "display_name");
            if (displayName.Length == 0) displayName = ReadString(item, "name");

            entries.Add(new MarketEntry
            {
                Id = id.Length > 0 ? id : (fileName.Length > 0 ? fileName : "?"),
                Name = displayName.Length > 0 ? displayName : (id.Length > 0 ? id : "未命名"),
                Version = version.Length > 0 ? version : "0.0.0",
                Format = "cblplugin",
                File = fileName,
                DownloadUrl = url,
                Sha256 = sha,
                Description = ReadString(item, "description"),
                Author = ReadString(item, "author_name"),
                Problem = problem,
            });
        }

        return new Result(entries, problems);
    }

    /// <summary>从 JSON 文本解析；文本不是合法 JSON 时抛异常。</summary>
    public static Result Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement.Clone());
    }

    private static string ReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        if (value.ValueKind != JsonValueKind.String) return "";

        return (value.GetString() ?? "").Trim();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];
}
