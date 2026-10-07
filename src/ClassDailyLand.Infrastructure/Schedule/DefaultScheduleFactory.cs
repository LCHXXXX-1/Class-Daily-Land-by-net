using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Schedule;

/// <summary>
/// 默认课表数据工厂。
/// 数据逐字对应源项目 settings/schedule.json 的初始内容（schedule.py 的 _default_data）。
/// </summary>
internal static class DefaultScheduleFactory
{
    public static ScheduleDocument Create()
    {
        var document = new ScheduleDocument
        {
            Active = ScheduleDocument.DefaultTimetableName,
        };

        document.Timetables[ScheduleDocument.DefaultTimetableName] = new TimetableDefinition
        {
            Periods = BuildPeriods(),
            Timetable = BuildTimetable(),
        };

        return document;
    }

    private static List<Period> BuildPeriods() => new()
    {
        new Period { Name = "第1节", Start = "08:00", End = "08:45" },
        new Period { Name = "第2节", Start = "08:55", End = "09:40" },
        new Period { Name = "第3节", Start = "10:00", End = "10:45" },
        new Period { Name = "第4节", Start = "10:55", End = "11:40" },
        new Period { Name = "第5节", Start = "14:00", End = "14:45" },
        new Period { Name = "第6节", Start = "14:55", End = "15:40" },
        new Period { Name = "第7节", Start = "16:00", End = "16:45" },
        new Period { Name = "第8节", Start = "16:55", End = "17:40" },
    };

    /// <summary>键为 "0".."6"（0=周一 … 6=周日），空课用空字符串占位。</summary>
    private static Dictionary<string, List<string>> BuildTimetable() => new()
    {
        ["0"] = new List<string> { "语文", "数学", "英语", "物理", "化学", "生物", "体育", "自习" },
        ["1"] = new List<string> { "数学", "英语", "语文", "化学", "物理", "体育", "自习", "" },
        ["2"] = new List<string> { "英语", "语文", "数学", "生物", "化学", "物理", "自习", "" },
        ["3"] = new List<string> { "物理", "化学", "数学", "语文", "英语", "生物", "体育", "" },
        ["4"] = new List<string> { "化学", "物理", "英语", "数学", "语文", "自习", "", "" },
        ["5"] = new List<string>(),
        ["6"] = new List<string>(),
    };
}
