using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Schedule;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>课表导入导出（对应 schedule.py 的 timetable_to_csv / csv_to_timetable / import_text）。</summary>
public class ScheduleImportExportTests
{
    private static ScheduleService Create(TestWorkspace workspace)
    {
        var service = new ScheduleService(workspace.Store);
        service.Load();

        return service;
    }

    // ---------- CSV 导出格式 ----------

    [Fact]
    public void CSV表头与源项目一致()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var csv = service.ExportCsv();
        var header = csv.Split("\r\n")[0];

        // 必须与源项目 CSV_HEADERS 一致，否则导出的文件自己再导入时解析不出来
        Assert.Equal("节次,开始,结束,周一,周二,周三,周四,周五,周六,周日", header);
    }

    [Fact]
    public void CSV使用CRLF换行()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        Assert.Contains("\r\n", service.ExportCsv());
    }

    [Fact]
    public void CSV每行十个字段()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var lines = service.ExportCsv().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.All(lines, line => Assert.Equal(10, line.Split(',').Length));
    }

    // ---------- CSV 往返 ----------

    [Fact]
    public void CSV导出后能原样导入回来()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var before = service.Document.Timetables[service.ActiveName];
        var csv = service.ExportCsv();

        // 换个名字导入，避免撞上同名
        var outcome = service.ImportText(csv, "merge", "往返测试.csv");
        var importedName = Assert.Single(outcome.Added);

        var after = service.Document.Timetables[importedName];

        Assert.Equal(before.Periods.Count, after.Periods.Count);

        for (var i = 0; i < before.Periods.Count; i++)
        {
            Assert.Equal(before.Periods[i].Start, after.Periods[i].Start);
            Assert.Equal(before.Periods[i].End, after.Periods[i].End);
            Assert.Equal(before.Periods[i].Name, after.Periods[i].Name);
        }

        // 课程也要一致（末尾空课程会被裁掉，所以只比非空部分）
        for (var day = 0; day < 7; day++)
        {
            var key = day.ToString();

            var expected = before.Timetable.GetValueOrDefault(key, new List<string>());
            var actual = after.Timetable.GetValueOrDefault(key, new List<string>());

            while (expected.Count > 0 && string.IsNullOrWhiteSpace(expected[^1])) expected.RemoveAt(expected.Count - 1);

            Assert.Equal(expected, actual);
        }
    }

    // ---------- CSV 导入的容错 ----------

    [Fact]
    public void CSV无表头也能导入()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        const string csv = "第1节,08:00,08:45,语文,数学,英语,物理,化学,生物,体育\r\n";

        var outcome = service.ImportText(csv, "merge", "无表头.csv");
        var definition = service.Document.Timetables[Assert.Single(outcome.Added)];

        Assert.Single(definition.Periods);
        Assert.Equal("语文", definition.Timetable["0"][0]);
        Assert.Equal("体育", definition.Timetable["6"][0]);
    }

    [Fact]
    public void CSV时间缺失时回退到默认作息并给出提示()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        const string csv = "节次,开始,结束,周一\r\n第1节,,,语文\r\n";

        var outcome = service.ImportText(csv, "merge", "缺时间.csv");
        var definition = service.Document.Timetables[Assert.Single(outcome.Added)];

        // 默认作息第 1 节是 08:00-08:45
        Assert.Equal("08:00", definition.Periods[0].Start);
        Assert.Equal("08:45", definition.Periods[0].End);
        Assert.Contains(outcome.Warnings, w => w.Contains("默认作息"));
    }

    [Fact]
    public void CSV里啥都没有时抛异常()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        Assert.Throws<InvalidDataException>(() => service.ImportText("   ", "merge", "x.csv"));
        Assert.Throws<InvalidDataException>(() => service.ImportText("节次,开始,结束\r\n", "merge", "x.csv"));
    }

    // ---------- 导入方式 ----------

    [Fact]
    public void 合并导入时同名课表自动改名()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var name = service.ActiveName;
        var json = service.ExportJson();

        // 连续导入两次：第一次改名「X (导入)」，第二次改名「X (导入2)」
        var first = service.ImportText(json, "merge", "备份.json");
        var second = service.ImportText(json, "merge", "备份.json");

        Assert.Equal(new[] { $"{name} (导入)" }, first.Added);
        Assert.Contains($"{name} (导入2)", second.Added);
        Assert.Contains(first.Renamed, r => r.Contains("(导入)"));

        // 原课表仍在
        Assert.True(service.Document.Timetables.ContainsKey(name));
    }

    [Fact]
    public void 覆盖导入时替换同名课表()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var name = service.ActiveName;
        var countBefore = service.Document.Timetables.Count;

        var outcome = service.ImportText(service.ExportJson(), "replace", "备份.json");

        Assert.Equal(new[] { name }, outcome.Replaced);
        Assert.Empty(outcome.Added);
        Assert.Equal(countBefore, service.Document.Timetables.Count);   // 没有多出课表
    }

    // ---------- 预览 ----------

    [Fact]
    public void 预览报告格式与课表构成()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var preview = service.PreviewImport(service.ExportJson(), "备份.json");

        Assert.Equal("json", preview.Format);
        Assert.Equal("timetables", preview.Kind);       // 单表导出不含附加设置

        var summary = Assert.Single(preview.Timetables);
        Assert.True(summary.Periods > 0);
        Assert.True(summary.Courses > 0);
        Assert.True(summary.Exists);                     // 同名课表就在当前数据里
        Assert.True(preview.HasSameName);
    }

    [Fact]
    public void 预览识别完整备份()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var preview = service.PreviewImport(
            service.ExportJson(includeExtras: true), "完整备份.json");

        Assert.Equal("full", preview.Kind);
        Assert.True(preview.CanReplaceFull);
    }

    [Fact]
    public void 预览报告CSV格式()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        var preview = service.PreviewImport(service.ExportCsv(), "课表.csv");

        Assert.Equal("csv", preview.Format);
        Assert.Equal("timetables", preview.Kind);
        Assert.False(preview.CanReplaceFull);
    }

    [Fact]
    public void 非法内容预览时抛异常()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        Assert.ThrowsAny<Exception>(() => service.PreviewImport("{ 这不是 json", "x.json"));
        Assert.Throws<InvalidDataException>(() => service.PreviewImport("{}", "x.json"));
    }

    [Fact]
    public void 完整备份的replace会整体恢复附加设置()
    {
        using var workspace = new TestWorkspace();
        var service = Create(workspace);

        service.AddHoliday("2026-10-01", "2026-10-07", "国庆");
        service.AdvanceMinutes = 5;
        service.Save();

        var backup = service.ExportJson(includeExtras: true);

        // 改掉当前数据
        service.SetTempAdjust("", Array.Empty<string>());
        service.AdvanceMinutes = 0;
        service.Save();

        service.ImportText(backup, "replace", "完整备份.json");

        Assert.Equal(5, service.Document.AdvanceMinutes);
        Assert.NotEmpty(service.Document.Holidays);
    }
}
