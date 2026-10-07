using System.IO;
using System.Text.RegularExpressions;

namespace ClassDailyLand.Infrastructure.Updates;

/// <summary>命中一条主入口特征。</summary>
/// <param name="Path">命中文件路径。</param>
/// <param name="How">命中原因：name（文件名即主入口）/ pattern（内容特征）/ project（工程文件）。</param>
public sealed record SourceEntryHit(string Path, string How);

/// <summary>
/// 源码运行保护。对应源模块：dev_guard.py。
///
/// 目的：程序目录里若存在「未打包的主入口源码」，说明当前是源码运行模式，
/// 此时自动更新会用发布版覆盖这些文件、抹掉本地改动，必须先拦下并提醒备份。
///
/// .NET 版的判定信号（对应 Python 版的文件名 + 内容特征两类）：
/// <list type="bullet">
/// <item><c>name</c>：文件名归一化后等于主入口候选（ClassDailyLand / Program / App / Main…）；</item>
/// <item><c>pattern</c>：文件里同时出现应用自身的类型引用与 <c>Main(</c> / <c>Application.Run</c>；</item>
/// <item><c>project</c>：存在 .sln / .csproj —— 这是 .NET 特有的强信号，
///       发布版目录里绝不会出现工程文件。</item>
/// </list>
/// </summary>
public static partial class SourceTreeGuard
{
    /// <summary>这些目录里出现源码一律不看（数据目录、缓存、打包产物…）。</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "settings", "plugins", "obj", "bin", "_internal", "build", "dist",
        ".git", ".hg", ".svn", ".venv", "venv", "env", "node_modules",
        ".idea", ".vscode", ".workbuddy", "site-packages", "publish",
    };

    /// <summary>
    /// 主入口候选文件名（大小写 / 空格 / 下划线 / 横线 全部归一化后比较）。
    /// 相对 Python 版把 classdailylandapp 换成了 Program —— .NET 的默认入口类名。
    /// </summary>
    private static readonly HashSet<string> EntryNames = new(StringComparer.Ordinal)
    {
        "classdailyland", "classdaily", "classdailyl", "main", "app", "start",
        "run", "program",
    };

    /// <summary>本项目核心类型，出现在主入口特征里才算数。</summary>
    private static readonly string[] CoreSignals =
    {
        "ClassDailyLand.App", "ClassDailyLand.Core", "ClassDailyLand.Infrastructure",
        "ClassDailyLand.PluginHost", "AppController", "PluginRuntime",
    };

    private const int MaxDepth = 2;            // 相对 root 的路径层数上限
    private const int MaxLines = 3000;         // 只看文件开头这么多行
    private const long MaxBytes = 512 * 1024;  // 单个文件最多读这么多

    [GeneratedRegex(@"^if\s*\(.*__main__", RegexOptions.CultureInvariant)]
    private static partial Regex PythonMainPattern();

    /// <summary>
    /// 扫描 root（含一层子目录）里命中的主入口源码。
    /// 返回按路径排序的命中列表；找不到就是空列表（= 正常打包版的行为）。
    /// </summary>
    public static IReadOnlyList<SourceEntryHit> Scan(string? root, int maxDepth = MaxDepth)
    {
        var hits = new List<SourceEntryHit>();

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return hits;

        try
        {
            Walk(new DirectoryInfo(root), root, 0, maxDepth, hits);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 扫不动就当没命中，绝不因保护逻辑本身报错阻断启动
        }

        hits.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return hits;
    }

    private static void Walk(DirectoryInfo directory, string root, int depth, int maxDepth, List<SourceEntryHit> hits)
    {
        if (depth > maxDepth) return;

        IEnumerable<FileInfo> files;
        try
        {
            files = directory.EnumerateFiles().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var file in files)
        {
            try
            {
                if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var how = LooksLikeEntry(file);
            if (how.Length > 0) hits.Add(new SourceEntryHit(file.FullName, how));
        }

        IEnumerable<DirectoryInfo> children;
        try
        {
            children = directory.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children)
        {
            if (SkipDirectories.Contains(child.Name)) continue;
            if (child.Name.StartsWith('.')) continue;

            Walk(child, root, depth + 1, maxDepth, hits);
        }
    }

    /// <summary>返回 name / pattern / project；空串表示不是主入口。</summary>
    private static string LooksLikeEntry(FileInfo file)
    {
        var stem = NormalizeStem(file.Name);
        var extension = file.Extension.ToLowerInvariant();

        // ---- 工程文件：.NET 特有的强信号 ----
        if (extension is ".sln" or ".csproj")
            return "project";

        if (extension is not (".cs" or ".py")) return "";

        if (EntryNames.Contains(stem)) return "name";

        // 本扫描器自身不该出现在命中列表里吓人
        if (stem is "sourcetreeguard" or "devguard") return "";

        if (file.Length > MaxBytes) return "";

        List<string> lines;
        try
        {
            using var stream = new FileStream(
                file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            lines = new List<string>();
            for (var i = 0; i < MaxLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                lines.Add(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }

        if (lines.Count == 0) return "";

        var core = false;
        var entry = false;
        var inDoc = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // ---- 跳过注释与文档块（否则本文件自己写在注释里的字样也会被算成命中）----
            if (line.StartsWith("\"\"\"", StringComparison.Ordinal) || line.StartsWith("'''", StringComparison.Ordinal))
            {
                inDoc = !inDoc;
                continue;
            }

            if (inDoc) continue;
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (line.StartsWith("*", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal))
                continue;

            if (!core && CoreSignals.Any(signal => line.Contains(signal, StringComparison.Ordinal)))
                core = true;

            // .NET 主入口：Main( 或 Application.Run；Python 主入口：if __name__ == '__main__'
            if (!entry
                && (line.Contains("static void Main", StringComparison.Ordinal)
                    || line.Contains("Application.Run", StringComparison.Ordinal)
                    || PythonMainPattern().IsMatch(line)))
            {
                entry = true;
            }

            if (core && entry) return "pattern";
        }

        return "";
    }

    /// <summary>`Class Daily Land` / `class_daily_land` / `ClassDailyLand` 一律归一。</summary>
    private static string NormalizeStem(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        foreach (var token in new[] { " ", "_", "-" })
            stem = stem.Replace(token, string.Empty, StringComparison.Ordinal);

        return stem.ToLowerInvariant();
    }

    /// <summary>当前目录是不是「源码运行模式」。扫描异常一律视为否。</summary>
    public static bool IsSourceTree(string? root)
    {
        try
        {
            return Scan(root).Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把命中的文件名拼成一句话，给弹窗文案用。</summary>
    public static string BriefNames(IReadOnlyList<SourceEntryHit> hits, int limit = 3)
    {
        var names = hits.Select(h => Path.GetFileName(h.Path)).ToList();

        if (names.Count <= limit) return string.Join("、", names);

        return string.Join("、", names.Take(limit)) + $" 等 {names.Count} 个文件";
    }
}
