using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Storage;

namespace ClassDailyLand.Infrastructure.Schedule;

/// <summary>
/// 课表服务实现。对应源模块：schedule.py 的 ScheduleManager。
///
/// 承载的功能：
/// 多套课表切换、临时调课、假期区间、调休上课日、周末作息、
/// 提前提醒、全局时间偏移，以及最核心的 <see cref="GetStatus"/> 状态计算。
/// </summary>
public sealed partial class ScheduleService : IScheduleService
{
    /// <summary>提前提醒的可调范围（对应源项目 set_advance_minutes）。</summary>
    public const int AdvanceMinutesMin = 0;

    /// <inheritdoc cref="AdvanceMinutesMin" />
    public const int AdvanceMinutesMax = 60;

    /// <summary>时间偏移的可调范围（对应源项目 set_time_offset_seconds 的 ±1800 秒）。</summary>
    public const int TimeOffsetSecondsMin = -1800;

    /// <inheritdoc cref="TimeOffsetSecondsMin" />
    public const int TimeOffsetSecondsMax = 1800;

    private const string FileName = "schedule.json";

    private readonly IJsonStore _store;

    public ScheduleDocument Document { get; private set; } = new();

    public event EventHandler? Changed;

    public ScheduleService(IJsonStore store) => _store = store;

    // ================= 加载 / 保存 =================

    public void Load()
    {
        var raw = _store.ReadObject(FileName);

        var document = DefaultScheduleFactory.Create();
        if (raw is not null) TolerantMerger.MergeInto(document, raw);

        Document = document;

        // 首次运行、或配置里存在越界数值被归位时，落一次盘
        if (NormalizeAfterLoad() || raw is null) Save();
    }

    public void Save()
    {
        NormalizeAfterLoad();
        _store.Write(FileName, Document);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 兜底修复：保证至少存在一套课表、active 指向有效课表，
    /// 并把越界的提醒 / 偏移数值归位。返回是否真的改动过。
    /// </summary>
    private bool NormalizeAfterLoad()
    {
        var changed = false;

        if (Document.Timetables.Count == 0)
        {
            var defaults = DefaultScheduleFactory.Create();
            foreach (var (name, definition) in defaults.Timetables)
                Document.Timetables[name] = definition;

            changed = true;
        }

        if (string.IsNullOrWhiteSpace(Document.Active) || !Document.Timetables.ContainsKey(Document.Active))
        {
            Document.Active = Document.Timetables.Keys.First();
            changed = true;
        }

        if (Document.TempAdjust is null) { Document.TempAdjust = new TempAdjust(); changed = true; }
        if (Document.Weekend is null) { Document.Weekend = new WeekendSchedule(); changed = true; }
        if (Document.Holidays is null) { Document.Holidays = new List<HolidayRange>(); changed = true; }
        if (Document.MakeupDays is null) { Document.MakeupDays = new List<MakeupDay>(); changed = true; }

        // 越界数值归位。源项目的每一条写入路径都会夹取，越界值只可能来自
        // 手工改配置文件；不归位的话灵动岛与课表编辑器会差出一大截，且界面上
        // 看不出原因（时间偏移控件只会显示到 1800，实际却在按 15840 走）。
        var advance = Math.Clamp(Document.AdvanceMinutes, AdvanceMinutesMin, AdvanceMinutesMax);
        if (advance != Document.AdvanceMinutes)
        {
            Document.AdvanceMinutes = advance;
            changed = true;
        }

        var offset = Math.Clamp(Document.TimeOffsetSeconds, TimeOffsetSecondsMin, TimeOffsetSecondsMax);
        if (offset != Document.TimeOffsetSeconds)
        {
            Document.TimeOffsetSeconds = offset;
            changed = true;
        }

        return changed;
    }

    // ================= 课表切换 =================

    /// <summary>当前激活的课表定义（active 指向不存在的键时回落到第一套）。</summary>
    private TimetableDefinition ActiveTimetable
    {
        get
        {
            if (Document.Timetables.TryGetValue(Document.Active, out var definition)) return definition;
            return Document.Timetables.Values.First();
        }
    }

    public IReadOnlyList<string> ListTimetables() => Document.Timetables.Keys.ToList();

    public string ActiveName => Document.Active;

    public IReadOnlyList<Period> Periods => ActiveTimetable.Periods;

    public IReadOnlyDictionary<string, List<string>> Timetable => ActiveTimetable.Timetable;

    public bool SwitchTimetable(string name)
    {
        if (!Document.Timetables.ContainsKey(name)) return false;
        Document.Active = name;
        Save();
        return true;
    }

    public bool CreateTimetable(string name, string? copyFrom = null)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name) || Document.Timetables.ContainsKey(name)) return false;

        TimetableDefinition definition;
        if (!string.IsNullOrEmpty(copyFrom) && Document.Timetables.TryGetValue(copyFrom, out var source))
        {
            definition = new TimetableDefinition
            {
                Periods = source.Periods.Select(p => p.Clone()).ToList(),
                Timetable = source.Timetable.ToDictionary(
                    kv => kv.Key,
                    kv => new List<string>(kv.Value)),
            };
        }
        else
        {
            definition = DefaultScheduleFactory.Create().Timetables[ScheduleDocument.DefaultTimetableName];
        }

        Document.Timetables[name] = definition;
        Save();
        return true;
    }

    public bool DeleteTimetable(string name)
    {
        // 至少保留一套课表
        if (Document.Timetables.Count <= 1) return false;
        if (!Document.Timetables.Remove(name)) return false;

        if (Document.Active == name) Document.Active = Document.Timetables.Keys.First();
        Save();
        return true;
    }

    public bool RenameTimetable(string oldName, string newName)
    {
        newName = (newName ?? "").Trim();
        if (string.IsNullOrEmpty(newName)) return false;
        if (!Document.Timetables.TryGetValue(oldName, out var definition)) return false;
        if (oldName == newName) return true;
        if (Document.Timetables.ContainsKey(newName)) return false;

        // 用有序重建保证顺序不被打乱
        var rebuilt = new Dictionary<string, TimetableDefinition>();
        foreach (var (key, value) in Document.Timetables)
            rebuilt[key == oldName ? newName : key] = value;

        Document.Timetables = rebuilt;
        if (Document.Active == oldName) Document.Active = newName;

        Save();
        return true;
    }

    // ================= 提醒与偏移 =================

    /// <summary>
    /// 提前提醒分钟数。写入时夹取到 0–60
    /// （对应源项目 set_advance_minutes 的 max(0, min(60, n))）。
    /// </summary>
    public int AdvanceMinutes
    {
        get => Document.AdvanceMinutes;
        set
        {
            Document.AdvanceMinutes = Math.Clamp(value, AdvanceMinutesMin, AdvanceMinutesMax);
            Save();
        }
    }

    /// <summary>
    /// 时间偏移秒数。写入时夹取到 ±1800
    /// （对应源项目 set_time_offset_seconds 的 max(-1800, min(1800, n))）。
    /// 范围必须由模型兜住：界面控件会夹取、命令行与手工改配置文件不会。
    /// </summary>
    public int TimeOffsetSeconds
    {
        get => Document.TimeOffsetSeconds;
        set
        {
            Document.TimeOffsetSeconds = Math.Clamp(value, TimeOffsetSecondsMin, TimeOffsetSecondsMax);
            Save();
        }
    }

    // ================= 假期 =================

    public IReadOnlyList<HolidayRange> Holidays => Document.Holidays;

    public void AddHoliday(string start, string end, string name = "")
    {
        start = (start ?? "").Trim();
        end = (end ?? "").Trim();
        if (string.IsNullOrEmpty(end)) end = start;

        // 保证 start <= end
        if (string.CompareOrdinal(end, start) < 0) (start, end) = (end, start);

        Document.Holidays.Add(new HolidayRange { Start = start, End = end, Name = (name ?? "").Trim() });
        Save();
    }

    public bool RemoveHoliday(int index)
    {
        if (index < 0 || index >= Document.Holidays.Count) return false;
        Document.Holidays.RemoveAt(index);
        Save();
        return true;
    }

    public HolidayRange? GetHoliday(DateOnly? date = null)
    {
        var day = (date ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");

        // 与源项目一致：按字符串区间比较（'YYYY-MM-DD' 的字典序即时间序）
        foreach (var holiday in Document.Holidays)
        {
            if (string.CompareOrdinal(holiday.Start, day) <= 0
                && string.CompareOrdinal(day, holiday.End) <= 0)
                return holiday;
        }
        return null;
    }

    public bool IsHoliday(DateOnly? date = null) => GetHoliday(date) is not null;

    // ================= 调休 =================

    public IReadOnlyList<MakeupDay> MakeupDays => Document.MakeupDays;

    public void AddMakeup(string date, int asWeekday)
    {
        Document.MakeupDays.Add(new MakeupDay
        {
            Date = (date ?? "").Trim(),
            AsWeekday = Math.Clamp(asWeekday, 0, 6),
        });
        Save();
    }

    public bool RemoveMakeup(int index)
    {
        if (index < 0 || index >= Document.MakeupDays.Count) return false;
        Document.MakeupDays.RemoveAt(index);
        Save();
        return true;
    }

    public int? MakeupWeekday(DateOnly? date = null)
    {
        var day = (date ?? DateOnly.FromDateTime(DateTime.Now)).ToString("yyyy-MM-dd");

        foreach (var makeup in Document.MakeupDays)
        {
            if (string.Equals(makeup.Date, day, StringComparison.Ordinal))
                return Math.Clamp(makeup.AsWeekday, 0, 6);
        }
        return null;
    }

    // ================= 周末作息 =================

    public WeekendSchedule Weekend => Document.Weekend;

    public void SetWeekend(string morningSplit, IEnumerable<Period> afternoonPeriods, IEnumerable<string> courses)
    {
        Document.Weekend = new WeekendSchedule
        {
            MorningSplit = string.IsNullOrWhiteSpace(morningSplit) ? "12:00" : morningSplit,
            AfternoonPeriods = afternoonPeriods?.ToList() ?? new List<Period>(),
            Courses = courses?.ToList() ?? new List<string>(),
        };
        Save();
    }

    /// <summary>上午时间片：沿用平日课表中开始时间早于分界点的部分。</summary>
    private List<Period> MorningPeriods()
    {
        var split = Document.Weekend.MorningSplit;
        return Periods.Where(p => IsHhmmBefore(p.Start, split)).Select(p => p.Clone()).ToList();
    }

    private List<Period> WeekendPeriods()
        => MorningPeriods().Concat(Document.Weekend.AfternoonPeriods.Select(p => p.Clone())).ToList();

    // ================= 临时调课 =================

    public TempAdjust TempAdjust => Document.TempAdjust;

    public void SetTempAdjust(string date, IEnumerable<string> courses)
    {
        Document.TempAdjust = new TempAdjust
        {
            Date = (date ?? "").Trim(),
            Courses = courses?.ToList() ?? new List<string>(),
        };
        Save();
    }

    public void ClearTempAdjust()
    {
        Document.TempAdjust = new TempAdjust();
        Save();
    }

    // ================= 当天有效作息 / 课程 =================

    public IReadOnlyList<Period> PeriodsFor(DateOnly? date = null)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.Now);

        if (MakeupWeekday(day) is not null) return Periods;
        if (IsHoliday(day)) return Array.Empty<Period>();
        if (PyWeekday(day) >= 5) return WeekendPeriods();
        return Periods;
    }

    public IReadOnlyList<string> CoursesFor(DateOnly? date = null)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.Now);

        var makeup = MakeupWeekday(day);
        if (makeup is not null)
            return Timetable.TryGetValue(makeup.Value.ToString(), out var makeupCourses)
                ? makeupCourses
                : Array.Empty<string>();

        if (IsHoliday(day)) return Array.Empty<string>();

        if (PyWeekday(day) >= 5) return Document.Weekend.Courses;

        return Timetable.TryGetValue(PyWeekday(day).ToString(), out var courses)
            ? courses
            : Array.Empty<string>();
    }

    public IReadOnlyList<string> TodayCourses(DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;

        // 临时调课优先覆盖当天课表
        if (string.Equals(Document.TempAdjust.Date, moment.ToString("yyyy-MM-dd"), StringComparison.Ordinal))
            return Document.TempAdjust.Courses;

        return CoursesFor(DateOnly.FromDateTime(moment));
    }

    // ================= 核心：状态计算 =================

    /// <inheritdoc />
    public ScheduleStatus GetStatus(DateTime? nowArg = null) => GetStatus(nowArg, null, null);

    /// <summary>
    /// 带参数覆盖的状态计算。状态测试对话框用它在不改动落盘配置的前提下，
    /// 试算「不同提前提醒 / 时间偏移 / 测试时刻」会得到什么状态。
    /// </summary>
    public ScheduleStatus GetStatus(DateTime? nowArg, int? advanceMinutes, int? offsetSeconds)
    {
        var now = nowArg ?? DateTime.Now;
        var date = DateOnly.FromDateTime(now);

        var tempToday = string.Equals(Document.TempAdjust.Date, now.ToString("yyyy-MM-dd"), StringComparison.Ordinal);

        // 假期判定：临时调课与调休优先于假期
        if (!tempToday && MakeupWeekday(date) is null && IsHoliday(date))
            return ScheduleStatus.Holiday(GetHoliday(date)?.Name ?? "");

        var courses = TodayCourses(now);
        var periods = PeriodsFor(date);

        if (!courses.Any(c => !string.IsNullOrWhiteSpace(c)))
            return ScheduleStatus.NoClass();

        var advanceValue = advanceMinutes ?? Document.AdvanceMinutes;
        var offsetValue = offsetSeconds ?? Document.TimeOffsetSeconds;

        var advanceSec = advanceValue * 60;
        var advance = TimeSpan.FromMinutes(advanceValue);
        var offset = TimeSpan.FromSeconds(offsetValue);

        for (var i = 0; i < periods.Count; i++)
        {
            var course = i < courses.Count ? courses[i] : "";
            if (string.IsNullOrWhiteSpace(course)) continue;

            var period = periods[i];
            if (!TryParseHhmm(period.Start, out var startTime)) continue;
            if (!TryParseHhmm(period.End, out var endTime)) continue;

            var rawStart = now.Date.AddHours(startTime.H).AddMinutes(startTime.M);
            var rawEnd = now.Date.AddHours(endTime.H).AddMinutes(endTime.M);
            if (rawEnd <= rawStart) rawEnd = rawEnd.AddDays(1);      // 跨午夜课程

            // 提前提醒：上课侧整体提前 advance，下课时间不动
            var start = rawStart + offset - advance;
            var end = rawEnd + offset;

            // 上课中（以上课时刻为界）
            if (start <= now && now <= end)
            {
                var remainSec = (int)Math.Max(0, (end - now).TotalSeconds);
                return new ScheduleStatus
                {
                    Status = ScheduleStatusKind.Ongoing,
                    Course = course,
                    Period = period.Name,
                    Start = period.Start,
                    End = period.End,
                    RemainSec = remainSec,
                    RemainMin = Math.Max(1, (remainSec + 59) / 60),
                    DurationSec = (int)Math.Max(1, (end - start).TotalSeconds),
                    AdvanceSec = advanceSec,
                    Index = i,
                };
            }

            // 未到上课时刻 → 这是下一节
            if (now < start)
            {
                var untilSec = (int)Math.Max(0, (start - now).TotalSeconds);
                return new ScheduleStatus
                {
                    Status = ScheduleStatusKind.Upcoming,
                    Course = course,
                    Period = period.Name,
                    Start = period.Start,
                    UntilSec = untilSec,
                    UntilMin = Math.Max(1, (untilSec + 59) / 60),
                    AdvanceSec = advanceSec,
                    Index = i,
                };
            }
        }

        // 今天有课但都已结束
        return ScheduleStatus.Finished();
    }

    /// <summary>
    /// 把课表时间按全局偏移换算为实际时间；无法解析时原样返回。
    /// 对应源项目 schedule.py 的 offset_time_str。
    /// </summary>
    public string OffsetTimeStr(string time)
    {
        if (!TryParseHhmm(time, out var value)) return time;

        var minutes = value.H * 60 + value.M + Document.TimeOffsetSeconds / 60;

        // 按一天为周期回绕，兼容负偏移与跨午夜
        minutes = ((minutes % 1440) + 1440) % 1440;
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }

    // ================= 假期推算 =================

    /// <inheritdoc />
    public DateOnly? NextClassDate(DateOnly date, int limit = 20)
    {
        for (var i = 1; i <= limit; i++)
        {
            var candidate = date.AddDays(i);

            // 调休日优先：即使原本是假期也算上学日
            if (MakeupWeekday(candidate) is not null) return candidate;
            if (IsHoliday(candidate)) continue;
            if (PyWeekday(candidate) < 5) return candidate;
        }

        return null;
    }

    /// <inheritdoc />
    public DateOnly? NextHolidayStart(DateOnly date, int limit = 20)
    {
        for (var i = 1; i <= limit; i++)
        {
            var candidate = date.AddDays(i);
            if (IsHoliday(candidate) && MakeupWeekday(candidate) is null) return candidate;
        }

        return null;
    }

    /// <inheritdoc />
    public bool IsLastSchoolDayBeforeHoliday(DateOnly? date = null)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.Now);

        // 当天本身就是假期，自然不是「放假前的上学日」
        if (MakeupWeekday(day) is null && IsHoliday(day)) return false;

        var nextHoliday = NextHolidayStart(day);
        if (nextHoliday is null) return false;

        var nextClass = NextClassDate(day);
        return nextClass is null || nextHoliday < nextClass;
    }

    /// <inheritdoc />
    public DateTime? LastCourseEndToday(DateTime? now = null)
    {
        var moment = now ?? DateTime.Now;
        var courses = TodayCourses(moment);
        var periods = PeriodsFor(DateOnly.FromDateTime(moment));

        DateTime? last = null;

        for (var i = 0; i < courses.Count && i < periods.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(courses[i])) continue;
            if (!TryParseHhmm(periods[i].End, out var end)) continue;

            last = moment.Date.AddHours(end.H).AddMinutes(end.M);
        }

        return last?.AddSeconds(Document.TimeOffsetSeconds);
    }

    // ================= 工具 =================

    /// <summary>
    /// 星期索引转换为「Python 惯例」：0=周一 … 6=周日。
    /// .NET 的 DayOfWeek 是 0=周日，必须转换，否则周末判断会整体错位。
    /// </summary>
    private static int PyWeekday(DateOnly date) => ((int)date.DayOfWeek + 6) % 7;

    private static bool IsHhmmBefore(string time, string split)
    {
        if (!TryParseHhmm(time, out var t) || !TryParseHhmm(split, out var s)) return false;

        // C# 元组不支持关系运算符，需逐字段比较（先比小时再比分钟）
        return t.H != s.H ? t.H < s.H : t.M < s.M;
    }

    private static bool TryParseHhmm(string? text, out (int H, int M) value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(':');
        if (parts.Length < 2) return false;
        if (!int.TryParse(parts[0], out var hour)) return false;
        if (!int.TryParse(parts[1], out var minute)) return false;

        value = (hour, minute);
        return true;
    }
}
