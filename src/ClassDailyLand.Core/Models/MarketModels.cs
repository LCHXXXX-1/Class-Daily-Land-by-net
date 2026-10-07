namespace ClassDailyLand.Core.Models;

/// <summary>市场条目的本地状态取值（对应源项目 _local_status 里写入的 status）。</summary>
public static class MarketEntryStatus
{
    /// <summary>索引记录本身有问题（缺字段 / 格式非法），装不了。</summary>
    public const string Invalid = "invalid";

    public const string NotInstalled = "not_installed";
    public const string Disabled = "disabled";

    /// <summary>已安装但索引里有更新版本。</summary>
    public const string Update = "update";

    /// <summary>已安装但加载失败。</summary>
    public const string Failed = "failed";

    public const string Installed = "installed";
}

/// <summary>
/// 插件市场的一个条目。对应源项目 plugin_market.py 里 entry 字典的 C# 形态，
/// 上半部分是索引数据，下半部分是扫描本地目录后附加的状态。
/// </summary>
public sealed class MarketEntry
{
    // ---------- 索引数据 ----------

    /// <summary>索引 id（远程列表里的 package_name），例如 class.lchx.clock。</summary>
    public string Id { get; set; } = "";

    /// <summary>展示名（远程列表里的 display_name）。</summary>
    public string Name { get; set; } = "";

    public string Version { get; set; } = "0.0.0";
    public string Format { get; set; } = "cblplugin";

    /// <summary>插件包文件名（已去掉索引里的 plugins/ 前缀）。</summary>
    public string File { get; set; } = "";

    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";

    /// <summary>
    /// 索引记录本身的问题描述。非空表示这条记录不合法：
    /// 仍然显示在列表里（避免插件莫名消失），但标注原因并禁用安装。
    /// </summary>
    public string Problem { get; set; } = "";

    // ---------- 本地状态 ----------

    public bool Installed { get; set; }
    public string InstalledDir { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public bool Disabled { get; set; }
    public string LoadError { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string Status { get; set; } = MarketEntryStatus.NotInstalled;

    public MarketEntry Clone() => (MarketEntry)MemberwiseClone();
}

/// <summary>索引同步结果。Method 用于界面上交代「从哪个源同步的」。</summary>
/// <param name="Entries">索引条目（已附加本地状态）。</param>
/// <param name="Method">来源标识，例如 remote-list / github-list / 本地缓存。</param>
public sealed record MarketSyncResult(IReadOnlyList<MarketEntry> Entries, string Method);

/// <summary>安装 / 卸载 / 更新单个插件的结果。</summary>
/// <param name="PluginId">插件 id。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Message">给用户看的消息。</param>
/// <param name="RestartRequired">是否需要重启应用才能生效。</param>
public sealed record MarketTaskResult(string PluginId, bool Success, string Message, bool RestartRequired);

/// <summary>批量更新的单项结果。</summary>
/// <param name="Name">插件展示名。</param>
/// <param name="Success">是否成功。</param>
/// <param name="Message">消息。</param>
public sealed record MarketBatchItem(string Name, bool Success, string Message);

/// <summary>
/// 依赖包目录里的一项。对应源项目 PackageStore._build_catalog 产出的条目字典。
/// </summary>
public sealed class PackageCatalogEntry
{
    public string Name { get; set; } = "";

    /// <summary>远端版本（拉不到清单时为空）。</summary>
    public string Version { get; set; } = "";

    public bool Installed { get; set; }
    public string InstalledVersion { get; set; } = "";

    /// <summary>手动安装（不随插件卸载回收）。</summary>
    public bool Manual { get; set; }

    /// <summary>声明依赖它的插件目录名。</summary>
    public List<string> UsedBy { get; set; } = new();

    /// <summary>见 <see cref="MarketEntryStatus"/>。</summary>
    public string Status { get; set; } = MarketEntryStatus.NotInstalled;

    /// <summary>给界面用的状态标签。</summary>
    public string StatusLabel => Status switch
    {
        MarketEntryStatus.Installed => "已安装",
        MarketEntryStatus.Update => "可更新",
        _ => "未安装",
    };
}
