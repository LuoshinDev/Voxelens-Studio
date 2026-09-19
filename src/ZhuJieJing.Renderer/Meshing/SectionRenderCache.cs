using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Controls;

namespace ZhuJieJing.Renderer.Meshing;

public sealed record SectionCacheChange(
    IReadOnlyList<SectionCoordinate> Rebuilt,
    IReadOnlyList<SectionCoordinate> Removed)
{
    public bool IsEmpty => Rebuilt.Count == 0 && Removed.Count == 0;
}

/// <summary>Reports completed CPU Section meshes for one immutable build request.</summary>
public sealed record SectionMeshBuildProgress(
    int CompletedSections,
    int TotalSections,
    SectionCoordinate? CurrentSection);

internal sealed record SectionRenderBuildRequest(
    object CacheIdentity,
    long Generation,
    IReadOnlyList<SectionCoordinate> Coordinates,
    IReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection> SectionSnapshot,
    IBlockRenderResolver RenderResolver,
    IBlockFaceMaterialResolver? MaterialResolver,
    bool MissingSectionsAreAir)
{
    public bool IsEmpty => Coordinates.Count == 0;
}

internal sealed record SectionRenderBuildResult(
    object CacheIdentity,
    long Generation,
    IReadOnlyList<SectionRenderGeometry> Geometries);

/// <summary>
/// Owns rebuildable CPU meshes by Section. Replacements invalidate that Section and loaded adjacent Sections;
/// corner adjacency matters because Minecraft-style ambient occlusion samples diagonal blocks.
/// </summary>
public sealed class SectionRenderCache
{
    private static readonly (int X, int Y, int Z)[] NeighborOffsets =
        (from y in Enumerable.Range(-1, 3)
         from z in Enumerable.Range(-1, 3)
         from x in Enumerable.Range(-1, 3)
         where x != 0 || y != 0 || z != 0
         select (x, y, z)).ToArray();

    private readonly object _cacheIdentity = new();
    private readonly IBlockRenderResolver _renderResolver;
    private readonly IBlockFaceMaterialResolver? _materialResolver;
    private readonly Dictionary<SectionCoordinate, NormalizedMinecraftSection> _sections = [];
    private readonly Dictionary<SectionCoordinate, SectionRenderGeometry> _geometries = [];
    private readonly HashSet<SectionCoordinate> _pendingRebuild = [];
    private readonly CachedNeighborQuery _neighborQuery;
    private bool _missingSectionsAreAir;
    private long _generation;

    public SectionRenderCache(
        IBlockRenderResolver renderResolver,
        IBlockFaceMaterialResolver? materialResolver = null,
        bool missingSectionsAreAir = true)
    {
        _renderResolver = renderResolver ?? throw new ArgumentNullException(nameof(renderResolver));
        _materialResolver = materialResolver;
        _missingSectionsAreAir = missingSectionsAreAir;
        _neighborQuery = new CachedNeighborQuery(_sections);
    }

    public int Count => _sections.Count;

    public IReadOnlyList<SectionCoordinate> Coordinates => _sections.Keys.Order().ToArray();

    public bool MissingSectionsAreAir => _missingSectionsAreAir;

    public void SetMissingSectionsAreAir(bool value)
    {
        if(_missingSectionsAreAir == value) return;
        _missingSectionsAreAir = value;
        foreach(SectionCoordinate coordinate in _sections.Keys) _pendingRebuild.Add(coordinate);
        NextGeneration();
    }

    public SectionCacheChange SetSections(IEnumerable<NormalizedMinecraftSection> sections)
    {
        var incoming = MaterializeUnique(sections);
        var removed = _sections.Keys.Where(coordinate => !incoming.ContainsKey(coordinate)).ToHashSet();
        var changed = incoming
            .Where(pair => !_sections.TryGetValue(pair.Key, out var current) || !HasSameRenderableContent(current, pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet();

        if(removed.Count == 0 && changed.Count == 0 && _pendingRebuild.Count == 0) return EmptyChange();

        NextGeneration();

        foreach(var coordinate in removed)
        {
            _sections.Remove(coordinate);
            _geometries.Remove(coordinate);
            _pendingRebuild.Remove(coordinate);
        }

        foreach(var coordinate in changed) _sections[coordinate] = incoming[coordinate];
        return RebuildChangedAndNeighbors(changed, removed);
    }

    public SectionCacheChange ReplaceSections(IEnumerable<NormalizedMinecraftSection> sections)
    {
        SectionRenderBuildRequest request = StageReplaceSections(sections);
        SectionRenderBuildResult result = Build(request, CancellationToken.None);
        if(!TryCommit(result, out SectionCacheChange change))
            throw new InvalidOperationException("Section 缓存在同步网格构建期间发生了变化。");
        return change;
    }

    /// <summary>
    /// Updates canonical Section references and captures only the read-only neighborhoods required by the
    /// affected meshes. The returned request owns no WPF or D3D objects and is safe to build off-thread.
    /// </summary>
    internal SectionRenderBuildRequest StageReplaceSections(IEnumerable<NormalizedMinecraftSection> sections)
    {
        var incoming = MaterializeUnique(sections);
        var changed = incoming
            .Where(pair => !_sections.TryGetValue(pair.Key, out var current) || !HasSameRenderableContent(current, pair.Value))
            .Select(pair => pair.Key)
            .ToHashSet();

        foreach(var coordinate in changed) _sections[coordinate] = incoming[coordinate];
        foreach(var coordinate in changed)
        {
            _pendingRebuild.Add(coordinate);
            AddLoadedNeighbors(coordinate, _pendingRebuild);
        }

        SectionCoordinate[] rebuild = _pendingRebuild
            .Where(_sections.ContainsKey)
            .Order()
            .ToArray();
        if(rebuild.Length == 0)
        {
            return new SectionRenderBuildRequest(
                _cacheIdentity,
                _generation,
                [],
                new ReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection>(
                    new Dictionary<SectionCoordinate, NormalizedMinecraftSection>()),
                _renderResolver,
                _materialResolver,
                _missingSectionsAreAir);
        }

        long generation = NextGeneration();
        var snapshotCoordinates = rebuild.ToHashSet();
        foreach(SectionCoordinate coordinate in rebuild) AddLoadedNeighbors(coordinate, snapshotCoordinates);
        var snapshot = snapshotCoordinates.ToDictionary(coordinate => coordinate, coordinate => _sections[coordinate]);
        return new SectionRenderBuildRequest(
            _cacheIdentity,
            generation,
            Array.AsReadOnly(rebuild),
            new ReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection>(snapshot),
            _renderResolver,
            _materialResolver,
            _missingSectionsAreAir);
    }

    /// <summary>Builds immutable CPU geometry only. Callers may run this on a serialized background worker.</summary>
    internal static SectionRenderBuildResult Build(
        SectionRenderBuildRequest request,
        CancellationToken cancellationToken,
        IProgress<SectionMeshBuildProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        int total = request.Coordinates.Count;
        progress?.Report(new SectionMeshBuildProgress(0, total, null));
        if(request.IsEmpty) return new SectionRenderBuildResult(request.CacheIdentity, request.Generation, []);

        using IDisposable? renderBatch = (request.RenderResolver as ISectionRenderBatchSource)?.BeginSectionRenderBatch();
        using IDisposable? materialBatch = ReferenceEquals(request.RenderResolver, request.MaterialResolver)
            ? null
            : (request.MaterialResolver as ISectionRenderBatchSource)?.BeginSectionRenderBatch();
        var renderResolver = new BuildCachedRenderResolver(request.RenderResolver);
        IBlockFaceMaterialResolver? materialResolver = request.MaterialResolver is null
            ? null
            : new BuildCachedMaterialResolver(request.MaterialResolver);
        PrewarmPaletteStates(request, renderResolver, cancellationToken);
        var neighborQuery = new NormalizedSectionNeighborQuery(request.SectionSnapshot.Values);
        var geometries = new SectionRenderGeometry[total];
        int reportStride = Math.Max(1, (total + 199) / 200);
        int completed = 0;
        object progressGate = new();
        // Meshing overlaps chunk decoding and allocation. Reserve CPU time for input/rendering,
        // rather than saturating almost every logical processor during a moving window update.
        int parallelism = Math.Min(total, Math.Clamp(Environment.ProcessorCount / 2, 1, 8));
        void BuildSection(int index)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SectionCoordinate coordinate = request.Coordinates[index];
            if(!request.SectionSnapshot.TryGetValue(coordinate, out NormalizedMinecraftSection? section))
                throw new InvalidDataException($"Section 网格请求缺少目标 {coordinate}。");
            var mesher = new GreedySectionMesher(renderResolver);
            SectionMesh mesh = mesher.Build(section, neighborQuery, request.MissingSectionsAreAir);
            cancellationToken.ThrowIfCancellationRequested();
            geometries[index] = SectionRenderGeometryBuilder.Build(mesh, materialResolver);
            lock(progressGate)
            {
                completed++;
                if(completed == total || completed % reportStride == 0)
                    progress?.Report(new SectionMeshBuildProgress(completed, total, coordinate));
            }
        }

        if(parallelism == 1)
        {
            for(int index = 0; index < total; index++) BuildSection(index);
        }
        else
        {
            Parallel.For(
                0,
                total,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = parallelism,
                },
                BuildSection);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new SectionRenderBuildResult(
            request.CacheIdentity,
            request.Generation,
            Array.AsReadOnly(geometries));
    }

    private static void PrewarmPaletteStates(
        SectionRenderBuildRequest request,
        IBlockRenderResolver renderResolver,
        CancellationToken cancellationToken)
    {
        var uniqueStates = new Dictionary<string, BlockState>(StringComparer.Ordinal);
        foreach(NormalizedMinecraftSection section in request.SectionSnapshot.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach(BlockState state in section.Palette)
            {
                if(!state.IsAir) uniqueStates.TryAdd(state.CanonicalKey, state);
            }
        }
        foreach(BlockState state in uniqueStates.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = renderResolver.Resolve(state);
        }
    }

    /// <summary>Publishes a completed CPU build only when both cache identity and generation still match.</summary>
    internal bool TryCommit(SectionRenderBuildResult result, out SectionCacheChange change)
    {
        ArgumentNullException.ThrowIfNull(result);
        if(!ReferenceEquals(result.CacheIdentity, _cacheIdentity) || result.Generation != _generation)
        {
            change = EmptyChange();
            return false;
        }

        foreach(SectionRenderGeometry geometry in result.Geometries)
        {
            if(!_pendingRebuild.Contains(geometry.Coordinate) || !_sections.ContainsKey(geometry.Coordinate))
            {
                change = EmptyChange();
                return false;
            }
        }

        SectionCoordinate[] rebuilt = result.Geometries.Select(geometry => geometry.Coordinate).Order().ToArray();
        foreach(SectionRenderGeometry geometry in result.Geometries)
        {
            _geometries[geometry.Coordinate] = geometry;
            _pendingRebuild.Remove(geometry.Coordinate);
        }
        change = new SectionCacheChange(rebuilt, []);
        return true;
    }

    public SectionCacheChange RemoveSections(IEnumerable<SectionCoordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        var removed = coordinates.Where(_sections.ContainsKey).ToHashSet();
        if(removed.Count == 0 && _pendingRebuild.Count == 0) return EmptyChange();

        NextGeneration();

        foreach(var coordinate in removed)
        {
            _sections.Remove(coordinate);
            _geometries.Remove(coordinate);
            _pendingRebuild.Remove(coordinate);
        }

        return RebuildChangedAndNeighbors(new HashSet<SectionCoordinate>(), removed);
    }

    public SectionCacheChange Clear()
    {
        var removed = _sections.Keys.Order().ToArray();
        NextGeneration();
        _sections.Clear();
        _geometries.Clear();
        _pendingRebuild.Clear();
        if(removed.Length == 0) return EmptyChange();
        return new SectionCacheChange([], removed);
    }

    public bool TryGetGeometry(SectionCoordinate coordinate, out SectionRenderGeometry geometry) =>
        _geometries.TryGetValue(coordinate, out geometry!);

    internal bool TryGetBlock(string dimension, BlockPosition position, out BlockState state) =>
        _neighborQuery.TryGetBlock(dimension, position, out state);

    internal LegacyBlockEncoding? GetSourceLegacyEncoding(string dimension, BlockPosition position)
    {
        if(!_sections.TryGetValue(SectionCoordinate.FromBlock(dimension, position), out var section) ||
           section.SourceLegacyStates.Length != 4096) return null;
        ushort packed = section.SourceLegacyStates.Span[SectionCoordinate.LocalIndex(position)];
        return new((ushort)(packed >> 4), (byte)(packed & 15));
    }

    internal bool TryGetLight(string dimension, BlockPosition position, out VoxelLightSample light) =>
        _neighborQuery.TryGetLight(dimension, position, out light);

    public IReadOnlyList<SectionRenderGeometry> SelectActive(
        Vector3 focus,
        float drawDistanceBlocks,
        int maximumCount,
        string? dimension = null,
        Matrix4x4? viewProjection = null)
    {
        if(!float.IsFinite(drawDistanceBlocks) || drawDistanceBlocks <= 0) throw new ArgumentOutOfRangeException(nameof(drawDistanceBlocks));
        if(maximumCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCount));
        if(!float.IsFinite(focus.X) || !float.IsFinite(focus.Y) || !float.IsFinite(focus.Z)) throw new ArgumentOutOfRangeException(nameof(focus));
        if(dimension is not null && string.IsNullOrWhiteSpace(dimension)) throw new ArgumentException("维度不能为空。", nameof(dimension));

        var maximumDistanceSquared = (double)drawDistanceBlocks * drawDistanceBlocks;
        SectionFrustum? frustum = viewProjection is Matrix4x4 matrix ? new SectionFrustum(matrix) : null;
        return _geometries.Values
            .Where(geometry => geometry.Indices.Length > 0 &&
                               (dimension is null || string.Equals(geometry.Coordinate.Dimension, dimension, StringComparison.Ordinal)))
            .Select(geometry => new ActiveCandidate(
                geometry,
                DistanceSquaredToBounds(geometry.Coordinate, focus),
                DistanceSquaredToCenter(geometry.Coordinate, focus)))
            .Where(candidate => candidate.BoundsDistanceSquared <= maximumDistanceSquared)
            .OrderByDescending(candidate => frustum is null || frustum.Value.Intersects(candidate.Geometry.Coordinate))
            .ThenBy(candidate => candidate.CenterDistanceSquared)
            .ThenBy(candidate => candidate.Geometry.Coordinate)
            .Take(maximumCount)
            .Select(candidate => candidate.Geometry)
            .ToArray();
    }

    private SectionCacheChange RebuildChangedAndNeighbors(
        IReadOnlySet<SectionCoordinate> changed,
        IReadOnlySet<SectionCoordinate> removed)
    {
        var rebuild = _pendingRebuild.Where(_sections.ContainsKey).ToHashSet();
        _pendingRebuild.Clear();
        foreach(var coordinate in changed)
        {
            if(_sections.ContainsKey(coordinate)) rebuild.Add(coordinate);
            AddLoadedNeighbors(coordinate, rebuild);
        }
        foreach(var coordinate in removed) AddLoadedNeighbors(coordinate, rebuild);

        using IDisposable? renderBatch = (_renderResolver as ISectionRenderBatchSource)?.BeginSectionRenderBatch();
        using IDisposable? materialBatch = ReferenceEquals(_renderResolver, _materialResolver)
            ? null
            : (_materialResolver as ISectionRenderBatchSource)?.BeginSectionRenderBatch();
        var mesher = new GreedySectionMesher(_renderResolver);
        foreach(var coordinate in rebuild)
        {
            var mesh = mesher.Build(_sections[coordinate], _neighborQuery, _missingSectionsAreAir);
            _geometries[coordinate] = SectionRenderGeometryBuilder.Build(mesh, _materialResolver);
        }

        return new SectionCacheChange(rebuild.Order().ToArray(), removed.Order().ToArray());
    }

    private void AddLoadedNeighbors(SectionCoordinate coordinate, ISet<SectionCoordinate> target)
    {
        foreach(var offset in NeighborOffsets)
        {
            var neighbor = new SectionCoordinate(
                coordinate.Dimension,
                checked(coordinate.X + offset.X),
                checked(coordinate.Y + offset.Y),
                checked(coordinate.Z + offset.Z));
            if(_sections.ContainsKey(neighbor)) target.Add(neighbor);
        }
    }

    private static Dictionary<SectionCoordinate, NormalizedMinecraftSection> MaterializeUnique(
        IEnumerable<NormalizedMinecraftSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var result = new Dictionary<SectionCoordinate, NormalizedMinecraftSection>();
        foreach(var section in sections)
        {
            ValidateSection(section);
            if(!result.TryAdd(section.Coordinate, section))
            {
                throw new ArgumentException($"批次中包含重复的 Section {section.Coordinate}。", nameof(sections));
            }
        }

        return result;
    }

    private static bool HasSameRenderableContent(
        NormalizedMinecraftSection current,
        NormalizedMinecraftSection incoming)
    {
        if(ReferenceEquals(current, incoming)) return true;
        if(current.Palette.Count != incoming.Palette.Count ||
           !current.Palette.SequenceEqual(incoming.Palette) ||
           !current.PaletteIndices.Span.SequenceEqual(incoming.PaletteIndices.Span))
        {
            return false;
        }

        MinecraftSectionLighting? currentLighting = current.Lighting;
        MinecraftSectionLighting? incomingLighting = incoming.Lighting;
        if(currentLighting is null || incomingLighting is null) return currentLighting is null && incomingLighting is null;
        return currentLighting.SkyLightNibbles.Span.SequenceEqual(incomingLighting.SkyLightNibbles.Span) &&
               currentLighting.BlockLightNibbles.Span.SequenceEqual(incomingLighting.BlockLightNibbles.Span);
    }

    private sealed class BuildCachedRenderResolver(IBlockRenderResolver inner) : IBlockRenderResolver
    {
        private readonly ConcurrentDictionary<string, Lazy<BlockRenderDefinition?>> definitions = new(StringComparer.Ordinal);

        public BlockRenderDefinition? Resolve(BlockState state)
        {
            ArgumentNullException.ThrowIfNull(state);
            return definitions.GetOrAdd(
                state.CanonicalKey,
                _ => new Lazy<BlockRenderDefinition?>(
                    () => inner.Resolve(state),
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }
    }

    private sealed class BuildCachedMaterialResolver(IBlockFaceMaterialResolver inner) : IBlockFaceLayerResolver, IBlockModelGeometryResolver
    {
        private readonly ConcurrentDictionary<(string State, BlockFace Face), Lazy<IReadOnlyList<BlockFaceMaterial>>> faces = [];
        private readonly ConcurrentDictionary<string, Lazy<ResolvedModels>> models = new(StringComparer.Ordinal);

        public BlockFaceMaterial ResolveFace(BlockState state, BlockFace face)
        {
            IReadOnlyList<BlockFaceMaterial> layers = ResolveFaceLayers(state, face);
            return layers.Count == 0 ? BlockFaceMaterial.Untextured(Vector4.Zero) : layers[0];
        }

        public IReadOnlyList<BlockFaceMaterial> ResolveFaceLayers(BlockState state, BlockFace face)
        {
            ArgumentNullException.ThrowIfNull(state);
            return faces.GetOrAdd(
                (state.CanonicalKey, face),
                _ => new Lazy<IReadOnlyList<BlockFaceMaterial>>(
                    () => inner is IBlockFaceLayerResolver layered
                        ? layered.ResolveFaceLayers(state, face)
                        : [inner.ResolveFace(state, face)],
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        public bool TryResolveModel(BlockState state, out IReadOnlyList<ResolvedBlockModel> resolvedModels)
        {
            ArgumentNullException.ThrowIfNull(state);
            ResolvedModels resolved = models.GetOrAdd(
                state.CanonicalKey,
                _ => new Lazy<ResolvedModels>(
                    () =>
                    {
                        if(inner is IBlockModelGeometryResolver modelResolver &&
                           modelResolver.TryResolveModel(state, out IReadOnlyList<ResolvedBlockModel> value))
                            return new ResolvedModels(true, value);
                        return new ResolvedModels(false, []);
                    },
                    LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            resolvedModels = resolved.Models;
            return resolved.Found;
        }

        private sealed record ResolvedModels(bool Found, IReadOnlyList<ResolvedBlockModel> Models);
    }

    private static void ValidateSection(NormalizedMinecraftSection section)
    {
        NormalizedSectionNeighborQuery.ValidateSection(section);
        if(section.Palette.Any(state => state is null)) throw new InvalidDataException($"Section {section.Coordinate} 的调色板包含空方块状态。");
        foreach(var paletteIndex in section.PaletteIndices.Span)
        {
            if(paletteIndex >= section.Palette.Count)
            {
                throw new InvalidDataException($"Section {section.Coordinate} 的调色板索引 {paletteIndex} 越界。");
            }
        }
    }

    private static double DistanceSquaredToBounds(SectionCoordinate coordinate, Vector3 focus)
    {
        var minimumX = coordinate.X * 16d;
        var minimumY = coordinate.Y * 16d;
        var minimumZ = coordinate.Z * 16d;
        var dx = AxisDistance(focus.X, minimumX, minimumX + 16d);
        var dy = AxisDistance(focus.Y, minimumY, minimumY + 16d);
        var dz = AxisDistance(focus.Z, minimumZ, minimumZ + 16d);
        return dx * dx + dy * dy + dz * dz;
    }

    private static double DistanceSquaredToCenter(SectionCoordinate coordinate, Vector3 focus)
    {
        var dx = coordinate.X * 16d + 8d - focus.X;
        var dy = coordinate.Y * 16d + 8d - focus.Y;
        var dz = coordinate.Z * 16d + 8d - focus.Z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static double AxisDistance(double value, double minimum, double maximum) =>
        value < minimum ? minimum - value : value > maximum ? value - maximum : 0d;

    private static SectionCacheChange EmptyChange() => new([], []);

    private long NextGeneration() => _generation = checked(_generation + 1);

    private readonly record struct ActiveCandidate(
        SectionRenderGeometry Geometry,
        double BoundsDistanceSquared,
        double CenterDistanceSquared);

    private sealed class CachedNeighborQuery : ISectionNeighborQuery
    {
        private readonly IReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection> _sections;

        public CachedNeighborQuery(IReadOnlyDictionary<SectionCoordinate, NormalizedMinecraftSection> sections)
        {
            _sections = sections;
        }

        public bool TryGetBlock(string dimension, BlockPosition position, out BlockState state)
        {
            var coordinate = SectionCoordinate.FromBlock(dimension, position);
            if(!_sections.TryGetValue(coordinate, out var section))
            {
                state = BlockState.Air;
                return false;
            }

            var paletteIndex = section.PaletteIndices.Span[SectionCoordinate.LocalIndex(position)];
            state = section.Palette[paletteIndex];
            return true;
        }

        public bool TryGetLight(string dimension, BlockPosition position, out VoxelLightSample light)
        {
            var coordinate = SectionCoordinate.FromBlock(dimension, position);
            if(!_sections.TryGetValue(coordinate, out var section))
            {
                light = VoxelLightSample.VisibleFallback;
                return false;
            }

            light = VoxelLightingMath.Sample(section, SectionCoordinate.LocalIndex(position));
            return true;
        }
    }
}
