using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Schedule;

/// <summary>
/// 课表的写入、恢复默认与导入导出。
/// 对应源模块：schedule.py 的 update_timetable / restore_default /
/// export_payload / export_text / preview_import / import_text 等。
///
/// 采用 JSON 作为交换格式（与配置文件同构），保证导出的备份可以原样导入。
/// CSV 导出面向 Excel 查看，周几为列、节次为行。
/// </summary>
public sealed partial class ScheduleService
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ================= 课表写入 =================

    /// <inheritdoc />
    public bool UpdateTimetable(
        string name,
        IEnumerable<Period> periods,
        IDictionary<string, List<string>> timetable)
    {
        if (!Document.Timetables.TryGetValue(name, out var definition)) return false;

        definition.Periods = periods?.Select(p => p.Clone()).ToList() ?? new List<Period>();
        definition.Timetable = timetable is null
            ? new Dictionary<string, List<string>>()
            : timetable.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));

        Save();
        return true;
    }

    /// <inheritdoc />
    public void RestoreDefault()
    {
        var defaults = DefaultScheduleFactory.Create();
        Document = defaults;
        Save();
    }

    // ================= 导出 =================

    /// <inheritdoc />
    public string ExportJson(IEnumerable<string>? names = null, bool includeExtras = false)
    {
        var payload = BuildExportPayload(names, includeExtras);
        return JsonSerializer.Serialize(payload, ExportOptions);
    }

    /// <inheritdoc />
    public string ExportCsv(IEnumerable<string>? names = null)
    {
        var selected = ResolveNames(names);
        if (selected.Count == 0) return "";

        // CSV 一次只表达一套课表，多选时导出第一套
        var name = selected[0];
        if (!Document.Timetables.TryGetValue(name, out var definition)) return "";

        var builder = new StringBuilder();

        // 表头与源项目 CSV_HEADERS 一致：节次 / 开始 / 结束 + 周一…周日
        // （必须一致，否则导出的 CSV 自己再导入时解析不出来）
        builder.Append(string.Join(',', new[] { "节次", "开始", "结束" }.Concat(DayHeaders)));
        builder.Append("\r\n");

        for (var i = 0; i < definition.Periods.Count; i++)
        {
            var period = definition.Periods[i];

            var cells = new List<string>
            {
                Escape(string.IsNullOrWhiteSpace(period.Name) ? $"第{i + 1}节" : period.Name),
                Escape(period.Start),
                Escape(period.End),
            };

            for (var day = 0; day < 7; day++)
            {
                var course = "";
                if (definition.Timetable.TryGetValue(day.ToString(), out var dayCourses) && i < dayCourses.Count)
                    course = dayCourses[i] ?? "";

                cells.Add(Escape(course));
            }

            builder.Append(string.Join(',', cells));
            builder.Append("\r\n");
        }

        return builder.ToString();
    }

    /// <inheritdoc />
    public string ExportFileName(ExportFormat format, IEnumerable<string>? names = null, bool includeExtras = false)
    {
        var selected = ResolveNames(names);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        if (includeExtras) return $"课表完整备份_{stamp}.json";

        var label = selected.Count switch
        {
            0 => "课表",
            1 => selected[0],
            _ => $"{selected[0]}等{selected.Count}套",
        };

        // 文件名不能包含路径分隔符等非法字符
        foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            label = label.Replace(invalid, '_');

        var extension = format == ExportFormat.Csv ? "csv" : "json";
        return $"{label}_{stamp}.{extension}";
    }

    private List<string> ResolveNames(IEnumerable<string>? names)
    {
        var list = names?.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (list is { Count: > 0 }) return list;

        return Document.Timetables.ContainsKey(Document.Active)
            ? new List<string> { Document.Active }
            : new List<string>();
    }

    private Dictionary<string, object> BuildExportPayload(IEnumerable<string>? names, bool includeExtras)
    {
        var selected = ResolveNames(names);
        var payload = new Dictionary<string, object>
        {
            ["version"] = 1,
            ["exported_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            ["active"] = Document.Active,
        };

        var timetables = new Dictionary<string, object>();
        foreach (var name in selected)
        {
            if (!Document.Timetables.TryGetValue(name, out var definition)) continue;

            timetables[name] = new Dictionary<string, object>
            {
                ["periods"] = definition.Periods,
                ["timetable"] = definition.Timetable,
            };
        }

        payload["timetables"] = timetables;

        if (includeExtras)
        {
            payload["temp_adjust"] = Document.TempAdjust;
            payload["advance_minutes"] = Document.AdvanceMinutes;
            payload["time_offset_seconds"] = Document.TimeOffsetSeconds;
            payload["holidays"] = Document.Holidays;
            payload["makeup_days"] = Document.MakeupDays;
            payload["weekend"] = Document.Weekend;
        }

        return payload;
    }

    private static string Escape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return '"' + value.Replace("\"", "\"\"") + '"';

        return value;
    }

    private static readonly string[] DayHeaders =
        { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    // ================= 导入 =================

    /// <summary>
    /// 解析但不写入，返回给用户看的摘要（对应源项目 schedule.py 的 preview_import）。
    /// </summary>
    public ScheduleImportPreview PreviewImport(string text, string fileName = "")
    {
        var document = ParseImportDocument(text, fileName);

        var timetables = document.Timetables
            .Select(pair => new ImportTimetableSummary(
                pair.Key,
                pair.Value.Periods.Count,
                CountCourses(pair.Value.Timetable),
                Document.Timetables.ContainsKey(pair.Key)))
            .ToList();

        var extras = new ImportExtrasSummary(
            document.Extras.Holidays,
            document.Extras.MakeupDays,
            document.Extras.Weekend,
            document.Extras.AdvanceMinutes,
            document.Extras.TimeOffsetSeconds);

        return new ScheduleImportPreview
        {
            Format = document.Format,
            Kind = document.Kind,
            Timetables = timetables,
            Warnings = document.Warnings,
            Extras = extras,
        };
    }

    /// <summary>
    /// 导入课表文本（对应源项目 import_text）。
    ///
    /// <c>mode = merge</c>：全部追加为新课表，同名自动改名为「xxx (导入)」；
    /// <c>mode = replace</c>：完整备份 → 整体恢复（课表 + 假期 / 周末等设置）；
    /// 单表文件 → 只替换同名课表（没有同名则新增）。
    /// </summary>
    public ImportOutcome ImportText(string text, string mode = "merge", string fileName = "")
    {
        var document = ParseImportDocument(text, fileName);
        var replace = string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase);

        var added = new List<string>();
        var replaced = new List<string>();
        var renamed = new List<string>();
        var removed = new List<string>();

        // ---- 完整备份 + replace：整体恢复 ----
        if (replace && document.Kind == "full" && document.Extras.HasAny)
        {
            var oldNames = new HashSet<string>(Document.Timetables.Keys, StringComparer.Ordinal);

            Document.Timetables = new Dictionary<string, TimetableDefinition>(document.Timetables);
            ApplyExtras(document);

            Document.Active = document.Active.Length > 0 && Document.Timetables.ContainsKey(document.Active)
                ? document.Active
                : Document.Timetables.Keys.First();

            replaced.AddRange(Document.Timetables.Keys);
            added.AddRange(replaced.Where(n => !oldNames.Contains(n)));
            removed.AddRange(oldNames.Where(n => !Document.Timetables.ContainsKey(n)));

            Save();

            return new ImportOutcome(true, "", added, replaced, renamed, removed, Document.Active, document.Warnings);
        }

        // ---- 逐套处理 ----
        foreach (var (name, definition) in document.Timetables)
        {
            if (replace && Document.Timetables.ContainsKey(name))
            {
                Document.Timetables[name] = definition;
                replaced.Add(name);
                continue;
            }

            var target = name;

            if (Document.Timetables.ContainsKey(target))
            {
                target = UniqueName(Document.Timetables.Keys, name);
                renamed.Add($"{name} → {target}");
            }

            Document.Timetables[target] = definition;
            added.Add(target);
        }

        if (added.Count == 0 && replaced.Count == 0)
            throw new InvalidDataException("没有可以导入的课表");

        // 导入单表时若当前没有课表，顺带设为当前
        if (!Document.Timetables.ContainsKey(Document.Active) && Document.Timetables.Count > 0)
            Document.Active = Document.Timetables.Keys.First();

        ApplyExtras(document);
        Save();

        return new ImportOutcome(true, "", added, replaced, renamed, removed, Document.Active, document.Warnings);
    }

    /// <summary>从文件导入（自动处理 BOM / GBK 编码）。</summary>
    public ImportOutcome ImportFile(string path, string mode = "merge")
        => ImportText(ReadTextFile(path), mode, path);

    /// <summary>读文本文件；兼容 UTF-8 BOM 与 Excel 导出的 GBK。</summary>
    public static string ReadTextFile(string path)
    {
        var raw = File.ReadAllBytes(path);

        foreach (var encoding in new[] { new UTF8Encoding(true), new UTF8Encoding(false), Encoding.GetEncoding("GBK") })
        {
            try
            {
                return encoding.GetString(raw);
            }
            catch (DecoderFallbackException)
            {
                // 换下一种编码
            }
        }

        return Encoding.UTF8.GetString(raw);
    }

    /// <summary>给课表取一个不冲突的名字：X → X (导入) → X (导入2)（对应 unique_name）。</summary>
    private static string UniqueName(IEnumerable<string> existing, string name)
    {
        var taken = new HashSet<string>(existing, StringComparer.Ordinal);

        if (!taken.Contains(name)) return name;

        var first = $"{name} (导入)";
        if (!taken.Contains(first)) return first;

        var index = 2;
        while (taken.Contains($"{name} (导入{index})")) index++;

        return $"{name} (导入{index})";
    }

    private static int CountCourses(IReadOnlyDictionary<string, List<string>> timetable)
        => timetable.Values.Sum(list => list.Count(c => !string.IsNullOrWhiteSpace(c)));

    private void ApplyExtras(ImportDocument document)
    {
        if (document.Extras.HolidayList is { } holidays) Document.Holidays = holidays;
        if (document.Extras.MakeupList is { } makeups) Document.MakeupDays = makeups;
        if (document.Extras.WeekendSchedule is { } weekend) Document.Weekend = weekend;
        if (document.Extras.AdvanceMinutes is { } advance) Document.AdvanceMinutes = advance;
        if (document.Extras.TimeOffsetSeconds is { } offset) Document.TimeOffsetSeconds = offset;
    }

    // ---------- 解析成统一结构 ----------

    private sealed class ImportDocument
    {
        public string Format { get; init; } = "json";
        public string Kind { get; init; } = "timetables";

        /// <summary>课表集合（保持文件里的顺序）。</summary>
        public List<KeyValuePair<string, TimetableDefinition>> Timetables { get; init; } = new();

        public string Active { get; init; } = "";
        public List<string> Warnings { get; init; } = new();
        public ImportExtras Extras { get; init; } = new();
    }

    private sealed class ImportExtras
    {
        public List<HolidayRange>? HolidayList { get; set; }
        public List<MakeupDay>? MakeupList { get; set; }
        public WeekendSchedule? WeekendSchedule { get; set; }
        public int? AdvanceMinutes { get; set; }
        public int? TimeOffsetSeconds { get; set; }

        public int Holidays => HolidayList?.Count ?? 0;
        public int MakeupDays => MakeupList?.Count ?? 0;
        public bool Weekend => WeekendSchedule is not null;

        /// <summary>是否有任何附加设置（决定 replace 能不能走「整体恢复」）。</summary>
        public bool HasAny => Holidays > 0 || MakeupDays > 0 || Weekend
                              || AdvanceMinutes is not null || TimeOffsetSeconds is not null;
    }

    /// <summary>按内容猜格式：以 { 或 [ 开头算 JSON，否则算 CSV。</summary>
    private static string DetectFormat(string text)
    {
        var trimmed = text.TrimStart('\ufeff').TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? "json" : "csv";
    }

    /// <summary>把导入文本解析成统一结构（对应源项目 parse_document）。</summary>
    private ImportDocument ParseImportDocument(string text, string fileName)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("文件是空的");

        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");

        if (DetectFormat(text) == "csv")
        {
            var (periods, timetable, csvWarnings) = ParseCsv(text);
            var csvName = stem.Length > 0 ? stem : "从文件导入的课表";

            return new ImportDocument
            {
                Format = "csv",
                Kind = "timetables",
                Timetables = new List<KeyValuePair<string, TimetableDefinition>>
                {
                    new(csvName, new TimetableDefinition { Periods = periods, Timetable = timetable }),
                },
                Warnings = csvWarnings,
            };
        }

        using var parsed = JsonDocument.Parse(text.TrimStart('\ufeff'), new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });

        var warnings = new List<string>();
        var timetables = new List<KeyValuePair<string, TimetableDefinition>>();

        if (parsed.RootElement.ValueKind == JsonValueKind.Array)
        {
            // 宽容：允许直接给 [课表对象, ...]
            var index = 0;

            foreach (var item in parsed.RootElement.EnumerateArray())
            {
                index++;
                var single = ParseSingleElement(item);

                if (single is null)
                {
                    warnings.Add($"第 {index} 项跳过了：内容无法识别");
                    continue;
                }

                var elementName = item.ValueKind == JsonValueKind.Object
                                  && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? (n.GetString() ?? "").Trim()
                    : "";

                if (elementName.Length == 0)
                    elementName = $"{(stem.Length > 0 ? stem : "从文件导入的课表")}{(index > 1 ? index.ToString() : "")}";

                timetables.Add(new KeyValuePair<string, TimetableDefinition>(elementName, single));
            }
        }
        else if (parsed.RootElement.ValueKind == JsonValueKind.Object
                 && parsed.RootElement.TryGetProperty("timetables", out var map)
                 && map.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in map.EnumerateObject())
            {
                var single = ParseSingleElement(property.Value);

                if (single is null)
                {
                    warnings.Add($"课表「{property.Name}」跳过了：内容无法识别");
                    continue;
                }

                timetables.Add(new KeyValuePair<string, TimetableDefinition>(property.Name, single));
            }
        }
        else
        {
            var single = ParseSingleElement(parsed.RootElement);
            if (single is not null)
            {
                var name = stem.Length > 0 ? stem : "从文件导入的课表";
                timetables.Add(new KeyValuePair<string, TimetableDefinition>(name, single));
            }
        }

        if (timetables.Count == 0) throw new InvalidDataException("没找到可导入的课表数据");

        var root = parsed.RootElement.ValueKind == JsonValueKind.Object
            ? parsed.RootElement
            : default;

        var extras = root.ValueKind == JsonValueKind.Object ? ReadExtras(root) : new ImportExtras();

        var active = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.String
            ? a.GetString() ?? ""
            : "";

        return new ImportDocument
        {
            Format = "json",
            Kind = extras.HasAny ? "full" : "timetables",
            Timetables = timetables,
            Active = active,
            Warnings = warnings,
            Extras = extras,
        };
    }

    private static ImportExtras ReadExtras(JsonElement root)
    {
        var extras = new ImportExtras();

        if (root.TryGetProperty("holidays", out var holidays) && holidays.ValueKind == JsonValueKind.Array)
            extras.HolidayList = Deserialize<List<HolidayRange>>(holidays);

        if (root.TryGetProperty("makeup_days", out var makeups) && makeups.ValueKind == JsonValueKind.Array)
            extras.MakeupList = Deserialize<List<MakeupDay>>(makeups);

        if (root.TryGetProperty("weekend", out var weekend) && weekend.ValueKind == JsonValueKind.Object)
            extras.WeekendSchedule = Deserialize<WeekendSchedule>(weekend);

        if (root.TryGetProperty("advance_minutes", out var advance) && advance.ValueKind == JsonValueKind.Number)
            extras.AdvanceMinutes = advance.GetInt32();

        if (root.TryGetProperty("time_offset_seconds", out var offset) && offset.ValueKind == JsonValueKind.Number)
            extras.TimeOffsetSeconds = offset.GetInt32();

        return extras;
    }

    /// <summary>
    /// CSV 文本 → (时间片, 课程表, 警告)。
    /// 兼容带或不带表头、时间列缺失（回退到默认作息）。
    /// </summary>
    private static (List<Period> Periods, Dictionary<string, List<string>> Timetable, List<string> Warnings) ParseCsv(string text)
    {
        var warnings = new List<string>();

        var rows = SplitCsv(text)
            .Where(row => row.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            .ToList();

        if (rows.Count == 0) throw new InvalidDataException("CSV 里啥都没有");

        // 表头识别
        var header = rows[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
        var hasHeader = header.Count > 0
                        && (header[0] is "节次" or "节" or "序号" or "period" or "no" or "no." or "#"
                            || (header.Count > 1 && header[1] is "开始" or "开始时间" or "start"));

        if (hasHeader) rows.RemoveAt(0);
        if (rows.Count == 0) throw new InvalidDataException("CSV 里一节课都没有");

        var defaults = DefaultScheduleFactory.Create().Timetables.Values.First().Periods;

        var periods = new List<Period>();
        var timetable = Enumerable.Range(0, 7).ToDictionary(d => d.ToString(), _ => new List<string>());

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Select(c => c.Trim()).ToList();
            while (cells.Count < 10) cells.Add("");

            var name = cells[0].Length > 0 ? cells[0] : $"第{i + 1}节";
            var start = cells[1];
            var end = cells[2];

            if (!IsValidTime(start) || !IsValidTime(end))
            {
                if (i < defaults.Count)
                {
                    start = IsValidTime(start) ? start : defaults[i].Start;
                    end = IsValidTime(end) ? end : defaults[i].End;
                    warnings.Add($"第 {i + 1} 节时间缺了或格式不对，先用默认作息 {start}-{end}");
                }
                else
                {
                    start = "08:00";
                    end = "08:45";
                    warnings.Add($"第 {i + 1} 节没写时间，先填上 08:00-08:45");
                }
            }

            periods.Add(new Period
            {
                Name = name.Length > 20 ? name[..20] : name,
                Start = start,
                End = end,
            });

            for (var day = 0; day < 7; day++)
            {
                var course = cells[3 + day];
                timetable[day.ToString()].Add(course.Length > 30 ? course[..30] : course);
            }
        }

        // 去掉每天末尾的空课程
        foreach (var key in timetable.Keys.ToList())
        {
            var list = timetable[key];
            while (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        }

        return (periods, timetable, warnings);
    }

    /// <summary>按 RFC 4180 拆分 CSV（支持引号包裹与转义）。</summary>
    private static List<List<string>> SplitCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inQuotes = true;
                    break;

                case ',':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;

                case '\r':
                    break;

                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = new List<string>();
                    break;

                default:
                    cell.Append(ch);
                    break;
            }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    private static bool IsValidTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(':');
        if (parts.Length != 2) return false;

        return int.TryParse(parts[0], out var hour) && hour is >= 0 and <= 23
               && int.TryParse(parts[1], out var minute) && minute is >= 0 and <= 59;
    }

    // ================= 兼容旧入口 =================

    /// <summary>
    /// 兼容旧入口：等价于 <see cref="ImportText"/>，把结果折叠成简短消息。
    /// </summary>
    public ImportResult ImportJson(string json, string mode = "merge")
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ImportResult(false, "内容为空。");

        try
        {
            var outcome = ImportText(json, mode);
            var count = outcome.Added.Count + outcome.Replaced.Count;
            var action = string.Equals(mode, "replace", StringComparison.OrdinalIgnoreCase) ? "替换为" : "新增";

            return new ImportResult(true, $"导入成功：{action} {count} 套课表。", count);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return new ImportResult(false, ex.Message);
        }
    }

    /// <summary>把一个课表对象元素解析成定义（用于数组形式 / 单表形式的导入）。</summary>
    private static TimetableDefinition? ParseSingleElement(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var dictionary = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) dictionary[property.Name] = property.Value.Clone();

        return ParseSingle(dictionary);
    }

    private static Dictionary<string, TimetableDefinition> ParseTimetables(JsonElement element)
    {
        var result = new Dictionary<string, TimetableDefinition>();

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;

            var dictionary = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in property.Value.EnumerateObject()) dictionary[child.Name] = child.Value.Clone();

            var definition = ParseSingle(dictionary);
            if (definition is not null) result[property.Name] = definition;
        }

        return result;
    }

    private static TimetableDefinition? ParseSingle(IDictionary<string, JsonElement> source)
    {
        var periods = source.TryGetValue("periods", out var periodsElement)
            ? Deserialize<List<Period>>(periodsElement)
            : null;

        var timetable = source.TryGetValue("timetable", out var timetableElement)
            ? Deserialize<Dictionary<string, List<string>>>(timetableElement)
            : null;

        if (periods is null || periods.Count == 0) return null;

        return new TimetableDefinition
        {
            Periods = periods,
            Timetable = timetable ?? new Dictionary<string, List<string>>(),
        };
    }

    private static T? Deserialize<T>(JsonElement element) where T : class
    {
        try
        {
            return element.Deserialize<T>(ExportOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
