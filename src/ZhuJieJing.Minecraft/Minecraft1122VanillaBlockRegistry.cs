using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Verified vanilla Java 1.12.2 numeric ID and metadata registry. The source table is embedded into this
/// assembly, so desktop world reads never depend on the retired web prototype at runtime.
/// </summary>
public sealed class Minecraft1122VanillaBlockRegistry : IMinecraftBlockRegistry
{
    public const string VisibleUnknownBlockName = "zhujiejing:visible_unknown_legacy_block";
    public const string VisibleUnknownRenderHint = "magenta_black_checker";

    private const string ResourceName =
        "ZhuJieJing.Minecraft.Resources.legacy-registry-1.12.2.json";
    private const string ExpectedSource =
        "PrismarineJS/minecraft-data@dd89a5f2f0054a6d504eef567f4891ab5033f1d1";
    private const string ExpectedRegistryJsonSha256 =
        "951702D618DCFAF4CE763A5DE8940D9D85D4FA1CB830FE5CCF309A5259133395";
    private const int ExpectedStateCount = 1682;
    private const int ExpectedNumericNameCount = 254;

    private static readonly Lazy<RegistryData> SharedData = new(
        LoadAndValidate,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Shared immutable registry instance.</summary>
    public static Minecraft1122VanillaBlockRegistry Instance { get; } = new();

    /// <summary>Attribution recorded in the embedded, verified source table.</summary>
    public string SourceAttribution => SharedData.Value.Source;

    /// <summary>Number of exact ID:metadata states available in the vanilla table.</summary>
    public int StateCount => SharedData.Value.States.Count;

    public MinecraftRegistryResolution ResolveLegacy(ushort numericId, byte metadata)
    {
        if (metadata > 15)
            throw new ArgumentOutOfRangeException(nameof(metadata), "Legacy metadata must fit one nibble.");

        RegistryData data = SharedData.Value;
        if (numericId == 0)
        {
            return new MinecraftRegistryResolution(
                BlockState.Air,
                MinecraftRegistryResolutionSource.VanillaVersionRegistry,
                "air");
        }

        int key = numericId << 4 | metadata;
        data.NumericNames.TryGetValue(numericId, out string? registeredName);
        if (data.States.TryGetValue(key, out BlockState? state))
        {
            return new MinecraftRegistryResolution(
                state,
                MinecraftRegistryResolutionSource.VanillaVersionRegistry,
                registeredName);
        }

        BlockState placeholder = new(
            VisibleUnknownBlockName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["legacy_id"] = numericId.ToString(CultureInfo.InvariantCulture),
                ["legacy_meta"] = metadata.ToString(CultureInfo.InvariantCulture),
                ["render_hint"] = VisibleUnknownRenderHint,
            });
        MinecraftDiagnostic diagnostic = new(
            "legacy.block.unknown",
            MinecraftDiagnosticSeverity.Warning,
            registeredName is null
                ? $"未知的非空气旧版方块 ID:meta={numericId}:{metadata}；已使用醒目占位方块。"
                : $"旧版方块 {registeredName} 的 metadata={metadata} 未知；已使用醒目占位方块。");
        return new MinecraftRegistryResolution(
            placeholder,
            MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder,
            registeredName,
            diagnostic);
    }

    private static RegistryData LoadAndValidate()
    {
        Assembly assembly = typeof(Minecraft1122VanillaBlockRegistry).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName) ??
                              throw new InvalidOperationException(
                                  $"Embedded Minecraft 1.12.2 registry is missing: {ResourceName}");
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        byte[] bytes = copy.ToArray();
        ReadOnlySpan<byte> sourceBytes = bytes;
        if (!sourceBytes.IsEmpty && sourceBytes[^1] == (byte)'\n')
            sourceBytes = sourceBytes[..^1];
        if (!sourceBytes.IsEmpty && sourceBytes[^1] == (byte)'\r')
            sourceBytes = sourceBytes[..^1];
        string hash = Convert.ToHexString(SHA256.HashData(sourceBytes));
        if (!hash.Equals(ExpectedRegistryJsonSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded Minecraft 1.12.2 registry failed its integrity check: {hash}");
        }

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Embedded Minecraft 1.12.2 registry root must be an object.");

        string source = root.GetProperty("source").GetString() ?? string.Empty;
        if (!source.Equals(ExpectedSource, StringComparison.Ordinal))
            throw new InvalidDataException($"Unexpected Minecraft 1.12.2 registry source: {source}");

        Dictionary<int, BlockState> states = new();
        foreach (JsonProperty property in root.GetProperty("states").EnumerateObject())
        {
            (ushort id, byte metadata) = ParseLegacyKey(property.Name);
            string canonical = property.Value.GetString() ??
                               throw new InvalidDataException($"Registry state {property.Name} is not a string.");
            BlockState state = ParseCanonicalState(canonical);
            if (id == 0 && !state.IsAir)
                throw new InvalidDataException("Legacy numeric ID 0 must map only to air.");
            if (id != 0 && state.IsAir)
                throw new InvalidDataException($"Non-zero legacy state {property.Name} cannot map to air.");
            if (!states.TryAdd(id << 4 | metadata, state))
                throw new InvalidDataException($"Duplicate legacy registry state: {property.Name}");
        }

        Dictionary<ushort, string> numericNames = new();
        foreach (JsonProperty property in root.GetProperty("numericNames").EnumerateObject())
        {
            if (!ushort.TryParse(property.Name, NumberStyles.None, CultureInfo.InvariantCulture, out ushort id) ||
                id > 4095)
            {
                throw new InvalidDataException($"Invalid legacy numeric name ID: {property.Name}");
            }

            string name = property.Value.GetString() ??
                          throw new InvalidDataException($"Legacy numeric name {property.Name} is not a string.");
            if (string.IsNullOrWhiteSpace(name) || !numericNames.TryAdd(id, name))
                throw new InvalidDataException($"Invalid or duplicate legacy numeric name: {property.Name}");
        }

        if (states.Count != ExpectedStateCount || numericNames.Count != ExpectedNumericNameCount)
        {
            throw new InvalidDataException(
                $"Embedded registry entry counts changed: states={states.Count}, names={numericNames.Count}.");
        }

        RequireState(states, 0, 0, "minecraft:air");
        RequireState(states, 1, 0, "minecraft:stone");
        RequireState(states, 2, 0, "minecraft:grass_block[snowy=false]");
        RequireState(states, 17, 4, "minecraft:oak_log[axis=x]");

        return new RegistryData(
            source,
            new ReadOnlyDictionary<int, BlockState>(states),
            new ReadOnlyDictionary<ushort, string>(numericNames));
    }

    private static (ushort Id, byte Metadata) ParseLegacyKey(string value)
    {
        int separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator != value.LastIndexOf(':'))
            throw new InvalidDataException($"Invalid legacy registry key: {value}");
        if (!ushort.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out ushort id) ||
            id > 4095 ||
            !byte.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out byte metadata) ||
            metadata > 15)
        {
            throw new InvalidDataException($"Invalid legacy registry key: {value}");
        }

        return (id, metadata);
    }

    private static BlockState ParseCanonicalState(string canonical)
    {
        int propertiesStart = canonical.IndexOf('[', StringComparison.Ordinal);
        if (propertiesStart < 0)
        {
            ValidateStateName(canonical);
            return new BlockState(canonical);
        }

        if (!canonical.EndsWith(']') || propertiesStart == 0)
            throw new InvalidDataException($"Invalid canonical block state: {canonical}");

        string name = canonical[..propertiesStart];
        ValidateStateName(name);
        string propertyText = canonical[(propertiesStart + 1)..^1];
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        foreach (string pair in propertyText.Split(','))
        {
            if (pair.Length == 0)
                throw new InvalidDataException($"Empty canonical block property in {canonical}");
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == pair.Length - 1 || separator != pair.LastIndexOf('='))
                throw new InvalidDataException($"Invalid canonical block property in {canonical}");
            string key = pair[..separator];
            string value = pair[(separator + 1)..];
            if (!properties.TryAdd(key, value))
                throw new InvalidDataException($"Duplicate canonical block property {key} in {canonical}");
        }

        if (properties.Count == 0)
            throw new InvalidDataException($"Empty canonical property list in {canonical}");
        return new BlockState(name, properties);
    }

    private static void ValidateStateName(string name)
    {
        int separator = name.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == name.Length - 1 || separator != name.LastIndexOf(':'))
            throw new InvalidDataException($"Invalid canonical block name: {name}");
    }

    private static void RequireState(
        IReadOnlyDictionary<int, BlockState> states,
        ushort id,
        byte metadata,
        string canonical)
    {
        int key = id << 4 | metadata;
        if (!states.TryGetValue(key, out BlockState? state) ||
            !state.CanonicalKey.Equals(canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Embedded registry validation state {id}:{metadata} does not match {canonical}.");
        }
    }

    private sealed record RegistryData(
        string Source,
        IReadOnlyDictionary<int, BlockState> States,
        IReadOnlyDictionary<ushort, string> NumericNames);
}
