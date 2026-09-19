using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Packed light values for the 4096 blocks in one section.</summary>
public sealed record MinecraftSectionLighting(
    ReadOnlyMemory<byte> SkyLightNibbles,
    ReadOnlyMemory<byte> BlockLightNibbles,
    ReadOnlyMemory<byte> StaticLightSourceNibbles)
{
    public MinecraftSectionLighting(ReadOnlyMemory<byte> SkyLightNibbles, ReadOnlyMemory<byte> BlockLightNibbles)
        : this(SkyLightNibbles, BlockLightNibbles, default) { }

    public void Deconstruct(out ReadOnlyMemory<byte> skyLightNibbles, out ReadOnlyMemory<byte> blockLightNibbles)
    {
        skyLightNibbles = SkyLightNibbles;
        blockLightNibbles = BlockLightNibbles;
    }
}

/// <summary>
/// Version-independent section content. PaletteIndices contains exactly 4096 entries in Minecraft's
/// x-fastest section order and indexes Palette.
/// </summary>
public sealed record NormalizedMinecraftSection(
    SectionCoordinate Coordinate,
    IReadOnlyList<BlockState> Palette,
    ReadOnlyMemory<ushort> PaletteIndices,
    MinecraftSectionLighting? Lighting,
    IReadOnlyList<MinecraftOpaqueFragment> UnknownFragments)
{
    /// <summary>Optional source ID:metadata provenance, packed as ID &lt;&lt; 4 | metadata.</summary>
    public ReadOnlyMemory<ushort> SourceLegacyStates { get; init; }
}

/// <summary>Canonical block entity data plus its original NBT for lossless passthrough.</summary>
public sealed record NormalizedMinecraftBlockEntity(
    string TypeId,
    BlockPosition Position,
    IReadOnlyDictionary<string, object?> KnownProperties,
    MinecraftOpaquePayload OriginalNbt)
{
    /// <summary>Original item/NBT schema, retained when a preview changes the chunk's target version.</summary>
    public int? SourceDataVersion { get; init; }
}

/// <summary>
/// One decoded chunk suitable for rendering, AI queries, overlays, and conversion. Minecraft-specific source
/// details stay in Passthrough rather than leaking into the canonical block-state palette.
/// </summary>
public sealed record NormalizedMinecraftChunk(
    MinecraftChunkAddress Address,
    MinecraftVersionDescriptor SourceVersion,
    IReadOnlyList<NormalizedMinecraftSection> Sections,
    IReadOnlyList<NormalizedMinecraftBlockEntity> BlockEntities,
    MinecraftChunkProvenance Provenance,
    MinecraftChunkPassthrough Passthrough,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>
/// Hashes connecting current canonical content to the mounted source. Raw payload copying is legal only while
/// IsCanonicalContentUnchanged is true and the source payload hash still matches its fingerprint.
/// </summary>
public sealed record MinecraftChunkProvenance(
    string SourcePayloadHash,
    string SourceCanonicalHash,
    string CurrentCanonicalHash)
{
    public bool IsCanonicalContentUnchanged =>
        string.Equals(SourceCanonicalHash, CurrentCanonicalHash, StringComparison.Ordinal);
}

/// <summary>Context required to normalize one raw chunk without guessing its version or dimension.</summary>
public sealed record MinecraftNormalizationRequest(
    RawMinecraftChunk Chunk,
    MinecraftVersionDescriptor WorldVersion,
    IMinecraftBlockRegistry Registry,
    MinecraftUnknownDataPolicy UnknownDataPolicy);

/// <summary>Origin of a legacy numeric-ID resolution.</summary>
public enum MinecraftRegistryResolutionSource
{
    EmbeddedWorldRegistry,
    MatchingModpackRegistry,
    VanillaVersionRegistry,
    VisibleUnknownPlaceholder,
}

/// <summary>Result of resolving a world-specific legacy numeric ID and metadata pair.</summary>
public sealed record MinecraftRegistryResolution(
    BlockState State,
    MinecraftRegistryResolutionSource Source,
    string? RegisteredName,
    MinecraftDiagnostic? Diagnostic = null);

/// <summary>
/// Resolves legacy IDs using world-embedded Forge registries before vanilla defaults. Unknown non-air IDs must
/// resolve to an explicit visible placeholder state and diagnostic, never to air.
/// </summary>
public interface IMinecraftBlockRegistry
{
    MinecraftRegistryResolution ResolveLegacy(ushort numericId, byte metadata);
}

/// <summary>
/// Decodes real NBT layouts into canonical block states. Implementations must reject unsupported layouts with
/// diagnostics instead of returning an empty or synthetic chunk.
/// </summary>
public interface IMinecraftChunkNormalizer
{
    ValueTask<NormalizedMinecraftChunk> NormalizeAsync(
        MinecraftNormalizationRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Lazy canonical chunk source used by the renderer, converter, AI bridge, and exporter.</summary>
public interface INormalizedMinecraftChunkSource
{
    /// <summary>Stable source or overlay revision used to reject stale previews and exports.</summary>
    string Revision { get; }

    /// <summary>Dimensions exposed by this source, including custom namespaced dimensions.</summary>
    IReadOnlyList<MinecraftDimensionId> Dimensions { get; }

    ValueTask<NormalizedMinecraftChunk?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        CancellationToken cancellationToken = default);

    /// <summary>Enumerates chunks in one region so exporters can keep memory bounded to one MCA at a time.</summary>
    IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(
        MinecraftRegionAddress region,
        CancellationToken cancellationToken = default);
}
