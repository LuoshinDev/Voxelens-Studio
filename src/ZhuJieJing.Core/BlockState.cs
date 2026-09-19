using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json.Serialization;

namespace ZhuJieJing.Core;

public sealed class BlockState : IEquatable<BlockState>
{
    private static readonly HashSet<string> AirNames = new(StringComparer.Ordinal)
    {
        "minecraft:air",
        "minecraft:cave_air",
        "minecraft:void_air",
    };

    [JsonConstructor]
    public BlockState(string name, IReadOnlyDictionary<string, string>? properties = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("方块名称不能为空。", nameof(name));

        Name = name;
        var sortedProperties = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (var property in properties) sortedProperties.Add(property.Key, property.Value);
        }

        Properties = new ReadOnlyDictionary<string, string>(sortedProperties);
        CanonicalKey = BuildCanonicalKey(Name, sortedProperties);
    }

    public static BlockState Air { get; } = new("minecraft:air");

    public string Name { get; }

    public IReadOnlyDictionary<string, string> Properties { get; }

    [JsonIgnore]
    public string CanonicalKey { get; }

    [JsonIgnore]
    public bool IsAir => AirNames.Contains(Name);

    public bool Equals(BlockState? other) => other is not null && string.Equals(CanonicalKey, other.CanonicalKey, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is BlockState other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(CanonicalKey);

    public override string ToString() => CanonicalKey;

    private static string BuildCanonicalKey(string name, IReadOnlyDictionary<string, string> properties)
    {
        if (properties.Count == 0) return name;

        var builder = new StringBuilder(name).Append('[');
        var first = true;
        foreach (var property in properties)
        {
            if (!first) builder.Append(',');
            builder.Append(property.Key).Append('=').Append(property.Value);
            first = false;
        }

        return builder.Append(']').ToString();
    }
}
