using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// A bounded, render-ready import of the legacy WorldEdit/MCEdit .schematic format. Origin is the restored
/// world-space minimum point: WEOrigin + WEOffset, or zero when those legacy tags are absent.
/// </summary>
public sealed record LegacySchematicImport(
    string SourcePath,
    int Width,
    int Height,
    int Length,
    BlockPosition Origin,
    BlockPosition ClipboardOrigin,
    BlockPosition WorldEditOffset,
    IReadOnlyList<NormalizedMinecraftSection> Sections,
    long NonAirBlockCount,
    IReadOnlyList<NormalizedMinecraftBlockEntity> BlockEntities,
    string Materials,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics)
{
    public int BlockEntityCount => BlockEntities.Count;
}

/// <summary>
/// Reads legacy numeric-ID schematics into the same version-neutral Section representation used by worlds.
/// The source file is never modified and unsupported non-air IDs remain visible through the legacy registry.
/// </summary>
public static class LegacySchematicImporter
{
    public const int MaximumVolume = 16_777_216;
    private const int MaximumEncodedBytes = 128 * 1024 * 1024;
    private const int MaximumDecodedBytes = 160 * 1024 * 1024;
    private const string SchematicDimension = "zhujiejing:schematic";

    public static LegacySchematicImport Import(string sourcePath, IMinecraftBlockRegistry? registry = null) =>
        Import(sourcePath, registry, CancellationToken.None);

    public static LegacySchematicImport Import(string sourcePath, CancellationToken cancellationToken) =>
        Import(sourcePath, null, cancellationToken);

    public static LegacySchematicImport Import(string sourcePath, IMinecraftBlockRegistry? registry, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(sourcePath);
        FileInfo source = new(fullPath);
        if(!source.Exists) throw new FileNotFoundException("找不到 .schematic 文件。", fullPath);
        if(source.Length is <= 0 or > MaximumEncodedBytes)
        {
            throw new InvalidDataException(
                $".schematic 文件大小必须在 1..{MaximumEncodedBytes:N0} 字节之间。");
        }

        using FileStream stream = new(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        byte[] nbt = DecodePayload(stream, cancellationToken);
        return ImportNbt(nbt, fullPath, registry ?? Minecraft1122VanillaBlockRegistry.Instance, cancellationToken);
    }

    internal static LegacySchematicImport ImportNbt(
        ReadOnlyMemory<byte> nbt,
        string sourcePath,
        IMinecraftBlockRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        cancellationToken.ThrowIfCancellationRequested();
        if(nbt.IsEmpty || nbt.Length > MaximumDecodedBytes)
            throw new InvalidDataException(".schematic NBT 为空或超过安全上限。");

        SchematicNbtData source = new SchematicNbtReader(nbt.ToArray(), cancellationToken).Read();
        long longVolume = checked((long)source.Width * source.Height * source.Length);
        if(longVolume > MaximumVolume)
            throw new InvalidDataException($"结构体积超过 {MaximumVolume:N0} 方块安全上限。");
        int volume = checked((int)longVolume);
        if(source.Blocks.Length != volume)
            throw new InvalidDataException($"Blocks 数组长度应为 {volume:N0}，实际为 {source.Blocks.Length:N0}。");
        if(source.Data is not null && source.Data.Length != volume)
            throw new InvalidDataException($"Data 数组长度应为 {volume:N0}，实际为 {source.Data.Length:N0}。");
        int addLength = checked((volume + 1) / 2);
        if(source.AddBlocks is not null && source.AddBlocks.Length != addLength)
            throw new InvalidDataException(
                $"AddBlocks 数组长度应为 {addLength:N0}，实际为 {source.AddBlocks.Length:N0}。");
        BlockPosition structureOrigin = ResolveStructureOrigin(source);

        Dictionary<(int X, int Y, int Z), SectionBuilder> builders = [];
        Dictionary<int, MinecraftRegistryResolution> resolutions = [];
        HashSet<(ushort Id, byte Metadata, string Code)> reportedDiagnostics = [];
        List<MinecraftDiagnostic> diagnostics = [];
        if(source.Data is null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.data.missing",
                MinecraftDiagnosticSeverity.Information,
                "结构没有 Data 数组；方块 metadata 已按 0 处理。"));
        }
        if(!source.Materials.Equals("Alpha", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.materials.unexpected",
                MinecraftDiagnosticSeverity.Warning,
                $"结构材质标记为 {source.Materials}，已按 Java 1.12.2 Legacy 规则解析。"));
        }

        long nonAir = 0;
        for(int index = 0; index < volume; index++)
        {
            if((index & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            ushort numericId = source.Blocks[index];
            if(source.AddBlocks is not null)
                numericId |= checked((ushort)(ReadNibble(source.AddBlocks, index) << 8));
            if(numericId == 0) continue;

            byte metadata = source.Data is null ? (byte)0 : (byte)(source.Data[index] & 0x0f);
            int resolutionKey = numericId << 4 | metadata;
            if(!resolutions.TryGetValue(resolutionKey, out MinecraftRegistryResolution? resolution))
            {
                resolution = registry.ResolveLegacy(numericId, metadata) ??
                             throw new InvalidOperationException($"Registry returned null for {numericId}:{metadata}.");
                if(resolution.State.IsAir)
                    throw new InvalidDataException($"非空气旧版方块 {numericId}:{metadata} 被错误映射为空气。");
                resolutions.Add(resolutionKey, resolution);
            }

            int x = index % source.Width;
            int yz = index / source.Width;
            int z = yz % source.Length;
            int y = yz / source.Length;
            BlockPosition worldPosition;
            try
            {
                worldPosition = structureOrigin.Offset(x, y, z);
            }
            catch(OverflowException exception)
            {
                throw new InvalidDataException(
                    $"$.Blocks[{index}]: WorldEdit 坐标与结构尺寸叠加后超出 Int32 范围。",
                    exception);
            }
            SectionCoordinate sectionCoordinate = SectionCoordinate.FromBlock(SchematicDimension, worldPosition);
            var sectionKey = (sectionCoordinate.X, sectionCoordinate.Y, sectionCoordinate.Z);
            if(!builders.TryGetValue(sectionKey, out SectionBuilder? builder))
            {
                builder = new SectionBuilder();
                builders.Add(sectionKey, builder);
            }
            int localIndex = SectionCoordinate.LocalIndex(worldPosition);
            builder.Set(localIndex, resolution.State, (ushort)resolutionKey);
            nonAir++;

            MinecraftDiagnostic? diagnostic = resolution.Diagnostic;
            if(resolution.Source == MinecraftRegistryResolutionSource.VisibleUnknownPlaceholder && diagnostic is null)
            {
                diagnostic = new MinecraftDiagnostic(
                    "legacy.block.unknown",
                    MinecraftDiagnosticSeverity.Warning,
                    $"未知的非空气旧版方块 {numericId}:{metadata} 已使用醒目占位方块。");
            }
            if(diagnostic is not null && reportedDiagnostics.Add((numericId, metadata, diagnostic.Code)))
            {
                diagnostics.Add(diagnostic with { NbtPath = $"$.Blocks[{index}]" });
            }
        }

        NormalizedMinecraftSection[] sections = builders
            .OrderBy(static pair => pair.Key.Y)
            .ThenBy(static pair => pair.Key.Z)
            .ThenBy(static pair => pair.Key.X)
            .Select(pair =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return pair.Value.Build(new SectionCoordinate(SchematicDimension, pair.Key.X, pair.Key.Y, pair.Key.Z));
            })
            .ToArray();
        if(sections.Length == 0)
        {
            // Keep an all-air schematic addressable. The declared Bounds retained by the import carries its
            // exact outer-air dimensions, while one sparse air Section lets the normal scene/export pipeline
            // retain the dimension instead of treating the source as an absent scene.
            sections =
            [
                new SectionBuilder().Build(SectionCoordinate.FromBlock(SchematicDimension, structureOrigin)),
            ];
        }
        IReadOnlyList<NormalizedMinecraftBlockEntity> blockEntities = NormalizeBlockEntities(
            source.BlockEntities,
            structureOrigin,
            source.Width,
            source.Height,
            source.Length,
            sourcePath,
            cancellationToken);
        return new LegacySchematicImport(
            sourcePath,
            source.Width,
            source.Height,
            source.Length,
            structureOrigin,
            source.WorldEditOrigin,
            source.WorldEditOffset,
            sections,
            nonAir,
            blockEntities,
            source.Materials,
            diagnostics);
    }

    private static IReadOnlyList<NormalizedMinecraftBlockEntity> NormalizeBlockEntities(
        IReadOnlyList<LegacyBlockEntityNbtData> sourceEntities,
        BlockPosition structureOrigin,
        int width,
        int height,
        int length,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var result = new List<NormalizedMinecraftBlockEntity>(sourceEntities.Count);
        var occupiedPositions = new HashSet<BlockPosition>();
        foreach(LegacyBlockEntityNbtData source in sourceEntities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(string.IsNullOrWhiteSpace(source.TypeId) ||
               source.X is null || source.Y is null || source.Z is null)
            {
                throw new InvalidDataException(
                    $"{source.NbtPath}: 方块实体缺少有效的 id/x/y/z；为避免实体错位，已拒绝导入。");
            }
            if(source.X < 0 || source.X >= width ||
               source.Y < 0 || source.Y >= height ||
               source.Z < 0 || source.Z >= length)
            {
                throw new InvalidDataException(
                    $"{source.NbtPath}: 方块实体局部坐标 ({source.X}, {source.Y}, {source.Z}) " +
                    $"超出结构范围 0..({width - 1}, {height - 1}, {length - 1})；为避免错位，已拒绝导入。");
            }

            BlockPosition worldPosition;
            try
            {
                worldPosition = structureOrigin.Offset(source.X.Value, source.Y.Value, source.Z.Value);
            }
            catch(OverflowException exception)
            {
                throw new InvalidDataException(
                    $"{source.NbtPath}: 方块实体局部坐标与结构原点叠加后超出 Int32 范围。",
                    exception);
            }
            if(!occupiedPositions.Add(worldPosition))
                throw new InvalidDataException($"{source.NbtPath}: 方块实体坐标 {worldPosition} 重复；已拒绝歧义导入。");

            string sourceHash = Convert.ToHexString(SHA256.HashData(source.CompoundPayload));
            MinecraftOpaquePayload localPayload = new(
                MinecraftOpaquePayloadFormat.EncodedNbtTag,
                source.CompoundPayload,
                sourcePath,
                source.NbtPath,
                sourceHash);
            NormalizedMinecraftBlockEntity localEntity = new(
                source.TypeId,
                worldPosition,
                new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>()),
                localPayload);
            LegacyBlockEntityRewriteResult rewritten = LegacyBlockEntityPayloadRewriter.Rewrite(localEntity);
            if(!rewritten.PreservedUnknownData)
            {
                throw new InvalidDataException(
                    $"{source.NbtPath}: 无法在保留完整 NBT 的同时恢复方块实体绝对坐标：" +
                    (rewritten.ReductionReason ?? "未知原因"));
            }
            string rewrittenHash = Convert.ToHexString(SHA256.HashData(rewritten.CompoundPayload));
            result.Add(localEntity with
            {
                OriginalNbt = localPayload with
                {
                    Bytes = rewritten.CompoundPayload,
                    ContentHash = rewrittenHash,
                },
            });
        }
        return result.ToArray();
    }

    private static byte[] DecodePayload(Stream source, CancellationToken cancellationToken)
    {
        Span<byte> header = stackalloc byte[2];
        int read = source.Read(header);
        source.Position = 0;
        Stream payload = source;
        bool ownsPayload = false;
        if(read == 2 && header[0] == 0x1f && header[1] == 0x8b)
        {
            payload = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            ownsPayload = true;
        }
        else if(read == 2 && header[0] == 0x78 && ((header[0] << 8) | header[1]) % 31 == 0)
        {
            payload = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: true);
            ownsPayload = true;
        }

        try
        {
            using MemoryStream decoded = new();
            byte[] buffer = new byte[64 * 1024];
            while(true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = payload.Read(buffer, 0, buffer.Length);
                if(count == 0) break;
                if(decoded.Length + count > MaximumDecodedBytes)
                    throw new InvalidDataException($".schematic 解压后超过 {MaximumDecodedBytes:N0} 字节安全上限。");
                decoded.Write(buffer, 0, count);
            }
            return decoded.ToArray();
        }
        catch(InvalidDataException exception) when(ownsPayload)
        {
            throw new InvalidDataException(".schematic 压缩数据损坏或不完整。", exception);
        }
        finally
        {
            if(ownsPayload) payload.Dispose();
        }
    }

    private static byte ReadNibble(byte[] values, int index)
    {
        byte packed = values[index >> 1];
        return (byte)((index & 1) == 0 ? packed & 0x0f : packed >> 4 & 0x0f);
    }

    private static BlockPosition ResolveStructureOrigin(SchematicNbtData source)
    {
        try
        {
            BlockPosition minimum = source.WorldEditOrigin.Offset(
                source.WorldEditOffset.X,
                source.WorldEditOffset.Y,
                source.WorldEditOffset.Z);
            _ = minimum.Offset(source.Width - 1, source.Height - 1, source.Length - 1);
            return minimum;
        }
        catch(OverflowException exception)
        {
            throw new InvalidDataException(
                "$.WEOrigin/WEOffset: WorldEdit 坐标与结构尺寸叠加后超出 Int32 范围。",
                exception);
        }
    }

    private sealed class SectionBuilder
    {
        private readonly ushort[] indices = new ushort[4096];
        private readonly ushort[] sourceStates = new ushort[4096];
        private readonly List<BlockState> palette = [BlockState.Air];
        private readonly Dictionary<BlockState, ushort> lookup = new() { [BlockState.Air] = 0 };

        public void Set(int index, BlockState state, ushort sourceState)
        {
            if(!lookup.TryGetValue(state, out ushort paletteIndex))
            {
                paletteIndex = checked((ushort)palette.Count);
                palette.Add(state);
                lookup.Add(state, paletteIndex);
            }
            indices[index] = paletteIndex;
            sourceStates[index] = sourceState;
        }

        public NormalizedMinecraftSection Build(SectionCoordinate coordinate) =>
            new(coordinate, palette.ToArray(), indices, null, []) { SourceLegacyStates = sourceStates };
    }

    private sealed record SchematicNbtData(
        int Width,
        int Height,
        int Length,
        byte[] Blocks,
        byte[]? Data,
        byte[]? AddBlocks,
        IReadOnlyList<LegacyBlockEntityNbtData> BlockEntities,
        string Materials,
        BlockPosition WorldEditOrigin,
        BlockPosition WorldEditOffset);

    private sealed class SchematicNbtReader
    {
        private const int MaximumDepth = 64;
        private const int MaximumCollectionElements = MaximumVolume;
        private const int MaximumBlockEntities = 65_536;

        private readonly byte[] source;
        private readonly CancellationToken cancellationToken;
        private int position;

        public SchematicNbtReader(byte[] source, CancellationToken cancellationToken)
        {
            this.source = source;
            this.cancellationToken = cancellationToken;
        }

        public SchematicNbtData Read()
        {
            byte rootType = ReadByte("$");
            if(rootType != 10) throw Error("$", $"根标签必须是 TAG_Compound，实际为 {rootType}");
            _ = ReadModifiedUtf8("$<name>");
            short? width = null;
            short? height = null;
            short? length = null;
            byte[]? blocks = null;
            byte[]? data = null;
            byte[]? addBlocks = null;
            IReadOnlyList<LegacyBlockEntityNbtData>? blockEntities = null;
            string materials = "Alpha";
            bool sawTileEntities = false;
            bool sawMaterials = false;
            int? worldEditOriginX = null;
            int? worldEditOriginY = null;
            int? worldEditOriginZ = null;
            int? worldEditOffsetX = null;
            int? worldEditOffsetY = null;
            int? worldEditOffsetZ = null;

            ReadNamedCompound(
                "$",
                0,
                (tagType, name, path, depth) =>
                {
                    switch(name)
                    {
                        case "Width":
                            RequireType(tagType, 2, path);
                            RequireMissing(width, path);
                            width = ReadInt16(path);
                            return true;
                        case "Height":
                            RequireType(tagType, 2, path);
                            RequireMissing(height, path);
                            height = ReadInt16(path);
                            return true;
                        case "Length":
                            RequireType(tagType, 2, path);
                            RequireMissing(length, path);
                            length = ReadInt16(path);
                            return true;
                        case "Blocks":
                            RequireType(tagType, 7, path);
                            if(blocks is not null) throw Error(path, "出现重复的 Blocks");
                            blocks = ReadByteArray(path, MaximumVolume);
                            return true;
                        case "Data":
                            RequireType(tagType, 7, path);
                            if(data is not null) throw Error(path, "出现重复的 Data");
                            data = ReadByteArray(path, MaximumVolume);
                            return true;
                        case "AddBlocks":
                            RequireType(tagType, 7, path);
                            if(addBlocks is not null) throw Error(path, "出现重复的 AddBlocks");
                            addBlocks = ReadByteArray(path, (MaximumVolume + 1) / 2);
                            return true;
                        case "TileEntities":
                            RequireType(tagType, 9, path);
                            if(sawTileEntities) throw Error(path, "出现重复的 TileEntities");
                            sawTileEntities = true;
                            blockEntities = ReadBlockEntities(path, depth);
                            return true;
                        case "Materials":
                            RequireType(tagType, 8, path);
                            if(sawMaterials) throw Error(path, "出现重复的 Materials");
                            sawMaterials = true;
                            materials = ReadModifiedUtf8(path);
                            return true;
                        case "WEOriginX":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOriginX, path);
                            worldEditOriginX = ReadInt32(path);
                            return true;
                        case "WEOriginY":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOriginY, path);
                            worldEditOriginY = ReadInt32(path);
                            return true;
                        case "WEOriginZ":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOriginZ, path);
                            worldEditOriginZ = ReadInt32(path);
                            return true;
                        case "WEOffsetX":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOffsetX, path);
                            worldEditOffsetX = ReadInt32(path);
                            return true;
                        case "WEOffsetY":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOffsetY, path);
                            worldEditOffsetY = ReadInt32(path);
                            return true;
                        case "WEOffsetZ":
                            RequireType(tagType, 3, path);
                            RequireMissing(worldEditOffsetZ, path);
                            worldEditOffsetZ = ReadInt32(path);
                            return true;
                        default:
                            return false;
                    }
                });

            if(position != source.Length) throw Error("$", "根标签结束后仍有多余字节");
            if(width is null || width <= 0) throw Error("$.Width", "缺少有效的正数 Width");
            if(height is null || height <= 0) throw Error("$.Height", "缺少有效的正数 Height");
            if(length is null || length <= 0) throw Error("$.Length", "缺少有效的正数 Length");
            if(blocks is null) throw Error("$.Blocks", "缺少 Blocks 数组");
            return new SchematicNbtData(
                width.Value,
                height.Value,
                length.Value,
                blocks,
                data,
                addBlocks,
                blockEntities ?? [],
                materials,
                new BlockPosition(
                    worldEditOriginX ?? 0,
                    worldEditOriginY ?? 0,
                    worldEditOriginZ ?? 0),
                new BlockPosition(
                    worldEditOffsetX ?? 0,
                    worldEditOffsetY ?? 0,
                    worldEditOffsetZ ?? 0));
        }

        private IReadOnlyList<LegacyBlockEntityNbtData> ReadBlockEntities(string path, int depth)
        {
            EnsureDepth(depth, path);
            byte elementType = ReadByte(path);
            int count = ReadBoundedCount(path, MaximumBlockEntities);
            if(elementType != 10 && !(count == 0 && elementType == 0))
                throw Error(path, $"TileEntities 元素必须是 TAG_Compound，实际类型为 {elementType}");

            var entities = new List<LegacyBlockEntityNbtData>(count);
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
                            case "id":
                                RequireType(tagType, 8, childPath);
                                if(typeId is not null) throw Error(childPath, "出现重复的方块实体 id");
                                typeId = ReadModifiedUtf8(childPath);
                                return true;
                            case "x":
                                RequireType(tagType, 3, childPath);
                                if(x is not null) throw Error(childPath, "出现重复的方块实体 x");
                                x = ReadInt32(childPath);
                                return true;
                            case "y":
                                RequireType(tagType, 3, childPath);
                                if(y is not null) throw Error(childPath, "出现重复的方块实体 y");
                                y = ReadInt32(childPath);
                                return true;
                            case "z":
                                RequireType(tagType, 3, childPath);
                                if(z is not null) throw Error(childPath, "出现重复的方块实体 z");
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
            while(true)
            {
                byte tagType = ReadByte(path);
                if(tagType == 0) return;
                string name = ReadModifiedUtf8($"{path}<name>");
                string childPath = $"{path}.{name}";
                if(!handler(tagType, name, childPath, depth + 1)) SkipPayload(tagType, childPath, depth + 1);
            }
        }

        private int ReadAndSkipList(string path, int depth, int maximum, bool requireCompound)
        {
            EnsureDepth(depth, path);
            byte elementType = ReadByte(path);
            int count = ReadBoundedCount(path, maximum);
            if(count > 0 && elementType == 0) throw Error(path, "非空 TAG_List 不能使用 TAG_End");
            if(elementType > 12) throw Error(path, $"TAG_List 使用未知元素类型 {elementType}");
            if(requireCompound && elementType != 10 && !(count == 0 && elementType == 0))
                throw Error(path, $"列表元素必须是 TAG_Compound，实际为 {elementType}");
            for(int index = 0; index < count; index++)
                SkipPayload(elementType, $"{path}[{index}]", depth + 1);
            return count;
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
                    _ = ReadAndSkipList(path, depth, MaximumCollectionElements, requireCompound: false);
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

        private byte[] ReadByteArray(string path, int maximum)
        {
            int length = ReadBoundedCount(path, maximum);
            EnsureAvailable(length, path);
            byte[] result = source.AsSpan(position, length).ToArray();
            position += length;
            return result;
        }

        private void SkipArray(string path, int elementSize)
        {
            int count = ReadBoundedCount(path, MaximumCollectionElements);
            long bytes = checked((long)count * elementSize);
            if(bytes > int.MaxValue) throw Error(path, "数组字节长度超出安全范围");
            Skip((int)bytes, path);
        }

        private int ReadBoundedCount(string path, int maximum)
        {
            int count = ReadInt32(path);
            if(count < 0 || count > maximum) throw Error(path, $"集合长度 {count} 超出安全范围 0..{maximum}");
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
                    if(codeUnit != 0 && codeUnit < 0x80) throw Error(path, "Modified UTF-8 包含过长双字节编码");
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
            cancellationToken.ThrowIfCancellationRequested();
            if(count < 0 || position > source.Length - count) throw Error(path, "NBT 数据被截断或长度字段越界");
        }

        private static void EnsureDepth(int depth, string path)
        {
            if(depth > MaximumDepth) throw Error(path, $"NBT 嵌套深度超过安全上限 {MaximumDepth}");
        }

        private static void RequireType(byte actual, byte expected, string path)
        {
            if(actual != expected) throw Error(path, $"NBT 标签类型必须为 {expected}，实际为 {actual}");
        }

        private static void RequireMissing<T>(T? value, string path) where T : struct
        {
            if(value.HasValue) throw Error(path, "出现重复标签");
        }

        private static InvalidDataException Error(string path, string message) => new($"{path}: {message}");

        private delegate bool NamedTagHandler(byte tagType, string name, string path, int depth);
    }
}
