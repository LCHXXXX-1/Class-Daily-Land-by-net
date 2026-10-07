using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Abstractions;

/// <summary>设置变更事件参数。</summary>
public sealed class SettingsChangedEventArgs : EventArgs
{
    /// <summary>发生变更的设置项名称集合；为空表示整体重载。</summary>
    public IReadOnlyCollection<string>? Keys { get; init; }
}

/// <summary>
/// 应用设置服务。对应源模块：settings_manager.py。
/// 容错行为：逐字段做类型校验，类型不符的字段回退默认值，而不是整体丢弃配置。
/// </summary>
public interface ISettingsService
{
    /// <summary>当前生效的设置（永不为 null）。</summary>
    AppSettings Current { get; }

    /// <summary>设置发生变更时触发，供各窗口订阅后即时应用。</summary>
    event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>从磁盘加载；文件缺失时写入一份默认配置。</summary>
    void Load();

    /// <summary>持久化当前设置。</summary>
    void Save();

    /// <summary>就地修改并保存（便捷方法，修改后触发 Changed）。</summary>
    void Update(Action<AppSettings> mutate);
}
