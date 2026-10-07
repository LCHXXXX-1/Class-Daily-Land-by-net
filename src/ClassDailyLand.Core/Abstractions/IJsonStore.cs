using System.Text.Json;

namespace ClassDailyLand.Core.Abstractions;

/// <summary>
/// JSON 持久化。对应源模块：storage.py。
/// 契约要求：写入必须为原子操作（临时文件 + 替换），降低数据损坏风险。
/// </summary>
public interface IJsonStore
{
    /// <summary>
    /// 读取对象并反序列化。文件不存在或内容损坏时返回 <paramref name="fallback"/>。
    /// </summary>
    T? Read<T>(string fileName, T? fallback = null) where T : class;

    /// <summary>
    /// 原子写入。先写临时文件，再以替换方式落盘，避免写入中断导致配置损坏。
    /// </summary>
    void Write<T>(string fileName, T data) where T : class;

    /// <summary>
    /// 以「键 → 原始 JSON 值」的形式读取对象，
    /// 供上层做逐字段类型校验与回退（对应源项目 _valid_type 的容错行为）。
    /// 文件不存在或非对象时返回 null。
    /// </summary>
    IReadOnlyDictionary<string, JsonElement>? ReadObject(string fileName);

    /// <summary>读取顶层数组，非数组时返回 null。</summary>
    IReadOnlyList<JsonElement>? ReadArray(string fileName);

    /// <summary>按绝对路径读取对象（用于读取根目录 config.json 等非配置目录文件）。</summary>
    IReadOnlyDictionary<string, JsonElement>? ReadObjectAt(string absolutePath);

    /// <summary>按绝对路径读取并反序列化。</summary>
    T? ReadAt<T>(string absolutePath) where T : class;

    /// <summary>按绝对路径原子写入（插件数据目录等宿主目录之外的位置）。</summary>
    void WriteAt<T>(string absolutePath, T data) where T : class;
}
