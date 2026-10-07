using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Duty;

/// <summary>
/// 值日生管理实现。对应源模块：StudentOnDuty.py。
///
/// 轮转规则：程序启动时检查日期，若与上次记录不同则索引自动前进一位，
/// 保证「每天换一个人」而不是「每次启动换一个人」。
///
/// 注意：轮转索引存放在 settings/config.json，与出勤人数是同一个文件，
/// 因此这里通过 IRuntimeConfigService 访问，绝不直接读写该文件，
/// 否则会覆盖掉出勤数据。
/// </summary>
public sealed class DutyService : IDutyService
{
    private const string NameFileName = "name.json";

    private static readonly string[] DefaultDutyList = { "001", "002", "003", "004", "005" };

    private readonly IJsonStore _store;
    private readonly IRuntimeConfigService _config;
    private readonly List<string> _dutyList = new();

    public event EventHandler? Changed;

    public IReadOnlyList<string> DutyList => _dutyList;

    /// <summary>当前索引（始终落在合法范围内）。</summary>
    public int CurrentIndex
    {
        get
        {
            if (_dutyList.Count == 0) return 0;
            var raw = _config.Current.CurrentIndex;
            return ((raw % _dutyList.Count) + _dutyList.Count) % _dutyList.Count;
        }
    }

    public string CurrentDuty
        => _dutyList.Count == 0 ? "今天没人值日哦" : _dutyList[CurrentIndex];

    public DutyService(IJsonStore store, IRuntimeConfigService config)
    {
        _store = store;
        _config = config;
        Load();
    }

    // ================= 加载 =================

    private void Load()
    {
        // 名单：空或缺失时写入默认名单（对应 load_duty_list 的行为）
        var names = _store.Read<List<string>>(NameFileName);
        if (names is { Count: > 0 })
            _dutyList.AddRange(names.Where(n => !string.IsNullOrWhiteSpace(n)));

        if (_dutyList.Count == 0)
        {
            _dutyList.AddRange(DefaultDutyList);
            SaveDutyList();
        }
    }

    // ================= 轮转 =================

    /// <inheritdoc />
    public void CheckAndRotate()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (string.Equals(_config.Current.LastDate, today, StringComparison.Ordinal)) return;

        _config.Update(config =>
        {
            if (_dutyList.Count > 0)
                config.CurrentIndex = (config.CurrentIndex + 1) % _dutyList.Count;
            config.LastDate = today;
        });

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public string NextDuty()
    {
        if (_dutyList.Count == 0) return CurrentDuty;

        _config.Update(config =>
        {
            config.CurrentIndex = (CurrentIndex + 1) % _dutyList.Count;
            config.LastDate = DateTime.Now.ToString("yyyy-MM-dd");
        });

        Changed?.Invoke(this, EventArgs.Empty);
        return CurrentDuty;
    }

    public string PreviousDuty()
    {
        if (_dutyList.Count == 0) return CurrentDuty;

        _config.Update(config =>
        {
            config.CurrentIndex = (CurrentIndex - 1 + _dutyList.Count) % _dutyList.Count;
            config.LastDate = DateTime.Now.ToString("yyyy-MM-dd");
        });

        Changed?.Invoke(this, EventArgs.Empty);
        return CurrentDuty;
    }

    // ================= 名单维护 =================

    public void AddDuty(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        _dutyList.Add(name.Trim());
        SaveDutyList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AddDuties(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name)) _dutyList.Add(name.Trim());
        }

        SaveDutyList();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool RemoveDuty(int index)
    {
        if (index < 0 || index >= _dutyList.Count) return false;

        _dutyList.RemoveAt(index);

        // 删到索引越界时重置为第一条（对应源项目 remove_duty 的行为）
        if (CurrentIndex >= _dutyList.Count)
            _config.Update(config => config.CurrentIndex = 0);

        SaveDutyList();
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void SaveDutyList() => _store.Write(NameFileName, _dutyList);
}
