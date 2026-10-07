namespace ClassDailyLand.Core.Models;

/// <summary>课表状态的取值集合。</summary>
public static class ScheduleStatusKind
{
    public const string Upcoming = "upcoming";   // 未到上课时刻（含提前提醒窗口）
    public const string Ongoing  = "ongoing";    // 上课中
    public const string Done     = "done";       // 今天的课都上完了
    public const string None     = "none";       // 今天没有课
    public const string Holiday  = "holiday";    // 假期
}

/// <summary>
/// 课表实时状态。对应源 schedule.py 中 get_status() 返回的状态字典，
/// 是灵动岛渲染（倒计时、提醒、假期文案）的唯一数据源。
/// </summary>
public sealed class ScheduleStatus
{
    /// <summary>见 <see cref="ScheduleStatusKind"/>。</summary>
    public string Status { get; init; } = ScheduleStatusKind.None;

    public string? Course { get; init; }
    public string? Period { get; init; }

    /// <summary>课表上的原始开始时间 "HH:MM"。</summary>
    public string? Start { get; init; }

    /// <summary>课表上的原始结束时间 "HH:MM"。</summary>
    public string? End { get; init; }

    /// <summary>距上课剩余秒数（upcoming）。</summary>
    public int? UntilSec { get; init; }

    /// <summary>距上课剩余分钟数，向上取整且最小为 1（upcoming）。</summary>
    public int? UntilMin { get; init; }

    /// <summary>距下课剩余秒数（ongoing）。</summary>
    public int? RemainSec { get; init; }

    /// <summary>距下课剩余分钟数，向上取整且最小为 1（ongoing）。</summary>
    public int? RemainMin { get; init; }

    /// <summary>本节课总时长（秒，含提前提醒的提前量）。</summary>
    public int? DurationSec { get; init; }

    /// <summary>提前提醒秒数。</summary>
    public int? AdvanceSec { get; init; }

    /// <summary>当前节次索引。</summary>
    public int? Index { get; init; }

    /// <summary>假期名称（仅 holiday 状态）。</summary>
    public string? Name { get; init; }

    public static ScheduleStatus Holiday(string name) => new()
    {
        Status = ScheduleStatusKind.Holiday,
        Name = name
    };

    public static ScheduleStatus NoClass() => new() { Status = ScheduleStatusKind.None };

    public static ScheduleStatus Finished() => new() { Status = ScheduleStatusKind.Done };
}
