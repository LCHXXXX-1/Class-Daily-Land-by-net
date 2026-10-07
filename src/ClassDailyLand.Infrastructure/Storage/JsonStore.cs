using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassDailyLand.Core.Abstractions;

namespace ClassDailyLand.Infrastructure.Storage;

/// <summary>
/// JSON 持久化实现。对应源模块：storage.py 的 read_json / write_json / update_json。
///
/// 核心约束：写入必须是原子的 —— 先写同目录临时文件，再整体替换目标文件。
/// 这样即使写入过程中断电或崩溃，原配置也不会变成半截 JSON。
/// </summary>
public sealed class JsonStore : IJsonStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>全局统一的序列化选项。</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 不转义中文，保持配置文件可读（对应源项目 json.dump(ensure_ascii=False)）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPathService _paths;

    public JsonStore(IPathService paths) => _paths = paths;

    /// <inheritdoc />
    public T? Read<T>(string fileName, T? fallback = null) where T : class
        => ReadAt<T>(_paths.ConfigFile(fileName)) ?? fallback;

    /// <inheritdoc />
    public void Write<T>(string fileName, T data) where T : class
        => WriteAtomic(_paths.ConfigFile(fileName), JsonSerializer.Serialize(data, Options));

    /// <inheritdoc />
    public void WriteAt<T>(string absolutePath, T data) where T : class
    {
        ArgumentNullException.ThrowIfNull(data);
        WriteAtomic(absolutePath, JsonSerializer.Serialize(data, Options));
    }

    /// <inheritdoc />
    public T? ReadAt<T>(string absolutePath) where T : class
    {
        try
        {
            if (!File.Exists(absolutePath)) return null;
            var json = File.ReadAllText(absolutePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // 文件损坏或不可读时静默回退，保证启动不被坏配置卡死
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement>? ReadObject(string fileName)
        => ReadObjectAt(_paths.ConfigFile(fileName));

    /// <inheritdoc />
    public IReadOnlyDictionary<string, JsonElement>? ReadObjectAt(string absolutePath)
    {
        try
        {
            if (!File.Exists(absolutePath)) return null;
            var json = File.ReadAllText(absolutePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Clone：JsonDocument 释放后原始 JsonElement 会失效
                result[prop.Name] = prop.Value.Clone();
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<JsonElement>? ReadArray(string fileName)
    {
        try
        {
            var path = _paths.ConfigFile(fileName);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var list = new List<JsonElement>();
            foreach (var item in doc.RootElement.EnumerateArray()) list.Add(item.Clone());
            return list;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>原子写入指定绝对路径。</summary>
    public void WriteAtomic(string absolutePath, string json)
    {
        var directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var tempPath = absolutePath + ".tmp";
        File.WriteAllText(tempPath, json, Utf8NoBom);
        // File.Move(overwrite: true) 底层是 MoveFileEx + REPLACE_EXISTING，同卷内为原子替换
        File.Move(tempPath, absolutePath, overwrite: true);
    }
}
