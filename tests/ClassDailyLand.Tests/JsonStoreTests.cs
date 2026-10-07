using System.IO;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// JSON 存储测试。对应源模块 storage.py 的行为契约。
/// </summary>
public sealed class JsonStoreTests
{
    [Fact]
    public void 写入后可读回_且中文不被转义()
    {
        using var workspace = new TestWorkspace();

        workspace.Store.Write("sample.json", new SampleDocument { Name = "默认课表", Count = 8 });

        var raw = workspace.ReadConfigRaw("sample.json");

        // 对应源项目 json.dump(ensure_ascii=False)：配置文件必须人可读
        Assert.Contains("默认课表", raw);
        Assert.DoesNotContain("\\u", raw);

        var loaded = workspace.Store.Read<SampleDocument>("sample.json");
        Assert.NotNull(loaded);
        Assert.Equal("默认课表", loaded!.Name);
        Assert.Equal(8, loaded.Count);
    }

    [Fact]
    public void 文件不存在时返回回退值()
    {
        using var workspace = new TestWorkspace();

        var missing = workspace.Store.Read<SampleDocument>("not-exist.json");
        Assert.Null(missing);

        var fallback = new SampleDocument { Name = "回退" };
        var result = workspace.Store.Read("not-exist.json", fallback);
        Assert.Same(fallback, result);
    }

    [Fact]
    public void 文件损坏时返回回退值而不是抛异常()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("broken.json", "{ 这不是合法 JSON ");

        // 关键：坏配置不能把启动流程炸掉
        var result = workspace.Store.Read<SampleDocument>("broken.json");
        Assert.Null(result);
    }

    [Fact]
    public void 原子写入不残留临时文件()
    {
        using var workspace = new TestWorkspace();

        workspace.Store.Write("sample.json", new SampleDocument { Name = "a" });
        workspace.Store.Write("sample.json", new SampleDocument { Name = "b" });

        Assert.False(File.Exists(workspace.ConfigFile("sample.json.tmp")));

        var loaded = workspace.Store.Read<SampleDocument>("sample.json");
        Assert.Equal("b", loaded!.Name);
    }

    [Fact]
    public void 以键值形式读取对象_供逐字段容错使用()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("raw.json", """{"text":"值","num":3,"flag":true}""");

        var raw = workspace.Store.ReadObject("raw.json");

        Assert.NotNull(raw);
        Assert.Equal(3, raw!.Count);
        Assert.Equal("值", raw["text"].GetString());
        Assert.Equal(3, raw["num"].GetInt32());
        Assert.True(raw["flag"].GetBoolean());
    }

    [Fact]
    public void 顶层为数组时_ReadObject返回null()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("list.json", """["a","b"]""");

        Assert.Null(workspace.Store.ReadObject("list.json"));

        var array = workspace.Store.ReadArray("list.json");
        Assert.NotNull(array);
        Assert.Equal(2, array!.Count);
    }

    private sealed class SampleDocument
    {
        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("count")]
        public int Count { get; set; }
    }
}
