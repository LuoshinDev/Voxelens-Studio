using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Immutable chunk source for imported structures and compiled AI plans. It lets every export path consume the
/// same canonical chunk contract without pretending that these scenes came from an MCA payload.
/// </summary>
public sealed class NormalizedMinecraftSceneChunkSource : INormalizedMinecraftChunkSource
{
    private static readonly byte[] EmptyCompoundNbt = [10, 0, 0, 0];
    private readonly IReadOnlyDictionary<MinecraftChunkAddress, NormalizedMinecraftChunk> chunks;

    public NormalizedMinecraftSceneChunkSource(
        IEnumerable<NormalizedMinecraftSection> sections,
        MinecraftVersionDescriptor sourceVersion,
        MinecraftDimensionId outputDimension,
        IEnumerable<NormalizedMinecraftBlockEntity>? blockEntities = null)
    {
        ArgumentNullException.ThrowIfNull(sections);
        ArgumentNullException.ThrowIfNull(sourceVersion);
        if(string.IsNullOrWhiteSpace(outputDimension.Value))
            throw new ArgumentException("输出维度不能为空。", nameof(outputDimension));

        NormalizedMinecraftSection[] normalizedSections = sections
            .Select(section => NormalizeSection(section, outputDimension))
            .OrderBy(static section => section.Coordinate.X)
            .ThenBy(static section => section.Coordinate.Z)
            .ThenBy(static section => section.Coordinate.Y)
            .ToArray();
        EnsureUniqueSections(normalizedSections);
        NormalizedMinecraftBlockEntity[] normalizedEntities = (blockEntities ?? [])
            .Select(CloneBlockEntity)
            .OrderBy(static entity => entity.Position.X)
            .ThenBy(static entity => entity.Position.Z)
            .ThenBy(static entity => entity.Position.Y)
            .ToArray();
        Revision = ComputeRevision(normalizedSections, normalizedEntities, sourceVersion, outputDimension);

        var sectionGroups = normalizedSections.GroupBy(static section =>
            (section.Coordinate.X, section.Coordinate.Z));
        var entityGroups = normalizedEntities.GroupBy(static entity =>
            (X: FloorDiv(entity.Position.X, 16), Z: FloorDiv(entity.Position.Z, 16)));
        Dictionary<(int X, int Z), IReadOnlyList<NormalizedMinecraftBlockEntity>> entitiesByChunk = entityGroups
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<NormalizedMinecraftBlockEntity>)Array.AsReadOnly(group.ToArray()));
        HashSet<(int X, int Z)> chunkCoordinates = sectionGroups
            .Select(static group => group.Key)
            .Concat(entitiesByChunk.Keys)
            .ToHashSet();
        Dictionary<(int X, int Z), IReadOnlyList<NormalizedMinecraftSection>> sectionsByChunk = normalizedSections
            .GroupBy(static section => (section.Coordinate.X, section.Coordinate.Z))
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<NormalizedMinecraftSection>)Array.AsReadOnly(group.ToArray()));

        Dictionary<MinecraftChunkAddress, NormalizedMinecraftChunk> built = [];
        foreach((int x, int z) in chunkCoordinates.OrderBy(static value => value.X).ThenBy(static value => value.Z))
        {
            MinecraftChunkAddress address = new(outputDimension, x, z);
            IReadOnlyList<NormalizedMinecraftSection> chunkSections = sectionsByChunk.GetValueOrDefault((x, z)) ?? [];
            IReadOnlyList<NormalizedMinecraftBlockEntity> chunkEntities = entitiesByChunk.GetValueOrDefault((x, z)) ?? [];
            string chunkHash = ComputeChunkHash(address, chunkSections, chunkEntities);
            MinecraftOpaquePayload generatedPayload = CreateGeneratedPayload(address);
            built.Add(address, new NormalizedMinecraftChunk(
                address,
                sourceVersion,
                chunkSections,
                chunkEntities,
                new MinecraftChunkProvenance(generatedPayload.ContentHash!, chunkHash, chunkHash),
                new MinecraftChunkPassthrough(
                    MinecraftChunkCompression.None,
                    MinecraftChunkStorageKind.UnknownUntilRead,
                    generatedPayload,
                    [],
                    SupportsStructuredMerge: false),
                []));
        }

        chunks = new ReadOnlyDictionary<MinecraftChunkAddress, NormalizedMinecraftChunk>(built);
        Dimensions = Array.AsReadOnly([outputDimension]);
    }

    public string Revision { get; }

    public IReadOnlyList<MinecraftDimensionId> Dimensions { get; }

    public static NormalizedMinecraftSceneChunkSource FromSceneDelta(
        SceneDelta delta,
        MinecraftVersionDescriptor sourceVersion,
        MinecraftDimensionId outputDimension)
    {
        ArgumentNullException.ThrowIfNull(delta);
        Dictionary<SectionCoordinate, SceneSectionBuilder> builders = [];
        foreach(SectionDelta sectionDelta in delta.Sections)
        {
            foreach(VoxelChange change in sectionDelta.Changes)
            {
                if(change.After.IsAir) continue;
                SectionCoordinate coordinate = sectionDelta.Section with { Dimension = outputDimension.Value };
                if(!builders.TryGetValue(coordinate, out SceneSectionBuilder? builder))
                {
                    builder = new SceneSectionBuilder(coordinate);
                    builders.Add(coordinate, builder);
                }
                builder.Set(change.LocalIndex, change.After);
            }
        }

        return new NormalizedMinecraftSceneChunkSource(
            builders.Values.Select(static builder => builder.Build()),
            sourceVersion,
            outputDimension);
    }

    public ValueTask<NormalizedMinecraftChunk?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        chunks.TryGetValue(address, out NormalizedMinecraftChunk? chunk);
        return ValueTask.FromResult(chunk);
    }

    public async IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach(NormalizedMinecraftChunk chunk in chunks.Values
                    .Where(chunk => chunk.Address.Dimension == dimension)
                    .Where(chunk => bounds is null || bounds.Value.Contains(chunk.Address))
                    .OrderBy(static chunk => chunk.Address.X)
                    .ThenBy(static chunk => chunk.Address.Z))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    public IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(
        MinecraftRegionAddress region,
        CancellationToken cancellationToken = default)
    {
        long minimumX = (long)region.X * MinecraftRegionAddress.ChunksPerAxis;
        long minimumZ = (long)region.Z * MinecraftRegionAddress.ChunksPerAxis;
        long maximumX = minimumX + MinecraftRegionAddress.ChunksPerAxis - 1;
        long maximumZ = minimumZ + MinecraftRegionAddress.ChunksPerAxis - 1;
        if(minimumX < int.MinValue || maximumX > int.MaxValue ||
           minimumZ < int.MinValue || maximumZ > int.MaxValue)
            return Empty(cancellationToken);
        return EnumerateAsync(
            region.Dimension,
            new MinecraftChunkBounds((int)minimumX, (int)minimumZ, (int)maximumX, (int)maximumZ),
            cancellationToken);
    }

    public static MinecraftOpaquePayload CreateGeneratedLevelMetadata(string sourceLabel)
    {
        string label = string.IsNullOrWhiteSpace(sourceLabel) ? "generated-scene" : sourceLabel.Trim();
        string hash = Convert.ToHexString(SHA256.HashData(EmptyCompoundNbt)).ToLowerInvariant();
        return new MinecraftOpaquePayload(
            MinecraftOpaquePayloadFormat.DecompressedNbtDocument,
            EmptyCompoundNbt.ToArray(),
            label,
            "$",
            hash);
    }

    private static async IAsyncEnumerable<NormalizedMinecraftChunk> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }

    private static NormalizedMinecraftSection NormalizeSection(
        NormalizedMinecraftSection section,
        MinecraftDimensionId outputDimension)
    {
        ArgumentNullException.ThrowIfNull(section);
        if(section.Palette.Count == 0)
            throw new InvalidDataException($"Section {section.Coordinate} 的调色板为空。");
        if(section.PaletteIndices.Length != 4096)
            throw new InvalidDataException($"Section {section.Coordinate} 必须包含 4096 个方块索引。");
        ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
        for(int index = 0; index < indices.Length; index++)
        {
            if(indices[index] >= section.Palette.Count)
                throw new InvalidDataException($"Section {section.Coordinate} 的索引 {index} 超出调色板。");
        }
        if(section.Lighting is MinecraftSectionLighting lighting &&
           (lighting.SkyLightNibbles.Length is not 0 and not 2048 ||
            lighting.BlockLightNibbles.Length is not 0 and not 2048 ||
            lighting.StaticLightSourceNibbles.Length is not 0 and not 2048))
            throw new InvalidDataException($"Section {section.Coordinate} 的光照数组长度无效。");

        MinecraftSectionLighting? lightingCopy = section.Lighting is null
            ? null
            : new MinecraftSectionLighting(
                section.Lighting.SkyLightNibbles.ToArray(),
                section.Lighting.BlockLightNibbles.ToArray(),
                section.Lighting.StaticLightSourceNibbles.ToArray());
        MinecraftOpaqueFragment[] unknownFragments = section.UnknownFragments
            .Select(CloneOpaqueFragment)
            .ToArray();
        return new NormalizedMinecraftSection(
            section.Coordinate with { Dimension = outputDimension.Value },
            Array.AsReadOnly(section.Palette.ToArray()),
            section.PaletteIndices.ToArray(),
            lightingCopy,
            Array.AsReadOnly(unknownFragments));
    }

    private static NormalizedMinecraftBlockEntity CloneBlockEntity(NormalizedMinecraftBlockEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        MinecraftOpaquePayload original = CloneOpaquePayload(entity.OriginalNbt);
        Dictionary<string, object?> knownProperties = entity.KnownProperties
            .ToDictionary(static pair => pair.Key, static pair => CloneKnownValue(pair.Value), StringComparer.Ordinal);
        return entity with
        {
            KnownProperties = new ReadOnlyDictionary<string, object?>(knownProperties),
            OriginalNbt = original,
        };
    }

    private static MinecraftOpaqueFragment CloneOpaqueFragment(MinecraftOpaqueFragment fragment) =>
        fragment with { Payload = CloneOpaquePayload(fragment.Payload) };

    private static MinecraftOpaquePayload CloneOpaquePayload(MinecraftOpaquePayload payload) =>
        payload with { Bytes = payload.Bytes.ToArray() };

    private static object? CloneKnownValue(object? value) => value switch
    {
        null or string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or
            float or double or decimal or char or DateTime or DateTimeOffset or TimeSpan or Guid => value,
        byte[] values => values.ToArray(),
        sbyte[] values => values.ToArray(),
        short[] values => values.ToArray(),
        ushort[] values => values.ToArray(),
        int[] values => values.ToArray(),
        uint[] values => values.ToArray(),
        long[] values => values.ToArray(),
        ulong[] values => values.ToArray(),
        float[] values => values.ToArray(),
        double[] values => values.ToArray(),
        bool[] values => values.ToArray(),
        char[] values => values.ToArray(),
        ReadOnlyMemory<byte> values => new ReadOnlyMemory<byte>(values.ToArray()),
        Memory<byte> values => new ReadOnlyMemory<byte>(values.ToArray()),
        IReadOnlyDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(static pair => pair.Key, static pair => CloneKnownValue(pair.Value), StringComparer.Ordinal)),
        IDictionary<string, object?> dictionary => new ReadOnlyDictionary<string, object?>(
            dictionary.ToDictionary(static pair => pair.Key, static pair => CloneKnownValue(pair.Value), StringComparer.Ordinal)),
        IEnumerable<object?> sequence => Array.AsReadOnly(sequence.Select(CloneKnownValue).ToArray()),
        _ => value,
    };

    private static void EnsureUniqueSections(IReadOnlyList<NormalizedMinecraftSection> sections)
    {
        HashSet<SectionCoordinate> seen = [];
        foreach(NormalizedMinecraftSection section in sections)
        {
            if(!seen.Add(section.Coordinate))
                throw new InvalidDataException($"场景包含重复 Section：{section.Coordinate}。");
        }
    }

    private static string ComputeRevision(
        IReadOnlyList<NormalizedMinecraftSection> sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> entities,
        MinecraftVersionDescriptor version,
        MinecraftDimensionId dimension)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "zhujiejing.scene-source/2");
        Append(hash, dimension.Value);
        Append(hash, version.VersionName ?? string.Empty);
        Append(hash, version.DataVersion ?? int.MinValue);
        Append(hash, (int)version.StorageFamily);
        Append(hash, sections.Count);
        foreach(NormalizedMinecraftSection section in sections)
        {
            Append(hash, section.Coordinate.X);
            Append(hash, section.Coordinate.Y);
            Append(hash, section.Coordinate.Z);
            Append(hash, section.Palette.Count);
            foreach(BlockState state in section.Palette) Append(hash, state.CanonicalKey);
            Append(hash, section.PaletteIndices.Length);
            foreach(ushort index in section.PaletteIndices.Span) Append(hash, index);
            AppendLighting(hash, section.Lighting);
            AppendOpaqueFragments(hash, section.UnknownFragments);
        }
        Append(hash, entities.Count);
        foreach(NormalizedMinecraftBlockEntity entity in entities)
        {
            Append(hash, entity.TypeId);
            Append(hash, entity.Position.X);
            Append(hash, entity.Position.Y);
            Append(hash, entity.Position.Z);
            Append(hash, entity.SourceDataVersion ?? -1);
            AppendKnownProperties(hash, entity.KnownProperties);
            AppendOpaquePayload(hash, entity.OriginalNbt);
        }
        return $"scene-source/2:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static string ComputeChunkHash(
        MinecraftChunkAddress address,
        IReadOnlyList<NormalizedMinecraftSection> sections,
        IReadOnlyList<NormalizedMinecraftBlockEntity> entities)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "zhujiejing.scene-chunk/2");
        Append(hash, address.Dimension.Value);
        Append(hash, address.X);
        Append(hash, address.Z);
        Append(hash, sections.Count);
        foreach(NormalizedMinecraftSection section in sections)
        {
            Append(hash, section.Coordinate.Y);
            Append(hash, section.Palette.Count);
            foreach(BlockState state in section.Palette) Append(hash, state.CanonicalKey);
            Append(hash, section.PaletteIndices.Length);
            foreach(ushort index in section.PaletteIndices.Span) Append(hash, index);
            AppendLighting(hash, section.Lighting);
            AppendOpaqueFragments(hash, section.UnknownFragments);
        }
        Append(hash, entities.Count);
        foreach(NormalizedMinecraftBlockEntity entity in entities)
        {
            Append(hash, entity.TypeId);
            Append(hash, entity.Position.X);
            Append(hash, entity.Position.Y);
            Append(hash, entity.Position.Z);
            Append(hash, entity.SourceDataVersion ?? -1);
            AppendKnownProperties(hash, entity.KnownProperties);
            AppendOpaquePayload(hash, entity.OriginalNbt);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendLighting(IncrementalHash hash, MinecraftSectionLighting? lighting)
    {
        Append(hash, lighting is null ? 0 : 1);
        if(lighting is null) return;
        Append(hash, lighting.SkyLightNibbles.Span);
        Append(hash, lighting.BlockLightNibbles.Span);
        Append(hash, lighting.StaticLightSourceNibbles.Span);
    }

    private static void AppendOpaqueFragments(
        IncrementalHash hash,
        IReadOnlyList<MinecraftOpaqueFragment> fragments)
    {
        Append(hash, fragments.Count);
        foreach(MinecraftOpaqueFragment fragment in fragments)
        {
            Append(hash, (int)fragment.Scope);
            Append(hash, fragment.NbtPath);
            Append(hash, fragment.RequiredForRoundTrip ? 1 : 0);
            AppendOpaquePayload(hash, fragment.Payload);
        }
    }

    private static void AppendOpaquePayload(IncrementalHash hash, MinecraftOpaquePayload payload)
    {
        Append(hash, (int)payload.Format);
        Append(hash, payload.SourceRelativePath);
        Append(hash, payload.NbtPath ?? string.Empty);
        Append(hash, payload.ContentHash ?? string.Empty);
        Append(hash, payload.Bytes.Span);
    }

    private static void AppendKnownProperties(
        IncrementalHash hash,
        IReadOnlyDictionary<string, object?> properties)
    {
        Append(hash, properties.Count);
        foreach(KeyValuePair<string, object?> property in properties.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, property.Key);
            AppendKnownValue(hash, property.Value);
        }
    }

    private static void AppendKnownValue(IncrementalHash hash, object? value)
    {
        if(value is null)
        {
            Append(hash, "null");
            return;
        }

        Append(hash, value.GetType().FullName ?? value.GetType().Name);
        switch(value)
        {
            case string text:
                Append(hash, text);
                return;
            case byte[] values:
                Append(hash, values);
                return;
            case ReadOnlyMemory<byte> values:
                Append(hash, values.Span);
                return;
            case Memory<byte> values:
                Append(hash, values.Span);
                return;
            case IReadOnlyDictionary<string, object?> dictionary:
                AppendKnownProperties(hash, dictionary);
                return;
            case IDictionary<string, object?> dictionary:
                AppendKnownProperties(hash, new ReadOnlyDictionary<string, object?>(dictionary));
                return;
            case IEnumerable sequence when value is not string:
            {
                List<object?> items = [];
                foreach(object? item in sequence) items.Add(item);
                Append(hash, items.Count);
                foreach(object? item in items) AppendKnownValue(hash, item);
                return;
            }
            case IFormattable formattable:
                Append(hash, formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty);
                return;
            default:
                Append(hash, value.ToString() ?? string.Empty);
                return;
        }
    }

    private static MinecraftOpaquePayload CreateGeneratedPayload(MinecraftChunkAddress address)
    {
        string hash = Convert.ToHexString(SHA256.HashData(EmptyCompoundNbt)).ToLowerInvariant();
        return new MinecraftOpaquePayload(
            MinecraftOpaquePayloadFormat.EncodedNbtTag,
            EmptyCompoundNbt.ToArray(),
            $"generated/{address.Dimension.Value}/{address.X}.{address.Z}.nbt",
            "$",
            hash);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Append(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void Append(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        Append(hash, value.Length);
        hash.AppendData(value);
    }

    private static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    private sealed class SceneSectionBuilder
    {
        private readonly List<BlockState> palette = [BlockState.Air];
        private readonly Dictionary<BlockState, ushort> paletteLookup = new() { [BlockState.Air] = 0 };
        private readonly ushort[] indices = new ushort[4096];

        public SceneSectionBuilder(SectionCoordinate coordinate)
        {
            Coordinate = coordinate;
        }

        public SectionCoordinate Coordinate { get; }

        public void Set(int localIndex, BlockState state)
        {
            if(localIndex is < 0 or >= 4096) throw new ArgumentOutOfRangeException(nameof(localIndex));
            if(!paletteLookup.TryGetValue(state, out ushort paletteIndex))
            {
                paletteIndex = checked((ushort)palette.Count);
                palette.Add(state);
                paletteLookup.Add(state, paletteIndex);
            }
            indices[localIndex] = paletteIndex;
        }

        public NormalizedMinecraftSection Build() => new(
            Coordinate,
            palette.ToArray(),
            indices,
            null,
            []);
    }
}

/// <summary>Read-only view exposing only explicitly selected dimensions from another canonical source.</summary>
public sealed class NormalizedMinecraftDimensionSubsetSource : INormalizedMinecraftChunkSource
{
    private readonly INormalizedMinecraftChunkSource source;
    private readonly HashSet<MinecraftDimensionId> dimensions;

    public NormalizedMinecraftDimensionSubsetSource(
        INormalizedMinecraftChunkSource source,
        IEnumerable<MinecraftDimensionId> dimensions)
    {
        this.source = source ?? throw new ArgumentNullException(nameof(source));
        this.dimensions = dimensions?.Distinct().ToHashSet() ?? throw new ArgumentNullException(nameof(dimensions));
        if(this.dimensions.Count == 0) throw new ArgumentException("至少需要选择一个维度。", nameof(dimensions));
        if(this.dimensions.Any(dimension => !source.Dimensions.Contains(dimension)))
            throw new ArgumentException("选择的维度不在源场景中。", nameof(dimensions));
        Dimensions = source.Dimensions.Where(this.dimensions.Contains).ToArray();
        Revision = $"dimension-subset/1:{source.Revision}:{string.Join(',', Dimensions.Select(static value => value.Value))}";
    }

    public string Revision { get; }

    public IReadOnlyList<MinecraftDimensionId> Dimensions { get; }

    public ValueTask<NormalizedMinecraftChunk?> FindAsync(
        MinecraftChunkAddress address,
        CancellationToken cancellationToken = default) => dimensions.Contains(address.Dimension)
        ? source.FindAsync(address, cancellationToken)
        : ValueTask.FromResult<NormalizedMinecraftChunk?>(null);

    public IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateAsync(
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        CancellationToken cancellationToken = default) => dimensions.Contains(dimension)
        ? source.EnumerateAsync(dimension, bounds, cancellationToken)
        : Empty(cancellationToken);

    public IAsyncEnumerable<NormalizedMinecraftChunk> EnumerateRegionAsync(
        MinecraftRegionAddress region,
        CancellationToken cancellationToken = default) => dimensions.Contains(region.Dimension)
        ? source.EnumerateRegionAsync(region, cancellationToken)
        : Empty(cancellationToken);

    private static async IAsyncEnumerable<NormalizedMinecraftChunk> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
