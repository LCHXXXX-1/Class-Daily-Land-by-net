using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 课表服务。对应源模块：schedule.py 的 ScheduleManager。
/// 覆盖多套课表、临时调课、假期、调休、周末作息、时间偏移与状态计算。
/// </summary>
public interface IScheduleService
{
    /// <summary>底层文档（多课表 + 全部扩展信息）。</summary>
    ScheduleDocument Document { get; }

    /// <summary>课表数据或设置发生变化时触发。</summary>
    event EventHandler? Changed;

    void Load();
    void Save();

    // ---------- 课表切换 ----------
    IReadOnlyList<string> ListTimetables();
    string ActiveName { get; }
    bool SwitchTimetable(string name);
    bool CreateTimetable(string name, string? copyFrom = null);
    bool DeleteTimetable(string name);
    bool RenameTimetable(string oldName, string newName);

    /// <summary>当前激活课表的时间片。</summary>
    IReadOnlyList<Period> Periods { get; }

    /// <summary>当前激活课表的课程表（键 "0".."6"）。</summary>
    IReadOnlyDictionary<string, List<string>> Timetable { get; }

    /// <summary>覆写指定课表的时间片与课程表（课表编辑器用）。</summary>
    bool UpdateTimetable(string name, IEnumerable<Period> periods, IDictionary<string, List<string>> timetable);

    /// <summary>恢复默认课表（会清除全部自定义课表，对应 restore_default）。</summary>
    void RestoreDefault();

    // ---------- 导入 / 导出 ----------

    /// <summary>
    /// 导出为 JSON 文本。
    /// <paramref name="names"/> 为空表示导出当前激活课表；
    /// <paramref name="includeExtras"/> 为 true 时连同假期 / 调休 / 周末设置一起导出（完整备份）。
    /// </summary>
    string ExportJson(IEnumerable<string>? names = null, bool includeExtras = false);

    /// <summary>导出为 CSV 文本（Excel 可打开）。</summary>
    string ExportCsv(IEnumerable<string>? names = null);

    /// <summary>从 JSON 文本导入。<paramref name="mode"/> 为 merge / replace。</summary>
    ImportResult ImportJson(string json, string mode = "merge");

    /// <summary>
    /// 解析但不写入，返回给用户看的导入摘要（对应源项目 preview_import）。
    /// <paramref name="text"/> 可以是 JSON 或 CSV。
    /// </summary>
    ScheduleImportPreview PreviewImport(string text, string fileName = "");

    /// <summary>
    /// 导入课表文本（JSON / CSV 自动识别）。
    /// merge：追加为新课表，同名自动改名「xxx (导入)」；
    /// replace：完整备份 → 整体恢复；单表 → 只替换同名课表。
    /// </summary>
    ImportOutcome ImportText(string text, string mode = "merge", string fileName = "");

    /// <summary>从文件导入（自动处理 BOM / GBK 编码）。</summary>
    ImportOutcome ImportFile(string path, string mode = "merge");

    /// <summary>生成导出文件名（不含目录）。</summary>
    string ExportFileName(ExportFormat format, IEnumerable<string>? names = null, bool includeExtras = false);

    // ---------- 提醒与偏移 ----------
    /// <summary>提前提醒分钟数。</summary>
    int AdvanceMinutes { get; set; }

    /// <summary>全局时间偏移秒数。</summary>
    int TimeOffsetSeconds { get; set; }

    // ---------- 假期 / 调休 / 周末 ----------
    IReadOnlyList<HolidayRange> Holidays { get; }
    void AddHoliday(string start, string end, string name = "");
    bool RemoveHoliday(int index);
    HolidayRange? GetHoliday(DateOnly? date = null);
    bool IsHoliday(DateOnly? date = null);

    IReadOnlyList<MakeupDay> MakeupDays { get; }
    void AddMakeup(string date, int asWeekday);
    bool RemoveMakeup(int index);

    /// <summary>若该日为调休上课日，返回按星期几上课；否则返回 null。</summary>
    int? MakeupWeekday(DateOnly? date = null);

    WeekendSchedule Weekend { get; }
    void SetWeekend(string morningSplit, IEnumerable<Period> afternoonPeriods, IEnumerable<string> courses);

    // ---------- 临时调课 ----------
    TempAdjust TempAdjust { get; }
    void SetTempAdjust(string date, IEnumerable<string> courses);
    void ClearTempAdjust();

    // ---------- 当天有效作息 / 课程 ----------
    IReadOnlyList<Period> PeriodsFor(DateOnly? date = null);
    IReadOnlyList<string> CoursesFor(DateOnly? date = null);
    IReadOnlyList<string> TodayCourses(DateTime? now = null);

    /// <summary>核心：计算当前课表状态（灵动岛唯一数据源）。</summary>
    ScheduleStatus GetStatus(DateTime? now = null);

    /// <summary>
    /// 带参数覆盖的状态计算（不落盘、不改动配置）。
    /// 状态测试对话框用它试算「不同提前提醒 / 时间偏移 / 测试时刻」下的状态。
    /// </summary>
    /// <param name="now">测试时刻，null 表示当前。</param>
    /// <param name="advanceMinutes">提前提醒分钟数，null 表示用配置值。</param>
    /// <param name="offsetSeconds">时间偏移秒数，null 表示用配置值。</param>
    ScheduleStatus GetStatus(DateTime? now, int? advanceMinutes, int? offsetSeconds);

    /// <summary>
    /// 把课表上的 "HH:MM" 按全局时间偏移换算为实际时间，用于下拉面板的课表预览
    /// （对应源项目 schedule.py 的 offset_time_str）。
    /// </summary>
    string OffsetTimeStr(string time);

    // ---------- 假期推算（供「放假啦」提醒使用） ----------

    /// <summary>从指定日期往后找第一个上学日（跳过假期与周末，调休日算上学日）。</summary>
    DateOnly? NextClassDate(DateOnly date, int limit = 20);

    /// <summary>从指定日期往后找第一个假期开始日（调休日不算）。</summary>
    DateOnly? NextHolidayStart(DateOnly date, int limit = 20);

    /// <summary>该日是否为「放假前最后一个上学日」。</summary>
    bool IsLastSchoolDayBeforeHoliday(DateOnly? date = null);

    /// <summary>今天最后一节非空课程的下课时刻（含时间偏移）；无课返回 null。</summary>
    DateTime? LastCourseEndToday(DateTime? now = null);
}

/// <summary>导出格式。</summary>
public enum ExportFormat
{
    Json,
    Csv,
}

/// <summary>导入结果（简版）。</summary>
public sealed record ImportResult(bool Success, string Message, int TimetableCount = 0);

/// <summary>导入文件里的一套课表摘要。</summary>
/// <param name="Name">课表名。</param>
/// <param name="Periods">节次数。</param>
/// <param name="Courses">非空课程数。</param>
/// <param name="Exists">当前数据里是否已有同名课表。</param>
public sealed record ImportTimetableSummary(string Name, int Periods, int Courses, bool Exists);

/// <summary>完整备份里顺带带过来的设置。</summary>
public sealed record ImportExtrasSummary(
    int Holidays,
    int MakeupDays,
    bool Weekend,
    int? AdvanceMinutes,
    int? TimeOffsetSeconds);

/// <summary>导入前的预览信息（对应源项目 preview_import 的返回字典）。</summary>
public sealed class ScheduleImportPreview
{
    /// <summary>json / csv。</summary>
    public string Format { get; init; } = "json";

    /// <summary>full（含附加设置）/ timetables。</summary>
    public string Kind { get; init; } = "timetables";

    public IReadOnlyList<ImportTimetableSummary> Timetables { get; init; } = Array.Empty<ImportTimetableSummary>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public ImportExtrasSummary Extras { get; init; } = new(0, 0, false, null, null);

    /// <summary>是否可以做「整体恢复」。</summary>
    public bool CanReplaceFull => Kind == "full";

    /// <summary>是否有同名课表（决定默认导入方式）。</summary>
    public bool HasSameName => Timetables.Any(t => t.Exists);
}

/// <summary>导入结果（详版，对应源项目 import_text 的返回字典）。</summary>
public sealed record ImportOutcome(
    bool Success,
    string Message,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Renamed,
    IReadOnlyList<string> Removed,
    string Active,
    IReadOnlyList<string> Warnings);
