using System.Buffers.Binary;
using System.Security.Cryptography;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal sealed record LegacyBlockEntityRewriteResult(
    byte[] CompoundPayload,
    bool PreservedUnknownData,
    string? ReductionReason);

internal static class LegacyBlockEntityPayloadRewriter
{
    public static LegacyBlockEntityRewriteResult Rewrite(NormalizedMinecraftBlockEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if(string.IsNullOrWhiteSpace(entity.TypeId))
            throw new InvalidDataException("A block entity cannot be exported without a type ID.");

        MinecraftOpaquePayload original = entity.OriginalNbt;
        VerifyContentHash(original);
        if(original.Format != MinecraftOpaquePayloadFormat.EncodedNbtTag || original.Bytes.IsEmpty)
        {
            return new LegacyBlockEntityRewriteResult(
                BuildBasicPayload(entity),
                false,
                "原始方块实体不是可重写的 compound payload");
        }

        try
        {
            CompoundCoordinateRewriter rewriter = new(original.Bytes.ToArray());
            return new LegacyBlockEntityRewriteResult(
                rewriter.Rewrite(entity.TypeId, entity.Position),
                true,
                null);
        }
        catch(InvalidDataException exception)
        {
            return new LegacyBlockEntityRewriteResult(
                BuildBasicPayload(entity),
                false,
                exception.Message);
        }
    }

    private static void VerifyContentHash(MinecraftOpaquePayload payload)
    {
        if(payload.ContentHash is null)
            return;
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(payload.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException("Block-entity payload has an invalid content hash.", exception);
        }
        byte[] actual = SHA256.HashData(payload.Bytes.Span);
        if(!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("Block-entity payload no longer matches its recorded content hash.");
    }

    private static byte[] BuildBasicPayload(NormalizedMinecraftBlockEntity entity)
    {
        using MemoryStream stream = new();
        LegacyNbtWriter writer = new(stream);
        writer.WriteString("id", entity.TypeId);
        writer.WriteInt("x", entity.Position.X);
        writer.WriteInt("y", entity.Position.Y);
        writer.WriteInt("z", entity.Position.Z);
        writer.WriteCompoundEnd();
        return stream.ToArray();
    }

    private sealed class CompoundCoordinateRewriter
    {
        private const int MaximumDepth = 64;
        private const int MaximumCollectionElements = 4 * 1024 * 1024;

        private readonly byte[] source;
        private int position;

        public CompoundCoordinateRewriter(byte[] source)
        {
            this.source = source;
        }

        public byte[] Rewrite(string typeId, BlockPosition blockPosition)
        {
            using MemoryStream output = new(source.Length + 32);
            LegacyNbtWriter writer = new(output);
            bool sawId = false;
            bool sawX = false;
            bool sawY = false;
            bool sawZ = false;
            while(true)
            {
                int tagStart = position;
                byte tagType = ReadByte();
                if(tagType == 0)
                {
                    output.WriteByte(0);
                    break;
                }

                string name = ReadModifiedUtf8();
                int payloadStart = position;
                switch(name)
                {
                    case "id":
                        if(sawId)
                            throw new InvalidDataException("方块实体 compound 含重复 id。");
                        sawId = true;
                        RequireType(tagType, 8, name);
                        _ = ReadModifiedUtf8();
                        output.Write(source, tagStart, payloadStart - tagStart);
                        writer.WriteStringPayload(typeId);
                        break;
                    case "x":
                        RewriteCoordinate(output, writer, tagStart, payloadStart, tagType, blockPosition.X, ref sawX, name);
                        break;
                    case "y":
                        RewriteCoordinate(output, writer, tagStart, payloadStart, tagType, blockPosition.Y, ref sawY, name);
                        break;
                    case "z":
                        RewriteCoordinate(output, writer, tagStart, payloadStart, tagType, blockPosition.Z, ref sawZ, name);
                        break;
                    default:
                        SkipPayload(tagType, depth: 1);
                        output.Write(source, tagStart, position - tagStart);
                        break;
                }
            }

            if(position != source.Length)
                throw new InvalidDataException("方块实体 compound 的 TAG_End 后仍有多余字节。");
            if(!sawId || !sawX || !sawY || !sawZ)
                throw new InvalidDataException("方块实体 compound 缺少 id/x/y/z，无法安全保留未知字段。");
            return output.ToArray();
        }

        private void RewriteCoordinate(
            Stream output,
            LegacyNbtWriter writer,
            int tagStart,
            int payloadStart,
            byte tagType,
            int value,
            ref bool saw,
            string name)
        {
            if(saw)
                throw new InvalidDataException($"方块实体 compound 含重复 {name}。");
            saw = true;
            RequireType(tagType, 3, name);
            EnsureAvailable(sizeof(int));
            position += sizeof(int);
            output.Write(source, tagStart, payloadStart - tagStart);
            writer.WriteIntPayload(value);
        }

        private void SkipPayload(byte tagType, int depth)
        {
            EnsureDepth(depth);
            switch(tagType)
            {
                case 1:
                    Skip(1);
                    return;
                case 2:
                    Skip(2);
                    return;
                case 3:
                case 5:
                    Skip(4);
                    return;
                case 4:
                case 6:
                    Skip(8);
                    return;
                case 7:
                    SkipArray(1);
                    return;
                case 8:
                    _ = ReadModifiedUtf8();
                    return;
                case 9:
                {
                    byte elementType = ReadByte();
                    int count = ReadCount();
                    if(count > 0 && elementType == 0)
                        throw new InvalidDataException("非空 NBT list 使用 TAG_End 元素类型。");
                    if(elementType > 12)
                        throw new InvalidDataException($"NBT list 使用未知元素类型 {elementType}。");
                    for(int index = 0; index < count; index++)
                        SkipPayload(elementType, depth + 1);
                    return;
                }
                case 10:
                    while(true)
                    {
                        byte nestedType = ReadByte();
                        if(nestedType == 0)
                            return;
                        _ = ReadModifiedUtf8();
                        SkipPayload(nestedType, depth + 1);
                    }
                case 11:
                    SkipArray(4);
                    return;
                case 12:
                    SkipArray(8);
                    return;
                default:
                    throw new InvalidDataException($"NBT 包含未知标签类型 {tagType}。");
            }
        }

        private void SkipArray(int elementSize)
        {
            int count = ReadCount();
            long byteCount = checked((long)count * elementSize);
            if(byteCount > int.MaxValue)
                throw new InvalidDataException("NBT array 超出安全字节范围。");
            Skip((int)byteCount);
        }

        private int ReadCount()
        {
            int count = ReadInt32();
            if(count < 0 || count > MaximumCollectionElements)
                throw new InvalidDataException($"NBT collection 长度 {count} 超出安全范围。");
            return count;
        }

        private int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            int value = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(position, sizeof(int)));
            position += sizeof(int);
            return value;
        }

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return source[position++];
        }

        private string ReadModifiedUtf8()
        {
            EnsureAvailable(sizeof(ushort));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(position, sizeof(ushort)));
            position += sizeof(ushort);
            EnsureAvailable(length);
            ReadOnlySpan<byte> bytes = source.AsSpan(position, length);
            position += length;

            char[] characters = GC.AllocateUninitializedArray<char>(length);
            int written = 0;
            int index = 0;
            while(index < bytes.Length)
            {
                byte first = bytes[index++];
                if((first & 0x80) == 0)
                {
                    if(first == 0)
                        throw new InvalidDataException("Modified UTF-8 包含原始 NUL。");
                    characters[written++] = (char)first;
                    continue;
                }
                if((first & 0xe0) == 0xc0)
                {
                    if(index >= bytes.Length)
                        throw new InvalidDataException("Modified UTF-8 双字节字符被截断。");
                    byte second = bytes[index++];
                    if((second & 0xc0) != 0x80)
                        throw new InvalidDataException("Modified UTF-8 续字节无效。");
                    int codeUnit = (first & 0x1f) << 6 | second & 0x3f;
                    if(codeUnit != 0 && codeUnit < 0x80)
                        throw new InvalidDataException("Modified UTF-8 包含过长编码。");
                    characters[written++] = (char)codeUnit;
                    continue;
                }
                if((first & 0xf0) == 0xe0)
                {
                    if(index + 1 >= bytes.Length)
                        throw new InvalidDataException("Modified UTF-8 三字节字符被截断。");
                    byte second = bytes[index++];
                    byte third = bytes[index++];
                    if((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                        throw new InvalidDataException("Modified UTF-8 续字节无效。");
                    int codeUnit = (first & 15) << 12 | (second & 63) << 6 | third & 63;
                    if(codeUnit < 0x800)
                        throw new InvalidDataException("Modified UTF-8 包含过长编码。");
                    characters[written++] = (char)codeUnit;
                    continue;
                }
                throw new InvalidDataException("Modified UTF-8 包含不支持的四字节编码。");
            }
            return new string(characters, 0, written);
        }

        private void Skip(int count)
        {
            EnsureAvailable(count);
            position += count;
        }

        private void EnsureAvailable(int count)
        {
            if(count < 0 || position > source.Length - count)
                throw new InvalidDataException("方块实体 NBT 被截断或长度越界。");
        }

        private static void EnsureDepth(int depth)
        {
            if(depth > MaximumDepth)
                throw new InvalidDataException($"方块实体 NBT 深度超过 {MaximumDepth}。");
        }

        private static void RequireType(byte actual, byte expected, string name)
        {
            if(actual != expected)
                throw new InvalidDataException($"方块实体 {name} 必须为 TAG_{expected}，实际为 {actual}。");
        }
    }
}
