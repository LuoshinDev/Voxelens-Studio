using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Performs bounded, validated random reads of inline Anvil records and external MCC payloads.
/// </summary>
public sealed class AnvilChunkReader : IMinecraftChunkReader, IAsyncDisposable
{
    private const int SectorSize = 4096;
    private const int MaximumCompressionOverheadBytes = 1024 * 1024;

    private readonly string rootPath;
    private readonly AnvilChunkIndex index;
    private readonly IReadOnlyDictionary<string, MinecraftFileFingerprint> fingerprints;
    private readonly bool forceFingerprintVerification;
    private readonly AnvilRegionReadHandlePool regionHandles = new();

    internal AnvilChunkReader(
        string rootPath,
        AnvilChunkIndex index,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> fingerprints,
        bool forceFingerprintVerification)
    {
        this.rootPath = rootPath;
        this.index = index;
        this.fingerprints = fingerprints;
        this.forceFingerprintVerification = forceFingerprintVerification;
    }

    public async ValueTask<RawMinecraftChunk> ReadAsync(
        MinecraftChunkIndexEntry entry,
        MinecraftChunkReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumDecompressedBytes <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumDecompressedBytes must be greater than zero.");
        if (!index.TryGetAuthoritativeEntry(entry, out MinecraftChunkIndexEntry authoritative))
            throw new InvalidOperationException("The chunk index entry does not belong to this mounted world.");

        bool verifyFingerprint = forceFingerprintVerification || options.VerifySourceFingerprint;
        if (verifyFingerprint && !MinecraftSourceFiles.MatchesFingerprintMetadata(
                rootPath,
                authoritative.SourceFingerprint))
        {
            throw new IOException(
                $"Region source changed after it was indexed: {authoritative.RegionRelativePath}");
        }

        string regionPath = MinecraftSourceFiles.ResolveRelativePath(rootPath, authoritative.RegionRelativePath);
        long recordOffset = checked((long)authoritative.SectorIndex * SectorSize);
        long allocationLength = checked((long)authoritative.AllocatedSectorCount * SectorSize);
        byte[] prefix = new byte[5];
        bool isExternal;
        MinecraftChunkCompression compression;
        byte[]? framedRecord = null;
        ReadOnlyMemory<byte> compressedPayload = default;

        using (AnvilRegionReadHandlePool.Lease region = await regionHandles.AcquireAsync(regionPath, cancellationToken).ConfigureAwait(false))
        {
            if (recordOffset < SectorSize * 2L || recordOffset + allocationLength > region.Length)
                throw new InvalidDataException(
                    $"Indexed allocation is outside the current region file: {authoritative.RegionRelativePath}");

            await region.ReadExactlyAsync(prefix, recordOffset, cancellationToken).ConfigureAwait(false);

            uint encodedLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(0, sizeof(uint)));
            byte compressionByte = prefix[4];
            isExternal = (compressionByte & 0x80) != 0;
            byte compressionId = (byte)(compressionByte & 0x7f);
            compression = DecodeCompression(compressionId, authoritative.Address);

            if (isExternal)
            {
                if (encodedLength != 1)
                    throw new InvalidDataException(
                        $"External chunk {authoritative.Address} must declare a one-byte inline record.");
            }
            else
            {
                if (encodedLength < 2)
                    throw new InvalidDataException(
                        $"Inline chunk {authoritative.Address} has an invalid record length of {encodedLength}.");

                long framedLength = checked((long)encodedLength + sizeof(uint));
                if (framedLength > allocationLength)
                    throw new InvalidDataException(
                        $"Inline chunk {authoritative.Address} exceeds its allocated sectors.");
                if (encodedLength > int.MaxValue)
                    throw new InvalidDataException($"Inline chunk {authoritative.Address} is too large to read safely.");

                framedRecord = GC.AllocateUninitializedArray<byte>(checked((int)framedLength));
                prefix.CopyTo(framedRecord, 0);
                int payloadLength = checked((int)encodedLength - 1);
                await region.ReadExactlyAsync(
                    framedRecord.AsMemory(prefix.Length, payloadLength),
                    recordOffset + prefix.Length,
                    cancellationToken).ConfigureAwait(false);
                compressedPayload = framedRecord.AsMemory(prefix.Length, payloadLength);
            }
        }

        if(verifyFingerprint && !MinecraftSourceFiles.MatchesFingerprintMetadata(rootPath, authoritative.SourceFingerprint))
            throw new IOException($"Region source changed during reading: {authoritative.RegionRelativePath}");

        if (isExternal)
        {
            return await ReadExternalAsync(
                authoritative,
                compression,
                options,
                verifyFingerprint,
                cancellationToken).ConfigureAwait(false);
        }

        if(framedRecord is null || compressedPayload.IsEmpty)
            throw new InvalidOperationException("Inline chunk payload was not read.");
        ReadOnlyMemory<byte> nbtPayload = options.DecompressPayload
            ? await DecompressBoundedAsync(
                compressedPayload,
                compression,
                options.MaximumDecompressedBytes,
                cancellationToken).ConfigureAwait(false)
            : compressedPayload;

        MinecraftChunkIndexEntry resolvedEntry = authoritative with
        {
            StorageKind = MinecraftChunkStorageKind.InlineRegionSector,
            ExternalPayloadRelativePath = null,
        };
        MinecraftOpaquePayload storedPayload = new(
            MinecraftOpaquePayloadFormat.StoredChunkPayload,
            framedRecord,
            authoritative.RegionRelativePath,
            null,
            Convert.ToHexString(SHA256.HashData(framedRecord)));

        return new RawMinecraftChunk(
            resolvedEntry,
            compression,
            options.DecompressPayload,
            nbtPayload,
            storedPayload);
    }

    private async ValueTask<RawMinecraftChunk> ReadExternalAsync(
        MinecraftChunkIndexEntry entry,
        MinecraftChunkCompression compression,
        MinecraftChunkReadOptions options,
        bool verifyFingerprint,
        CancellationToken cancellationToken)
    {
        string regionPath = MinecraftSourceFiles.ResolveRelativePath(rootPath, entry.RegionRelativePath);
        string externalPath = Path.Combine(
            Path.GetDirectoryName(regionPath)!,
            $"c.{entry.Address.X}.{entry.Address.Z}.mcc");
        string externalRelativePath = MinecraftSourceFiles.NormalizeRelativePath(rootPath, externalPath);

        if (!fingerprints.TryGetValue(externalRelativePath, out MinecraftFileFingerprint? externalFingerprint))
            throw new IOException(
                $"External chunk payload was not present when the world was mounted: {externalRelativePath}");
        if (verifyFingerprint && !MinecraftSourceFiles.MatchesFingerprintMetadata(
                rootPath,
                externalFingerprint))
        {
            throw new IOException($"External chunk source changed after mount: {externalRelativePath}");
        }

        int maximumStoredBytes;
        try
        {
            maximumStoredBytes = checked(options.MaximumDecompressedBytes + MaximumCompressionOverheadBytes);
        }
        catch (OverflowException)
        {
            maximumStoredBytes = int.MaxValue;
        }

        byte[] compressedPayload = await MinecraftSourceFiles.ReadAllBoundedAsync(
            externalPath,
            maximumStoredBytes,
            cancellationToken).ConfigureAwait(false);
        if (compressedPayload.Length == 0)
            throw new InvalidDataException($"External chunk payload is empty: {externalRelativePath}");
        if(verifyFingerprint && !MinecraftSourceFiles.MatchesFingerprintMetadata(rootPath, externalFingerprint))
            throw new IOException($"External chunk source changed during reading: {externalRelativePath}");

        ReadOnlyMemory<byte> nbtPayload = options.DecompressPayload
            ? await DecompressBoundedAsync(
                compressedPayload,
                compression,
                options.MaximumDecompressedBytes,
                cancellationToken).ConfigureAwait(false)
            : compressedPayload;
        MinecraftChunkIndexEntry resolvedEntry = entry with
        {
            StorageKind = MinecraftChunkStorageKind.ExternalMcc,
            ExternalPayloadRelativePath = externalRelativePath,
        };
        MinecraftOpaquePayload storedPayload = new(
            MinecraftOpaquePayloadFormat.StoredChunkPayload,
            compressedPayload,
            externalRelativePath,
            null,
            Convert.ToHexString(SHA256.HashData(compressedPayload)));

        return new RawMinecraftChunk(
            resolvedEntry,
            compression,
            options.DecompressPayload,
            nbtPayload,
            storedPayload);
    }

    private static MinecraftChunkCompression DecodeCompression(
        byte compressionId,
        MinecraftChunkAddress address) => compressionId switch
        {
            1 => MinecraftChunkCompression.GZip,
            2 => MinecraftChunkCompression.ZLib,
            3 => MinecraftChunkCompression.None,
            4 => MinecraftChunkCompression.Lz4,
            _ => throw new NotSupportedException(
                $"Chunk {address} uses unsupported Anvil compression type {compressionId}."),
        };

    private static async ValueTask<ReadOnlyMemory<byte>> DecompressBoundedAsync(
        ReadOnlyMemory<byte> payload,
        MinecraftChunkCompression compression,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (compression == MinecraftChunkCompression.None)
        {
            if (payload.Length > maximumBytes)
                throw new InvalidDataException(
                    $"Uncompressed chunk exceeds the safe {maximumBytes:N0}-byte limit.");
            return payload;
        }
        if(compression == MinecraftChunkCompression.Lz4)
            return MinecraftLz4BlockDecoder.Decode(payload.Span, maximumBytes, cancellationToken);

        if(!MemoryMarshal.TryGetArray(payload, out ArraySegment<byte> segment))
            throw new InvalidOperationException("Chunk input must use an owned byte buffer.");
        using MemoryStream source = new(segment.Array!, segment.Offset, segment.Count, writable: false);
        using Stream decoder = compression switch
        {
            MinecraftChunkCompression.GZip => new GZipStream(source, CompressionMode.Decompress),
            MinecraftChunkCompression.ZLib => new ZLibStream(source, CompressionMode.Decompress),
            _ => throw new NotSupportedException($"Unsupported chunk compression: {compression}"),
        };
        using MemoryStream destination = new(Math.Min(maximumBytes, 256 * 1024));
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            int read = await decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > maximumBytes)
                throw new InvalidDataException(
                    $"Decompressed chunk exceeds the safe {maximumBytes:N0}-byte limit.");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    public ValueTask DisposeAsync() => regionHandles.DisposeAsync();
}
