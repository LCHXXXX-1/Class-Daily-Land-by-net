using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Schedule;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 课表导入导出与写入测试。对应源模块 schedule.py 的
/// update_timetable / restore_default / export_payload / import_text。
/// </summary>
public sealed class SchedulePortabilityTests
{
    private static ScheduleService CreateService(TestWorkspace workspace)
    {
        var service = new ScheduleService(workspace.Store);
        service.Load();
        return service;
    }

    // ================= 导出 =================

    [Fact]
    public void 导出当前课表_含课表数据与激活名()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var json = service.ExportJson();

        Assert.Contains("\"timetables\"", json);
        Assert.Contains(ScheduleDocument.DefaultTimetableName, json);
        Assert.Contains("\"active\"", json);

        // 不应包含扩展设置（未请求完整备份）
        Assert.DoesNotContain("\"holidays\"", json);
    }

    [Fact]
    public void 导出完整备份_含假期调休周末与提醒设置()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-12", "2026-10-14", "测试假期");
        service.AddMakeup("2026-10-10", 0);
        service.AdvanceMinutes = 5;

        var json = service.ExportJson(service.ListTimetables(), includeExtras: true);

        Assert.Contains("\"holidays\"", json);
        Assert.Contains("2026-10-12", json);
        Assert.Contains("\"makeup_days\"", json);
        Assert.Contains("\"weekend\"", json);
        Assert.Contains("\"advance_minutes\": 5", json);
    }

    [Fact]
    public void 导出中文不被转义()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var json = service.ExportJson();

        Assert.Contains("默认课表", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void 导出CSV_表头为节次加七个星期()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var csv = service.ExportCsv();
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // 表头 + 8 节课
        Assert.Equal(9, lines.Length);

        // 表头与源项目 CSV_HEADERS 一致（节次 / 开始 / 结束 + 七个星期）
        Assert.Equal("节次,开始,结束,周一,周二,周三,周四,周五,周六,周日", lines[0]);

        // 第 1 节行按「名称, 开始, 结束」分列
        var first = lines[1].Split(',');
        Assert.Equal("第1节", first[0]);
        Assert.Equal("08:00", first[1]);
        Assert.Equal("08:45", first[2]);
    }

    [Fact]
    public void 导出文件名_含课表名与时间戳()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var name = service.ExportFileName(ExportFormat.Json);

        Assert.StartsWith(ScheduleDocument.DefaultTimetableName, name);
        Assert.EndsWith(".json", name);
    }

    [Fact]
    public void 完整备份的文件名带固定前缀()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var name = service.ExportFileName(ExportFormat.Json, null, includeExtras: true);

        Assert.StartsWith("课表完整备份_", name);
        Assert.EndsWith(".json", name);
    }

    // ================= 导入 =================

    [Fact]
    public void 合并导入同名课表_自动追加序号不覆盖原课表()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var json = service.ExportJson(new[] { ScheduleDocument.DefaultTimetableName });
        var result = service.ImportJson(json, "merge");

        Assert.True(result.Success);
        Assert.Equal(1, result.TimetableCount);
        Assert.Equal(2, service.ListTimetables().Count);

        // 原课表仍在，导入的按源项目规则改名为「X (导入)」
        Assert.Contains(ScheduleDocument.DefaultTimetableName, service.ListTimetables());
        Assert.Contains($"{ScheduleDocument.DefaultTimetableName} (导入)", service.ListTimetables());
    }

    [Fact]
    public void 替换导入_只替换同名课表其余保留()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.CreateTimetable("临时课表");
        Assert.Equal(2, service.ListTimetables().Count);

        var json = service.ExportJson(new[] { ScheduleDocument.DefaultTimetableName });
        var result = service.ImportJson(json, "replace");

        // 源项目语义：单表文件的 replace 是「覆盖同名课表」，
        // 不是清空全部 —— 其他课表要保留
        Assert.True(result.Success);
        Assert.Equal(2, service.ListTimetables().Count);
        Assert.Contains("临时课表", service.ListTimetables());
        Assert.Equal(ScheduleDocument.DefaultTimetableName, service.ActiveName);
    }

    [Fact]
    public void 导入完整备份_一并恢复假期与提醒设置()
    {
        using var workspace = new TestWorkspace();
        var source = CreateService(workspace);

        source.AddHoliday("2026-10-12", "2026-10-14", "备份假期");
        source.AdvanceMinutes = 7;
        var backup = source.ExportJson(source.ListTimetables(), includeExtras: true);

        // 换一份全新的配置再导入
        using var target = new TestWorkspace();
        var service = CreateService(target);

        Assert.Empty(service.Holidays);

        var result = service.ImportJson(backup, "replace");

        Assert.True(result.Success);
        Assert.Single(service.Holidays);
        Assert.Equal("备份假期", service.Holidays[0].Name);
        Assert.Equal(7, service.AdvanceMinutes);
    }

    [Fact]
    public void 导入非法内容_返回失败而不抛异常()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.False(service.ImportJson("").Success);

        // 不以 { 或 [ 开头的内容会被当成 CSV（与源项目 detect_format 一致），
        // 所以「这不是 JSON」这种串反而能按 CSV 解析出一节课来 —— 不视为失败
        Assert.True(service.ImportJson("这不是 JSON").Success);

        Assert.False(service.ImportJson("[1,2,3]").Success);
        Assert.False(service.ImportJson("{\"foo\":1}").Success);
    }

    [Fact]
    public void 导入缺少年的课表_返回失败()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var result = service.ImportJson("""{"timetables":{"空课表":{"timetable":{}}}}""", "merge");

        Assert.False(result.Success);
    }

    // ================= 课表写入与恢复默认 =================

    [Fact]
    public void 覆写课表_时间片与课程同步更新()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var periods = new List<Period>
        {
            new() { Name = "早读", Start = "07:30", End = "07:50" },
        };
        var timetable = new Dictionary<string, List<string>>
        {
            ["0"] = new() { "英语" },
        };

        Assert.True(service.UpdateTimetable(ScheduleDocument.DefaultTimetableName, periods, timetable));
        Assert.Single(service.Periods);
        Assert.Equal("早读", service.Periods[0].Name);

        // 重新加载后依然生效（说明已落盘）
        var reloaded = new ScheduleService(workspace.Store);
        reloaded.Load();
        Assert.Single(reloaded.Periods);
        Assert.Equal("早读", reloaded.Periods[0].Name);
    }

    [Fact]
    public void 覆写不存在的课表_返回失败()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.False(service.UpdateTimetable("不存在的课表",
            new List<Period> { new() { Name = "第1节", Start = "08:00", End = "08:45" } },
            new Dictionary<string, List<string>>()));
    }

    [Fact]
    public void 恢复默认课表_清除自定义课表与假期()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.CreateTimetable("自定义课表");
        service.AddHoliday("2026-10-12", "2026-10-14");
        service.AdvanceMinutes = 9;

        service.RestoreDefault();

        Assert.Single(service.ListTimetables());
        Assert.Equal(ScheduleDocument.DefaultTimetableName, service.ActiveName);
        Assert.Empty(service.Holidays);
        Assert.Equal(2, service.AdvanceMinutes);
        Assert.Equal(8, service.Periods.Count);
    }
}
