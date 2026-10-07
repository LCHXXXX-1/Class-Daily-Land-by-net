using System.Text.Json;
using ClassDailyLand.Infrastructure.Market;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>语义化版本比较（对应 plugin_market.py 的 parse_version / version_gt）。</summary>
public class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0")]
    [InlineData("0.0.1")]
    [InlineData("10.20.30")]
    [InlineData("1.0.0-beta")]
    [InlineData("1.0.0-alpha.1")]
    [InlineData("1.0.0-beta.2.3")]
    [InlineData("1.0.0-rc.1+build.5")]
    [InlineData("  2.3.4  ")]
    public void 合法版本号可解析(string text) => Assert.NotNull(SemVersion.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("1.0")]
    [InlineData("1")]
    [InlineData("v1.0.0")]
    [InlineData("1.0.0.0")]
    [InlineData("abc")]
    [InlineData(null)]
    public void 非法版本号返回空(string? text) => Assert.Null(SemVersion.Parse(text));

    [Fact]
    public void 按主次修订号比较()
    {
        Assert.True(SemVersion.IsNewer("1.0.1", "1.0.0"));
        Assert.True(SemVersion.IsNewer("1.1.0", "1.0.9"));
        Assert.True(SemVersion.IsNewer("2.0.0", "1.99.99"));
        Assert.False(SemVersion.IsNewer("1.0.0", "1.0.1"));
        Assert.False(SemVersion.IsNewer("1.0.0", "1.0.0"));
    }

    [Fact]
    public void 正式版大于同号预发布版()
    {
        Assert.True(SemVersion.IsNewer("1.0.0", "1.0.0-beta"));
        Assert.False(SemVersion.IsNewer("1.0.0-beta", "1.0.0"));
    }

    [Fact]
    public void 预发布段数字排在字母之前()
    {
        // 源实现把纯数字段编码成 (0, 数值)、字母段编码成 (1, 文本)，
        // 所以 1.0.0-1 小于 1.0.0-alpha
        Assert.True(SemVersion.IsNewer("1.0.0-alpha", "1.0.0-1"));
        Assert.False(SemVersion.IsNewer("1.0.0-1", "1.0.0-alpha"));
    }

    [Fact]
    public void 预发布段前缀相同时段数多者更大()
    {
        Assert.True(SemVersion.IsNewer("1.0.0-alpha.1", "1.0.0-alpha"));
        Assert.False(SemVersion.IsNewer("1.0.0-alpha", "1.0.0-alpha.1"));
    }

    [Fact]
    public void 构建元数据不参与比较()
    {
        Assert.False(SemVersion.IsNewer("1.0.0+build.2", "1.0.0+build.1"));
        Assert.False(SemVersion.IsNewer("1.0.0+1", "1.0.0"));
    }

    [Fact]
    public void 缺失版本视为最旧()
    {
        // 本地没写 version（或写成垃圾）时，索引里的版本一律算更新
        Assert.True(SemVersion.IsNewer("1.0.0", ""));
        Assert.True(SemVersion.IsNewer("1.0.0", "不是版本号"));
        Assert.True(SemVersion.IsNewer("1.0.0", null));

        // 反过来：索引里的版本号非法时，绝不判定为「有更新」
        Assert.False(SemVersion.IsNewer("", "1.0.0"));
        Assert.False(SemVersion.IsNewer("坏版本", "1.0.0"));
    }
}

/// <summary>远程索引解析（对应 parse_remote_list）。</summary>
public class RemoteListParserTests
{
    /// <summary>官方索引的真实片段。</summary>
    private const string ValidList = """
    {
      "plugins": [
        {
          "package_name": "class.lchx.clock",
          "name": "clock",
          "display_name": "时钟",
          "author_name": "LCHXXXX",
          "version": "1.0.0",
          "description": "在灵动岛下拉面板显示当前时间和日期",
          "file": "plugins/class.lchx.clock.cblplugin",
          "sha256": "de39a1b2c3d4e5f60718293a4b5c6d7e8f9012345678abcdef01234567895e1f",
          "size": 802,
          "url": "https://plugin.example.com/plugins/class.lchx.clock.cblplugin"
        }
      ]
    }
    """;

    [Fact]
    public void 字段映射正确()
    {
        var result = RemoteListParser.Parse(ValidList);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("class.lchx.clock", entry.Id);
        Assert.Equal("时钟", entry.Name);
        Assert.Equal("1.0.0", entry.Version);
        Assert.Equal("LCHXXXX", entry.Author);
        Assert.Equal("cblplugin", entry.Format);

        // file 里的 plugins/ 前缀被去掉，只留文件名
        Assert.Equal("class.lchx.clock.cblplugin", entry.File);

        Assert.Equal("", entry.Problem);
        Assert.Equal(0, result.ProblemCount);
    }

    [Fact]
    public void 结构不合法时抛异常()
    {
        Assert.Throws<InvalidDataException>(() => RemoteListParser.Parse("[]"));
        Assert.Throws<InvalidDataException>(() => RemoteListParser.Parse("{}"));
        Assert.Throws<InvalidDataException>(() => RemoteListParser.Parse("""{"plugins": {}}"""));
    }

    [Fact]
    public void 脏数据不丢条目而是标注原因()
    {
        // 这是移植中特意保留的行为：以前一条脏数据就把插件悄悄抹掉，
        // 用户只能看到「这插件哪去了」，查都没处查
        const string json = """
        {
          "plugins": [
            { "package_name": "good.one", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/a.cblplugin", "file": "plugins/a.cblplugin" },
            { "package_name": "bad.ver", "version": "1.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/b.cblplugin" },
            { "package_name": "bad.sha", "version": "1.0.0", "sha256": "zz", "url": "https://x.test/c.cblplugin" },
            { "package_name": "bad.url", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "http://x.test/d.cblplugin" },
            { "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/e.cblplugin" },
            { "package_name": "bad id!", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/f.cblplugin" }
          ]
        }
        """;

        var result = RemoteListParser.Parse(json);

        Assert.Equal(6, result.Entries.Count);       // 一条都没丢
        Assert.Equal(5, result.ProblemCount);        // 五条有问题

        Assert.Equal("", result.Entries[0].Problem);
        Assert.Contains("SemVer", result.Entries[1].Problem);
        Assert.Contains("sha256", result.Entries[2].Problem);
        Assert.Contains("https", result.Entries[3].Problem);
        Assert.Contains("package_name", result.Entries[4].Problem);
        Assert.Contains("不合法", result.Entries[5].Problem);
    }

    [Fact]
    public void 重复的索引id会被标出()
    {
        const string json = """
        {
          "plugins": [
            { "package_name": "dup", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/a.cblplugin" },
            { "package_name": "dup", "version": "2.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/b.cblplugin" }
          ]
        }
        """;

        var result = RemoteListParser.Parse(json);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("", result.Entries[0].Problem);
        Assert.Contains("重复", result.Entries[1].Problem);
    }

    [Fact]
    public void 缺少下载地址也会被标出()
    {
        const string json = """
        {
          "plugins": [
            { "package_name": "no.url", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }
          ]
        }
        """;

        var result = RemoteListParser.Parse(json);

        Assert.Contains("下载地址", Assert.Single(result.Entries).Problem);
    }

    [Fact]
    public void 文件名为空时按id推断()
    {
        const string json = """
        {
          "plugins": [
            { "package_name": "class.lchx.clock", "version": "1.0.0", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "url": "https://x.test/a.cblplugin" }
          ]
        }
        """;

        Assert.Equal("class.lchx.clock.cblplugin", Assert.Single(RemoteListParser.Parse(json).Entries).File);
    }

    [Fact]
    public void 非法json文本抛异常()
        => Assert.ThrowsAny<JsonException>(() => RemoteListParser.Parse("{ 这不是 json"));
}

/// <summary>插件包路径与镜像候选（对应 _safe_join / _package_root / download_candidates）。</summary>
public class PluginPackageTests
{
    // ---------- 下载镜像候选 ----------

    [Fact]
    public void GitHub地址展开成镜像候选且jsdelivr优先()
    {
        var candidates = PluginPackage.DownloadCandidates(
            "https://github.com/LCHXXXX-1/Class-Daily-Land/raw/plugins/plugins/clock.cblplugin");

        // 原地址与候选里的 github.com 形态重复，会按源实现去重 → 3 个
        Assert.Equal(3, candidates.Count);
        Assert.Equal(
            "https://cdn.jsdelivr.net/gh/LCHXXXX-1/Class-Daily-Land@plugins/plugins/clock.cblplugin",
            candidates[0]);
        Assert.Equal(
            "https://raw.githubusercontent.com/LCHXXXX-1/Class-Daily-Land/plugins/plugins/clock.cblplugin",
            candidates[1]);
        Assert.Equal(
            "https://github.com/LCHXXXX-1/Class-Daily-Land/raw/plugins/plugins/clock.cblplugin",
            candidates[2]);
    }

    [Fact]
    public void raw域名地址同样展开()
    {
        var candidates = PluginPackage.DownloadCandidates(
            "https://raw.githubusercontent.com/o/r/main/a/b.cblplugin");

        Assert.Equal(3, candidates.Count);
        Assert.Equal("https://cdn.jsdelivr.net/gh/o/r@main/a/b.cblplugin", candidates[0]);
        Assert.Equal("https://raw.githubusercontent.com/o/r/main/a/b.cblplugin", candidates[1]);
        Assert.Equal("https://github.com/o/r/raw/main/a/b.cblplugin", candidates[2]);
    }

    [Fact]
    public void 非GitHub地址原样返回()
    {
        var candidates = PluginPackage.DownloadCandidates("https://plugin.example.com/a.cblplugin");

        Assert.Equal(new[] { "https://plugin.example.com/a.cblplugin" }, candidates);
    }

    [Fact]
    public void 空地址或非https地址被过滤()
    {
        Assert.Empty(PluginPackage.DownloadCandidates(""));
        Assert.Empty(PluginPackage.DownloadCandidates(null));
        Assert.Empty(PluginPackage.DownloadCandidates("http://x.test/a.cblplugin"));
    }

    // ---------- 防目录穿越 ----------

    [Fact]
    public void 正常相对路径可拼接()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-safe");
        var joined = PluginPackage.SafeJoin(root, "sub/file.txt");

        Assert.NotNull(joined);
        Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(joined!));
    }

    [Theory]
    [InlineData("../../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\..\\evil.txt")]
    public void 目录穿越被挡下(string relative)
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-safe");
        var joined = PluginPackage.SafeJoin(root, relative);

        // 源实现是「过滤掉 .. 段」而不是报错：过滤后落在 root 内部，所以这里应仍为安全路径，
        // 关键是绝不能逃出 root
        if (joined is not null)
            Assert.StartsWith(Path.GetFullPath(root), Path.GetFullPath(joined));
    }

    [Fact]
    public void 只有上级目录的路径返回空()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdl-safe");

        Assert.Null(PluginPackage.SafeJoin(root, ".."));
        Assert.Null(PluginPackage.SafeJoin(root, ""));
        Assert.Null(PluginPackage.SafeJoin(root, "."));
    }

    // ---------- 包内根目录 ----------

    [Fact]
    public void 清单在包根时无前缀()
    {
        var members = new[] { "plugin.json", "main.dll" };
        Assert.Equal("", PluginPackage.PackageRoot(members));
    }

    [Fact]
    public void 清单在唯一顶层目录时以它为根()
    {
        var members = new[] { "clock/plugin.json", "clock/main.dll", "clock/lib/x.dll" };
        Assert.Equal("clock/", PluginPackage.PackageRoot(members));
    }

    [Fact]
    public void 找不到清单返回空()
    {
        Assert.Null(PluginPackage.PackageRoot(new[] { "a/b.txt" }));
        Assert.Null(PluginPackage.PackageRoot(new[] { "readme.md" }));
    }

    [Fact]
    public void 多个顶层目录且都无清单时返回空()
    {
        Assert.Null(PluginPackage.PackageRoot(new[] { "a/x.txt", "b/y.txt" }));
    }

    // ---------- 目录名净化 ----------

    [Theory]
    [InlineData("clock", "clock")]
    [InlineData("My Plugin", "MyPlugin")]
    [InlineData("a/b\\c", "abc")]
    [InlineData("...", "plugin")]
    [InlineData("", "plugin")]
    [InlineData(null, "plugin")]
    public void 目录名被洗净(string? input, string expected)
        => Assert.Equal(expected, PluginPackage.SafeDirectoryName(input));

    [Fact]
    public void 目录名允许保留点号以对上命名空间id()
    {
        Assert.Equal("class.lchx.clock", PluginPackage.SafeDirectoryName("class.lchx.clock", allowDot: true));

        // 首尾的点一律去掉，避免生成 "." / ".."
        Assert.Equal("x", PluginPackage.SafeDirectoryName(".x.", allowDot: true));
    }

    [Fact]
    public void 带命名空间的id不会被洗掉点号()
    {
        using var document = JsonDocument.Parse("""{"id":"class.lchx.clock","name":"时钟"}""");
        var names = PluginPackage.DeclaredDirectoryNames(document.RootElement);

        Assert.Contains("class.lchx.clock", names);
    }

    [Fact]
    public void 只写name没写id时用name兜底()
    {
        using var document = JsonDocument.Parse("""{"name":"weather"}""");
        var names = PluginPackage.DeclaredDirectoryNames(document.RootElement);

        Assert.Equal(new[] { "weather" }, names);
    }

    // ---------- 包后缀 ----------

    [Theory]
    [InlineData("a.cblplugin", ".cblplugin")]
    [InlineData("A.CBLPLUGIN", ".cblplugin")]
    [InlineData("b.cbplugin", ".cbplugin")]
    [InlineData("c.zip", null)]
    [InlineData("d", null)]
    public void 包后缀识别(string fileName, string? expected)
        => Assert.Equal(expected, PluginPackage.MatchExtension(fileName));
}
