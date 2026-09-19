using System.Runtime.CompilerServices;
using ZhuJieJing.Minecraft;

namespace ZhuJieJing.App.WorldPreview;

public sealed record NormalizedMinecraftChunkBatch(
    IReadOnlyList<NormalizedMinecraftSection> Sections,
    int BatchChunkCount,
    int ProcessedChunkCount)
{
    public WorldPreviewStatistics BatchStatistics { get; init; } = WorldPreviewStatistics.Empty;

    public WorldPreviewStatistics ProcessedStatistics { get; init; } = WorldPreviewStatistics.Empty;

    public IReadOnlyDictionary<MinecraftChunkAddress, WorldPreviewStatistics> ChunkStatistics { get; init; } =
        new Dictionary<MinecraftChunkAddress, WorldPreviewStatistics>();
}

public static class NormalizedMinecraftChunkBatcher
{
    // One Chunk per UI publication keeps CPU mesh work bounded and makes the first useful block visible promptly.
    public const int DefaultBatchSize = 1;
    public const int MinimumLoadConcurrency = 1;
    public const int DefaultLoadConcurrency = 6;
    public const int MaximumLoadConcurrency = 100;

    public static async IAsyncEnumerable<NormalizedMinecraftChunkBatch> EnumerateAsync(
        INormalizedMinecraftChunkSource source,
        MinecraftDimensionId dimension,
        MinecraftChunkBounds? bounds = null,
        int batchSize = DefaultBatchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        await foreach(NormalizedMinecraftChunkBatch batch in EnumerateCoreAsync(
                          source.EnumerateAsync(dimension, bounds, cancellationToken),
                          batchSize,
                          cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    public static async IAsyncEnumerable<NormalizedMinecraftChunkBatch> EnumerateAddressesAsync(
        INormalizedMinecraftChunkSource source,
        IEnumerable<MinecraftChunkAddress> addresses,
        int batchSize = DefaultBatchSize,
        int loadConcurrency = MinimumLoadConcurrency,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(addresses);
        await foreach(NormalizedMinecraftChunkBatch batch in EnumerateCoreAsync(
                          ReadAddressesAsync(source, addresses, loadConcurrency, cancellationToken),
                          batchSize,
                          cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<NormalizedMinecraftChunk> ReadAddressesAsync(
        INormalizedMinecraftChunkSource source,
        IEnumerable<MinecraftChunkAddress> addresses,
        int loadConcurrency,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(loadConcurrency is < MinimumLoadConcurrency or > MaximumLoadConcurrency)
            throw new ArgumentOutOfRangeException(nameof(loadConcurrency));

        HashSet<MinecraftChunkAddress> seen = [];
        using IEnumerator<MinecraftChunkAddress> enumerator = addresses.GetEnumerator();
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = new List<MinecraftChunkAddress>(loadConcurrency);
            while(window.Count < loadConcurrency && enumerator.MoveNext())
            {
                MinecraftChunkAddress address = enumerator.Current;
                if(!seen.Add(address)) throw new ArgumentException($"地址列表包含重复 Chunk：{address}。", nameof(addresses));
                window.Add(address);
            }
            if(window.Count == 0) yield break;

            Task<NormalizedMinecraftChunk?>[] reads = window
                .Select(address => source.FindAsync(address, cancellationToken).AsTask())
                .ToArray();
            // Reads stay concurrent, but completed chunks are published in center-first address order. This keeps
            // the visible neighborhood contiguous so temporary missing neighbors are never exposed as tall walls.
            foreach(Task<NormalizedMinecraftChunk?> read in reads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NormalizedMinecraftChunk? chunk = await read.ConfigureAwait(false);
                if(chunk is not null) yield return chunk;
            }
        }
    }

    private static async IAsyncEnumerable<NormalizedMinecraftChunkBatch> EnumerateCoreAsync(
        IAsyncEnumerable<NormalizedMinecraftChunk> chunks,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if(batchSize is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(batchSize));

        List<NormalizedMinecraftSection> sections = new();
        Dictionary<MinecraftChunkAddress, WorldPreviewStatistics> batchChunkStatistics = [];
        WorldPreviewStatisticsAccumulator batchStatistics = new();
        WorldPreviewStatisticsAccumulator processedStatistics = new();
        int batchChunkCount = 0;
        int processedChunkCount = 0;

        await foreach(NormalizedMinecraftChunk chunk in chunks
                          .WithCancellation(cancellationToken)
                          .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            WorldPreviewStatistics chunkStatistics = WorldPreviewStatisticsAnalyzer.AnalyzeChunk(chunk, cancellationToken);
            sections.AddRange(chunk.Sections);
            batchChunkStatistics[chunk.Address] = chunkStatistics;
            batchStatistics.Add(chunkStatistics);
            processedStatistics.Add(chunkStatistics);
            batchChunkCount = checked(batchChunkCount + 1);
            processedChunkCount = checked(processedChunkCount + 1);
            if(batchChunkCount < batchSize) continue;

            yield return new NormalizedMinecraftChunkBatch(
                sections.ToArray(),
                batchChunkCount,
                processedChunkCount)
            {
                BatchStatistics = batchStatistics.Snapshot(),
                ProcessedStatistics = processedStatistics.Snapshot(),
                ChunkStatistics = new Dictionary<MinecraftChunkAddress, WorldPreviewStatistics>(batchChunkStatistics),
            };
            sections.Clear();
            batchChunkStatistics.Clear();
            batchStatistics.Clear();
            batchChunkCount = 0;
        }

        if(batchChunkCount > 0)
        {
            yield return new NormalizedMinecraftChunkBatch(
                sections.ToArray(),
                batchChunkCount,
                processedChunkCount)
            {
                BatchStatistics = batchStatistics.Snapshot(),
                ProcessedStatistics = processedStatistics.Snapshot(),
                ChunkStatistics = new Dictionary<MinecraftChunkAddress, WorldPreviewStatistics>(batchChunkStatistics),
            };
        }
    }
}
