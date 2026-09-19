namespace ZhuJieJing.Minecraft;

/// <summary>Bounded read-only region handles; offset reads allow concurrent chunks without sharing a stream cursor.</summary>
internal sealed class AnvilRegionReadHandlePool : IAsyncDisposable
{
    private const int Capacity = 8;
    private readonly object sync = new();
    private readonly SemaphoreSlim leases = new(Capacity, Capacity);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private long sequence;
    private bool disposed;

    public async ValueTask<Lease> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        await leases.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock(sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if(!entries.TryGetValue(path, out Entry? entry))
                {
                    if(entries.Count == Capacity)
                    {
                        KeyValuePair<string, Entry> oldest = entries.Where(pair => pair.Value.Users == 0)
                            .MinBy(pair => pair.Value.LastUse);
                        entries.Remove(oldest.Key);
                        oldest.Value.Stream.Dispose();
                    }
                    entry = new Entry(MinecraftSourceFiles.OpenRead(path, FileOptions.Asynchronous | FileOptions.RandomAccess));
                    entries.Add(path, entry);
                }
                entry.Users++;
                entry.LastUse = ++sequence;
                return new Lease(this, entry);
            }
        }
        catch
        {
            leases.Release();
            throw;
        }
    }

    private void Release(Entry entry)
    {
        lock(sync)
        {
            entry.Users--;
            entry.LastUse = ++sequence;
            if(disposed && entry.Users == 0) entry.Stream.Dispose();
        }
        leases.Release();
    }

    public ValueTask DisposeAsync()
    {
        lock(sync)
        {
            if(disposed) return ValueTask.CompletedTask;
            disposed = true;
            foreach(Entry entry in entries.Values)
                if(entry.Users == 0) entry.Stream.Dispose();
            entries.Clear();
        }
        // Active leases close their handles on return. The semaphore also wakes queued readers,
        // which observe disposal instead of hanging or racing a disposed semaphore.
        return ValueTask.CompletedTask;
    }

    internal sealed class Entry(FileStream stream)
    {
        public FileStream Stream { get; } = stream;
        public int Users { get; set; }
        public long LastUse { get; set; }
    }

    internal sealed class Lease(AnvilRegionReadHandlePool owner, Entry entry) : IDisposable
    {
        private AnvilRegionReadHandlePool? owner = owner;
        public long Length => RandomAccess.GetLength(entry.Stream.SafeFileHandle);

        public async ValueTask ReadExactlyAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken)
        {
            while(!buffer.IsEmpty)
            {
                int count = await RandomAccess.ReadAsync(entry.Stream.SafeFileHandle, buffer, offset, cancellationToken)
                    .ConfigureAwait(false);
                if(count == 0) throw new EndOfStreamException("Region record was truncated during reading.");
                offset = checked(offset + count);
                buffer = buffer[count..];
            }
        }

        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(entry);
    }
}
