using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 作业管理。对应源模块：homework.py 的 HomeworkManager。
/// 读取时需兼容「纯字符串数组」的历史格式，并自动升级为结构化条目。
/// </summary>
public interface IHomeworkService
{
    event EventHandler? Changed;

    IReadOnlyList<HomeworkItem> All { get; }

    /// <summary>按科目归组，供主窗口分区渲染。</summary>
    IReadOnlyList<HomeworkGroup> Grouped();

    bool Add(string subject, string content, string? date = null);
    bool Update(int index, string subject, string content, string date);
    bool Remove(int index);

    void Save();
}
