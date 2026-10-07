using ClassDailyLand.Infrastructure.Duty;
using ClassDailyLand.Infrastructure.State;
using Xunit;

namespace ClassDailyLand.Tests;

/// <summary>
/// 值日生与出勤测试。
///
/// 这里有一条最关键的回归测试：源项目中值日生轮转与出勤人数
/// <b>共用 settings/config.json</b>。若两者各自整文件写回，
/// 就会把对方的字段抹掉 —— C# 版通过「单一服务持有整份文档」根治，
/// 下面的 SharedConfigTests 专门守住这个行为。
/// </summary>
public sealed class DutyAttendanceTests
{
    private sealed record Fixture(
        TestWorkspace Workspace,
        RuntimeConfigService Config,
        DutyService Duty,
        AttendanceService Attendance);

    private static Fixture Create(TestWorkspace workspace)
    {
        var config = new RuntimeConfigService(workspace.Store);
        config.Load();

        var duty = new DutyService(workspace.Store, config);
        var attendance = new AttendanceService(config);

        return new Fixture(workspace, config, duty, attendance);
    }

    // ================= 共享配置文件（关键回归） =================

    [Fact]
    public void 修改出勤后轮转值日生_出勤数据不会被覆盖()
    {
        using var workspace = new TestWorkspace();
        var fixture = Create(workspace);

        fixture.Attendance.Set("45", "42");
        Assert.Equal("45", fixture.Attendance.Should);
        Assert.Equal("42", fixture.Attendance.Actual);

        // 轮转值日生会写 config.json —— 不能把 should / actual 冲掉
        fixture.Duty.NextDuty();

        var reloaded = new RuntimeConfigService(workspace.Store);
        reloaded.Load();

        Assert.Equal("45", reloaded.Current.Should);
        Assert.Equal("42", reloaded.Current.Actual);
    }

    [Fact]
    public void 轮转值日生后修改出勤_轮转索引不会被覆盖()
    {
        using var workspace = new TestWorkspace();
        var fixture = Create(workspace);

        var after = fixture.Duty.NextDuty();
        var indexAfterRotate = fixture.Duty.CurrentIndex;

        fixture.Attendance.Set("50", "48");

        var reloaded = new RuntimeConfigService(workspace.Store);
        reloaded.Load();

        Assert.Equal(indexAfterRotate, reloaded.Current.CurrentIndex);
        Assert.Equal(after, fixture.Duty.CurrentDuty);
    }

    [Fact]
    public void 出勤文案格式与源项目一致()
    {
        using var workspace = new TestWorkspace();
        var fixture = Create(workspace);

        fixture.Attendance.Set("40", "38");
        Assert.Equal("应到 40 人 · 实到 38 人", fixture.Attendance.Summary);
    }

    [Fact]
    public void 出勤默认值_应到40实到0()
    {
        using var workspace = new TestWorkspace();
        var fixture = Create(workspace);

        Assert.Equal("40", fixture.Attendance.Should);
        Assert.Equal("0", fixture.Attendance.Actual);
    }

    // ================= 值日生名单 =================

    [Fact]
    public void 名单缺失时写入默认名单()
    {
        using var workspace = new TestWorkspace();
        var fixture = Create(workspace);

        Assert.True(File.Exists(workspace.ConfigFile("name.json")));
        Assert.Equal(5, fixture.Duty.DutyList.Count);
        Assert.Equal("001", fixture.Duty.DutyList[0]);
    }

    [Fact]
    public void 名单空数组时同样回退默认名单()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", "[]");

        var fixture = Create(workspace);
        Assert.Equal(5, fixture.Duty.DutyList.Count);
    }

    [Fact]
    public void 读取已有名单()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", """["张三","李四","王五"]""");

        var fixture = Create(workspace);

        Assert.Equal(3, fixture.Duty.DutyList.Count);
        Assert.Equal("张三", fixture.Duty.CurrentDuty);
    }

    // ================= 轮转 =================

    [Fact]
    public void 跨天时自动前进一位_且同一天内不重复轮转()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("config.json", """{"current_index":0,"last_date":"2000-01-01"}""");

        var fixture = Create(workspace);
        Assert.Equal(0, fixture.Duty.CurrentIndex);

        fixture.Duty.CheckAndRotate();
        Assert.Equal(1, fixture.Duty.CurrentIndex);

        // 同一天内再次检查不应继续轮转
        fixture.Duty.CheckAndRotate();
        Assert.Equal(1, fixture.Duty.CurrentIndex);
    }

    [Fact]
    public void 上次轮转日期为今天时不动索引()
    {
        using var workspace = new TestWorkspace();
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        workspace.WriteConfigRaw("config.json", $$"""{"current_index":2,"last_date":"{{today}}"}""");

        var fixture = Create(workspace);
        fixture.Duty.CheckAndRotate();

        Assert.Equal(2, fixture.Duty.CurrentIndex);
    }

    [Fact]
    public void 轮转到末尾后回到第一位()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", """["A","B","C"]""");
        workspace.WriteConfigRaw("config.json", """{"current_index":2,"last_date":"2000-01-01"}""");

        var fixture = Create(workspace);
        fixture.Duty.CheckAndRotate();

        Assert.Equal(0, fixture.Duty.CurrentIndex);
        Assert.Equal("A", fixture.Duty.CurrentDuty);
    }

    [Fact]
    public void 索引越界时自动归位而不是抛异常()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", """["A","B","C"]""");
        workspace.WriteConfigRaw("config.json", """{"current_index":99,"last_date":"2000-01-01"}""");

        var fixture = Create(workspace);

        // 99 % 3 = 0
        Assert.Equal(0, fixture.Duty.CurrentIndex);
        Assert.Equal("A", fixture.Duty.CurrentDuty);
    }

    [Fact]
    public void 上一位与下一位可双向切换()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", """["A","B","C"]""");

        var fixture = Create(workspace);

        Assert.Equal("B", fixture.Duty.NextDuty());
        Assert.Equal("A", fixture.Duty.PreviousDuty());
        // 再往前应环绕到末尾
        Assert.Equal("C", fixture.Duty.PreviousDuty());
    }

    [Fact]
    public void 删除值日生后索引越界会自动重置()
    {
        using var workspace = new TestWorkspace();
        workspace.WriteConfigRaw("name.json", """["A","B","C"]""");

        var fixture = Create(workspace);
        fixture.Duty.NextDuty();
        fixture.Duty.NextDuty();          // index = 2

        Assert.True(fixture.Duty.RemoveDuty(2));

        // 名单只剩 2 人，索引 2 越界 → 重置为 0
        Assert.Equal(0, fixture.Duty.CurrentIndex);
    }
}
