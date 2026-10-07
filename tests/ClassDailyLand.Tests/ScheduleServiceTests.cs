using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Schedule;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 课表服务测试。重点是 GetStatus 状态机 —— 它是灵动岛渲染的唯一数据源。
///
/// 固定基准日期：2026-10-07（周三）。默认课表中周三为
/// 第1-8节 08:00 / 08:55 / 10:00 / 10:55 / 14:00 / 14:55 / 16:00 / 16:55 开始，
/// 课程为 英语 / 语文 / 数学 / 生物 / 化学 / 物理 / 自习 / 空。
/// </summary>
public sealed class ScheduleServiceTests
{
    private static readonly DateOnly Wednesday = new(2026, 10, 7);
    private static readonly DateOnly Saturday = new(2026, 10, 10);

    private static ScheduleService CreateService(TestWorkspace workspace)
    {
        var service = new ScheduleService(workspace.Store);
        service.Load();
        return service;
    }

    private static DateTime At(DateOnly date, int hour, int minute)
        => date.ToDateTime(new TimeOnly(hour, minute));

    // ================= 默认数据 =================

    [Fact]
    public void 首次加载_写入默认课表()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.True(File.Exists(workspace.ConfigFile("schedule.json")));
        Assert.Equal(ScheduleDocument.DefaultTimetableName, service.ActiveName);
        Assert.Equal(8, service.Periods.Count);
        Assert.Equal(2, service.AdvanceMinutes);
    }

    [Fact]
    public void 默认课表内容与源项目一致()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.Equal("第1节", service.Periods[0].Name);
        Assert.Equal("08:00", service.Periods[0].Start);
        Assert.Equal("17:40", service.Periods[7].End);

        // 周一（键 "0"）第一节为语文，与源项目默认课表逐字对应
        var monday = service.Timetable["0"];
        Assert.Equal("语文", monday[0]);
        Assert.Equal("自习", monday[7]);
    }

    // ================= 状态机 =================

    [Fact]
    public void 上课前_返回upcoming并给出距上课时间()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 第1节 08:00 开始，提前提醒 2 分钟 → 有效上课时刻 07:58
        var status = service.GetStatus(At(Wednesday, 7, 30));

        Assert.Equal(ScheduleStatusKind.Upcoming, status.Status);
        Assert.Equal("英语", status.Course);
        Assert.Equal("第1节", status.Period);
        Assert.Equal(28 * 60, status.UntilSec);   // 07:30 → 07:58
        Assert.Equal(28, status.UntilMin);
        Assert.Equal(120, status.AdvanceSec);
    }

    [Fact]
    public void 课间_指向下一节而不是上一节()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 第1节 08:45 下课；第2节 08:55 上课、提前 2 分钟 → 08:53
        var status = service.GetStatus(At(Wednesday, 8, 50));

        Assert.Equal(ScheduleStatusKind.Upcoming, status.Status);
        Assert.Equal("语文", status.Course);
        Assert.Equal("第2节", status.Period);
        Assert.Equal(3 * 60, status.UntilSec);
    }

    [Fact]
    public void 上课中_返回ongoing与剩余时间()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var status = service.GetStatus(At(Wednesday, 8, 10));

        Assert.Equal(ScheduleStatusKind.Ongoing, status.Status);
        Assert.Equal("英语", status.Course);
        Assert.Equal(35 * 60, status.RemainSec);   // 08:10 → 08:45
        Assert.Equal(35, status.RemainMin);

        // 时长 = 结束 - 开始（含提前量）→ 07:58 → 08:45
        Assert.Equal(47 * 60, status.DurationSec);
        Assert.Equal(0, status.Index);
    }

    [Fact]
    public void 提前提醒时间可调_影响上课判定()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 默认提前 2 分钟时 07:55 仍是 upcoming
        Assert.Equal(ScheduleStatusKind.Upcoming, service.GetStatus(At(Wednesday, 7, 55)).Status);

        service.AdvanceMinutes = 10;

        // 改为提前 10 分钟后，07:55 已进入上课中
        var status = service.GetStatus(At(Wednesday, 7, 55));
        Assert.Equal(ScheduleStatusKind.Ongoing, status.Status);
        Assert.Equal("英语", status.Course);
    }

    [Fact]
    public void 当天课程全部结束_返回done()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        var status = service.GetStatus(At(Wednesday, 23, 0));

        Assert.Equal(ScheduleStatusKind.Done, status.Status);
    }

    [Fact]
    public void 周末无课_返回none()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 周六课程列表为空
        Assert.Equal(ScheduleStatusKind.None, service.GetStatus(At(Saturday, 9, 0)).Status);
    }

    // ================= 假期 / 调休 / 临时调课 =================

    [Fact]
    public void 假期_返回holiday并带名称()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-08", "2026-10-09", "国庆调休");

        var status = service.GetStatus(At(new DateOnly(2026, 10, 8), 9, 0));

        Assert.Equal(ScheduleStatusKind.Holiday, status.Status);
        Assert.Equal("国庆调休", status.Name);
    }

    [Fact]
    public void 假期边界为闭区间_首尾当天均判定为假期()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-08", "2026-10-09");

        Assert.True(service.IsHoliday(new DateOnly(2026, 10, 8)));
        Assert.True(service.IsHoliday(new DateOnly(2026, 10, 9)));
        Assert.False(service.IsHoliday(new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void 调休上课日_按指定星期的课表上课()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 把周六调成按周一上课 → 课程应变为周一课表（第一节语文）
        service.AddMakeup("2026-10-10", 0);

        var status = service.GetStatus(At(Saturday, 8, 10));

        Assert.Equal(ScheduleStatusKind.Ongoing, status.Status);
        Assert.Equal("语文", status.Course);
    }

    [Fact]
    public void 调休优先于假期()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.AddHoliday("2026-10-10", "2026-10-10", "本应放假");
        service.AddMakeup("2026-10-10", 0);

        // 调休日按上课处理，不应被判为假期
        Assert.NotEqual(ScheduleStatusKind.Holiday, service.GetStatus(At(Saturday, 8, 10)).Status);
    }

    [Fact]
    public void 临时调课_覆盖当天课程()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        service.SetTempAdjust("2026-10-07", new[] { "音乐" });

        var status = service.GetStatus(At(Wednesday, 8, 10));

        Assert.Equal(ScheduleStatusKind.Ongoing, status.Status);
        Assert.Equal("音乐", status.Course);
    }

    // ================= 周末作息 =================

    [Fact]
    public void 周末作息_上午沿用平日时间片下午用独立时间片()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 默认分界点 12:00 → 上午取平日 08:00/08:55/10:00/10:55 共 4 节
        var periods = service.PeriodsFor(Saturday);
        Assert.Equal(4, periods.Count);
        Assert.Equal("08:00", periods[0].Start);

        // 追加下午时间片后应被合并进来
        service.SetWeekend("12:00", new[] { new Period { Name = "午1", Start = "13:30", End = "14:15" } }, Array.Empty<string>());

        var merged = service.PeriodsFor(Saturday);
        Assert.Equal(5, merged.Count);
        Assert.Equal("午1", merged[4].Name);
    }

    // ================= 多套课表 =================

    [Fact]
    public void 新建课表_可复制自现有课表且可切换()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        Assert.True(service.CreateTimetable("冬季课表", ScheduleDocument.DefaultTimetableName));
        Assert.True(service.SwitchTimetable("冬季课表"));

        Assert.Equal("冬季课表", service.ActiveName);
        Assert.Equal(2, service.ListTimetables().Count);
        Assert.Equal("英语", service.Timetable["2"][0]);
    }

    [Fact]
    public void 最后一套课表不允许删除()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 只剩一套时必须拒绝删除，否则激活课表会失效
        Assert.False(service.DeleteTimetable(ScheduleDocument.DefaultTimetableName));
        Assert.Single(service.ListTimetables());
    }

    [Fact]
    public void 长时间偏移_整体平移上课时刻()
    {
        using var workspace = new TestWorkspace();
        var service = CreateService(workspace);

        // 向前偏移 30 分钟：07:40 时第1节已开始（07:58 - 30min = 07:28）
        service.TimeOffsetSeconds = -30 * 60;

        var status = service.GetStatus(At(Wednesday, 7, 40));
        Assert.Equal(ScheduleStatusKind.Ongoing, status.Status);
    }
}
