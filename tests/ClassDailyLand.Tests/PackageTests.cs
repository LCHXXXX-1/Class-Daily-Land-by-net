using System.Text.Json;
using ClassDailyLand.Infrastructure.Market;
using ClassDailyLand.Infrastructure.Storage;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>依赖清单解析与路径防护（对应 plugin_deps.py 的 parse_manifest / safe_join）。</summary>
public class PackageManifestTests
{
    private const string ShaA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void 合法清单可解析()
    {
        var manifest = Parse($$"""
        {
          "name": "requests",
          "version": "2.31.0",
          "requires": ["urllib3"],
          "files": [
            { "path": "requests/__init__.py", "sha256": "{{ShaA}}", "urls": ["https://x.test/a"], "size": 100 }
          ]
        }
        """, "requests");

        Assert.Equal("requests", manifest.Name);
        Assert.Equal("2.31.0", manifest.Version);
        Assert.Equal(new[] { "urllib3" }, manifest.Requires);
        Assert.Single(manifest.Files);
        Assert.False(manifest.IsArchive);
    }

    [Fact]
    public void 归档包被识别()
    {
        var manifest = Parse($$"""
        {
          "name": "somedep",
          "version": "1.0.0",
          "files": [ { "name": "somedep-1.0.0.nupkg", "sha256": "{{ShaA}}", "url": "https://x.test/a.nupkg" } ]
        }
        """, "somedep");

        Assert.True(manifest.IsArchive);
    }

    [Fact]
    public void 包名不一致被拒绝()
    {
        var error = TryParseError("""{"name":"other","version":"1.0.0","files":[{"path":"a","sha256":"AAA"}]}""", "requests");
        Assert.Contains("不一致", error);
    }

    [Theory]
    [InlineData("""{"name":"p","version":"","files":[{"path":"a","sha256":"x"}]}""")]
    [InlineData("""{"name":"p","files":[{"path":"a","sha256":"x"}]}""")]
    [InlineData("""{"name":"p","version":"1.0.0"}""")]
    [InlineData("""{"name":"p","version":"1.0.0","files":[]}""")]
    [InlineData("""{"name":"p","version":"1/2/3","files":[{"path":"a","sha256":"x"}]}""")]
    public void 结构不合法被拒绝(string json)
        => Assert.NotEqual("", TryParseError(json, "p"));

    [Fact]
    public void sha256非64位十六进制被拒绝()
        => Assert.Contains("sha256", TryParseError(
            """{"name":"p","version":"1.0.0","files":[{"path":"a","sha256":"xyz","url":"https://x.test/a"}]}""", "p"));

    [Fact]
    public void 绝对路径与盘符被拒绝()
    {
        Assert.Contains("路径不合法", TryParseError(
            $$"""{"name":"p","version":"1.0.0","files":[{"path":"/etc/passwd","sha256":"{{ShaA}}","url":"https://x.test/a"}]}""", "p"));

        Assert.Contains("路径不合法", TryParseError(
            $$"""{"name":"p","version":"1.0.0","files":[{"path":"C:/Windows/x","sha256":"{{ShaA}}","url":"https://x.test/a"}]}""", "p"));
    }

    [Fact]
    public void 目录穿越路径被拒绝()
        => Assert.Contains("路径不合法", TryParseError(
            $$"""{"name":"p","version":"1.0.0","files":[{"path":"../../evil","sha256":"{{ShaA}}","url":"https://x.test/a"}]}""", "p"));

    [Fact]
    public void 非https地址被剔除()
    {
        // url 是 http，urls 里有一个合法 https → 用合法的那个
        var manifest = Parse($$"""
        {
          "name": "p",
          "version": "1.0.0",
          "files": [ { "path": "a", "sha256": "{{ShaA}}",
                       "urls": ["http://x.test/a", "https://y.test/a"] } ]
        }
        """, "p");

        Assert.Equal(new[] { "https://y.test/a" }, manifest.Files[0].Urls);
    }

    [Fact]
    public void 全部地址非法时给出可读原因()
    {
        // 典型场景：清单生成时把长路径截断成了省略号
        var error = TryParseError(
            $$"""{"name":"p","version":"1.0.0","files":[{"path":"a","sha256":"{{ShaA}}","url":"https://x.test/…/a"}]}""", "p");

        Assert.Contains("非 ASCII", error);
        Assert.Contains("重新生成依赖清单", error);
    }

    [Fact]
    public void 包名规范化去重去自环并过滤非法()
    {
        var names = PackageManifest.NormalizeRequires(
            new[] { "urllib3", "URLLIB3", "self", "Bad Name", "ok_dep", "" }, "self");

        Assert.Equal(new[] { "urllib3", "ok_dep" }, names);
    }

    [Fact]
    public void 中文省略号与空白地址被拒绝()
    {
        // 省略号是最典型的成因：清单生成时把长路径截断了
        Assert.Contains("非 ASCII", PackageManifest.UrlRejectReason("https://x.test/…/a"));
        Assert.Contains("非 ASCII", PackageManifest.UrlRejectReason("https://x.test/中文"));
        Assert.Contains("空白", PackageManifest.UrlRejectReason("https://x.test/a b"));

        // 纯 ASCII 的正常地址不受影响
        Assert.Equal("", PackageManifest.UrlRejectReason("https://x.test/abc-1.0.nupkg"));
    }

    // ---------- safe_join（与包解压的版本语义不同：含 .. 一律拒绝）----------

    [Fact]
    public void 依赖路径含上级目录一律拒绝()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-deps");

        Assert.Null(PackageDownloader.SafeJoin(root, "../evil"));
        Assert.Null(PackageDownloader.SafeJoin(root, "a/../../evil"));
        Assert.Null(PackageDownloader.SafeJoin(root, "/abs/path"));
        Assert.Null(PackageDownloader.SafeJoin(root, ""));
    }

    [Fact]
    public void 依赖正常路径可拼接()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-deps");
        var joined = PackageDownloader.SafeJoin(root, "pkg/lib/x.dll");

        Assert.NotNull(joined);
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(joined!));
    }

    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(3145728, "3.0 MB")]
    public void 字节数格式化(long bytes, string expected)
        => Assert.Equal(expected, PackageDownloader.FormatBytes(bytes));

    // ---------- 辅助 ----------

    private static DependencyManifest Parse(string json, string package)
    {
        using var document = JsonDocument.Parse(json);
        var manifest = PackageManifest.TryParse(document.RootElement, package, out var error);

        Assert.True(manifest is not null, error);
        return manifest!;
    }

    private static string TryParseError(string json, string package)
    {
        using var document = JsonDocument.Parse(json);
        PackageManifest.TryParse(document.RootElement, package, out var error);
        return error;
    }
}

/// <summary>依赖登记表的引用计数回收（对应 PackageDB）。</summary>
public class PackageDatabaseTests
{
    [Fact]
    public void 登记与查询()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        Directory.CreateDirectory(Path.Combine(workspace.Root, "urllib3"));
        database.RecordUse("urllib3", "2.0.0", "clock", files: 5);

        Assert.Equal("2.0.0", database.InstalledVersion("urllib3"));
        Assert.Equal(5, database.Entry("urllib3")!.Files);
        Assert.Equal(new[] { "clock" }, database.Entry("urllib3")!.UsedBy);
    }

    [Fact]
    public void 目录不存在时视为未安装()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        database.RecordUse("ghost", "1.0.0", "clock");

        // 登记表里有，但磁盘上没有目录 → 视为未安装
        Assert.Equal("", database.InstalledVersion("ghost"));
    }

    [Fact]
    public void 同版本重复只合并使用方()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        Directory.CreateDirectory(Path.Combine(workspace.Root, "dep"));
        database.RecordUse("dep", "1.0.0", "clock");
        database.RecordUse("dep", "1.0.0", "weather");
        database.RecordUse("dep", "1.0.0", "clock");

        Assert.Equal(new[] { "clock", "weather" }, database.Entry("dep")!.UsedBy);
    }

    [Fact]
    public void 手动安装的包不随插件卸载回收()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        Directory.CreateDirectory(Path.Combine(workspace.Root, "manualdep"));
        database.RecordUse("manualdep", "1.0.0", plugin: "");

        Assert.True(database.Entry("manualdep")!.Manual);

        // 某个插件卸载，不应带走手动装的包
        var removed = database.Release("clock");

        Assert.Empty(removed);
        Assert.NotNull(database.Entry("manualdep"));
    }

    [Fact]
    public void 最后一个使用方卸载时回收目录()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        var directory = Path.Combine(workspace.Root, "shared");
        Directory.CreateDirectory(directory);

        database.RecordUse("shared", "1.0.0", "clock");
        database.RecordUse("shared", "1.0.0", "weather");

        // 还剩一个使用方 → 不删
        Assert.Empty(database.Release("clock"));
        Assert.True(Directory.Exists(directory));
        Assert.Equal(new[] { "weather" }, database.Entry("shared")!.UsedBy);

        // 最后一个也走了 → 删目录 + 销登记
        Assert.Equal(new[] { "shared" }, database.Release("weather"));
        Assert.False(Directory.Exists(directory));
        Assert.Null(database.Entry("shared"));
    }

    [Fact]
    public void 彻底删除会同时清掉目录与登记()
    {
        using var workspace = new TestWorkspace();
        var database = new PackageDatabase(workspace.Root, workspace.Store);

        var directory = Path.Combine(workspace.Root, "goner");
        Directory.CreateDirectory(directory);
        database.RecordUse("goner", "1.0.0", "");

        Assert.True(database.Remove("goner"));
        Assert.False(Directory.Exists(directory));
        Assert.Null(database.Entry("goner"));
    }

    [Fact]
    public void 登记表损坏时不影响启动()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store;
        var database = new PackageDatabase(workspace.Root, store);

        Directory.CreateDirectory(workspace.Root);
        File.WriteAllText(Path.Combine(workspace.Root, PackageDatabase.FileName), "{ 这不是 json");

        Assert.Empty(database.AllEntries());
        Assert.Null(database.Entry("anything"));
    }

    [Fact]
    public void 解析出的是与源项目一致的登记表结构()
    {
        using var workspace = new TestWorkspace();
        var store = workspace.Store;
        var database = new PackageDatabase(workspace.Root, store);

        Directory.CreateDirectory(Path.Combine(workspace.Root, "requests"));
        database.RecordUse("requests", "2.31.0", "clock", files: 12);

        var json = File.ReadAllText(Path.Combine(workspace.Root, PackageDatabase.FileName));

        Assert.Contains("\"packages\"", json);
        Assert.Contains("\"used_by\"", json);
        Assert.Contains("\"installed_at\"", json);
        Assert.Contains("2.31.0", json);
    }
}

/// <summary>下载源地址解析（对应 resolve_bases）。</summary>
public class PackageSourcesTests
{
    [Fact]
    public void 默认源是gitee()
        => Assert.Equal("gitee", PackageSources.DefaultSourceId);

    [Fact]
    public void 主地址加镜像按顺序返回()
    {
        var github = PackageSources.ResolveBases("github");

        Assert.Equal(2, github.Count);
        Assert.StartsWith("https://raw.githubusercontent.com/", github[0]);
        Assert.StartsWith("https://cdn.jsdelivr.net/", github[1]);
    }

    [Fact]
    public void 自定义地址只认它自己()
    {
        var bases = PackageSources.ResolveBases("github", "https://my.test/packages/");

        // 尾部斜杠会被裁掉
        Assert.Equal(new[] { "https://my.test/packages" }, bases);
    }

    [Fact]
    public void 未知源回落到默认源()
    {
        Assert.Equal(PackageSources.ResolveBases("gitee"), PackageSources.ResolveBases("不存在"));
        Assert.Equal(PackageSources.ResolveBases(null), PackageSources.ResolveBases(""));
    }

    [Fact]
    public void 可选源列表带显示名()
    {
        var options = PackageSources.Options;

        Assert.Contains(("Gitee", "gitee"), options);
        Assert.Contains(("GitHub", "github"), options);
    }
}
