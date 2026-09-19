namespace ZhuJieJing.Minecraft;

/// <summary>
/// Streaming legacy chunk source that composes a mounted world, random reader, and 1.12.2 normalizer. It owns
/// no world-sized cache and does not own or dispose the supplied read-only mount.
/// </summary>
public sealed class LegacyNormalizedMinecraftChunkSource : INormalizedMinecraftChunkSource
{
    private readonly IReadOnlyMinecraftWorld world;
    private readonly IMinecraftChunkNormalizer normalizer;
    private readonly IMinecraftBlockRegistry registry;
    private readonly MinecraftUnknownDataPolicy unknownDataPolicy;
    private readonly MinecraftChunkReadOptions readOptions;

    public LegacyNormalizedMinecraftChunkSource(
        IReadOnlyMinecraftWorld world,
        IMinecraftBlockRegistry? registry = null,
        MinecraftUnknownDataPolicy? unknownDataPolicy = null,
        MinecraftChunkReadOptions? readOptions = null,
        IMinecraftChunkNormalizer? normalizer = null)
    {
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        this.registry = registry ?? Minecraft1122VanillaBlockRegistry.Instance;
        this.unknownDataPolicy = unknownDataPolicy ?? new MinecraftUnknownDataPolicy();
        this.readOptions = readOptions ?? new MinecraftChunkReadOptions();
        this.normalizer = normalizer ?? new LegacyAnvilChunkNormalizer();
        if (!this.readOptions.DecompressPayload)
            throw new ArgumentException("A normalized chunk source requires decompressed NBT payloads.", nameof(readOptions));

        Dimensions = world.Descriptor.Dimensions
            .Select(static dimension => dimension.Id)
            .Distinct()
            .OrderBy(static dimension => dimension.Value, StringComparer.Ordinal)
            .ToArray();
        Revision = $"legacy-anvil/1:{world.Descriptor.SourceRevisionStrength}:{world.Descriptor.SourceRevision}";
    }

    /// <summary>Revision of the exact mounted source from which every streamed chunk is verified.</summary>
    public string Revision { get; }

    public IReadOnlyList<MinecraftDimensionId> Dimensions { get; }

    public async ValueTask<NormalizedMinecraftChunk?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default)
    {
        MinecraftChunkIndexEntry? entry = await world.ChunkIndex.FindAsync(address, cancellationToken)
            .ConfigureAwait(false);
        return entry is null
            ? null
            : await ReadAndNormalizeAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (MinecraftChunkIndexEntry entry in world.ChunkIndex
                           .EnumerateAsync(dimension, bounds, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return await ReadAndNormalizeAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(
        MinecraftRegionAddress region,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long minimumX = (long)region.X * MinecraftRegionAddress.ChunksPerAxis;
        long minimumZ = (long)region.Z * MinecraftRegionAddress.ChunksPerAxis;
        long maximumX = minimumX + MinecraftRegionAddress.ChunksPerAxis - 1;
        long maximumZ = minimumZ + MinecraftRegionAddress.ChunksPerAxis - 1;
        if (minimumX < int.MinValue || maximumX > int.MaxValue ||
            minimumZ < int.MinValue || maximumZ > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "Region lies outside the supported chunk coordinate range.");
        }

        MinecraftChunkBounds bounds = new((int)minimumX, (int)minimumZ, (int)maximumX, (int)maximumZ);
        await foreach (MinecraftChunkIndexEntry entry in world.ChunkIndex
                           .EnumerateAsync(region.Dimension, bounds, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (entry.Region != region)
                throw new InvalidDataException($"Chunk index returned {entry.Address} outside requested region {region}.");
            yield return await ReadAndNormalizeAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<NormalizedMinecraftChunk> ReadAndNormalizeAsync(
        MinecraftChunkIndexEntry entry,
        CancellationToken cancellationToken)
    {
        RawMinecraftChunk rawChunk = await world.ChunkReader.ReadAsync(entry, readOptions, cancellationToken)
            .ConfigureAwait(false);
        MinecraftNormalizationRequest request = new(
            rawChunk,
            world.Descriptor.Version,
            registry,
            unknownDataPolicy);
        return await normalizer.NormalizeAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
