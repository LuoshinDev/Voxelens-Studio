using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// One user-selected replacement. <see cref="SourceKey"/> may be a canonical state or a block name;
/// canonical states win, while a bare name applies to every state of that modern block.
/// </summary>
public sealed record Minecraft1122BlockMappingOverride(
    string SourceKey,
    ushort TargetNumericId,
    byte TargetMetadata,
    string? Note = null)
{
    public LegacyBlockEncoding LegacyEncoding => new(TargetNumericId, TargetMetadata);
}

/// <summary>An immutable, validated snapshot of all user mappings.</summary>
public sealed class Minecraft1122BlockMappingOverrideSet
{
    private readonly IReadOnlyDictionary<string, Minecraft1122BlockMappingOverride> bySource;

    public Minecraft1122BlockMappingOverrideSet(IEnumerable<Minecraft1122BlockMappingOverride>? entries = null)
    {
        var normalized = new SortedDictionary<string, Minecraft1122BlockMappingOverride>(StringComparer.Ordinal);
        foreach(Minecraft1122BlockMappingOverride entry in entries ?? [])
        {
            Minecraft1122BlockMappingOverride valid = Validate(entry);
            if(!normalized.TryAdd(valid.SourceKey, valid))
                throw new InvalidDataException($"方块映射表包含重复来源键：{valid.SourceKey}");
        }

        Entries = new ReadOnlyCollection<Minecraft1122BlockMappingOverride>(normalized.Values.ToArray());
        bySource = new ReadOnlyDictionary<string, Minecraft1122BlockMappingOverride>(normalized);
        Revision = BuildRevision(Entries);
    }

    public static Minecraft1122BlockMappingOverrideSet Empty { get; } = new();

    public IReadOnlyList<Minecraft1122BlockMappingOverride> Entries { get; }

    public int Count => Entries.Count;

    public string Revision { get; }

    public bool TryResolve(BlockState source, out Minecraft1122BlockMappingOverride mapping)
    {
        ArgumentNullException.ThrowIfNull(source);
        return bySource.TryGetValue(source.CanonicalKey, out mapping!) ||
               bySource.TryGetValue(source.Name, out mapping!);
    }

    public Minecraft1122BlockMappingOverrideSet With(Minecraft1122BlockMappingOverride mapping)
    {
        Minecraft1122BlockMappingOverride valid = Validate(mapping);
        return new Minecraft1122BlockMappingOverrideSet(
            Entries.Where(entry => !string.Equals(entry.SourceKey, valid.SourceKey, StringComparison.Ordinal))
                .Append(valid));
    }

    public Minecraft1122BlockMappingOverrideSet Without(string sourceKey)
    {
        string normalized = NormalizeSourceKey(sourceKey);
        return new Minecraft1122BlockMappingOverrideSet(
            Entries.Where(entry => !string.Equals(entry.SourceKey, normalized, StringComparison.Ordinal)));
    }

    internal static Minecraft1122BlockMappingOverride Validate(Minecraft1122BlockMappingOverride mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        string sourceKey = NormalizeSourceKey(mapping.SourceKey);
        LegacyBlockEncoding encoding = new(mapping.TargetNumericId, mapping.TargetMetadata);
        MinecraftRegistryResolution target = Minecraft1122VanillaBlockRegistry.Instance.ResolveLegacy(
            encoding.NumericId,
            encoding.Metadata);
        if(target.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder)
            throw new InvalidDataException($"用户映射目标 {encoding.NumericId}:{encoding.Metadata} 不是有效的 Java 1.12.2 方块状态。");
        if(encoding.NumericId == 0 && !IsInvisibleLightKey(sourceKey))
            throw new InvalidDataException("只有 minecraft:light 可以显式映射为空气；其他非空气方块必须保持可见。");

        string? note = string.IsNullOrWhiteSpace(mapping.Note) ? null : mapping.Note.Trim();
        return mapping with { SourceKey = sourceKey, Note = note };
    }

    internal static bool IsInvisibleLightKey(string sourceKey) =>
        string.Equals(sourceKey, "minecraft:light", StringComparison.Ordinal) ||
        sourceKey.StartsWith("minecraft:light[", StringComparison.Ordinal);

    private static string NormalizeSourceKey(string sourceKey)
    {
        if(string.IsNullOrWhiteSpace(sourceKey)) throw new InvalidDataException("用户方块映射的来源键不能为空。");
        string value = sourceKey.Trim();
        int bracket = value.IndexOf('[');
        string name = bracket < 0 ? value : value[..bracket];
        if(!name.Contains(':', StringComparison.Ordinal) ||
           name.Any(static character => char.IsWhiteSpace(character)) ||
           bracket >= 0 && (!value.EndsWith(']') || bracket == 0))
        {
            throw new InvalidDataException($"无效的规范方块来源键：{value}");
        }
        return value;
    }

    private static string BuildRevision(IReadOnlyList<Minecraft1122BlockMappingOverride> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach(Minecraft1122BlockMappingOverride entry in entries)
        {
            Append(hash, entry.SourceKey);
            Append(hash, entry.TargetNumericId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.TargetMetadata.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, entry.Note ?? string.Empty);
        }
        return $"user:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }
}
