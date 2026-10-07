using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClassDailyLand.Infrastructure.Storage;

/// <summary>
/// 容错合并器：把磁盘上的原始 JSON 逐字段合并进一个已填好默认值的模型对象。
///
/// 存在的意义（对应源项目 settings_manager.py 的 _valid_type）：
/// 配置文件是人手工可编辑的，某个字段类型写错时，
/// 不应该导致整份配置被丢弃，而应该只让该字段回退默认值。
/// </summary>
internal static class TolerantMerger
{
    private const string ModelNamespacePrefix = "ClassDailyLand.Core.Models";

    /// <summary>把 <paramref name="raw"/> 中类型合法的字段合并进 <paramref name="target"/>。</summary>
    public static void MergeInto(object target, IReadOnlyDictionary<string, JsonElement> raw)
    {
        var properties = target.GetType()
            .GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            if (!property.CanWrite) continue;

            var jsonName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                           ?? property.Name;

            if (!raw.TryGetValue(jsonName, out var element)) continue;

            TryApply(property, target, element);
        }
    }

    private static void TryApply(PropertyInfo property, object target, JsonElement element)
    {
        var type = property.PropertyType;

        try
        {
            // ---------- 标量：宽进严出 ----------
            if (type == typeof(string))
            {
                if (element.ValueKind == JsonValueKind.String)
                    property.SetValue(target, element.GetString());
                return;
            }

            if (type == typeof(bool))
            {
                if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    property.SetValue(target, element.GetBoolean());
                return;
            }

            if (type == typeof(int))
            {
                // 源项目允许 int/float 互通，此处同样宽松处理
                if (element.ValueKind == JsonValueKind.Number)
                    property.SetValue(target, (int)Math.Round(element.GetDouble()));
                return;
            }

            if (type == typeof(double))
            {
                if (element.ValueKind == JsonValueKind.Number)
                    property.SetValue(target, element.GetDouble());
                return;
            }

            // ---------- 嵌套模型对象：递归合并，单字段出错不影响整块 ----------
            if (IsMergeableModel(type) && element.ValueKind == JsonValueKind.Object)
            {
                if (property.GetValue(target) is { } nested && ToDictionary(element) is { } childRaw)
                {
                    MergeInto(nested, childRaw);
                    return;
                }
            }

            // ---------- 集合与其它类型：整体反序列化 ----------
            var value = JsonSerializer.Deserialize(element.GetRawText(), type, JsonStore.Options);
            if (value is not null) property.SetValue(target, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                      or ArgumentException or InvalidOperationException)
        {
            // 该字段类型不匹配 → 保留模型上的默认值，继续处理其它字段
        }
    }

    /// <summary>是否为「本方案的领域模型」（非泛型、非集合），可递归合并。</summary>
    private static bool IsMergeableModel(Type type)
        => type.IsClass
           && !type.IsGenericType
           && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type)
           && type.Namespace?.StartsWith(ModelNamespacePrefix, StringComparison.Ordinal) == true;

    private static Dictionary<string, JsonElement>? ToDictionary(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject()) result[property.Name] = property.Value;
        return result;
    }
}
