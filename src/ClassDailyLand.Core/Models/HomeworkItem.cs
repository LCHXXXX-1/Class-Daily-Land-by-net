using System.Text.Json.Serialization;

namespace ClassDailyLand.Core.Models;

/// <summary>
/// 作业条目。对应源 modules/homework.py 的 {'subject','content','date'} 结构。
/// </summary>
public sealed class HomeworkItem
{
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "未分类";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    public HomeworkItem Clone() => new() { Subject = Subject, Content = Content, Date = Date };
}

/// <summary>按科目归组后的作业视图（供主窗口渲染）。</summary>
public sealed class HomeworkGroup
{
    public string Subject { get; init; } = "";
    public List<string> Contents { get; init; } = new();
    public List<int> Indices { get; init; } = new();
    public string Date { get; init; } = "";
}
