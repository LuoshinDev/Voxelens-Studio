using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZhuJieJing.App.Resources;
using ZhuJieJing.App.WorldPreview;
using ZhuJieJing.Assets;
using ZhuJieJing.Core;
using ZhuJieJing.Minecraft;
using ZhuJieJing.Renderer.Meshing;
using ZhuJieJing.Renderer.Minecraft;

namespace ZhuJieJing.App;

internal enum WorldCropPlaneLoadStage
{
    ReadingChunkIndex,
    ResolvingMinecraftResources,
    ReadingTerrain,
    BuildingSatelliteTiles,
}

internal sealed record WorldCropPlaneLoadProgress(
    WorldCropPlaneLoadStage Stage,
    int CompletedItems,
    int TotalItems,
    string Message);

internal sealed record WorldCropSatelliteTile(
    int TileX,
    int TileZ,
    int PixelOffsetX,
    int PixelOffsetZ,
    BitmapSource Bitmap);

internal abstract record WorldCropPlaneUpdate;

internal sealed record WorldCropIndexReadyUpdate(
    WorldCropChunkPlaneSnapshot Snapshot,
    WorldCropChunkRaster PlaceholderRaster) : WorldCropPlaneUpdate;

internal sealed record WorldCropSatelliteBatchUpdate(
    IReadOnlyList<WorldCropSatelliteTile> Tiles,
    WorldCropChunkRasterPatch? OverviewPatch) : WorldCropPlaneUpdate;

/// <summary>
/// Sparse block-resolution satellite tiles. A tile covers only eight by eight Chunk columns and is created
/// only when one of those Chunks exists, so far-away or sparse worlds never become one enormous dense bitmap.
/// </summary>
internal sealed class WorldCropSatelliteMap
{
    public const int ChunksPerTile = 8;
    public const int BlocksPerTile = ChunksPerTile * 16;

    private readonly Dictionary<long, WorldCropSatelliteTile> tilesByCoordinate = [];
    private readonly SortedSet<int> tileRows = [];
    private readonly Dictionary<int, SortedSet<int>> tileColumnsByRow = [];

    public WorldCropSatelliteMap(IEnumerable<WorldCropSatelliteTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        Upsert(tiles);
    }

    public int TileCount => tilesByCoordinate.Count;

    public void Upsert(IEnumerable<WorldCropSatelliteTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        foreach(WorldCropSatelliteTile tile in tiles)
        {
            long key = PackCoordinates(tile.TileX, tile.TileZ);
            if(!tilesByCoordinate.ContainsKey(key))
            {
                tileRows.Add(tile.TileZ);
                if(!tileColumnsByRow.TryGetValue(tile.TileZ, out SortedSet<int>? columns))
                {
                    columns = [];
                    tileColumnsByRow.Add(tile.TileZ, columns);
                }
                columns.Add(tile.TileX);
            }
            tilesByCoordinate[key] = tile;
        }
    }

    public IEnumerable<WorldCropSatelliteTile> EnumerateVisible(
        int minimumTileX,
        int minimumTileZ,
        int maximumTileX,
        int maximumTileZ)
    {
        if(minimumTileX > maximumTileX || minimumTileZ > maximumTileZ) yield break;
        foreach(int tileZ in tileRows.GetViewBetween(minimumTileZ, maximumTileZ))
        {
            if(!tileColumnsByRow.TryGetValue(tileZ, out SortedSet<int>? columns)) continue;
            foreach(int tileX in columns.GetViewBetween(minimumTileX, maximumTileX))
            {
                if(tilesByCoordinate.TryGetValue(
                       PackCoordinates(tileX, tileZ),
                       out WorldCropSatelliteTile? tile))
                    yield return tile;
            }
        }
    }

    private static long PackCoordinates(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;
}

internal static class WorldCropSatelliteMapLoader
{
    private const int MaximumOverviewDimension = 2048;
    internal const int MaximumLoadConcurrency = 100;

    internal static int ClampLoadConcurrency(int loadConcurrency) => Math.Clamp(
        loadConcurrency,
        NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency,
        MaximumLoadConcurrency);

    public static async Task<WorldCropPlaneLoadResult> LoadAsync(
        IReadOnlyMinecraftWorld world,
        MinecraftDimensionId dimension,
        IProgress<WorldCropPlaneLoadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int loadConcurrency = NormalizedMinecraftChunkBatcher.DefaultLoadConcurrency,
        Func<WorldCropPlaneUpdate, CancellationToken, ValueTask>? publishAsync = null,
        WorldCropChunkPoint? priorityChunk = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        if(loadConcurrency is < NormalizedMinecraftChunkBatcher.MinimumLoadConcurrency or
           > MaximumLoadConcurrency)
            throw new ArgumentOutOfRangeException(nameof(loadConcurrency));
        cancellationToken.ThrowIfCancellationRequested();
        int effectiveLoadConcurrency = loadConcurrency;
        int expectedChunkCount = world.ChunkIndex.GetChunkCount(dimension);
        List<MinecraftChunkIndexEntry> entries = new(expectedChunkCount);
        await foreach(MinecraftChunkIndexEntry entry in world.ChunkIndex
                          .EnumerateAsync(dimension, cancellationToken: cancellationToken)
                          .WithCancellation(cancellationToken)
                          .ConfigureAwait(false))
        {
            entries.Add(entry);
            if((entries.Count & 1023) == 0)
                Report(progress, WorldCropPlaneLoadStage.ReadingChunkIndex, entries.Count, expectedChunkCount,
                    UiText.Get("正在读取当前维度全部 Chunk 索引"));
        }
        Report(progress, WorldCropPlaneLoadStage.ReadingChunkIndex, entries.Count, expectedChunkCount,
            UiText.Get("当前维度 Chunk 索引读取完成"));

        WorldCropChunkPlaneSnapshot snapshot = WorldCropChunkPlaneSnapshot.Create(
            dimension,
            entries.Select(static entry => new WorldCropChunkPoint(entry.Address.X, entry.Address.Z)));
        SatelliteRasterBuilder rasterBuilder = new(snapshot, entries, MaximumOverviewDimension);
        await PublishAsync(
                publishAsync,
                new WorldCropIndexReadyUpdate(snapshot, rasterBuilder.InitialOverview),
                cancellationToken)
            .ConfigureAwait(false);
        if(entries.Count == 0)
            return new WorldCropPlaneLoadResult(snapshot, snapshot, rasterBuilder.InitialOverview);

        WorldCropChunkPoint focus = ResolvePriorityChunk(snapshot, priorityChunk);
        SortEntriesForProgressiveDisplay(entries, focus);

        Report(progress, WorldCropPlaneLoadStage.ResolvingMinecraftResources, 0, 0,
            UiText.Get("正在复用本地 Minecraft 材质缓存"));
        (Minecraft1122BlockRenderResources? resources, string resourceStatus) =
            await OpenResourcesAsync(world.Descriptor, cancellationToken).ConfigureAwait(false);
        using(resources)
        {
            SurfaceVisualResolver visualResolver = new(resources);
            INormalizedMinecraftChunkSource source = NormalizedMinecraftChunkSourceFactory.Create(world);
            int completed = 0;
            long lastProgressTimestamp = Stopwatch.GetTimestamp();
            using CancellationTokenSource producerCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Channel<SurfaceChunk> loadedChunks = Channel.CreateBounded<SurfaceChunk>(
                new BoundedChannelOptions(Math.Max(1, Math.Min(effectiveLoadConcurrency, 4)))
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.Wait,
                });
            Task producerTask = ProduceChunksAsync(
                entries,
                source,
                loadedChunks.Writer,
                visualResolver,
                effectiveLoadConcurrency,
                producerCancellation.Token);
            try
            {
                await foreach(SurfaceChunk chunk in loadedChunks.Reader
                                  .ReadAllAsync(cancellationToken)
                                  .ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rasterBuilder.AddChunk(chunk, effectiveLoadConcurrency, cancellationToken);
                    completed++;
                    long now = Stopwatch.GetTimestamp();
                    if(completed == 1 || completed == entries.Count ||
                       Stopwatch.GetElapsedTime(lastProgressTimestamp, now) >= TimeSpan.FromMilliseconds(100))
                    {
                        Report(progress, WorldCropPlaneLoadStage.ReadingTerrain, completed, entries.Count,
                            UiText.Format($"正在以 {effectiveLoadConcurrency:N0} 路并行读取、解析并提取最高可见方块"));
                        if(publishAsync is not null &&
                           rasterBuilder.TakeProgressiveUpdate(effectiveLoadConcurrency, cancellationToken) is { } update)
                            await PublishAsync(publishAsync, update, cancellationToken).ConfigureAwait(false);
                        lastProgressTimestamp = now;
                    }
                }
                await producerTask.ConfigureAwait(false);
            }
            catch
            {
                producerCancellation.Cancel();
                try
                {
                    await producerTask.ConfigureAwait(false);
                }
                catch
                {
                    // Preserve the consumer/source exception that caused this pipeline to stop.
                }
                throw;
            }

            Report(progress, WorldCropPlaneLoadStage.BuildingSatelliteTiles, 0, rasterBuilder.TileCount,
                UiText.Get("正在收束可缩放卫星图分块"));
            SatelliteRasterResult raster = rasterBuilder.Build(progress, cancellationToken, effectiveLoadConcurrency);
            return new WorldCropPlaneLoadResult(
                snapshot,
                raster.ContentSnapshot,
                raster.Overview,
                new WorldCropSatelliteMap(raster.Tiles),
                raster.SurfaceColumnCount,
                resourceStatus);
        }
    }

    private static async Task ProduceChunksAsync(
        IReadOnlyList<MinecraftChunkIndexEntry> entries,
        INormalizedMinecraftChunkSource source,
        ChannelWriter<SurfaceChunk> writer,
        SurfaceVisualResolver visualResolver,
        int loadConcurrency,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            ParallelOptions parallelOptions = new()
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = loadConcurrency,
            };
            await Parallel.ForEachAsync(entries, parallelOptions, async (entry, token) =>
            {
                NormalizedMinecraftChunk? chunk = await source.FindAsync(entry.Address, token).ConfigureAwait(false);
                if(chunk is null)
                    throw new InvalidDataException($"Chunk 索引与地形内容不一致：{entry.Address} 无法读取。");
                SurfaceChunk surface = ExtractSurface(chunk, visualResolver, token);
                chunk = null;
                await writer.WriteAsync(surface, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch(Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private static SurfaceChunk ExtractSurface(
        NormalizedMinecraftChunk chunk,
        SurfaceVisualResolver visualResolver,
        CancellationToken cancellationToken)
    {
        int[] heights = new int[256];
        Array.Fill(heights, int.MinValue);
        SurfaceColor[] colors = new SurfaceColor[256];
        int unresolved = 256;
        bool containsContent = false;
        foreach(NormalizedMinecraftSection section in chunk.Sections.OrderByDescending(static item => item.Coordinate.Y))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(section.Palette.Count == 0 || section.PaletteIndices.Length != 4096)
                throw new InvalidDataException($"Chunk {chunk.Address} 的 Section {section.Coordinate.Y} 数据不完整。");
            if(section.Palette.All(static state => state.IsAir)) continue;
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            for(int localY = 15; localY >= 0 && unresolved > 0; localY--)
            {
                int layerOffset = localY << 8;
                for(int localZ = 0; localZ < 16; localZ++)
                {
                    int rowOffset = layerOffset | localZ << 4;
                    for(int localX = 0; localX < 16; localX++)
                    {
                        int column = localZ << 4 | localX;
                        if(heights[column] != int.MinValue) continue;
                        ushort paletteIndex = indices[rowOffset | localX];
                        if(paletteIndex >= section.Palette.Count)
                            throw new InvalidDataException($"Chunk {chunk.Address} 的方块调色板索引越界。");
                        BlockState state = section.Palette[paletteIndex];
                        if(!state.IsAir) containsContent = true;
                        SurfaceVisual visual = visualResolver.Resolve(state);
                        if(!visual.IsVisible) continue;
                        heights[column] = checked(section.Coordinate.Y * 16 + localY);
                        colors[column] = visual.Color;
                        unresolved--;
                    }
                }
            }
            if(unresolved == 0) break;
        }

        return new SurfaceChunk(chunk.Address, heights, colors, containsContent);
    }

    private static WorldCropChunkPoint ResolvePriorityChunk(
        WorldCropChunkPlaneSnapshot snapshot,
        WorldCropChunkPoint? requested)
    {
        if(requested is WorldCropChunkPoint preferred)
        {
            if(snapshot.ContainsChunk(preferred.X, preferred.Z)) return preferred;
            MinecraftChunkSquareBounds all = WorldCropSelectionMath.CreateInitial(snapshot);
            if(snapshot.TryFindClosestWithin(all, preferred.X, preferred.Z, out WorldCropChunkPoint nearest))
                return nearest;
        }

        double centerX = snapshot.MinimumX + ((long)snapshot.MaximumX - snapshot.MinimumX) / 2d;
        double centerZ = snapshot.MinimumZ + ((long)snapshot.MaximumZ - snapshot.MinimumZ) / 2d;
        MinecraftChunkSquareBounds bounds = WorldCropSelectionMath.CreateInitial(snapshot);
        return snapshot.TryFindClosestWithin(bounds, centerX, centerZ, out WorldCropChunkPoint centered)
            ? centered
            : new WorldCropChunkPoint(snapshot.MinimumX, snapshot.MinimumZ);
    }

    private static void SortEntriesForProgressiveDisplay(
        List<MinecraftChunkIndexEntry> entries,
        WorldCropChunkPoint focus)
    {
        int focusRegionX = WorldCropChunkPlaneSnapshot.FloorDiv(focus.X, MinecraftRegionAddress.ChunksPerAxis);
        int focusRegionZ = WorldCropChunkPlaneSnapshot.FloorDiv(focus.Z, MinecraftRegionAddress.ChunksPerAxis);
        entries.Sort((left, right) =>
        {
            long leftChunkDistance = ChebyshevDistance(left.Address.X, left.Address.Z, focus.X, focus.Z);
            long rightChunkDistance = ChebyshevDistance(right.Address.X, right.Address.Z, focus.X, focus.Z);
            bool leftIsImmediate = leftChunkDistance <= WorldCropSatelliteMap.ChunksPerTile;
            bool rightIsImmediate = rightChunkDistance <= WorldCropSatelliteMap.ChunksPerTile;
            int comparison = rightIsImmediate.CompareTo(leftIsImmediate);
            if(comparison != 0) return comparison;
            if(leftIsImmediate)
            {
                comparison = leftChunkDistance.CompareTo(rightChunkDistance);
                if(comparison != 0) return comparison;
            }
            else
            {
                long leftRegionDistance = ChebyshevDistance(
                    left.Region.X,
                    left.Region.Z,
                    focusRegionX,
                    focusRegionZ);
                long rightRegionDistance = ChebyshevDistance(
                    right.Region.X,
                    right.Region.Z,
                    focusRegionX,
                    focusRegionZ);
                comparison = leftRegionDistance.CompareTo(rightRegionDistance);
                if(comparison != 0) return comparison;
            }

            comparison = left.Region.X.CompareTo(right.Region.X);
            if(comparison != 0) return comparison;
            comparison = left.Region.Z.CompareTo(right.Region.Z);
            if(comparison != 0) return comparison;
            return left.SectorIndex.CompareTo(right.SectorIndex);
        });
    }

    private static long ChebyshevDistance(int leftX, int leftZ, int rightX, int rightZ) =>
        Math.Max(Math.Abs((long)leftX - rightX), Math.Abs((long)leftZ - rightZ));

    private static ValueTask PublishAsync(
        Func<WorldCropPlaneUpdate, CancellationToken, ValueTask>? publishAsync,
        WorldCropPlaneUpdate update,
        CancellationToken cancellationToken) =>
        publishAsync is null ? ValueTask.CompletedTask : publishAsync(update, cancellationToken);

    private static async Task<(Minecraft1122BlockRenderResources? Resources, string Status)> OpenResourcesAsync(
        MinecraftWorldDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        MinecraftResourceSelectionResult selection = await new MinecraftResourceCacheService()
            .ResolveAsync(descriptor.RootPath, descriptor.Version, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if(!selection.IsAvailable || string.IsNullOrWhiteSpace(selection.ClientJarPath))
            return (null, "未找到匹配材质，卫星图已使用稳定的方块类别颜色");

        try
        {
            string resourcePackPath = Path.Combine(descriptor.RootPath, "resources.zip");
            IReadOnlyList<MinecraftResourceOverlay> overlays = File.Exists(resourcePackPath)
                ? [MinecraftResourceOverlay.ResourcePack(resourcePackPath, "地图资源包")]
                : [];
            Minecraft1122BlockRenderResources resources = Minecraft1122BlockRenderResources.Open(
                new Minecraft1122ResourceConfiguration(
                    selection.ClientJarPath,
                    overlays,
                    AtlasTileSize: 16,
                    AtlasTilesPerRow: 64));
            return (resources, $"{selection.Status.TrimEnd('。')} 卫星图已叠加方块顶面纹理平均色");
        }
        catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException)
        {
            return (null, $"材质解析不可用，卫星图已使用稳定的方块类别颜色：{exception.Message}");
        }
    }

    private static void Report(
        IProgress<WorldCropPlaneLoadProgress>? progress,
        WorldCropPlaneLoadStage stage,
        int completed,
        int total,
        string message) => progress?.Report(new WorldCropPlaneLoadProgress(stage, completed, total, message));

    private sealed class SurfaceVisualResolver
    {
        private static readonly BlockFace[] PreferredFaces =
        [
            BlockFace.Up,
            BlockFace.North,
            BlockFace.South,
            BlockFace.East,
            BlockFace.West,
            BlockFace.Down,
        ];

        private readonly Minecraft1122BlockRenderResources? resources;
        private readonly ConcurrentDictionary<string, SurfaceVisual> cache = new(StringComparer.Ordinal);

        public SurfaceVisualResolver(Minecraft1122BlockRenderResources? resources)
        {
            this.resources = resources;
        }

        public SurfaceVisual Resolve(BlockState state)
        {
            if(state.IsAir) return SurfaceVisual.Hidden;
            return cache.GetOrAdd(state.CanonicalKey, _ => ResolveCore(state));
        }

        private SurfaceVisual ResolveCore(BlockState state)
        {
            if(resources is null)
                return IsKnownInvisible(state.Name)
                    ? SurfaceVisual.Hidden
                    : new SurfaceVisual(true, FallbackColor(state));

            try
            {
                BlockRenderDefinition? definition = resources.Resolve(state);
                if(definition is null || definition.GeometryKind is BlockGeometryKind.Empty or BlockGeometryKind.Invisible)
                    return SurfaceVisual.Hidden;
                foreach(BlockFace face in PreferredFaces)
                {
                    IReadOnlyList<BlockFaceMaterial> materials = resources.ResolveFaceLayers(state, face);
                    if(TryAverageMaterials(resources.Atlas, materials, out SurfaceColor color))
                        return new SurfaceVisual(true, color);
                }
            }
            catch(Exception exception) when(exception is IOException or InvalidDataException or NotSupportedException)
            {
                // One broken block model must not erase the rest of an otherwise readable world satellite map.
            }
            return new SurfaceVisual(true, FallbackColor(state));
        }

        private static bool TryAverageMaterials(
            MinecraftTextureAtlas atlas,
            IReadOnlyList<BlockFaceMaterial> materials,
            out SurfaceColor color)
        {
            double red = 0d;
            double green = 0d;
            double blue = 0d;
            double weight = 0d;
            foreach(BlockFaceMaterial material in materials)
            {
                if(material.IsFallback) continue;
                MinecraftTextureTileSnapshot tile = atlas.SnapshotTile(material.AtlasRegion);
                float tintAlpha = Math.Clamp(material.Tint.W, 0f, 1f);
                float tintRed = Math.Clamp(material.Tint.X, 0f, 1f);
                float tintGreen = Math.Clamp(material.Tint.Y, 0f, 1f);
                float tintBlue = Math.Clamp(material.Tint.Z, 0f, 1f);
                for(int y = 0; y < tile.Height; y++)
                {
                    int row = y * tile.RowPitch;
                    for(int x = 0; x < tile.Width; x++)
                    {
                        int offset = row + x * 4;
                        double alpha = tile.BgraPixels[offset + 3] / 255d * tintAlpha;
                        if(alpha <= 0.01d) continue;
                        blue += tile.BgraPixels[offset] * tintBlue * alpha;
                        green += tile.BgraPixels[offset + 1] * tintGreen * alpha;
                        red += tile.BgraPixels[offset + 2] * tintRed * alpha;
                        weight += alpha;
                    }
                }
            }

            if(weight <= 0d)
            {
                color = default;
                return false;
            }
            color = new SurfaceColor(ToByte(blue / weight), ToByte(green / weight), ToByte(red / weight));
            return true;
        }

        private static SurfaceColor FallbackColor(BlockState state)
        {
            string name = state.Name;
            if(name.Contains("water", StringComparison.Ordinal)) return new SurfaceColor(183, 103, 48);
            if(name.Contains("lava", StringComparison.Ordinal)) return new SurfaceColor(23, 101, 239);
            if(name.Contains("grass", StringComparison.Ordinal) || name.Contains("leaves", StringComparison.Ordinal) ||
               name.Contains("vine", StringComparison.Ordinal) || name.Contains("moss", StringComparison.Ordinal) ||
               name.Contains("fern", StringComparison.Ordinal) || name.Contains("sapling", StringComparison.Ordinal))
                return new SurfaceColor(58, 132, 79);
            if(name.Contains("sand", StringComparison.Ordinal) || name.Contains("end_stone", StringComparison.Ordinal))
                return new SurfaceColor(163, 205, 218);
            if(name.Contains("snow", StringComparison.Ordinal) || name.Contains("ice", StringComparison.Ordinal))
                return new SurfaceColor(235, 235, 229);
            if(name.Contains("dirt", StringComparison.Ordinal) || name.Contains("mud", StringComparison.Ordinal) ||
               name.Contains("soul_soil", StringComparison.Ordinal))
                return new SurfaceColor(54, 86, 121);
            if(name.Contains("log", StringComparison.Ordinal) || name.Contains("wood", StringComparison.Ordinal) ||
               name.Contains("planks", StringComparison.Ordinal))
                return new SurfaceColor(55, 104, 143);
            if(name.Contains("netherrack", StringComparison.Ordinal) || name.Contains("nether_brick", StringComparison.Ordinal))
                return new SurfaceColor(64, 55, 112);
            if(name.Contains("brick", StringComparison.Ordinal) || name.Contains("terracotta", StringComparison.Ordinal))
                return new SurfaceColor(70, 91, 151);
            if(name.Contains("stone", StringComparison.Ordinal) || name.Contains("ore", StringComparison.Ordinal) ||
               name.Contains("deepslate", StringComparison.Ordinal) || name.Contains("cobble", StringComparison.Ordinal))
                return new SurfaceColor(123, 126, 128);
            if(name.Contains("unknown", StringComparison.Ordinal)) return new SurfaceColor(196, 35, 230);
            return new SurfaceColor(126, 130, 132);
        }

        private static bool IsKnownInvisible(string name) => name is
            "minecraft:barrier" or "minecraft:structure_void" or "minecraft:light";

        private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value), 0d, 255d);
    }

    private sealed record SurfaceChunk(
        MinecraftChunkAddress Address,
        int[] Heights,
        SurfaceColor[] Colors,
        bool ContainsContent);

    private sealed class SatelliteRasterBuilder
    {
        private const int MaximumTilesPerProgressiveBatch = 32;
        private static readonly SurfaceColor PlaceholderColor = new(0xC8, 0xAF, 0x96);

        private readonly Dictionary<long, TileBuilder> tiles = [];
        private readonly Dictionary<long, int> expectedChunksByTile = [];
        private readonly Queue<TileBuilder> completedTileBuilders = [];
        private readonly Queue<WorldCropSatelliteTile> pendingTilesToPublish = [];
        private readonly List<WorldCropSatelliteTile> builtTiles = [];
        private readonly HashSet<WorldCropChunkPoint> contentChunks = [];
        private readonly WorldCropChunkPlaneSnapshot storedSnapshot;
        private readonly OverviewBuilder progressiveOverview;
        private long surfaceColumnCount;

        public SatelliteRasterBuilder(
            WorldCropChunkPlaneSnapshot snapshot,
            IReadOnlyList<MinecraftChunkIndexEntry> entries,
            int maximumOverviewDimension)
        {
            storedSnapshot = snapshot;
            progressiveOverview = new OverviewBuilder(snapshot, maximumOverviewDimension);
            foreach(MinecraftChunkIndexEntry entry in entries)
            {
                int tileX = WorldCropChunkPlaneSnapshot.FloorDiv(entry.Address.X, WorldCropSatelliteMap.ChunksPerTile);
                int tileZ = WorldCropChunkPlaneSnapshot.FloorDiv(entry.Address.Z, WorldCropSatelliteMap.ChunksPerTile);
                long key = PackCoordinates(tileX, tileZ);
                expectedChunksByTile[key] = expectedChunksByTile.GetValueOrDefault(key) + 1;
                progressiveOverview.FillChunk(entry.Address.X, entry.Address.Z, PlaceholderColor);
            }
            progressiveOverview.ResetDirtyBounds();
            InitialOverview = progressiveOverview.Snapshot();
        }

        public int TileCount => expectedChunksByTile.Count;

        public WorldCropChunkRaster InitialOverview { get; }

        public void AddChunk(
            SurfaceChunk chunk,
            int buildConcurrency,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(chunk.ContainsContent)
                contentChunks.Add(new WorldCropChunkPoint(chunk.Address.X, chunk.Address.Z));

            int tileX = WorldCropChunkPlaneSnapshot.FloorDiv(chunk.Address.X, WorldCropSatelliteMap.ChunksPerTile);
            int tileZ = WorldCropChunkPlaneSnapshot.FloorDiv(chunk.Address.Z, WorldCropSatelliteMap.ChunksPerTile);
            TileBuilder tile = GetOrCreateTile(tileX, tileZ);
            progressiveOverview.CompleteChunk(chunk.Address.X, chunk.Address.Z);
            int localChunkX = chunk.Address.X - tileX * WorldCropSatelliteMap.ChunksPerTile;
            int localChunkZ = chunk.Address.Z - tileZ * WorldCropSatelliteMap.ChunksPerTile;
            for(int localZ = 0; localZ < 16; localZ++)
            {
                for(int localX = 0; localX < 16; localX++)
                {
                    int column = localZ << 4 | localX;
                    int height = chunk.Heights[column];
                    if(height == int.MinValue) continue;
                    int neighborColumn = localZ > 0 ? column - 16 : localZ < 15 ? column + 16 : column;
                    int neighborHeight = chunk.Heights[neighborColumn] == int.MinValue
                        ? height
                        : chunk.Heights[neighborColumn];
                    double shade = 1d + Math.Clamp(height - neighborHeight, -4, 4) * 0.035d;
                    SurfaceColor shaded = chunk.Colors[column].Shade(shade);
                    int pixelX = localChunkX * 16 + localX;
                    int pixelZ = localChunkZ * 16 + localZ;
                    tile.SetPixel(pixelX, pixelZ, shaded);
                    progressiveOverview.SetPixel(
                        (long)chunk.Address.X * 16L + localX,
                        (long)chunk.Address.Z * 16L + localZ,
                        height,
                        shaded);
                    surfaceColumnCount++;
                }
            }

            if(tile.CompleteChunk())
            {
                completedTileBuilders.Enqueue(tile);
                if(completedTileBuilders.Count >= MaximumTilesPerProgressiveBatch)
                    BuildCompletedTiles(MaximumTilesPerProgressiveBatch, buildConcurrency, cancellationToken);
            }
        }

        public WorldCropSatelliteBatchUpdate? TakeProgressiveUpdate(
            int buildConcurrency,
            CancellationToken cancellationToken)
        {
            BuildCompletedTiles(
                MaximumTilesPerProgressiveBatch,
                buildConcurrency,
                cancellationToken);
            int tileCount = Math.Min(MaximumTilesPerProgressiveBatch, pendingTilesToPublish.Count);
            WorldCropSatelliteTile[] tiles = new WorldCropSatelliteTile[tileCount];
            for(int index = 0; index < tileCount; index++) tiles[index] = pendingTilesToPublish.Dequeue();
            WorldCropChunkRasterPatch? patch = progressiveOverview.TakeDirtyPatch();
            return tiles.Length == 0 && patch is null
                ? null
                : new WorldCropSatelliteBatchUpdate(tiles, patch);
        }

        public SatelliteRasterResult Build(
            IProgress<WorldCropPlaneLoadProgress>? progress,
            CancellationToken cancellationToken,
            int buildConcurrency)
        {
            while(completedTileBuilders.Count > 0)
                BuildCompletedTiles(MaximumTilesPerProgressiveBatch, buildConcurrency, cancellationToken);

            WorldCropChunkPlaneSnapshot contentSnapshot = WorldCropChunkPlaneSnapshot.Create(
                storedSnapshot.Dimension,
                contentChunks);
            if(tiles.Count > 0)
                throw new InvalidDataException($"卫星图仍有 {tiles.Count:N0} 个 Tile 未完成，无法安全收束。");

            WorldCropSatelliteTile[] orderedBuilt = builtTiles
                .OrderBy(static tile => tile.TileZ)
                .ThenBy(static tile => tile.TileX)
                .ToArray();
            if(orderedBuilt.Length > 0)
            {
                int tileBuildConcurrency = Math.Max(1, Math.Min(buildConcurrency, Environment.ProcessorCount));
                Report(progress, WorldCropPlaneLoadStage.BuildingSatelliteTiles, orderedBuilt.Length, orderedBuilt.Length,
                    UiText.Format($"已使用最多 {Math.Min(orderedBuilt.Length, tileBuildConcurrency):N0} 个 CPU 线程分批生成卫星图"));
            }
            return new SatelliteRasterResult(
                contentSnapshot,
                progressiveOverview.Build(),
                orderedBuilt,
                surfaceColumnCount);
        }

        private WorldCropSatelliteTile[] BuildCompletedTiles(
            int maximumCount,
            int buildConcurrency,
            CancellationToken cancellationToken)
        {
            int count = Math.Min(maximumCount, completedTileBuilders.Count);
            if(count <= 0) return [];
            TileBuilder[] builders = new TileBuilder[count];
            for(int index = 0; index < count; index++) builders[index] = completedTileBuilders.Dequeue();
            WorldCropSatelliteTile?[] results = new WorldCropSatelliteTile?[count];
            int tileBuildConcurrency = Math.Max(1, Math.Min(buildConcurrency, Environment.ProcessorCount));
            Parallel.For(
                0,
                builders.Length,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = tileBuildConcurrency,
                },
                index =>
                {
                    if(builders[index].HasPixels) results[index] = builders[index].Build();
                });
            WorldCropSatelliteTile[] built = results.OfType<WorldCropSatelliteTile>().ToArray();
            builtTiles.AddRange(built);
            foreach(WorldCropSatelliteTile tile in built) pendingTilesToPublish.Enqueue(tile);
            foreach(TileBuilder builder in builders)
                tiles.Remove(PackCoordinates(builder.TileX, builder.TileZ));
            return built;
        }

        private TileBuilder GetOrCreateTile(int tileX, int tileZ)
        {
            long key = PackCoordinates(tileX, tileZ);
            if(tiles.TryGetValue(key, out TileBuilder? tile)) return tile;
            if(!expectedChunksByTile.TryGetValue(key, out int expectedChunks))
                throw new InvalidDataException($"Chunk 卫星图索引缺少 Tile（{tileX}, {tileZ}）。");
            tile = new TileBuilder(tileX, tileZ, expectedChunks);
            tiles.Add(key, tile);
            return tile;
        }

        private static long PackCoordinates(int tileX, int tileZ) => ((long)tileX << 32) | (uint)tileZ;
    }

    private sealed class TileBuilder
    {
        private readonly byte[] pixels = new byte[WorldCropSatelliteMap.BlocksPerTile * WorldCropSatelliteMap.BlocksPerTile * 4];
        private readonly int expectedChunks;
        private int completedChunks;
        private int minimumPixelX = WorldCropSatelliteMap.BlocksPerTile;
        private int minimumPixelZ = WorldCropSatelliteMap.BlocksPerTile;
        private int maximumPixelX = -1;
        private int maximumPixelZ = -1;

        public TileBuilder(int tileX, int tileZ, int expectedChunks)
        {
            if(expectedChunks <= 0) throw new ArgumentOutOfRangeException(nameof(expectedChunks));
            TileX = tileX;
            TileZ = tileZ;
            this.expectedChunks = expectedChunks;
        }

        public int TileX { get; }

        public int TileZ { get; }

        public bool HasPixels { get; private set; }

        public void SetPixel(int x, int z, SurfaceColor color)
        {
            int pixelIndex = z * WorldCropSatelliteMap.BlocksPerTile + x;
            int offset = pixelIndex * 4;
            color.WriteTo(pixels, offset);
            minimumPixelX = Math.Min(minimumPixelX, x);
            minimumPixelZ = Math.Min(minimumPixelZ, z);
            maximumPixelX = Math.Max(maximumPixelX, x);
            maximumPixelZ = Math.Max(maximumPixelZ, z);
            HasPixels = true;
        }

        public bool CompleteChunk()
        {
            completedChunks = checked(completedChunks + 1);
            if(completedChunks > expectedChunks)
                throw new InvalidDataException($"Tile（{TileX}, {TileZ}）收到重复 Chunk。");
            return completedChunks == expectedChunks;
        }

        public WorldCropSatelliteTile Build()
        {
            if(!HasPixels) throw new InvalidOperationException("空白 Tile 不应构建卫星图位图。");
            int bitmapWidth = maximumPixelX - minimumPixelX + 1;
            int bitmapHeight = maximumPixelZ - minimumPixelZ + 1;
            int bitmapStride = checked(bitmapWidth * 4);
            byte[] croppedPixels = GC.AllocateUninitializedArray<byte>(checked(bitmapStride * bitmapHeight));
            int sourceStride = WorldCropSatelliteMap.BlocksPerTile * 4;
            for(int row = 0; row < bitmapHeight; row++)
            {
                int sourceOffset = checked((minimumPixelZ + row) * sourceStride + minimumPixelX * 4);
                Buffer.BlockCopy(pixels, sourceOffset, croppedPixels, row * bitmapStride, bitmapStride);
            }
            BitmapSource bitmap = BitmapSource.Create(
                bitmapWidth,
                bitmapHeight,
                96d,
                96d,
                PixelFormats.Bgra32,
                null,
                croppedPixels,
                bitmapStride);
            RenderOptions.SetBitmapScalingMode(bitmap, BitmapScalingMode.NearestNeighbor);
            bitmap.Freeze();
            return new WorldCropSatelliteTile(TileX, TileZ, minimumPixelX, minimumPixelZ, bitmap);
        }
    }

    private sealed class OverviewBuilder
    {
        private readonly int width;
        private readonly int height;
        private readonly int stride;
        private readonly long minimumBlockX;
        private readonly long minimumBlockZ;
        private readonly long sourceWidth;
        private readonly long sourceHeight;
        private readonly int minimumChunkX;
        private readonly int minimumChunkZ;
        private readonly int maximumChunkX;
        private readonly int maximumChunkZ;
        private readonly byte[] pixels;
        private readonly int[] topHeights;
        private readonly int[] placeholderContributions;
        private int dirtyMinimumX = int.MaxValue;
        private int dirtyMinimumY = int.MaxValue;
        private int dirtyMaximumX = int.MinValue;
        private int dirtyMaximumY = int.MinValue;

        public OverviewBuilder(WorldCropChunkPlaneSnapshot snapshot, int maximumDimension)
        {
            minimumChunkX = snapshot.MinimumX;
            minimumChunkZ = snapshot.MinimumZ;
            maximumChunkX = snapshot.MaximumX;
            maximumChunkZ = snapshot.MaximumZ;
            minimumBlockX = (long)snapshot.MinimumX * 16L;
            minimumBlockZ = (long)snapshot.MinimumZ * 16L;
            sourceWidth = ((long)snapshot.MaximumX - snapshot.MinimumX + 1L) * 16L;
            sourceHeight = ((long)snapshot.MaximumZ - snapshot.MinimumZ + 1L) * 16L;
            double ratio = Math.Min(1d, maximumDimension / (double)Math.Max(sourceWidth, sourceHeight));
            width = Math.Max(1, (int)Math.Ceiling(sourceWidth * ratio));
            height = Math.Max(1, (int)Math.Ceiling(sourceHeight * ratio));
            stride = checked(width * 4);
            pixels = new byte[checked(stride * height)];
            topHeights = new int[checked(width * height)];
            placeholderContributions = new int[checked(width * height)];
            Array.Fill(topHeights, int.MinValue);
        }

        public void FillChunk(int chunkX, int chunkZ, SurfaceColor color)
        {
            (int left, int top, int right, int bottom) = GetChunkPixelBounds(chunkX, chunkZ);
            for(int y = top; y < bottom; y++)
            {
                for(int x = left; x < right; x++)
                {
                    int pixelIndex = y * width + x;
                    placeholderContributions[pixelIndex] = checked(placeholderContributions[pixelIndex] + 1);
                    color.WriteTo(pixels, pixelIndex * 4);
                }
            }
        }

        public void CompleteChunk(int chunkX, int chunkZ)
        {
            (int left, int top, int right, int bottom) = GetChunkPixelBounds(chunkX, chunkZ);
            for(int y = top; y < bottom; y++)
            {
                for(int x = left; x < right; x++)
                {
                    int pixelIndex = y * width + x;
                    int remaining = placeholderContributions[pixelIndex] - 1;
                    if(remaining < 0)
                        throw new InvalidDataException($"Chunk（{chunkX}, {chunkZ}）的卫星图占位计数无效。");
                    placeholderContributions[pixelIndex] = remaining;
                    if(remaining != 0 || topHeights[pixelIndex] != int.MinValue) continue;
                    int offset = pixelIndex * 4;
                    if(pixels[offset] == 0 && pixels[offset + 1] == 0 &&
                       pixels[offset + 2] == 0 && pixels[offset + 3] == 0) continue;
                    pixels.AsSpan(offset, 4).Clear();
                    MarkDirty(x, y);
                }
            }
        }

        public void SetPixel(
            long blockX,
            long blockZ,
            int topHeight,
            SurfaceColor color)
        {
            int x = Math.Clamp((int)((blockX - minimumBlockX) * width / sourceWidth), 0, width - 1);
            int z = Math.Clamp((int)((blockZ - minimumBlockZ) * height / sourceHeight), 0, height - 1);
            int pixelIndex = z * width + x;
            if(topHeight < topHeights[pixelIndex]) return;
            topHeights[pixelIndex] = topHeight;
            color.WriteTo(pixels, pixelIndex * 4);
            MarkDirty(x, z);
        }

        public void ResetDirtyBounds()
        {
            dirtyMinimumX = int.MaxValue;
            dirtyMinimumY = int.MaxValue;
            dirtyMaximumX = int.MinValue;
            dirtyMaximumY = int.MinValue;
        }

        public WorldCropChunkRasterPatch? TakeDirtyPatch()
        {
            if(dirtyMaximumX < dirtyMinimumX || dirtyMaximumY < dirtyMinimumY) return null;
            int patchWidth = dirtyMaximumX - dirtyMinimumX + 1;
            int patchHeight = dirtyMaximumY - dirtyMinimumY + 1;
            int patchStride = checked(patchWidth * 4);
            byte[] patch = GC.AllocateUninitializedArray<byte>(checked(patchStride * patchHeight));
            for(int row = 0; row < patchHeight; row++)
            {
                int sourceOffset = checked((dirtyMinimumY + row) * stride + dirtyMinimumX * 4);
                Buffer.BlockCopy(pixels, sourceOffset, patch, row * patchStride, patchStride);
            }
            WorldCropChunkRasterPatch result = new(
                dirtyMinimumX,
                dirtyMinimumY,
                patchWidth,
                patchHeight,
                patchStride,
                patch);
            ResetDirtyBounds();
            return result;
        }

        public WorldCropChunkRaster Snapshot() => new(
            width,
            height,
            stride,
            (byte[])pixels.Clone(),
            minimumChunkX,
            minimumChunkZ,
            maximumChunkX,
            maximumChunkZ);

        public WorldCropChunkRaster Build() => new(
            width,
            height,
            stride,
            pixels,
            minimumChunkX,
            minimumChunkZ,
            maximumChunkX,
            maximumChunkZ);

        private (int Left, int Top, int Right, int Bottom) GetChunkPixelBounds(int chunkX, int chunkZ)
        {
            int left = Math.Clamp(
                ScaleBoundary((long)chunkX * 16L, minimumBlockX, sourceWidth, width, roundUp: false),
                0,
                width - 1);
            int top = Math.Clamp(
                ScaleBoundary((long)chunkZ * 16L, minimumBlockZ, sourceHeight, height, roundUp: false),
                0,
                height - 1);
            int right = ScaleBoundary((long)chunkX * 16L + 16L, minimumBlockX, sourceWidth, width, roundUp: true);
            int bottom = ScaleBoundary((long)chunkZ * 16L + 16L, minimumBlockZ, sourceHeight, height, roundUp: true);
            right = Math.Clamp(Math.Max(left + 1, right), 1, width);
            bottom = Math.Clamp(Math.Max(top + 1, bottom), 1, height);
            return (left, top, right, bottom);
        }

        private void MarkDirty(int x, int y)
        {
            dirtyMinimumX = Math.Min(dirtyMinimumX, x);
            dirtyMinimumY = Math.Min(dirtyMinimumY, y);
            dirtyMaximumX = Math.Max(dirtyMaximumX, x);
            dirtyMaximumY = Math.Max(dirtyMaximumY, y);
        }

        private static int ScaleBoundary(
            long value,
            long minimum,
            long sourceSize,
            int targetSize,
            bool roundUp)
        {
            double scaled = (value - minimum) * targetSize / (double)sourceSize;
            int result = roundUp ? (int)Math.Ceiling(scaled) : (int)Math.Floor(scaled);
            return Math.Clamp(result, 0, targetSize);
        }
    }

    private readonly record struct SurfaceVisual(bool IsVisible, SurfaceColor Color)
    {
        public static SurfaceVisual Hidden { get; } = new(false, default);
    }

    private readonly record struct SurfaceColor(byte Blue, byte Green, byte Red)
    {
        public SurfaceColor Shade(double factor) => new(
            Scale(Blue, factor),
            Scale(Green, factor),
            Scale(Red, factor));

        public void WriteTo(byte[] destination, int offset)
        {
            destination[offset] = Blue;
            destination[offset + 1] = Green;
            destination[offset + 2] = Red;
            destination[offset + 3] = 255;
        }

        private static byte Scale(byte value, double factor) =>
            (byte)Math.Clamp(Math.Round(value * factor), 0d, 255d);
    }

    private sealed record SatelliteRasterResult(
        WorldCropChunkPlaneSnapshot ContentSnapshot,
        WorldCropChunkRaster Overview,
        IReadOnlyList<WorldCropSatelliteTile> Tiles,
        long SurfaceColumnCount);
}
