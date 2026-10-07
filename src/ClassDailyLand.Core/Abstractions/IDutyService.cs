namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 值日生管理。对应源模块：StudentOnDuty.py。
/// 含跨天自动轮转：日期变化时索引自动前进一位。
/// </summary>
public interface IDutyService
{
    event EventHandler? Changed;

    IReadOnlyList<string> DutyList { get; }

    /// <summary>当前值日生；名单为空时返回提示文案。</summary>
    string CurrentDuty { get; }

    int CurrentIndex { get; }

    /// <summary>检查是否跨天并自动轮转（启动时调用）。</summary>
    void CheckAndRotate();

    string NextDuty();
    string PreviousDuty();

    void AddDuty(string name);
    void AddDuties(IEnumerable<string> names);
    bool RemoveDuty(int index);

    void SaveDutyList();
}
