using System.Buffers.Binary;

namespace ZhuJieJing.Minecraft;

internal sealed record LegacyChunkNbtData(
    int ChunkX,
    int ChunkZ,
    IReadOnlyList<LegacySectionNbtData> Sections,
    IReadOnlyList<LegacyBlockEntityNbtData> BlockEntities);

internal sealed record LegacySectionNbtData(
    int Y,
    byte[] Blocks,
    byte[] Data,
    byte[]? Add,
    byte[]? BlockLight,
    byte[]? SkyLight);

internal sealed record LegacyBlockEntityNbtData(
    string? TypeId,
    int? X,
    int? Y,
    int? Z,
    byte[] CompoundPayload,
    string NbtPath);

/// <summary>
/// Narrow, allocation-bounded reader for the 1.12.2 chunk fields needed by normalization. Unknown fields are
/// validated and skipped; their bytes remain available through the whole-chunk opaque payload.
/// </summary>
internal sealed class LegacyChunkNbtReader
{
    private const int MaximumDepth = 64;
    private const int MaximumGeneralCollectionElements = 4 * 1024 * 1024;
    private const int MaximumSections = 256;
    private const int MaximumBlockEntities = 65_536;

    private readonly byte[] source;
    private int position;

    public LegacyChunkNbtReader(byte[] source)
    {
        this.source = source;
        position = 0;
    }

    public LegacyChunkNbtData Read()
    {
        byte rootType = ReadByte("$");
        if (rootType != 10)
            throw Error("$", $"根标签必须是 TAG_Compound，实际类型为 {rootType}");
        _ = ReadModifiedUtf8("$<name>");

        LegacyChunkNbtData? level = null;
        ReadNamedCompound(
            "$",
            depth: 0,
            (tagType, name, path, depth) =>
            {
                if (!name.Equals("Level", StringComparison.Ordinal))
                    return false;
                RequireType(tagType, 10, path);
                if (level is not null)
                    throw Error(path, "出现重复的 Level compound");
                level = ReadLevel(path, depth);
                return true;
            });

        if (position != source.Length)
            throw Error("$", "根标签结束后仍有多余字节");
        return level ?? throw Error("$.Level", "缺少 1.12.2 Level compound");
    }

    private LegacyChunkNbtData ReadLevel(string path, int depth)
    {
        int? chunkX = null;
        int? chunkZ = null;
        List<LegacySectionNbtData>? sections = null;
        List<LegacyBlockEntityNbtData> blockEntities = new();
        bool sawBlockEntities = false;

        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, childDepth) =>
            {
                switch (name)
                {
                    case "xPos":
                        RequireType(tagType, 3, childPath);
                        if (chunkX is not null)
                            throw Error(childPath, "出现重复的 xPos");
                        chunkX = ReadInt32(childPath);
                        return true;
                    case "zPos":
                        RequireType(tagType, 3, childPath);
                        if (chunkZ is not null)
                            throw Error(childPath, "出现重复的 zPos");
                        chunkZ = ReadInt32(childPath);
                        return true;
                    case "Sections":
                        RequireType(tagType, 9, childPath);
                        if (sections is not null)
                            throw Error(childPath, "出现重复的 Sections");
                        sections = ReadSections(childPath, childDepth);
                        return true;
                    case "TileEntities":
                        RequireType(tagType, 9, childPath);
                        if (sawBlockEntities)
                            throw Error(childPath, "出现重复的 TileEntities");
                        sawBlockEntities = true;
                        blockEntities.AddRange(ReadBlockEntities(childPath, childDepth));
                        return true;
                    default:
                        if(name.Equals("Sections", StringComparison.OrdinalIgnoreCase))
                            throw Error(childPath, "1.12.2 Sections 标签大小写异常，拒绝将可能存在的地形误判为空区块");
                        return false;
                }
            });

        if (chunkX is null)
            throw Error($"{path}.xPos", "缺少区块 X 坐标");
        if (chunkZ is null)
            throw Error($"{path}.zPos", "缺少区块 Z 坐标");
        return new LegacyChunkNbtData(
            chunkX.Value,
            chunkZ.Value,
            sections is null ? Array.Empty<LegacySectionNbtData>() : sections,
            blockEntities);
    }

    private List<LegacySectionNbtData> ReadSections(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path, MaximumSections);
        if (elementType > 12)
            throw Error(path, $"Sections 使用未知元素类型 {elementType}");
        if (count > 0 && elementType != 10)
            throw Error(path, $"Sections 元素必须是 TAG_Compound，实际类型为 {elementType}");

        List<LegacySectionNbtData> sections = new(count);
        HashSet<int> sectionYs = new();
        for (int index = 0; index < count; index++)
        {
            LegacySectionNbtData section = ReadSection($"{path}[{index}]", depth + 1);
            if (!sectionYs.Add(section.Y))
                throw Error($"{path}[{index}].Y", $"Section Y={section.Y} 重复");
            sections.Add(section);
        }

        return sections;
    }

    private LegacySectionNbtData ReadSection(string path, int depth)
    {
        int? sectionY = null;
        byte[]? blocks = null;
        byte[]? data = null;
        byte[]? add = null;
        byte[]? blockLight = null;
        byte[]? skyLight = null;
        bool sawAdd = false;
        bool sawBlockLight = false;
        bool sawSkyLight = false;

        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, _) =>
            {
                switch (name)
                {
                    case "Y":
                        RequireType(tagType, 1, childPath);
                        if (sectionY is not null)
                            throw Error(childPath, "出现重复的 Section Y");
                        sectionY = unchecked((sbyte)ReadByte(childPath));
                        return true;
                    case "Blocks":
                        RequireType(tagType, 7, childPath);
                        if (blocks is not null)
                            throw Error(childPath, "出现重复的 Blocks");
                        blocks = ReadExactByteArray(childPath, 4096);
                        return true;
                    case "Data":
                        RequireType(tagType, 7, childPath);
                        if (data is not null)
                            throw Error(childPath, "出现重复的 Data");
                        data = ReadExactByteArray(childPath, 2048);
                        return true;
                    case "Add":
                        RequireType(tagType, 7, childPath);
                        if (sawAdd)
                            throw Error(childPath, "出现重复的 Add");
                        sawAdd = true;
                        add = ReadExactByteArray(childPath, 2048);
                        return true;
                    case "BlockLight":
                        RequireType(tagType, 7, childPath);
                        if (sawBlockLight)
                            throw Error(childPath, "出现重复的 BlockLight");
                        sawBlockLight = true;
                        blockLight = ReadExactByteArray(childPath, 2048);
                        return true;
                    case "SkyLight":
                        RequireType(tagType, 7, childPath);
                        if (sawSkyLight)
                            throw Error(childPath, "出现重复的 SkyLight");
                        sawSkyLight = true;
                        skyLight = ReadExactByteArray(childPath, 2048);
                        return true;
                    default:
                        return false;
                }
            });

        if (sectionY is null)
            throw Error($"{path}.Y", "缺少 Section Y");
        if (blocks is null)
            throw Error($"{path}.Blocks", "缺少长度为 4096 的 Blocks");
        if (data is null)
            throw Error($"{path}.Data", "缺少长度为 2048 的 Data");
        return new LegacySectionNbtData(sectionY.Value, blocks, data, add, blockLight, skyLight);
    }

    private IReadOnlyList<LegacyBlockEntityNbtData> ReadBlockEntities(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path, MaximumBlockEntities);
        if (elementType > 12)
            throw Error(path, $"TileEntities 使用未知元素类型 {elementType}");
        if (count > 0 && elementType != 10)
            throw Error(path, $"TileEntities 元素必须是 TAG_Compound，实际类型为 {elementType}");

        List<LegacyBlockEntityNbtData> entities = new(count);
        for (int index = 0; index < count; index++)
        {
            string entityPath = $"{path}[{index}]";
            int payloadStart = position;
            string? typeId = null;
            int? x = null;
            int? y = null;
            int? z = null;

            ReadNamedCompound(
                entityPath,
                depth + 1,
                (tagType, name, childPath, _) =>
                {
                    switch (name)
                    {
                        case "id" when tagType == 8:
                            if (typeId is not null)
                                throw Error(childPath, "出现重复的方块实体 id");
                            typeId = ReadModifiedUtf8(childPath);
                            return true;
                        case "x" when tagType == 3:
                            if (x is not null)
                                throw Error(childPath, "出现重复的方块实体 x");
                            x = ReadInt32(childPath);
                            return true;
                        case "y" when tagType == 3:
                            if (y is not null)
                                throw Error(childPath, "出现重复的方块实体 y");
                            y = ReadInt32(childPath);
                            return true;
                        case "z" when tagType == 3:
                            if (z is not null)
                                throw Error(childPath, "出现重复的方块实体 z");
                            z = ReadInt32(childPath);
                            return true;
                        default:
                            return false;
                    }
                });

            entities.Add(new LegacyBlockEntityNbtData(
                typeId,
                x,
                y,
                z,
                source.AsSpan(payloadStart, position - payloadStart).ToArray(),
                entityPath));
        }

        return entities;
    }

    private void ReadNamedCompound(string path, int depth, NamedTagHandler handler)
    {
        EnsureDepth(depth, path);
        while (true)
        {
            byte tagType = ReadByte(path);
            if (tagType == 0)
                return;
            string name = ReadModifiedUtf8($"{path}<name>");
            string childPath = $"{path}.{name}";
            if (!handler(tagType, name, childPath, depth + 1))
                SkipPayload(tagType, childPath, depth + 1);
        }
    }

    private void SkipPayload(byte tagType, string path, int depth)
    {
        EnsureDepth(depth, path);
        switch (tagType)
        {
            case 1:
                Skip(1, path);
                return;
            case 2:
                Skip(2, path);
                return;
            case 3:
            case 5:
                Skip(4, path);
                return;
            case 4:
            case 6:
                Skip(8, path);
                return;
            case 7:
                SkipArray(path, 1);
                return;
            case 8:
                _ = ReadModifiedUtf8(path);
                return;
            case 9:
                byte elementType = ReadByte(path);
                int count = ReadBoundedCount(path, MaximumGeneralCollectionElements);
                if (count > 0 && elementType == 0)
                    throw Error(path, "非空 TAG_List 不能使用 TAG_End 元素类型");
                if (elementType > 12)
                    throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
                string elementPath = $"{path}[]";
                for (int index = 0; index < count; index++)
                    SkipPayload(elementType, elementPath, depth + 1);
                return;
            case 10:
                ReadNamedCompound(path, depth, static (_, _, _, _) => false);
                return;
            case 11:
                SkipArray(path, 4);
                return;
            case 12:
                SkipArray(path, 8);
                return;
            default:
                throw Error(path, $"未知 NBT 标签类型 {tagType}");
        }
    }

    private byte[] ReadExactByteArray(string path, int expectedLength)
    {
        int length = ReadInt32(path);
        if (length != expectedLength)
            throw Error(path, $"数组长度必须是 {expectedLength}，实际为 {length}");
        EnsureAvailable(length, path);
        byte[] result = source.AsSpan(position, length).ToArray();
        position += length;
        return result;
    }

    private void SkipArray(string path, int elementSize)
    {
        int count = ReadBoundedCount(path, MaximumGeneralCollectionElements);
        long bytes;
        try
        {
            bytes = checked((long)count * elementSize);
        }
        catch (OverflowException exception)
        {
            throw Error(path, "数组长度溢出", exception);
        }
        if (bytes > int.MaxValue)
            throw Error(path, "数组字节长度超出安全范围");
        Skip((int)bytes, path);
    }

    private int ReadBoundedCount(string path, int maximum)
    {
        int count = ReadInt32(path);
        if (count < 0 || count > maximum)
            throw Error(path, $"集合长度 {count} 超出安全范围 0..{maximum}");
        return count;
    }

    private int ReadInt32(string path)
    {
        EnsureAvailable(sizeof(int), path);
        int value = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(position, sizeof(int)));
        position += sizeof(int);
        return value;
    }

    private byte ReadByte(string path)
    {
        EnsureAvailable(1, path);
        return source[position++];
    }

    private string ReadModifiedUtf8(string path)
    {
        EnsureAvailable(sizeof(ushort), path);
        ushort byteLength = BinaryPrimitives.ReadUInt16BigEndian(source.AsSpan(position, sizeof(ushort)));
        position += sizeof(ushort);
        EnsureAvailable(byteLength, path);
        ReadOnlySpan<byte> bytes = source.AsSpan(position, byteLength);
        position += byteLength;

        char[] characters = GC.AllocateUninitializedArray<char>(byteLength);
        int characterCount = 0;
        int index = 0;
        while (index < bytes.Length)
        {
            byte first = bytes[index++];
            if ((first & 0x80) == 0)
            {
                if (first == 0)
                    throw Error(path, "Modified UTF-8 包含原始 NUL 字节");
                characters[characterCount++] = (char)first;
                continue;
            }

            if ((first & 0xe0) == 0xc0)
            {
                if (index >= bytes.Length)
                    throw Error(path, "Modified UTF-8 双字节字符被截断");
                byte second = bytes[index++];
                if ((second & 0xc0) != 0x80)
                    throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x1f) << 6 | second & 0x3f;
                if (codeUnit != 0 && codeUnit < 0x80)
                    throw Error(path, "Modified UTF-8 包含过长双字节编码");
                characters[characterCount++] = (char)codeUnit;
                continue;
            }

            if ((first & 0xf0) == 0xe0)
            {
                if (index + 1 >= bytes.Length)
                    throw Error(path, "Modified UTF-8 三字节字符被截断");
                byte second = bytes[index++];
                byte third = bytes[index++];
                if ((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                    throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                if (codeUnit < 0x800)
                    throw Error(path, "Modified UTF-8 包含过长三字节编码");
                characters[characterCount++] = (char)codeUnit;
                continue;
            }

            throw Error(path, "Modified UTF-8 包含不受支持的四字节编码");
        }

        return new string(characters, 0, characterCount);
    }

    private void Skip(int count, string path)
    {
        EnsureAvailable(count, path);
        position += count;
    }

    private void EnsureAvailable(int count, string path)
    {
        if (count < 0 || position > source.Length - count)
            throw Error(path, "NBT 数据被截断或长度字段越界");
    }

    private static void EnsureDepth(int depth, string path)
    {
        if (depth > MaximumDepth)
            throw Error(path, $"NBT 嵌套深度超过安全上限 {MaximumDepth}");
    }

    private static void RequireType(byte actual, byte expected, string path)
    {
        if (actual != expected)
            throw Error(path, $"NBT 标签类型必须为 {expected}，实际为 {actual}");
    }

    private static InvalidDataException Error(string path, string message, Exception? inner = null) =>
        new($"{path}: {message}", inner);

    private delegate bool NamedTagHandler(byte tagType, string name, string path, int depth);
}
