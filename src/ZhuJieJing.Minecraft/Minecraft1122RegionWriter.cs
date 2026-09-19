using System.Buffers.Binary;

namespace ZhuJieJing.Minecraft;

internal readonly record struct MinecraftRegionAppendResult(bool CreatedRegion, int RecordBytes);

/// <summary>Eight open region handles at most; headers and disk flushes are amortized per region.</summary>
internal sealed class Minecraft1122RegionWriter : IAsyncDisposable
{
    private const int Sector = 4096;
    private const int HeaderBytes = 8192;
    private const int MaximumOpenRegions = 8;
    private readonly Dictionary<string, LinkedListNode<Writer>> writers = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Writer> recency = new();

    internal async ValueTask<MinecraftRegionAppendResult> AppendAsync(string path, MinecraftChunkAddress address,
        ReadOnlyMemory<byte> compressed, CancellationToken cancellationToken)
    {
        bool created = false;
        if(!writers.TryGetValue(path, out LinkedListNode<Writer>? node))
        {
            created = !File.Exists(path);
            if(writers.Count >= MaximumOpenRegions)
            {
                LinkedListNode<Writer> victim = recency.Last!;
                await CloseAsync(victim.Value, cancellationToken).ConfigureAwait(false);
                writers.Remove(victim.Value.Path);
                recency.RemoveLast();
            }
            FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            try
            {
                byte[] header = new byte[HeaderBytes];
                if(stream.Length == 0) stream.SetLength(HeaderBytes);
                else
                {
                    if(stream.Length < HeaderBytes || stream.Length % Sector != 0)
                        throw new InvalidDataException($"Staged region file has an invalid length: {path}");
                    await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                }
                node = recency.AddFirst(new Writer(path, stream, header));
                writers.Add(path, node);
            }
            catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
        }
        else
        {
            recency.Remove(node);
            recency.AddFirst(node);
        }

        Writer writer = node.Value;
        int local = (address.X & 31) | (address.Z & 31) << 5;
        int locationOffset = local * 4;
        if(BinaryPrimitives.ReadInt32BigEndian(writer.Header.AsSpan(locationOffset, 4)) != 0)
            throw new InvalidDataException($"Duplicate chunk address encountered during export: {address}");
        int length = checked(compressed.Length + 1);
        int rawBytes = checked(length + 4);
        int sectors = checked((rawBytes + Sector - 1) / Sector);
        if(sectors > 255) throw new InvalidDataException($"Chunk {address} exceeds the 255-sector 1.12.2 limit.");
        long sectorIndex = writer.Stream.Length / Sector;
        if(sectorIndex > 0x00ff_ffff) throw new InvalidDataException($"Region sector offset exceeds the MCA limit: {path}");
        writer.Stream.Position = writer.Stream.Length;
        byte[] prefix = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(prefix, length);
        prefix[4] = (byte)MinecraftChunkCompression.ZLib;
        await writer.Stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await writer.Stream.WriteAsync(compressed, cancellationToken).ConfigureAwait(false);
        int padding = sectors * Sector - rawBytes;
        if(padding > 0) await writer.Stream.WriteAsync(new byte[padding], cancellationToken).ConfigureAwait(false);
        BinaryPrimitives.WriteUInt32BigEndian(writer.Header.AsSpan(locationOffset, 4), checked((uint)(sectorIndex << 8)) | (uint)sectors);
        BinaryPrimitives.WriteUInt32BigEndian(writer.Header.AsSpan(Sector + locationOffset, 4),
            (uint)Math.Clamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0L, uint.MaxValue));
        return new MinecraftRegionAppendResult(created, sectors * Sector);
    }

    internal async ValueTask FlushAndCloseAsync(CancellationToken cancellationToken)
    {
        while(recency.First is { } node)
        {
            await CloseAsync(node.Value, cancellationToken).ConfigureAwait(false);
            recency.RemoveFirst();
            writers.Remove(node.Value.Path);
        }
    }

    private static async ValueTask CloseAsync(Writer writer, CancellationToken cancellationToken)
    {
        try
        {
            writer.Stream.Position = 0;
            await writer.Stream.WriteAsync(writer.Header, cancellationToken).ConfigureAwait(false);
            await writer.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            writer.Stream.Flush(flushToDisk: true);
        }
        finally { await writer.Stream.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        // Failure/cancellation never publishes this staging directory; close handles without more writes.
        foreach(Writer writer in recency) await writer.Stream.DisposeAsync().ConfigureAwait(false);
        recency.Clear();
        writers.Clear();
    }

    private sealed record Writer(string Path, FileStream Stream, byte[] Header);

}
