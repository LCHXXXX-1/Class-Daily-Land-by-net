using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Infrastructure.Homework;

/// <summary>
/// 作业管理实现。对应源模块：homework.py 的 HomeworkManager。
///
/// 兼容性要点：历史上作业曾以「纯字符串数组」保存，
/// 读取时若发现该格式，会自动升级为结构化条目并写回磁盘。
/// </summary>
public sealed class HomeworkService : IHomeworkService
{
    private const string FileName = "homework.json";
    private const string Uncategorized = "未分类";

    private readonly IJsonStore _store;
    private readonly List<HomeworkItem> _items = new();

    public event EventHandler? Changed;

    public IReadOnlyList<HomeworkItem> All => _items;

    public HomeworkService(IJsonStore store)
    {
        _store = store;
        Load();
    }

    // ================= 加载 =================

    private void Load()
    {
        // 结构化格式（当前格式）
        var items = _store.Read<List<HomeworkItem>>(FileName);
        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is null) continue;
                item.Date ??= "";
                _items.Add(item);
            }
            return;
        }

        // 历史格式：字符串数组 → 升级为结构化条目并写回
        var legacy = _store.Read<List<string>>(FileName);
        if (legacy is { Count: > 0 })
        {
            foreach (var content in legacy)
                _items.Add(new HomeworkItem { Subject = Uncategorized, Content = content, Date = "" });

            Save();
        }
    }

    // ================= 查询 =================

    /// <inheritdoc />
    public IReadOnlyList<HomeworkGroup> Grouped()
    {
        var order = new List<string>();
        var contents = new Dictionary<string, List<string>>();
        var indices = new Dictionary<string, List<int>>();
        var dates = new Dictionary<string, string>();

        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var subject = string.IsNullOrWhiteSpace(item.Subject) ? Uncategorized : item.Subject;

            if (!contents.ContainsKey(subject))
            {
                contents[subject] = new List<string>();
                indices[subject] = new List<int>();
                dates[subject] = "";
                order.Add(subject);        // 保持首次出现的顺序
            }

            contents[subject].Add(item.Content);
            indices[subject].Add(i);

            if (string.IsNullOrEmpty(dates[subject]) && !string.IsNullOrEmpty(item.Date))
                dates[subject] = item.Date;
        }

        return order.Select(subject => new HomeworkGroup
        {
            Subject = subject,
            Contents = contents[subject],
            Indices = indices[subject],
            Date = dates[subject],
        }).ToList();
    }

    // ================= 增删改 =================

    public bool Add(string subject, string content, string? date = null)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(content)) return false;

        _items.Add(new HomeworkItem
        {
            Subject = subject.Trim(),
            Content = content.Trim(),
            Date = date ?? "",
        });

        Save();
        return true;
    }

    public bool Update(int index, string subject, string content, string date)
    {
        if (index < 0 || index >= _items.Count) return false;

        var item = _items[index];
        item.Subject = subject.Trim();
        item.Content = content.Trim();
        item.Date = date ?? "";

        Save();
        return true;
    }

    public bool Remove(int index)
    {
        if (index < 0 || index >= _items.Count) return false;

        _items.RemoveAt(index);
        Save();
        return true;
    }

    public void Save()
    {
        _store.Write(FileName, _items);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
