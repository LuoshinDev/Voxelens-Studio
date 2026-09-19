using System.Buffers.Binary;
using System.Collections.ObjectModel;

namespace ZhuJieJing.Minecraft;

internal sealed record ModernChunkNbtData(
    int? DataVersion,
    int ChunkX,
    int ChunkZ,
    IReadOnlyList<ModernSectionNbtData> Sections,
    IReadOnlyList<ModernBlockEntityNbtData> BlockEntities);

internal sealed record ModernSectionNbtData(
    int Y,
    bool HasBlockStates,
    IReadOnlyList<ModernPaletteEntryNbtData>? Palette,
    long[]? PackedBlockStates,
    byte[]? BlockLight,
    byte[]? SkyLight,
    string NbtPath);

internal sealed record ModernPaletteEntryNbtData(
    string Name,
    IReadOnlyDictionary<string, string> Properties,
    string NbtPath);

internal sealed record ModernBlockEntityNbtData(
    string? TypeId,
    int? X,
    int? Y,
    int? Z,
    byte[] CompoundPayload,
    string NbtPath);

/// <summary>
/// Bounded reader for the flattened (1.13-1.17) and modern (1.18+) Java chunk layouts. It reads only
/// canonical block content and block-entity identity while validating every skipped NBT payload.
/// </summary>
internal sealed class ModernChunkNbtReader
{
    private const int MaximumDepth = 64;
    private const int MaximumGeneralCollectionElements = 4 * 1024 * 1024;
    private const int MaximumSections = 1024;
    private const int MaximumPaletteEntries = 4096;
    private const int MaximumBlockEntities = 65_536;

    private readonly byte[] source;
    private int position;

    public ModernChunkNbtReader(byte[] source)
    {
        this.source = source;
    }

    public ModernChunkNbtData Read()
    {
        byte rootType = ReadByte("$");
        if(rootType != 10)
            throw Error("$", $"根标签必须是 TAG_Compound，实际类型为 {rootType}");
        _ = ReadModifiedUtf8("$<name>");

        int? dataVersion = null;
        PartialChunkNbtData? level = null;
        PartialChunkBuilder root = new("$");
        ReadNamedCompound(
            "$",
            0,
            (tagType, name, path, depth) =>
            {
                switch(name)
                {
                    case "DataVersion":
                        RequireType(tagType, 3, path);
                        if(dataVersion is not null)
                            throw Error(path, "出现重复的 DataVersion");
                        dataVersion = ReadInt32(path);
                        return true;
                    case "Level":
                        RequireType(tagType, 10, path);
                        if(level is not null)
                            throw Error(path, "出现重复的 Level compound");
                        level = ReadChunkContainer(path, depth, allowLowerCaseNames: true);
                        return true;
                    default:
                        return TryReadChunkContainerTag(root, tagType, name, path, depth, allowLowerCaseNames: true);
                }
            });

        if(position != source.Length)
            throw Error("$", "根标签结束后仍有多余字节");

        PartialChunkNbtData rootData = root.Build();
        bool rootHasChunkContent = rootData.ChunkX is not null || rootData.ChunkZ is not null || rootData.Sections is not null;
        bool levelHasChunkContent = level is not null &&
                                    (level.ChunkX is not null || level.ChunkZ is not null || level.Sections is not null);
        if(rootHasChunkContent && levelHasChunkContent)
            throw Error("$", "根 compound 与 Level compound 同时包含区块内容，无法确定权威布局");

        PartialChunkNbtData selected = rootHasChunkContent ? rootData : level ?? rootData;
        if(dataVersion is not null && selected.DataVersion is not null && dataVersion != selected.DataVersion)
        {
            throw Error(
                "$.DataVersion",
                $"根 DataVersion={dataVersion} 与 {selected.Path}.DataVersion={selected.DataVersion} 不一致");
        }
        int? selectedDataVersion = dataVersion ?? selected.DataVersion;
        if(selected.ChunkX is null)
            throw Error($"{selected.Path}.xPos", "缺少区块 X 坐标");
        if(selected.ChunkZ is null)
            throw Error($"{selected.Path}.zPos", "缺少区块 Z 坐标");
        if(selected.Sections is null)
            throw Error(selected.Path, "缺少 Sections/sections 列表");

        return new ModernChunkNbtData(
            selectedDataVersion,
            selected.ChunkX.Value,
            selected.ChunkZ.Value,
            selected.Sections,
            selected.BlockEntities ?? Array.Empty<ModernBlockEntityNbtData>());
    }

    private PartialChunkNbtData ReadChunkContainer(string path, int depth, bool allowLowerCaseNames)
    {
        PartialChunkBuilder builder = new(path);
        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, childDepth) =>
                TryReadChunkContainerTag(builder, tagType, name, childPath, childDepth, allowLowerCaseNames));
        return builder.Build();
    }

    private bool TryReadChunkContainerTag(
        PartialChunkBuilder builder,
        byte tagType,
        string name,
        string path,
        int depth,
        bool allowLowerCaseNames)
    {
        switch(name)
        {
            case "DataVersion":
                RequireType(tagType, 3, path);
                builder.SetDataVersion(ReadInt32(path), path);
                return true;
            case "xPos":
                RequireType(tagType, 3, path);
                builder.SetChunkX(ReadInt32(path), path);
                return true;
            case "zPos":
                RequireType(tagType, 3, path);
                builder.SetChunkZ(ReadInt32(path), path);
                return true;
            case "Sections":
                RequireType(tagType, 9, path);
                builder.SetSections(ReadSections(path, depth), path);
                return true;
            case "sections" when allowLowerCaseNames:
                RequireType(tagType, 9, path);
                builder.SetSections(ReadSections(path, depth), path);
                return true;
            case "TileEntities":
                RequireType(tagType, 9, path);
                builder.SetBlockEntities(ReadBlockEntities(path, depth), path);
                return true;
            case "block_entities" when allowLowerCaseNames:
                RequireType(tagType, 9, path);
                builder.SetBlockEntities(ReadBlockEntities(path, depth), path);
                return true;
            default:
                return false;
        }
    }

    private IReadOnlyList<ModernSectionNbtData> ReadSections(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path, MaximumSections);
        if(elementType != 10 && !(count == 0 && elementType == 0))
            throw Error(path, $"Sections 元素必须是 TAG_Compound，实际类型为 {elementType}");

        List<ModernSectionNbtData> sections = new(count);
        HashSet<int> sectionYs = new();
        for(int index = 0; index < count; index++)
        {
            ModernSectionNbtData section = ReadSection($"{path}[{index}]", depth + 1);
            if(!sectionYs.Add(section.Y))
                throw Error($"{path}[{index}].Y", $"Section Y={section.Y} 重复");
            sections.Add(section);
        }

        return sections;
    }

    private ModernSectionNbtData ReadSection(string path, int depth)
    {
        int? sectionY = null;
        IReadOnlyList<ModernPaletteEntryNbtData>? flattenedPalette = null;
        long[]? flattenedData = null;
        ModernBlockStatesNbtData? modernBlockStates = null;
        byte[]? blockLight = null;
        byte[]? skyLight = null;
        bool sawFlattenedPalette = false;
        bool sawFlattenedData = false;
        bool sawModernBlockStates = false;
        bool sawBlockLight = false;
        bool sawSkyLight = false;

        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, childDepth) =>
            {
                switch(name)
                {
                    case "Y":
                        if(sectionY is not null)
                            throw Error(childPath, "出现重复的 Section Y");
                        sectionY = ReadIntegralSectionY(tagType, childPath);
                        return true;
                    case "Palette":
                        RequireType(tagType, 9, childPath);
                        if(sawFlattenedPalette)
                            throw Error(childPath, "出现重复的 Palette");
                        sawFlattenedPalette = true;
                        flattenedPalette = ReadPalette(childPath, childDepth);
                        return true;
                    case "BlockStates":
                        RequireType(tagType, 12, childPath);
                        if(sawFlattenedData)
                            throw Error(childPath, "出现重复的 BlockStates");
                        sawFlattenedData = true;
                        flattenedData = ReadLongArray(childPath);
                        return true;
                    case "block_states":
                        RequireType(tagType, 10, childPath);
                        if(sawModernBlockStates)
                            throw Error(childPath, "出现重复的 block_states");
                        sawModernBlockStates = true;
                        modernBlockStates = ReadModernBlockStates(childPath, childDepth);
                        return true;
                    case "BlockLight":
                        RequireType(tagType, 7, childPath);
                        if(sawBlockLight)
                            throw Error(childPath, "出现重复的 BlockLight");
                        sawBlockLight = true;
                        blockLight = ReadExactByteArray(childPath, 2048);
                        return true;
                    case "SkyLight":
                        RequireType(tagType, 7, childPath);
                        if(sawSkyLight)
                            throw Error(childPath, "出现重复的 SkyLight");
                        sawSkyLight = true;
                        skyLight = ReadExactByteArray(childPath, 2048);
                        return true;
                    default:
                        return false;
                }
            });

        if(sectionY is null)
            throw Error($"{path}.Y", "缺少 Section Y");
        bool hasFlattened = sawFlattenedPalette || sawFlattenedData;
        if(hasFlattened && sawModernBlockStates)
            throw Error(path, "同一 Section 同时包含旧式 Palette/BlockStates 与现代 block_states");
        if(hasFlattened && !sawFlattenedPalette)
            throw Error($"{path}.Palette", "BlockStates 存在但缺少 Palette，无法把全局状态 ID 安全还原为方块名称");

        return sawModernBlockStates
            ? new ModernSectionNbtData(
                sectionY.Value,
                true,
                modernBlockStates!.Palette,
                modernBlockStates.Data,
                blockLight,
                skyLight,
                path)
            : new ModernSectionNbtData(
                sectionY.Value,
                hasFlattened,
                flattenedPalette,
                flattenedData,
                blockLight,
                skyLight,
                path);
    }

    private ModernBlockStatesNbtData ReadModernBlockStates(string path, int depth)
    {
        IReadOnlyList<ModernPaletteEntryNbtData>? palette = null;
        long[]? data = null;
        bool sawPalette = false;
        bool sawData = false;
        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, childDepth) =>
            {
                switch(name)
                {
                    case "palette":
                        RequireType(tagType, 9, childPath);
                        if(sawPalette)
                            throw Error(childPath, "出现重复的 palette");
                        sawPalette = true;
                        palette = ReadPalette(childPath, childDepth);
                        return true;
                    case "data":
                        RequireType(tagType, 12, childPath);
                        if(sawData)
                            throw Error(childPath, "出现重复的 data");
                        sawData = true;
                        data = ReadLongArray(childPath);
                        return true;
                    default:
                        return false;
                }
            });

        if(!sawPalette)
            throw Error($"{path}.palette", "block_states 缺少 palette");
        return new ModernBlockStatesNbtData(palette!, data);
    }

    private IReadOnlyList<ModernPaletteEntryNbtData> ReadPalette(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path, MaximumPaletteEntries);
        if(elementType != 10 && !(count == 0 && elementType == 0))
            throw Error(path, $"palette 元素必须是 TAG_Compound，实际类型为 {elementType}");

        List<ModernPaletteEntryNbtData> entries = new(count);
        for(int index = 0; index < count; index++)
            entries.Add(ReadPaletteEntry($"{path}[{index}]", depth + 1));
        return entries;
    }

    private ModernPaletteEntryNbtData ReadPaletteEntry(string path, int depth)
    {
        string? name = null;
        IReadOnlyDictionary<string, string>? properties = null;
        bool sawProperties = false;
        ReadNamedCompound(
            path,
            depth,
            (tagType, tagName, childPath, childDepth) =>
            {
                switch(tagName)
                {
                    case "Name":
                        RequireType(tagType, 8, childPath);
                        if(name is not null)
                            throw Error(childPath, "出现重复的方块 Name");
                        name = ReadModifiedUtf8(childPath);
                        return true;
                    case "Properties":
                        RequireType(tagType, 10, childPath);
                        if(sawProperties)
                            throw Error(childPath, "出现重复的 Properties");
                        sawProperties = true;
                        properties = ReadProperties(childPath, childDepth);
                        return true;
                    default:
                        return false;
                }
            });

        if(string.IsNullOrWhiteSpace(name))
            throw Error($"{path}.Name", "palette 条目缺少非空方块 Name；不能将未知内容当作空气");
        return new ModernPaletteEntryNbtData(
            name,
            properties ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>()),
            path);
    }

    private IReadOnlyDictionary<string, string> ReadProperties(string path, int depth)
    {
        Dictionary<string, string> properties = new(StringComparer.Ordinal);
        ReadNamedCompound(
            path,
            depth,
            (tagType, name, childPath, _) =>
            {
                RequireType(tagType, 8, childPath);
                if(!properties.TryAdd(name, ReadModifiedUtf8(childPath)))
                    throw Error(childPath, $"出现重复的方块属性 {name}");
                return true;
            });
        return new ReadOnlyDictionary<string, string>(properties);
    }

    private IReadOnlyList<ModernBlockEntityNbtData> ReadBlockEntities(string path, int depth)
    {
        EnsureDepth(depth, path);
        byte elementType = ReadByte(path);
        int count = ReadBoundedCount(path, MaximumBlockEntities);
        if(elementType != 10 && !(count == 0 && elementType == 0))
            throw Error(path, $"方块实体列表元素必须是 TAG_Compound，实际类型为 {elementType}");

        List<ModernBlockEntityNbtData> entities = new(count);
        for(int index = 0; index < count; index++)
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
                    switch(name)
                    {
                        case "id" when tagType == 8:
                            if(typeId is not null)
                                throw Error(childPath, "出现重复的方块实体 id");
                            typeId = ReadModifiedUtf8(childPath);
                            return true;
                        case "x" when tagType == 3:
                            if(x is not null)
                                throw Error(childPath, "出现重复的方块实体 x");
                            x = ReadInt32(childPath);
                            return true;
                        case "y" when tagType == 3:
                            if(y is not null)
                                throw Error(childPath, "出现重复的方块实体 y");
                            y = ReadInt32(childPath);
                            return true;
                        case "z" when tagType == 3:
                            if(z is not null)
                                throw Error(childPath, "出现重复的方块实体 z");
                            z = ReadInt32(childPath);
                            return true;
                        default:
                            return false;
                    }
                });

            entities.Add(new ModernBlockEntityNbtData(
                typeId,
                x,
                y,
                z,
                source.AsSpan(payloadStart, position - payloadStart).ToArray(),
                entityPath));
        }

        return entities;
    }

    private int ReadIntegralSectionY(byte tagType, string path) => tagType switch
    {
        1 => unchecked((sbyte)ReadByte(path)),
        2 => ReadInt16(path),
        3 => ReadInt32(path),
        _ => throw Error(path, $"Section Y 必须是 TAG_Byte、TAG_Short 或 TAG_Int，实际类型为 {tagType}"),
    };

    private void ReadNamedCompound(string path, int depth, NamedTagHandler handler)
    {
        EnsureDepth(depth, path);
        while(true)
        {
            byte tagType = ReadByte(path);
            if(tagType == 0)
                return;
            string name = ReadModifiedUtf8($"{path}<name>");
            string childPath = $"{path}.{name}";
            if(!handler(tagType, name, childPath, depth + 1))
                SkipPayload(tagType, childPath, depth + 1);
        }
    }

    private void SkipPayload(byte tagType, string path, int depth)
    {
        EnsureDepth(depth, path);
        switch(tagType)
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
            {
                byte elementType = ReadByte(path);
                int count = ReadBoundedCount(path, MaximumGeneralCollectionElements);
                if(count > 0 && elementType == 0)
                    throw Error(path, "非空 TAG_List 不能使用 TAG_End 元素类型");
                if(elementType > 12)
                    throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
                for(int index = 0; index < count; index++)
                    SkipPayload(elementType, $"{path}[]", depth + 1);
                return;
            }
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
        if(length != expectedLength)
            throw Error(path, $"数组长度必须是 {expectedLength}，实际为 {length}");
        EnsureAvailable(length, path);
        byte[] result = source.AsSpan(position, length).ToArray();
        position += length;
        return result;
    }

    private long[] ReadLongArray(string path)
    {
        int count = ReadBoundedCount(path, MaximumGeneralCollectionElements);
        long[] values = GC.AllocateUninitializedArray<long>(count);
        for(int index = 0; index < count; index++)
            values[index] = ReadInt64(path);
        return values;
    }

    private void SkipArray(string path, int elementSize)
    {
        int count = ReadBoundedCount(path, MaximumGeneralCollectionElements);
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

    private int ReadBoundedCount(string path, int maximum)
    {
        int count = ReadInt32(path);
        if(count < 0 || count > maximum)
            throw Error(path, $"集合长度 {count} 超出安全范围 0..{maximum}");
        return count;
    }

    private short ReadInt16(string path)
    {
        EnsureAvailable(sizeof(short), path);
        short value = BinaryPrimitives.ReadInt16BigEndian(source.AsSpan(position, sizeof(short)));
        position += sizeof(short);
        return value;
    }

    private int ReadInt32(string path)
    {
        EnsureAvailable(sizeof(int), path);
        int value = BinaryPrimitives.ReadInt32BigEndian(source.AsSpan(position, sizeof(int)));
        position += sizeof(int);
        return value;
    }

    private long ReadInt64(string path)
    {
        EnsureAvailable(sizeof(long), path);
        long value = BinaryPrimitives.ReadInt64BigEndian(source.AsSpan(position, sizeof(long)));
        position += sizeof(long);
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
        while(index < bytes.Length)
        {
            byte first = bytes[index++];
            if((first & 0x80) == 0)
            {
                if(first == 0)
                    throw Error(path, "Modified UTF-8 包含原始 NUL 字节");
                characters[characterCount++] = (char)first;
                continue;
            }

            if((first & 0xe0) == 0xc0)
            {
                if(index >= bytes.Length)
                    throw Error(path, "Modified UTF-8 双字节字符被截断");
                byte second = bytes[index++];
                if((second & 0xc0) != 0x80)
                    throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x1f) << 6 | second & 0x3f;
                if(codeUnit != 0 && codeUnit < 0x80)
                    throw Error(path, "Modified UTF-8 包含过长双字节编码");
                characters[characterCount++] = (char)codeUnit;
                continue;
            }

            if((first & 0xf0) == 0xe0)
            {
                if(index + 1 >= bytes.Length)
                    throw Error(path, "Modified UTF-8 三字节字符被截断");
                byte second = bytes[index++];
                byte third = bytes[index++];
                if((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                    throw Error(path, "Modified UTF-8 包含无效续字节");
                int codeUnit = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                if(codeUnit < 0x800)
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
        if(count < 0 || position > source.Length - count)
            throw Error(path, "NBT 数据被截断或长度字段越界");
    }

    private static void EnsureDepth(int depth, string path)
    {
        if(depth > MaximumDepth)
            throw Error(path, $"NBT 嵌套深度超过安全上限 {MaximumDepth}");
    }

    private static void RequireType(byte actual, byte expected, string path)
    {
        if(actual != expected)
            throw Error(path, $"NBT 标签类型必须为 {expected}，实际为 {actual}");
    }

    private static InvalidDataException Error(string path, string message, Exception? inner = null) =>
        new($"{path}: {message}", inner);

    private delegate bool NamedTagHandler(byte tagType, string name, string path, int depth);

    private sealed record ModernBlockStatesNbtData(
        IReadOnlyList<ModernPaletteEntryNbtData> Palette,
        long[]? Data);

    private sealed record PartialChunkNbtData(
        string Path,
        int? DataVersion,
        int? ChunkX,
        int? ChunkZ,
        IReadOnlyList<ModernSectionNbtData>? Sections,
        IReadOnlyList<ModernBlockEntityNbtData>? BlockEntities);

    private sealed class PartialChunkBuilder
    {
        private int? dataVersion;
        private int? chunkX;
        private int? chunkZ;
        private IReadOnlyList<ModernSectionNbtData>? sections;
        private IReadOnlyList<ModernBlockEntityNbtData>? blockEntities;
        private bool sawDataVersion;
        private bool sawChunkX;
        private bool sawChunkZ;
        private bool sawSections;
        private bool sawBlockEntities;

        public PartialChunkBuilder(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public void SetDataVersion(int value, string path)
        {
            if(sawDataVersion)
                throw Error(path, "出现重复的 DataVersion");
            sawDataVersion = true;
            dataVersion = value;
        }

        public void SetChunkX(int value, string path)
        {
            if(sawChunkX)
                throw Error(path, "出现重复的 xPos");
            sawChunkX = true;
            chunkX = value;
        }

        public void SetChunkZ(int value, string path)
        {
            if(sawChunkZ)
                throw Error(path, "出现重复的 zPos");
            sawChunkZ = true;
            chunkZ = value;
        }

        public void SetSections(IReadOnlyList<ModernSectionNbtData> value, string path)
        {
            if(sawSections)
                throw Error(path, "出现重复的 Sections/sections");
            sawSections = true;
            sections = value;
        }

        public void SetBlockEntities(IReadOnlyList<ModernBlockEntityNbtData> value, string path)
        {
            if(sawBlockEntities)
                throw Error(path, "出现重复的方块实体列表");
            sawBlockEntities = true;
            blockEntities = value;
        }

        public PartialChunkNbtData Build() =>
            new(Path, dataVersion, chunkX, chunkZ, sections, blockEntities);
    }
}
