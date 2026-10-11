using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Storage;

namespace ClassDailyLand.Infrastructure.Settings;

/// <summary>
/// 应用设置服务实现。对应源模块：settings_manager.py 的 SettingsManager。
///
/// 容错策略：先构造「默认值对象」，再把磁盘内容逐字段合并进去。
/// 类型不符的字段保留默认值，其余字段照常生效 —— 与源项目 _valid_type 行为一致。
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private const string FileName = "settings.json";

    private readonly IJsonStore _store;

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<SettingsChangedEventArgs>? Changed;

    public SettingsService(IJsonStore store) => _store = store;

    public void Load()
    {
        var raw = _store.ReadObject(FileName);

        var settings = new AppSettings();
        if (raw is not null) TolerantMerger.MergeInto(settings, raw);

        Current = settings;

        // 首次运行：配置文件不存在 → 落盘一份默认配置，方便用户手工编辑
        if (raw is null) Save();
    }

    public void Save() => _store.Write(FileName, Current);

    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        mutate(Current);
        Save();
        Changed?.Invoke(this, new SettingsChangedEventArgs());
    }
}
