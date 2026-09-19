using System.Buffers.Binary;
using K4os.Compression.LZ4;
using K4os.Hash.xxHash;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// Decodes the legacy block stream emitted by lz4-java's LZ4BlockOutputStream. Minecraft compression ID 4
/// uses this container, not the interoperable LZ4 Frame format handled by K4os.Compression.LZ4.Streams.
/// </summary>
internal static class MinecraftLz4BlockDecoder
{
    private const int HeaderLength = 21;
    private const int CompressionLevelBase = 10;
    private const byte CompressionMethodRaw = 0x10;
    private const byte CompressionMethodLz4 = 0x20;
    private const uint ChecksumSeed = 0x9747b28c;
    private const uint JavaChecksumMask = 0x0fffffff;
    private static ReadOnlySpan<byte> Magic => "LZ4Block"u8;

    public static ReadOnlyMemory<byte> Decode(
        ReadOnlySpan<byte> source,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if(maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if(source.IsEmpty)
            throw new InvalidDataException("Minecraft LZ4 block stream is empty.");

        using MemoryStream destination = new(Math.Min(maximumBytes, 256 * 1024));
        int offset = 0;
        while(true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(source.Length - offset < HeaderLength)
                throw Corrupt("stream ended before a complete block header or end marker");

            ReadOnlySpan<byte> header = source.Slice(offset, HeaderLength);
            if(!header[..Magic.Length].SequenceEqual(Magic))
                throw Corrupt("block magic is not LZ4Block");

            byte token = header[Magic.Length];
            byte compressionMethod = (byte)(token & 0xf0);
            int compressionLevel = CompressionLevelBase + (token & 0x0f);
            int maximumBlockBytes = 1 << compressionLevel;
            int compressedLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(Magic.Length + 1, 4));
            int decompressedLength = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(Magic.Length + 5, 4));
            uint expectedChecksum = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(Magic.Length + 9, 4));
            offset += HeaderLength;

            if(compressionMethod is not CompressionMethodRaw and not CompressionMethodLz4)
                throw Corrupt($"unknown block compression method 0x{compressionMethod:x2}");
            if(compressedLength < 0 || decompressedLength < 0)
                throw Corrupt("block lengths cannot be negative");
            if(decompressedLength > maximumBlockBytes)
                throw Corrupt($"block declares {decompressedLength:N0} bytes beyond token limit {maximumBlockBytes:N0}");
            if((decompressedLength == 0) != (compressedLength == 0))
                throw Corrupt("only the terminal block may have zero lengths");
            if(compressionMethod == CompressionMethodRaw && decompressedLength != compressedLength)
                throw Corrupt("raw block lengths differ");

            if(decompressedLength == 0)
            {
                if(expectedChecksum != 0)
                    throw Corrupt("terminal block checksum is not zero");
                if(offset != source.Length)
                    throw Corrupt("bytes remain after the terminal block");
                return destination.ToArray();
            }

            if(compressedLength > source.Length - offset)
                throw Corrupt("block payload is truncated");
            if(destination.Length + decompressedLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"Decompressed LZ4 chunk exceeds the safe {maximumBytes:N0}-byte limit.");
            }

            ReadOnlySpan<byte> compressed = source.Slice(offset, compressedLength);
            offset += compressedLength;
            if(compressionMethod == CompressionMethodRaw)
            {
                VerifyChecksum(compressed, expectedChecksum);
                destination.Write(compressed);
                continue;
            }

            if(compressedLength > LZ4Codec.MaximumOutputSize(decompressedLength))
                throw Corrupt("compressed block length exceeds the LZ4 bound for its declared output");
            byte[] decoded = GC.AllocateUninitializedArray<byte>(decompressedLength);
            int decodedLength = LZ4Codec.Decode(compressed, decoded);
            if(decodedLength != decompressedLength)
                throw Corrupt("raw LZ4 payload does not match its declared output length");
            VerifyChecksum(decoded, expectedChecksum);
            destination.Write(decoded);
        }
    }

    private static void VerifyChecksum(ReadOnlySpan<byte> decoded, uint expected)
    {
        XXH32.State state = default;
        XXH32.Reset(ref state, ChecksumSeed);
        XXH32.Update(ref state, decoded);
        // lz4-java writes its streaming hash through StreamingXXHash32.asChecksum(), whose
        // long-valued adapter deliberately preserves only the low 28 bits. Minecraft's
        // region compression ID 4 inherits that historical container-format behavior.
        uint actual = XXH32.Digest(in state) & JavaChecksumMask;
        if(actual != expected)
            throw Corrupt($"decoded block checksum 0x{actual:x8} does not match XXH32 0x{expected:x8}");
    }

    private static InvalidDataException Corrupt(string reason) =>
        new($"Minecraft LZ4 block stream is corrupt: {reason}.");
}
