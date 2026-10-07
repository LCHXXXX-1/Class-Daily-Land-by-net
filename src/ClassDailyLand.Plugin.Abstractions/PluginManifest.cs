using System.Text.Json.Serialization;

namespace ClassDailyLand.Plugin.Abstractions;

/// <summary>
/// 插件元数据，对应源项目 plugins/&lt;name&gt;/plugin.json。
///
/// 与源项目的格式差异（迁移说明）：
/// 源项目 entry 指向 python 文件（main.py），此处指向编译后的程序集（MyPlugin.dll）。
/// </summary>
public sealed class PluginManifest
{
    /// <summary>展示名。</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>入口程序集文件名，例如 MyPlugin.dll。</summary>
    [JsonPropertyName("entry")]
    public string Entry { get; set; } = "";

    /// <summary>
    /// 实现 IPlugin 的完整类型名。留空时由宿主自动扫描该程序集。
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>插件 API 版本，宿主据此做兼容性提示。</summary>
    [JsonPropertyName("api_version")]
    public int ApiVersion { get; set; } = PluginApiVersion.Current;

    /// <summary>声明的第三方依赖包名（小写、不带版本），对应源项目的 requires。</summary>
    [JsonPropertyName("requires")]
    public List<string> Requires { get; set; } = new();

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("author")]
    public string Author { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}

/// <summary>插件 API 版本常量。</summary>
public static class PluginApiVersion
{
    public const int Current = 1;
}
