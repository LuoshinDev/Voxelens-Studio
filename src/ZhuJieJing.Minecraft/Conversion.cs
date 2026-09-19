using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Target format and height constraints for a Minecraft export or derived preview.</summary>
public sealed record MinecraftTargetProfile(
    string Id,
    MinecraftVersionDescriptor Version,
    BlockYRange BuildRange,
    bool RequiresLegacyNumericEncoding)
{
    public static MinecraftTargetProfile Java1122 { get; } = new(
        "java-1.12.2",
        new MinecraftVersionDescriptor(1343, "1.12.2", MinecraftStorageFamily.LegacyNumericAnvil),
        new BlockYRange(0, 255),
        true);
}

/// <summary>Legacy Java block encoding written into Blocks, Data, and optional Add arrays.</summary>
public readonly record struct LegacyBlockEncoding
{
    public LegacyBlockEncoding(ushort numericId, byte metadata)
    {
        if (numericId > 4095)
            throw new ArgumentOutOfRangeException(nameof(numericId), "Legacy Anvil block IDs are limited to 12 bits.");
        if (metadata > 15)
            throw new ArgumentOutOfRangeException(nameof(metadata), "Legacy block metadata is limited to 4 bits.");

        NumericId = numericId;
        Metadata = metadata;
    }

    public ushort NumericId { get; }

    public byte Metadata { get; }
}

/// <summary>Visual-fidelity grade assigned to one downgrade mapping.</summary>
public enum MinecraftBlockMappingQuality
{
    Exact,
    Similar,
    VisibleFallback,
    Blocked,
}

/// <summary>Deterministic conversion rule result for one canonical block state.</summary>
public sealed record MinecraftBlockMapping(
    BlockState Source,
    BlockState? Target,
    LegacyBlockEncoding? LegacyEncoding,
    MinecraftBlockMappingQuality Quality,
    string RuleId,
    string Explanation,
    bool AllowsInvisibleTarget = false);

/// <summary>
/// Resolves target blocks. A non-air source may return Blocked, but it must never masquerade as transparent
/// air; a fallback must be an explicit visible target state.
/// </summary>
public interface IMinecraftBlockDowngradeRules
{
    MinecraftBlockMapping Resolve(BlockState source, MinecraftTargetProfile target);
}

/// <summary>
/// Optional stable identity for a rule snapshot. Preview caches include this value so a saved user override
/// cannot accidentally reuse chunks produced by an older mapping table.
/// </summary>
public interface IRevisionedMinecraftBlockDowngradeRules : IMinecraftBlockDowngradeRules
{
    string Revision { get; }
}

/// <summary>Controls fidelity and clipping behavior for a downgrade preview.</summary>
public sealed record MinecraftDowngradePolicy
{
    public MinecraftDowngradePolicy(
        BlockState visibleFallback,
        bool allowVerticalClipping = false,
        bool preserveLighting = true,
        MinecraftUnknownDataPolicy? unknownDataPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(visibleFallback);
        if (visibleFallback.IsAir)
            throw new ArgumentException("The downgrade fallback must be visible and cannot be air.", nameof(visibleFallback));

        VisibleFallback = visibleFallback;
        AllowVerticalClipping = allowVerticalClipping;
        PreserveLighting = preserveLighting;
        UnknownDataPolicy = unknownDataPolicy;
    }

    public BlockState VisibleFallback { get; }

    public bool AllowVerticalClipping { get; }

    public bool PreserveLighting { get; }

    public MinecraftUnknownDataPolicy? UnknownDataPolicy { get; }
}

/// <summary>Inputs for a read-only, lazily materialized conversion preview.</summary>
public sealed record MinecraftDowngradePreviewRequest(
    INormalizedMinecraftChunkSource Source,
    MinecraftTargetProfile Target,
    MinecraftDowngradePolicy Policy,
    int? RequestedYOffset = null,
    MinecraftChunkBounds? Bounds = null,
    IReadOnlyList<MinecraftChunkAddress>? ChunkAddresses = null,
    int MaximumConcurrency = 1);

/// <summary>Aggregated use count and result for one source block-state mapping.</summary>
public sealed record MinecraftBlockMappingSummary(MinecraftBlockMapping Mapping, long BlockCount);

/// <summary>Summary displayed before any converted world is exported.</summary>
public sealed record MinecraftDowngradePreviewSummary(
    string SourceRevision,
    MinecraftTargetProfile Target,
    MinecraftYTranslationLimits YTranslation,
    int AppliedYOffset,
    bool VerticalClippingAllowed,
    long ExactBlockCount,
    long SimilarBlockCount,
    long VisibleFallbackBlockCount,
    long BlockedBlockCount,
    long VerticallyClippedBlockCount,
    IReadOnlyList<MinecraftBlockMappingSummary> Mappings,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics)
{
    public bool CanExport =>
        BlockedBlockCount == 0 &&
        (YTranslation.CanFitWithoutClipping || VerticalClippingAllowed) &&
        (VerticallyClippedBlockCount == 0 || VerticalClippingAllowed) &&
        Diagnostics.All(diagnostic => diagnostic.Severity is not MinecraftDiagnosticSeverity.Blocking);
}

/// <summary>
/// A derived target-version view. Converted chunks are generated on demand and may be rendered directly
/// without changing either the source world or the project's canonical overlay.
/// </summary>
public interface IMinecraftDowngradePreview : IAsyncDisposable
{
    MinecraftDowngradePreviewSummary Summary { get; }

    INormalizedMinecraftChunkSource ConvertedChunks { get; }
}

/// <summary>Builds a downgrade report and lazy target-version chunk view.</summary>
public interface IMinecraftDowngradePreviewService
{
    ValueTask<IMinecraftDowngradePreview> CreateAsync(
        MinecraftDowngradePreviewRequest request,
        IProgress<MinecraftConversionProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Progress emitted while scanning palettes and occupied Y ranges.</summary>
public sealed record MinecraftConversionProgress(
    long ChunksScanned,
    long? ChunksTotal,
    long BlocksScanned,
    MinecraftChunkAddress? CurrentChunk,
    string Message);
