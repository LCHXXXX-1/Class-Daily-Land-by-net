using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Duty;

/// <summary>
/// 出勤人数服务。对应源模块：gui.py 的 load_config / save_config。
/// 数据落在 settings/config.json 的 should / actual 两个键。
/// </summary>
public sealed class AttendanceService : IAttendanceService
{
    private readonly IRuntimeConfigService _config;

    public event EventHandler? Changed;

    public AttendanceService(IRuntimeConfigService config)
    {
        _config = config;
        _config.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public string Should => _config.Current.Should;

    public string Actual => _config.Current.Actual;

    public string Summary => $"应到 {Should} 人 · 实到 {Actual} 人";

    public void Set(string should, string actual)
    {
        _config.Update(config =>
        {
            config.Should = string.IsNullOrWhiteSpace(should) ? config.Should : should.Trim();
            config.Actual = string.IsNullOrWhiteSpace(actual) ? config.Actual : actual.Trim();
        });
    }
}
