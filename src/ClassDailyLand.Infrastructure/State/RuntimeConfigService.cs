using ClassDailyLand.Core.Abstractions;
using ClassDailyLand.Core.Models;
using ClassDailyLand.Infrastructure.Storage;

namespace ClassDailyLand.Infrastructure.State;

/// <summary>
/// 运行时状态服务实现：settings/config.json 的唯一所有者。
/// 对应源模块：StudentOnDuty.py 与 gui.py 共用的 config.json 读写逻辑。
/// </summary>
public sealed class RuntimeConfigService : IRuntimeConfigService
{
    private const string FileName = "config.json";

    private readonly IJsonStore _store;

    public RuntimeConfig Current { get; private set; } = new();

    public event EventHandler? Changed;

    public RuntimeConfigService(IJsonStore store) => _store = store;

    public void Load()
    {
        var raw = _store.ReadObject(FileName);

        var config = new RuntimeConfig();
        if (raw is not null) TolerantMerger.MergeInto(config, raw);

        Current = config;

        // 首次运行：文件不存在则落盘默认值（对应源项目 load_config 的 else 分支）
        if (raw is null) Save();
    }

    public void Save()
    {
        _store.Write(FileName, Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Update(Action<RuntimeConfig> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        mutate(Current);
        Save();
    }
}
