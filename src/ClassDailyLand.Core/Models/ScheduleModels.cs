using System.Text.Json.Serialization;

namespace ClassDailyLand.Core.Models;

/// <summary>课表的一节课（时间片）。对应 schedule.json 中 periods 数组元素。</summary>
public sealed class Period
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>格式 "HH:MM"。</summary>
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    /// <summary>格式 "HH:MM"。</summary>
    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    public Period Clone() => new() { Name = Name, Start = Start, End = End };
}

/// <summary>一套课表：时间片定义 + 每个星期几的课程名数组。</summary>
public sealed class TimetableDefinition
{
    [JsonPropertyName("periods")]
    public List<Period> Periods { get; set; } = new();

    /// <summary>键为 "0".."6"（0=周一 … 6=周日），值为课程名数组，长度与 Periods 对齐。</summary>
    [JsonPropertyName("timetable")]
    public Dictionary<string, List<string>> Timetable { get; set; } = new();
}

/// <summary>临时调课：仅对指定日期生效的当日课表覆盖。</summary>
public sealed class TempAdjust
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("courses")]
    public List<string> Courses { get; set; } = new();
}

/// <summary>假期区间（闭区间）。</summary>
public sealed class HolidayRange
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = "";

    [JsonPropertyName("end")]
    public string End { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

/// <summary>调休上课日：该日期按某一个星期几的课表上课。</summary>
public sealed class MakeupDay
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    /// <summary>0=周一 … 6=周日。</summary>
    [JsonPropertyName("as_weekday")]
    public int AsWeekday { get; set; }
}

/// <summary>周末作息：上午沿用平日时间片，下午使用独立时间片。</summary>
public sealed class WeekendSchedule
{
    [JsonPropertyName("morning_split")]
    public string MorningSplit { get; set; } = "12:00";

    [JsonPropertyName("afternoon_periods")]
    public List<Period> AfternoonPeriods { get; set; } = new();

    [JsonPropertyName("courses")]
    public List<string> Courses { get; set; } = new();
}

/// <summary>schedule.json 的完整文档模型。</summary>
public sealed class ScheduleDocument
{
    public const string DefaultTimetableName = "默认课表";

    [JsonPropertyName("timetables")]
    public Dictionary<string, TimetableDefinition> Timetables { get; set; } = new();

    [JsonPropertyName("active")]
    public string Active { get; set; } = DefaultTimetableName;

    [JsonPropertyName("temp_adjust")]
    public TempAdjust TempAdjust { get; set; } = new();

    /// <summary>提前提醒分钟数（上课侧整体提前）。</summary>
    [JsonPropertyName("advance_minutes")]
    public int AdvanceMinutes { get; set; } = 2;

    /// <summary>全局时间偏移（秒），用于手动校准课表时间。</summary>
    [JsonPropertyName("time_offset_seconds")]
    public int TimeOffsetSeconds { get; set; }

    [JsonPropertyName("holidays")]
    public List<HolidayRange> Holidays { get; set; } = new();

    [JsonPropertyName("makeup_days")]
    public List<MakeupDay> MakeupDays { get; set; } = new();

    [JsonPropertyName("weekend")]
    public WeekendSchedule Weekend { get; set; } = new();
}
