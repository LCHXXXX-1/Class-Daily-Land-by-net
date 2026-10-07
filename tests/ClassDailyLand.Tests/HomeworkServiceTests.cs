using ClassDailyLand.Infrastructure.Homework;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 作业服务测试。对应源模块 homework.py 的 HomeworkManager。
/// 重点验证「纯字符串数组」历史格式的自动升级，以及按科目归组的顺序保持。
/// </summary>
public sealed class HomeworkServiceTests
{
    [Fact]
    public void 首次运行_文件不存在时为空列表且不报错()
    {
        using var workspace = new TestWorkspace();
        var service = new HomeworkService(workspace.Store);

        Assert.Empty(service.All);
        Assert.Empty(service.Grouped());
    }

    [Fact]
    public void 历史字符串数组格式_自动升级为结构化条目并写回()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("homework.json", """["数学卷子","背诵课文"]""");

        var service = new HomeworkService(workspace.Store);

        Assert.Equal(2, service.All.Count);
        Assert.Equal("未分类", service.All[0].Subject);
        Assert.Equal("数学卷子", service.All[0].Content);

        // 升级结果必须落盘，否则每次启动都要重做一遍转换
        var raw = workspace.ReadConfigRaw("homework.json");
        Assert.Contains("\"subject\": \"未分类\"", raw);
    }

    [Fact]
    public void 结构化格式可直接读取()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("homework.json", """
        [
          {"subject":"语文","content":"背诵《赤壁赋》","date":"2026-10-07"},
          {"subject":"数学","content":"卷子一套","date":""}
        ]
        """);

        var service = new HomeworkService(workspace.Store);

        Assert.Equal(2, service.All.Count);
        Assert.Equal("背诵《赤壁赋》", service.All[0].Content);
    }

    [Fact]
    public void 按科目归组_保持首次出现顺序()
    {
        using var workspace = new TestWorkspace();
        var service = new HomeworkService(workspace.Store);

        service.Add("数学", "卷子 A");
        service.Add("语文", "背诵");
        service.Add("数学", "卷子 B");

        var groups = service.Grouped();

        Assert.Equal(2, groups.Count);
        Assert.Equal("数学", groups[0].Subject);   // 首次出现的科目排在前
        Assert.Equal("语文", groups[1].Subject);

        Assert.Equal(2, groups[0].Contents.Count);
        Assert.Equal(new[] { "卷子 A", "卷子 B" }, groups[0].Contents);
    }

    [Fact]
    public void 空科目名归入未分类()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("homework.json", """[{"subject":"","content":"随手记"}]""");

        var service = new HomeworkService(workspace.Store);

        Assert.Single(service.Grouped());
        Assert.Equal("未分类", service.Grouped()[0].Subject);
    }

    [Fact]
    public void 增删改并存盘()
    {
        using var workspace = new TestWorkspace();
        var service = new HomeworkService(workspace.Store);

        Assert.True(service.Add("英语", "背单词"));
        Assert.True(service.Update(0, "英语", "背单词 + 听力", "2026-10-08"));
        Assert.Equal("背单词 + 听力", service.All[0].Content);
        Assert.Equal("2026-10-08", service.All[0].Date);

        Assert.True(service.Remove(0));
        Assert.Empty(service.All);

        var reloaded = new HomeworkService(workspace.Store);
        Assert.Empty(reloaded.All);
    }

    [Fact]
    public void 空科目或空内容不允许添加()
    {
        using var workspace = new TestWorkspace();
        var service = new HomeworkService(workspace.Store);

        Assert.False(service.Add("", "内容"));
        Assert.False(service.Add("科目", "   "));
        Assert.Empty(service.All);
    }

    [Fact]
    public void 越界索引的删除与修改返回false()
    {
        using var workspace = new TestWorkspace();
        var service = new HomeworkService(workspace.Store);

        Assert.False(service.Remove(0));
        Assert.False(service.Update(5, "a", "b", ""));
    }
}
