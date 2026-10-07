using System.Globalization;
using System.Text.RegularExpressions;

namespace ClassDailyLand.Infrastructure.Market;

/// <summary>
/// 语义化版本。对应源模块：plugin_market.py 的 parse_version / version_gt。
///
/// 比较规则（与源实现逐条对齐）：
/// <list type="number">
/// <item>先比 major / minor / patch；</item>
/// <item>版本号相同则**正式版大于预发布版**（1.0.0 &gt; 1.0.0-beta）；</item>
/// <item>预发布段逐段比较：纯数字段按数值比（且整体排在字母段之前），
///       字母段按序数比；前缀相同时段数多者更大（1.0.0-a.b &gt; 1.0.0-a）。</item>
/// </list>
/// 构建元数据（+xxx）不参与比较。
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private static readonly Regex Pattern = new(
        @"^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>预发布段：数字段（Kind=0）排在字母段（Kind=1）之前。</summary>
    private readonly record struct PrePart(int Kind, int Number, string Text) : IComparable<PrePart>
    {
        public int CompareTo(PrePart other)
            => Kind != other.Kind ? Kind.CompareTo(other.Kind)
            : Kind == 0 ? Number.CompareTo(other.Number)
            : string.CompareOrdinal(Text, other.Text);
    }

    public int Major { get; private init; }
    public int Minor { get; private init; }
    public int Patch { get; private init; }

    /// <summary>是否为正式版（无预发布后缀）。</summary>
    public bool IsRelease { get; private init; }

    private PrePart[] Prerelease { get; init; } = Array.Empty<PrePart>();

    /// <summary>
    /// 解析版本号；非法返回 null（调用方据此判定「不是 SemVer」）。
    /// </summary>
    public static SemVersion? Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        var match = Pattern.Match(raw);
        if (!match.Success) return null;

        var preText = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;

        PrePart[] parts;
        if (preText is null)
        {
            parts = Array.Empty<PrePart>();
        }
        else
        {
            var segments = preText.Split('.');
            parts = new PrePart[segments.Length];

            for (var i = 0; i < segments.Length; i++)
            {
                var segment = segments[i];

                parts[i] = int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    ? new PrePart(0, number, "")
                    : new PrePart(1, 0, segment);
            }
        }

        return new SemVersion
        {
            Major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture),
            Minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture),
            Patch = int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture),
            IsRelease = preText is null,
            Prerelease = parts,
        };
    }

    /// <summary>
    /// <paramref name="candidate"/> 是否比 <paramref name="current"/> 新。
    /// current 非法或缺省视为最旧；candidate 非法则一律返回 false。
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        var a = Parse(candidate);
        if (a is null) return false;

        var b = Parse(current);
        if (b is null) return true;

        return a.CompareTo(b) > 0;
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null) return 1;

        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // 正式版 > 同号预发布版
        if (IsRelease != other.IsRelease) return IsRelease ? 1 : -1;

        var count = Math.Min(Prerelease.Length, other.Prerelease.Length);
        for (var i = 0; i < count; i++)
        {
            var diff = Prerelease[i].CompareTo(other.Prerelease[i]);
            if (diff != 0) return diff;
        }

        // 前缀相同：段数多者更大
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    public bool Equals(SemVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, IsRelease, Prerelease.Length);

    public override string ToString()
    {
        var core = $"{Major}.{Minor}.{Patch}";
        if (IsRelease) return core;

        var pre = string.Join('.', Prerelease.Select(p => p.Kind == 0
            ? p.Number.ToString(CultureInfo.InvariantCulture)
            : p.Text));

        return $"{core}-{pre}";
    }

    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
}
