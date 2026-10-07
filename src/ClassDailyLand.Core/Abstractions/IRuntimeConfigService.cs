using ClassDailyLand.Core.Models;

namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// 运行时状态服务：独占持有 settings/config.json（值日生轮转 + 出勤人数）。
///
/// 设计意图：把「文件所有权」收敛到单一服务，
/// 避免多个模块各自读改写同一文件导致字段互相覆盖。
/// </summary>
public interface IRuntimeConfigService
{
    RuntimeConfig Current { get; }

    event EventHandler? Changed;

    void Load();
    void Save();

    /// <summary>就地修改并保存。</summary>
    void Update(Action<RuntimeConfig> mutate);
}

/// <summary>
/// 出勤人数。对应源模块：gui.py 的 load_config / save_config 与 menu.py 的 AttendanceDialog。
/// </summary>
public interface IAttendanceService
{
    event EventHandler? Changed;

    /// <summary>应到人数。</summary>
    string Should { get; }

    /// <summary>实到人数。</summary>
    string Actual { get; }

    /// <summary>展示文案，例如「应到 40 人 · 实到 0 人」。</summary>
    string Summary { get; }

    void Set(string should, string actual);
}
