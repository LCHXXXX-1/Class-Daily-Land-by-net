using ClassDailyLand.Infrastructure.Schedule;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 课表扩展能力测试：时间偏移换算、放假前最后上学日推算、最后一节课下课时刻。
/// 这些方法直接支撑灵动岛的下拉面板与「放假啦」提醒。
/// 基准：2026-10-07 周三，2026-10-09 周五，2026-10-10 周六。
/// </summary>
public sealed class ScheduleExtendedTests
{
    private static ScheduleService CreateService(TestWorkspace workspace)
    {
        var service = new ScheduleService(workspace.Store);
        service.Load();
        return service;
    }

    // ================= 时间偏移换算 =================

    [Fact]
    public void 时间偏移为零时_原样返回()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.Equal("08:00", service.OffsetTimeStr("08:00"));
    }

    [Fact]
    public void 负向偏移_时间前移()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);
        service.TimeOffsetSeconds = -1800;   // 合法上限 ±30 分钟

        Assert.Equal("07:30", service.OffsetTimeStr("08:00"));
    }

    [Fact]
    public void 正向偏移跨午夜_按一天回绕()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);
        service.TimeOffsetSeconds = 1800;

        Assert.Equal("00:15", service.OffsetTimeStr("23:45"));
    }

    [Fact]
    public void 时间偏移越界_写入时夹取到边界()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 越界值只可能来自手工改配置文件；模型层必须夹取
        // （对应源项目 set_time_offset_seconds 的 max(-1800, min(1800, n))）
        service.TimeOffsetSeconds = 15840;
        Assert.Equal(1800, service.TimeOffsetSeconds);

        service.TimeOffsetSeconds = -99999;
        Assert.Equal(-1800, service.TimeOffsetSeconds);
    }

    [Fact]
    public void 加载越界配置_自动归位并回写()
    {
        using var workspace = new TestWorkspace();

        // 模拟一份被手工改坏的配置：time_offset_seconds = 15840
        workspace.Store.Write("schedule.json", new Dictionary<string, object?>
        {
            ["active"] = "默认课表",
            ["timetables"] = new Dictionary<string, object?>
            {
                ["默认课表"] = new Dictionary<string, object?>
                {
                    ["periods"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["name"] = "第1节", ["start"] = "08:00", ["end"] = "08:45",
                        },
                    },
                    ["timetable"] = new Dictionary<string, object?>(),
                },
            },
            ["time_offset_seconds"] = 15840,
        });

        var service = CreateService(workspace);
        Assert.Equal(1800, service.TimeOffsetSeconds);

        // 重新加载后仍是归位后的值（回写落盘了）
        var reloaded = CreateService(workspace);
        Assert.Equal(1800, reloaded.TimeOffsetSeconds);
    }

    [Fact]
    public void 提前提醒越界_写入时夹取到边界()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AdvanceMinutes = 120;
        Assert.Equal(60, service.AdvanceMinutes);

        service.AdvanceMinutes = -5;
        Assert.Equal(0, service.AdvanceMinutes);
    }

    [Fact]
    public void 无法解析的时间字符串_原样返回()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.Equal("abc", service.OffsetTimeStr("abc"));
        Assert.Equal("", service.OffsetTimeStr(""));
    }

    // ================= 放假前最后上学日 =================

    [Fact]
    public void 下周一放假_本周五判定为放假前最后上学日()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-12", "2026-10-14", "测试假期");

        Assert.True(service.IsLastSchoolDayBeforeHoliday(new DateOnly(2026, 10, 9)));
    }

    [Fact]
    public void 普通工作日_不是放假前最后上学日()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-12", "2026-10-14", "测试假期");

        // 周三之后周四还要上课，因此周三不算「放假前最后上学日」
        Assert.False(service.IsLastSchoolDayBeforeHoliday(new DateOnly(2026, 10, 7)));
    }

    [Fact]
    public void 当天本身在假期内_判定为假()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-12", "2026-10-14", "测试假期");

        Assert.False(service.IsLastSchoolDayBeforeHoliday(new DateOnly(2026, 10, 12)));
    }

    [Fact]
    public void 完全没有假期配置_判定为假()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.False(service.IsLastSchoolDayBeforeHoliday(new DateOnly(2026, 10, 7)));
    }

    [Fact]
    public void 调休日占用了假期_不视为假期起点()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-12", "2026-10-12", "名义假期");
        service.AddMakeup("2026-10-12", 0);   // 该日调休上课

        // 调休日不算假期，因此下一个假期起点不存在 → 判定为假
        Assert.False(service.IsLastSchoolDayBeforeHoliday(new DateOnly(2026, 10, 9)));
    }

    // ================= 最后一节课下课时刻 =================

    [Fact]
    public void 取当天最后一个非空课程的下课时刻()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 周三课表最后一个非空课程是第7节的「自习」（16:00–16:45）
        var end = service.LastCourseEndToday(new DateTime(2026, 10, 7, 9, 0, 0));

        Assert.NotNull(end);
        Assert.Equal(new DateTime(2026, 10, 7, 16, 45, 0), end!.Value);
    }

    [Fact]
    public void 下课时刻会叠加时间偏移()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);
        service.TimeOffsetSeconds = -1800;

        var end = service.LastCourseEndToday(new DateTime(2026, 10, 7, 9, 0, 0));

        Assert.Equal(new DateTime(2026, 10, 7, 16, 15, 0), end!.Value);
    }

    [Fact]
    public void 当天无课时_返回空()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 周六默认没有课
        Assert.Null(service.LastCourseEndToday(new DateTime(2026, 10, 10, 9, 0, 0)));
    }

    // ================= 上学日 / 假期推算 =================

    [Fact]
    public void 下一个上学日_跳过周末与假期()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-08", "2026-10-09");

        // 周三往后：周四、周五都是假期，周六周日不是上学日 → 下周一
        Assert.Equal(new DateOnly(2026, 10, 12), service.NextClassDate(new DateOnly(2026, 10, 7)));
    }

    [Fact]
    public void 下一个上学日_调休日算上学日()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddMakeup("2026-10-10", 0);   // 周六调休上课

        Assert.Equal(new DateOnly(2026, 10, 10), service.NextClassDate(new DateOnly(2026, 10, 9)));
    }

    [Fact]
    public void 下一个假期开始日_跳过调休日()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-10", "2026-10-11", "名义假期");
        service.AddMakeup("2026-10-10", 0);   // 周六调休上课

        // 10-10 被调休占用 → 起点顺延到 10-11
        Assert.Equal(new DateOnly(2026, 10, 11), service.NextHolidayStart(new DateOnly(2026, 10, 9)));
    }
}
