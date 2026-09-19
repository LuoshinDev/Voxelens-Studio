namespace ZhuJieJing.Minecraft;

public readonly record struct MinecraftChunkCacheStatistics(int Count, long EstimatedBytes, long ByteBudget);

/// <summary>Optional metrics for memory-budgeted interactive chunk sources.</summary>
public interface IMinecraftChunkCacheMetrics
{
    MinecraftChunkCacheStatistics CacheStatistics { get; }
}

/// <summary>Bulk consumers can own a small working-set cache without filling the interactive source cache.</summary>
internal interface IUncachedNormalizedMinecraftChunkSource
{
    ValueTask<NormalizedMinecraftChunk?> FindUncachedAsync(MinecraftChunkAddress address, CancellationToken cancellationToken);
}

/// <summary>Completed chunks only. In-flight operations own their payload until their callers finish.</summary>
internal sealed class MinecraftChunkMemoryCache(long byteBudget)
{
    private readonly object gate = new();
    private readonly Dictionary<MinecraftChunkAddress, LinkedListNode<Entry>> entries = [];
    private readonly LinkedList<Entry> recency = new();
    private long estimatedBytes;

    internal MinecraftChunkCacheStatistics Statistics
    {
        get { lock(gate) return new(entries.Count, estimatedBytes, byteBudget); }
    }

    internal bool TryGet(MinecraftChunkAddress address, out NormalizedMinecraftChunk? chunk)
    {
        lock(gate)
        {
            if(!entries.TryGetValue(address, out LinkedListNode<Entry>? node))
            {
                chunk = null;
                return false;
            }
            recency.Remove(node);
            recency.AddFirst(node);
            chunk = node.Value.Chunk;
            return true;
        }
    }

    internal void Add(MinecraftChunkAddress address, NormalizedMinecraftChunk? chunk)
    {
        long bytes = EstimateBytes(chunk);
        lock(gate)
        {
            if(entries.Remove(address, out LinkedListNode<Entry>? old))
            {
                recency.Remove(old);
                estimatedBytes -= old.Value.Bytes;
            }
            // Oversized chunks are still returned to callers, but never retained by this cache.
            if(bytes > byteBudget) return;
            while(estimatedBytes + bytes > byteBudget && recency.Last is { } victim)
            {
                entries.Remove(victim.Value.Address);
                recency.RemoveLast();
                estimatedBytes -= victim.Value.Bytes;
            }
            LinkedListNode<Entry> added = recency.AddFirst(new Entry(address, chunk, bytes));
            entries.Add(address, added);
            estimatedBytes += bytes;
        }
    }

    internal void Clear()
    {
        lock(gate)
        {
            entries.Clear();
            recency.Clear();
            estimatedBytes = 0;
        }
    }

    internal static long EstimateBytes(NormalizedMinecraftChunk? chunk)
    {
        if(chunk is null) return 128;
        long bytes = 1024 + chunk.Passthrough.OriginalStoredPayload.Bytes.Length;
        foreach(MinecraftOpaqueFragment fragment in chunk.Passthrough.UnknownFragments)
            bytes += fragment.Payload.Bytes.Length + 256L;
        foreach(NormalizedMinecraftSection section in chunk.Sections)
        {
            bytes += 256L + section.PaletteIndices.Length * 2L;
            foreach(var state in section.Palette) bytes += 128L + state.CanonicalKey.Length * 2L;
            if(section.Lighting is { } light)
                bytes += light.SkyLightNibbles.Length + light.BlockLightNibbles.Length + light.StaticLightSourceNibbles.Length;
            foreach(MinecraftOpaqueFragment fragment in section.UnknownFragments)
                bytes += fragment.Payload.Bytes.Length + 256L;
        }
        foreach(NormalizedMinecraftBlockEntity entity in chunk.BlockEntities)
            bytes += entity.OriginalNbt.Bytes.Length + 512L;
        foreach(MinecraftDiagnostic diagnostic in chunk.Diagnostics)
            bytes += 128L + diagnostic.Message.Length * 2L;
        return bytes;
    }

    private sealed record Entry(MinecraftChunkAddress Address, NormalizedMinecraftChunk? Chunk, long Bytes);

}
