using System.Text.Json.Serialization;

namespace ClassDailyLand.Core.Models;

/// <summary>
/// 程序清单（根目录 config.json）。
/// 对应源项目根 config.json，记录版本号、更新地址与程序文件名。
/// </summary>
public sealed class AppManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("version_url")]
    public string VersionUrl { get; set; } = "";

    [JsonPropertyName("exe_name")]
    public string ExeName { get; set; } = "";
}

/// <summary>
/// 运行时状态（settings/config.json）。
///
/// 注意：源项目里 <b>值日生轮转</b>与<b>出勤人数</b>共用这一个文件
/// （StudentOnDuty.py 写入 current_index/last_date，gui.py 写入 should/actual）。
/// 因此 C# 版必须由单一服务持有整份文档再整体写回，
/// 否则两个模块各自整文件覆盖会互相抹掉对方的数据 —— 移植时最容易踩的坑。
/// </summary>
public sealed class RuntimeConfig
{
    // ---- 值日生轮转 ----
    [JsonPropertyName("current_index")]
    public int CurrentIndex { get; set; }

    [JsonPropertyName("last_date")]
    public string LastDate { get; set; } = "";

    // ---- 出勤人数 ----
    /// <summary>应到人数（源项目以字符串保存，默认 "40"）。</summary>
    [JsonPropertyName("should")]
    public string Should { get; set; } = "40";

    /// <summary>实到人数（默认 "0"）。</summary>
    [JsonPropertyName("actual")]
    public string Actual { get; set; } = "0";
}
