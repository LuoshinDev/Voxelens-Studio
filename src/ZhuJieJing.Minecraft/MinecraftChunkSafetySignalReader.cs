using System.Buffers.Binary;

namespace ZhuJieJing.Minecraft;

internal readonly record struct MinecraftChunkSafetySignals(
    bool HasActivity,
    bool HasUnknownDangerousMetadata);

/// <summary>
/// Bounded NBT safety pass used only after canonical content proved to be all air. It deliberately understands
/// a narrow set of passive chunk metadata; any other root/chunk/section field makes the chunk non-removable.
/// </summary>
internal ref struct MinecraftChunkSafetySignalReader
{
    private const int MaximumDepth = 64;
    private const int MaximumCollectionElements = 4 * 1024 * 1024;

    private readonly ReadOnlySpan<byte> source;
    private int position;
    private bool hasActivity;
    private bool hasUnknownDangerousMetadata;

    private MinecraftChunkSafetySignalReader(ReadOnlySpan<byte> source)
    {
        this.source = source;
    }

    public static MinecraftChunkSafetySignals Read(ReadOnlySpan<byte> source)
    {
        MinecraftChunkSafetySignalReader reader = new(source);
        return reader.ReadDocument();
    }

    private MinecraftChunkSafetySignals ReadDocument()
    {
        byte rootType = ReadByte("$");
        if(rootType != 10)
            throw Error("$", $"根标签必须是 TAG_Compound，实际类型为 {rootType}");
        _ = ReadModifiedUtf8("$<name>");
        ReadNamedCompound(ChunkContainerKind.Root, "$", 0);
        if(position != source.Length)
            throw Error("$", "根标签结束后仍有多余字节");
        return new MinecraftChunkSafetySignals(hasActivity, hasUnknownDangerousMetadata);
    }

    private void ReadNamedCompound(ChunkContainerKind kind, string path, int depth)
    {
        EnsureDepth(depth, path);
        while(true)
        {
            byte tagType = ReadByte(path);
            if(tagType == 0) return;
            string name = ReadModifiedUtf8($"{path}<name>");
            string childPath = $"{path}.{name}";
            switch(kind)
            {
                case ChunkContainerKind.Root:
                    ReadRootTag(tagType, name, childPath, depth + 1);
                    break;
                case ChunkContainerKind.Chunk:
                    ReadChunkTag(tagType, name, childPath, depth + 1);
                    break;
                case ChunkContainerKind.Section:
                    ReadSectionTag(tagType, name, childPath, depth + 1);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }

    private void ReadRootTag(byte tagType, string name, string path, int depth)
    {
        if(name == "Level")
        {
            if(tagType != 10)
            {
                hasUnknownDangerousMetadata = true;
                SkipPayload(tagType, path, depth);
                return;
            }
            ReadNamedCompound(ChunkContainerKind.Chunk, path, depth);
            return;
        }
        if(name == "DataVersion")
        {
            SkipExpected(tagType, 3, path, depth);
            return;
        }

        // Since 1.18, the chunk container is the root compound itself.
        ReadChunkTag(tagType, name, path, depth);
    }

    private void ReadChunkTag(byte tagType, string name, string path, int depth)
    {
        switch(name)
        {
            case "DataVersion":
            case "xPos":
            case "zPos":
            case "yPos":
                SkipExpected(tagType, 3, path, depth);
                return;
            case "LastUpdate":
                SkipExpected(tagType, 4, path, depth);
                return;
            case "InhabitedTime":
                ReadInhabitedTime(tagType, path, depth);
                return;
            case "Status":
                SkipExpected(tagType, 8, path, depth);
                return;
            case "V":
            case "TerrainPopulated":
            case "LightPopulated":
            case "isLightOn":
                SkipExpected(tagType, 1, path, depth);
                return;
            case "HeightMap":
                SkipExpected(tagType, 11, path, depth);
                return;
            case "Biomes":
                if(tagType is not 7 and not 11)
                    hasUnknownDangerousMetadata = true;
                SkipPayload(tagType, path, depth);
                return;
            case "Heightmaps":
            case "CarvingMasks":
                SkipExpected(tagType, 10, path, depth);
                return;
            case "Sections":
            case "sections":
                ReadSections(tagType, path, depth);
                return;
            case "Entities":
            case "TileEntities":
            case "block_entities":
            case "TileTicks":
            case "LiquidTicks":
            case "block_ticks":
            case "fluid_ticks":
            case "block_events":
                ReadActiveList(tagType, path, depth);
                return;
            case "PostProcessing":
            case "Lights":
            case "ToBeTicked":
            case "LiquidsToBeTicked":
                ReadNestedActivityList(tagType, path, depth);
                return;
            case "Structures":
            case "structures":
                ReadStructures(tagType, path, depth);
                return;
            case "UpgradeData":
            case "below_zero_retrogen":
            case "blending_data":
                hasActivity = true;
                SkipPayload(tagType, path, depth);
                return;
            default:
                hasUnknownDangerousMetadata = true;
                SkipPayload(tagType, path, depth);
                return;
        }
    }

    private void ReadSectionTag(byte tagType, string name, string path, int depth)
    {
        switch(name)
        {
            case "Y":
                if(tagType is not 1 and not 2 and not 3)
                    hasUnknownDangerousMetadata = true;
                SkipPayload(tagType, path, depth);
                return;
            case "Blocks":
            case "Data":
            case "Add":
            case "BlockLight":
            case "SkyLight":
                SkipExpected(tagType, 7, path, depth);
                return;
            case "Palette":
                SkipExpected(tagType, 9, path, depth);
                return;
            case "BlockStates":
                SkipExpected(tagType, 12, path, depth);
                return;
            case "block_states":
            case "biomes":
                SkipExpected(tagType, 10, path, depth);
                return;
            default:
                hasUnknownDangerousMetadata = true;
                SkipPayload(tagType, path, depth);
                return;
        }
    }

    private void ReadInhabitedTime(byte tagType, string path, int depth)
    {
        if(tagType == 4)
        {
            if(ReadInt64(path) != 0) hasActivity = true;
            return;
        }
        if(tagType == 3)
        {
            if(ReadInt32(path) != 0) hasActivity = true;
            return;
        }
        hasUnknownDangerousMetadata = true;
        SkipPayload(tagType, path, depth);
    }

    private void ReadSections(byte tagType, string path, int depth)
    {
        if(tagType != 9)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path);
        if(elementType != 10 && !(count == 0 && elementType == 0))
            throw Error(path, $"Section 列表元素必须是 TAG_Compound，实际类型为 {elementType}");
        for(int index = 0; index < count; index++)
            ReadNamedCompound(ChunkContainerKind.Section, $"{path}[{index}]", depth + 1);
    }

    private void ReadActiveList(byte tagType, string path, int depth)
    {
        if(tagType != 9)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path);
        if(count > 0) hasActivity = true;
        if(count > 0 && elementType == 0)
            throw Error(path, "非空 TAG_List 不能使用 TAG_End 元素类型");
        if(elementType > 12)
            throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
        for(int index = 0; index < count; index++)
            SkipPayload(elementType, $"{path}[]", depth + 1);
    }

    private void ReadNestedActivityList(byte tagType, string path, int depth)
    {
        if(tagType != 9)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        if(ReadListContainsData(path, depth)) hasActivity = true;
    }

    private bool ReadListContainsData(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path);
        if(count > 0 && elementType == 0)
            throw Error(path, "非空 TAG_List 不能使用 TAG_End 元素类型");
        if(elementType > 12)
            throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
        bool containsData = false;
        for(int index = 0; index < count; index++)
        {
            string childPath = $"{path}[{index}]";
            if(elementType == 9)
            {
                containsData |= ReadListContainsData(childPath, depth + 1);
            }
            else
            {
                containsData = true;
                SkipPayload(elementType, childPath, depth + 1);
            }
        }
        return containsData;
    }

    private void ReadStructures(byte tagType, string path, int depth)
    {
        if(tagType != 10)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        EnsureDepth(depth, path);
        while(true)
        {
            byte childType = ReadByte(path);
            if(childType == 0) return;
            string name = ReadModifiedUtf8($"{path}<name>");
            string childPath = $"{path}.{name}";
            if(name is "Starts" or "starts")
            {
                ReadStructureStarts(childType, childPath, depth + 1);
            }
            else if(name is "References" or "references")
            {
                ReadStructureReferences(childType, childPath, depth + 1);
            }
            else
            {
                hasUnknownDangerousMetadata = true;
                SkipPayload(childType, childPath, depth + 1);
            }
        }
    }

    private void ReadStructureStarts(byte tagType, string path, int depth)
    {
        if(tagType != 10)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        EnsureDepth(depth, path);
        while(true)
        {
            byte childType = ReadByte(path);
            if(childType == 0) return;
            _ = ReadModifiedUtf8($"{path}<name>");
            hasActivity = true;
            SkipPayload(childType, $"{path}[]", depth + 1);
        }
    }

    private void ReadStructureReferences(byte tagType, string path, int depth)
    {
        if(tagType != 10)
        {
            hasUnknownDangerousMetadata = true;
            SkipPayload(tagType, path, depth);
            return;
        }
        EnsureDepth(depth, path);
        while(true)
        {
            byte childType = ReadByte(path);
            if(childType == 0) return;
            _ = ReadModifiedUtf8($"{path}<name>");
            string childPath = $"{path}[]";
            if(childType != 12)
            {
                hasUnknownDangerousMetadata = true;
                SkipPayload(childType, childPath, depth + 1);
                continue;
            }
            int count = ReadBoundedCount(childPath);
            if(count > 0) hasActivity = true;
            SkipArrayBytes(count, sizeof(long), childPath);
        }
    }

    private void SkipExpected(byte actualType, byte expectedType, string path, int depth)
    {
        if(actualType != expectedType) hasUnknownDangerousMetadata = true;
        SkipPayload(actualType, path, depth);
    }

    private void SkipPayload(byte tagType, string path, int depth)
    {
        EnsureDepth(depth, path);
        switch(tagType)
        {
            case 0:
                return;
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
            {
                byte elementType = ReadByte(path);
                int count = ReadBoundedCount(path);
                if(count > 0 && elementType == 0)
                    throw Error(path, "非空 TAG_List 不能使用 TAG_End 元素类型");
                if(elementType > 12)
                    throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
                for(int index = 0; index < count; index++)
                    SkipPayload(elementType, $"{path}[]", depth + 1);
                return;
            }
            case 10:
                SkipCompound(path, depth);
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

    private void SkipCompound(string path, int depth)
    {
        EnsureDepth(depth, path);
        while(true)
        {
            byte type = ReadByte(path);
            if(type == 0) return;
            string name = ReadModifiedUtf8($"{path}<name>");
            SkipPayload(type, $"{path}.{name}", depth + 1);
        }
    }

    private void SkipArray(string path, int elementSize)
    {
        int count = ReadBoundedCount(path);
        SkipArrayBytes(count, elementSize, path);
    }

    private void SkipArrayBytes(int count, int elementSize, string path)
    {
        long bytes;
        try
        {
            bytes = checked((long)count * elementSize);
        }
        catch(OverflowException exception)
        {
            throw Error(path, "数组长度溢出", exception);
        }
        if(bytes > int.MaxValue)
            throw Error(path, "数组字节长度超出安全范围");
        Skip((int)bytes, path);
    }

    private int ReadBoundedCount(string path)
    {
        int count = ReadInt32(path);
        if(count is < 0 or > MaximumCollectionElements)
            throw Error(path, $"集合长度 {count} 超出安全范围 0..{MaximumCollectionElements}");
        return count;
    }

    private long ReadInt64(string path)
    {
        EnsureAvailable(sizeof(long), path);
        long result = BinaryPrimitives.ReadInt64BigEndian(source.Slice(position, sizeof(long)));
        position += sizeof(long);
        return result;
    }

    private int ReadInt32(string path)
    {
        EnsureAvailable(sizeof(int), path);
        int result = BinaryPrimitives.ReadInt32BigEndian(source.Slice(position, sizeof(int)));
        position += sizeof(int);
        return result;
    }

    private byte ReadByte(string path)
    {
        EnsureAvailable(1, path);
        return source[position++];
    }

    private string ReadModifiedUtf8(string path)
    {
        EnsureAvailable(sizeof(ushort), path);
        ushort byteLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, sizeof(ushort)));
        position += sizeof(ushort);
        EnsureAvailable(byteLength, path);
        ReadOnlySpan<byte> bytes = source.Slice(position, byteLength);
        position += byteLength;

        char[] characters = GC.AllocateUninitializedArray<char>(byteLength);
        int characterCount = 0;
        int index = 0;
        while(index < bytes.Length)
        {
            byte first = bytes[index++];
            if((first & 0x80) == 0)
            {
                if(first == 0) throw Error(path, "Modified UTF-8 包含原始 NUL 字节");
                characters[characterCount++] = (char)first;
                continue;
            }
            if((first & 0xe0) == 0xc0)
            {
                if(index >= bytes.Length) throw Error(path, "Modified UTF-8 双字节字符被截断");
                byte second = bytes[index++];
                if((second & 0xc0) != 0x80) throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x1f) << 6 | second & 0x3f;
                if(codeUnit != 0 && codeUnit < 0x80)
                    throw Error(path, "Modified UTF-8 包含过长双字节编码");
                characters[characterCount++] = (char)codeUnit;
                continue;
            }
            if((first & 0xf0) == 0xe0)
            {
                if(index + 1 >= bytes.Length) throw Error(path, "Modified UTF-8 三字节字符被截断");
                byte second = bytes[index++];
                byte third = bytes[index++];
                if((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                    throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                if(codeUnit < 0x800) throw Error(path, "Modified UTF-8 包含过长三字节编码");
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
        if(count < 0 || position > source.Length - count)
            throw Error(path, "NBT 数据被截断或长度字段越界");
    }

    private static void EnsureDepth(int depth, string path)
    {
        if(depth > MaximumDepth)
            throw Error(path, $"NBT 嵌套深度超过安全上限 {MaximumDepth}");
    }

    private static InvalidDataException Error(string path, string message, Exception? inner = null) =>
        new($"{path}: {message}", inner);

    private enum ChunkContainerKind
    {
        Root,
        Chunk,
        Section,
    }
}
