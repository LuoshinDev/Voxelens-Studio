using System.IO.Compression;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>
/// A bounded request for the legacy WorldEdit/MCEdit schematic format. When Bounds is omitted the exporter
/// trims only outer air; air inside the occupied bounds remains part of the structure. ClipboardOrigin is the
/// WorldEdit paste anchor. When omitted it defaults to Bounds.Min, producing a zero WEOffset.
/// </summary>
public sealed record LegacySchematicExportRequest(
    string DestinationPath,
    IEnumerable<NormalizedMinecraftSection> Sections,
    BoxSelection? Bounds = null,
    BlockPosition? ClipboardOrigin = null,
    IEnumerable<NormalizedMinecraftBlockEntity>? BlockEntities = null);

public sealed record LegacySchematicExportResult(
    string DestinationPath,
    string Dimension,
    BoxSelection Bounds,
    BlockPosition ClipboardOrigin,
    BlockPosition WorldEditOffset,
    long NonAirBlockCount,
    int BlockEntityCount,
    long BytesWritten,
    IReadOnlyList<MinecraftBlockMappingSummary> Mappings,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>
/// Writes canonical Sections as a gzip-compressed Alpha .schematic. The destination is completed with a
/// same-directory atomic rename and is never overwritten. Canonical block entities inside the exported bounds
/// are converted to schematic-local coordinates while their complete compound payload is preserved when valid.
/// </summary>
public sealed class LegacySchematicExporter
{
    public const int MaximumVolume = LegacySchematicImporter.MaximumVolume;
    public const int MaximumInputSections = 65_536;
    public const int MaximumBlockEntities = 65_536;
    private const int MaximumBlockEntityPayloadBytes = 16 * 1024 * 1024;
    private const long MaximumTotalBlockEntityPayloadBytes = 128L * 1024 * 1024;
    private readonly IMinecraftBlockDowngradeRules rules;

    public LegacySchematicExporter(IMinecraftBlockDowngradeRules? rules = null)
    {
        this.rules = rules ?? Minecraft1122BlockDowngradeRules.Instance;
    }

    public LegacySchematicExportResult Export(
        LegacySchematicExportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentNullException.ThrowIfNull(request.Sections);
        cancellationToken.ThrowIfCancellationRequested();

        string destination = Path.GetFullPath(request.DestinationPath);
        if(!string.Equals(Path.GetExtension(destination), ".schematic", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("目标文件必须使用 .schematic 扩展名。", nameof(request));
        if(File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("目标 .schematic 已存在；为保护源文件和既有导出，拒绝覆盖。");
        string parentDirectory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("目标文件缺少父目录。", nameof(request));
        if(!Directory.Exists(parentDirectory)) throw new DirectoryNotFoundException($"目标目录不存在：{parentDirectory}");

        NormalizedMinecraftSection[] sections = MaterializeSections(request.Sections, cancellationToken);
        NormalizedMinecraftBlockEntity[] blockEntities = MaterializeBlockEntities(
            request.BlockEntities,
            cancellationToken);
        InputScan scan = ScanInput(sections, request.Bounds, cancellationToken);
        BoxSelection bounds = request.Bounds ?? scan.OccupiedBounds ?? FullSectionBounds(sections[0].Coordinate);
        SchematicDimensions dimensions = ValidateBounds(bounds);
        BlockPosition clipboardOrigin = request.ClipboardOrigin ?? bounds.Min;
        BlockPosition worldEditOffset = new(
            checked(bounds.Min.X - clipboardOrigin.X),
            checked(bounds.Min.Y - clipboardOrigin.Y),
            checked(bounds.Min.Z - clipboardOrigin.Z));

        var blocks = new byte[dimensions.Volume];
        var data = new byte[dimensions.Volume];
        var addBlocks = new byte[(dimensions.Volume + 1) / 2];
        var mappingCache = new Dictionary<BlockState, MinecraftBlockMapping>();
        var mappingCounts = new Dictionary<BlockState, long>();
        long nonAir = EncodeSections(
            sections,
            bounds,
            dimensions,
            blocks,
            data,
            addBlocks,
            mappingCache,
            mappingCounts,
            cancellationToken);

        List<MinecraftDiagnostic> diagnostics = BuildDiagnostics(scan, mappingCache, mappingCounts);
        IReadOnlyList<byte[]> encodedBlockEntities = EncodeBlockEntities(
            blockEntities,
            bounds,
            diagnostics,
            cancellationToken);
        string temporary = Path.Combine(
            parentDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using(var file = new FileStream(
                      temporary,
                      FileMode.CreateNew,
                      FileAccess.Write,
                      FileShare.None,
                      64 * 1024,
                      FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                using(var gzip = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true))
                {
                    WriteNbt(
                        gzip,
                        dimensions,
                        clipboardOrigin,
                        worldEditOffset,
                        blocks,
                        data,
                        addBlocks,
                        encodedBlockEntities);
                }
                file.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: false);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        long bytesWritten = new FileInfo(destination).Length;
        MinecraftBlockMappingSummary[] mappings = mappingCache
            .OrderBy(static pair => pair.Key.CanonicalKey, StringComparer.Ordinal)
            .Select(pair => new MinecraftBlockMappingSummary(pair.Value, mappingCounts[pair.Key]))
            .ToArray();
        return new LegacySchematicExportResult(
            destination,
            scan.Dimension,
            bounds,
            clipboardOrigin,
            worldEditOffset,
            nonAir,
            encodedBlockEntities.Count,
            bytesWritten,
            mappings,
            diagnostics);
    }

    private static NormalizedMinecraftSection[] MaterializeSections(
        IEnumerable<NormalizedMinecraftSection> source,
        CancellationToken cancellationToken)
    {
        var sections = new List<NormalizedMinecraftSection>();
        foreach(NormalizedMinecraftSection? section in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(section is null) throw new InvalidDataException("Section 集合不能包含 null。");
            if(sections.Count >= MaximumInputSections)
                throw new InvalidDataException($"Section 数量超过安全上限 {MaximumInputSections:N0}。");
            sections.Add(section);
        }
        if(sections.Count == 0) throw new InvalidDataException("至少需要一个 Section 才能导出 .schematic。");
        return sections.ToArray();
    }

    private static NormalizedMinecraftBlockEntity[] MaterializeBlockEntities(
        IEnumerable<NormalizedMinecraftBlockEntity>? source,
        CancellationToken cancellationToken)
    {
        if(source is null) return [];
        var entities = new List<NormalizedMinecraftBlockEntity>();
        var positions = new HashSet<BlockPosition>();
        long payloadBytes = 0;
        foreach(NormalizedMinecraftBlockEntity? entity in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(entity is null) throw new InvalidDataException("方块实体集合不能包含 null。");
            if(entities.Count >= MaximumBlockEntities)
                throw new InvalidDataException($"方块实体数量超过安全上限 {MaximumBlockEntities:N0}。");
            if(string.IsNullOrWhiteSpace(entity.TypeId))
                throw new InvalidDataException($"坐标 {entity.Position} 的方块实体缺少有效类型 ID。");
            if(entity.OriginalNbt is null)
                throw new InvalidDataException($"方块实体 {entity.TypeId} @ {entity.Position} 缺少原始 NBT 载荷。");
            if(entity.OriginalNbt.Bytes.Length > MaximumBlockEntityPayloadBytes)
            {
                throw new InvalidDataException(
                    $"方块实体 {entity.TypeId} @ {entity.Position} 的 NBT 超过 " +
                    $"{MaximumBlockEntityPayloadBytes:N0} 字节安全上限。");
            }
            payloadBytes = checked(payloadBytes + entity.OriginalNbt.Bytes.Length);
            if(payloadBytes > MaximumTotalBlockEntityPayloadBytes)
                throw new InvalidDataException("方块实体 NBT 总量超过 128 MiB 安全上限。");
            if(!positions.Add(entity.Position))
                throw new InvalidDataException($"方块实体坐标 {entity.Position} 重复；已拒绝歧义导出。");
            entities.Add(entity);
        }
        return entities.ToArray();
    }

    private static InputScan ScanInput(
        IReadOnlyList<NormalizedMinecraftSection> sections,
        BoxSelection? requestedBounds,
        CancellationToken cancellationToken)
    {
        var coordinates = new HashSet<SectionCoordinate>();
        string? dimension = null;
        BlockPosition? minimum = null;
        BlockPosition? maximum = null;
        long croppedNonAir = 0;
        long opaqueFragmentCount = 0;
        foreach(NormalizedMinecraftSection section in sections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateSection(section);
            if(!coordinates.Add(section.Coordinate))
                throw new InvalidDataException($"Section 坐标重复：{section.Coordinate}。");
            dimension ??= section.Coordinate.Dimension;
            if(!string.Equals(dimension, section.Coordinate.Dimension, StringComparison.Ordinal))
                throw new InvalidDataException("一个 .schematic 只能包含单一维度的 Sections。");
            opaqueFragmentCount = checked(opaqueFragmentCount + section.UnknownFragments.Count);

            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                ushort paletteIndex = indices[localIndex];
                if(paletteIndex >= section.Palette.Count)
                {
                    throw new InvalidDataException(
                        $"Section {section.Coordinate} 的索引 {localIndex} 引用了越界 palette[{paletteIndex}]。");
                }
                BlockState state = section.Palette[paletteIndex]
                    ?? throw new InvalidDataException($"Section {section.Coordinate} 的 palette[{paletteIndex}] 为 null。");
                if(state.IsAir) continue;
                BlockPosition position = section.Coordinate.ToBlockPosition(localIndex);
                if(requestedBounds is BoxSelection clip && !clip.Contains(position))
                {
                    croppedNonAir = checked(croppedNonAir + 1);
                    continue;
                }
                minimum = minimum is null ? position : Min(minimum.Value, position);
                maximum = maximum is null ? position : Max(maximum.Value, position);
            }
        }

        BoxSelection? occupied = minimum is null
            ? null
            : new BoxSelection(minimum.Value, maximum!.Value.Offset(1, 1, 1));
        return new InputScan(dimension!, occupied, croppedNonAir, opaqueFragmentCount);
    }

    private static void ValidateSection(NormalizedMinecraftSection section)
    {
        if(string.IsNullOrWhiteSpace(section.Coordinate.Dimension))
            throw new InvalidDataException("Section 维度不能为空。");
        if(section.Palette.Count == 0 || section.PaletteIndices.Length != 4096)
            throw new InvalidDataException($"Section {section.Coordinate} 的规范 palette 布局无效。");
        for(int paletteIndex = 0; paletteIndex < section.Palette.Count; paletteIndex++)
        {
            if(section.Palette[paletteIndex] is null)
                throw new InvalidDataException($"Section {section.Coordinate} 的 palette[{paletteIndex}] 为 null。");
        }
        if(section.UnknownFragments is null)
            throw new InvalidDataException($"Section {section.Coordinate} 的 UnknownFragments 不能为 null。");
    }

    private long EncodeSections(
        IReadOnlyList<NormalizedMinecraftSection> sections,
        BoxSelection bounds,
        SchematicDimensions dimensions,
        Span<byte> blocks,
        Span<byte> data,
        Span<byte> addBlocks,
        IDictionary<BlockState, MinecraftBlockMapping> mappingCache,
        IDictionary<BlockState, long> mappingCounts,
        CancellationToken cancellationToken)
    {
        long nonAir = 0;
        foreach(NormalizedMinecraftSection section in sections)
        {
            ReadOnlySpan<ushort> indices = section.PaletteIndices.Span;
            for(int localIndex = 0; localIndex < indices.Length; localIndex++)
            {
                if((localIndex & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                BlockState state = section.Palette[indices[localIndex]];
                if(state.IsAir) continue;
                BlockPosition position = section.Coordinate.ToBlockPosition(localIndex);
                if(!bounds.Contains(position)) continue;

                MinecraftBlockMapping mapping = ResolveMapping(state, mappingCache);
                LegacyBlockEncoding encoding = mapping.LegacyEncoding!.Value;
                mappingCounts[state] = mappingCounts.TryGetValue(state, out long count) ? checked(count + 1) : 1;
                if(encoding.NumericId == 0) continue;
                int x = position.X - bounds.Min.X;
                int y = position.Y - bounds.Min.Y;
                int z = position.Z - bounds.Min.Z;
                int outputIndex = checked(y * dimensions.Width * dimensions.Length + z * dimensions.Width + x);
                blocks[outputIndex] = (byte)(encoding.NumericId & 0xff);
                data[outputIndex] = encoding.Metadata;
                SetNibble(addBlocks, outputIndex, (byte)(encoding.NumericId >> 8));
                nonAir = checked(nonAir + 1);
            }
        }
        return nonAir;
    }

    private MinecraftBlockMapping ResolveMapping(
        BlockState state,
        IDictionary<BlockState, MinecraftBlockMapping> cache)
    {
        if(cache.TryGetValue(state, out MinecraftBlockMapping? cached)) return cached;
        MinecraftBlockMapping mapping = rules.Resolve(state, MinecraftTargetProfile.Java1122)
            ?? throw new InvalidDataException($"降级规则没有返回 {state.CanonicalKey} 的结果。");
        bool validInvisibleEncoding = mapping.AllowsInvisibleTarget &&
                                      mapping.Target?.IsAir == true &&
                                      mapping.LegacyEncoding is { NumericId: 0, Metadata: 0 };
        if(!state.Equals(mapping.Source) ||
           mapping.Quality == MinecraftBlockMappingQuality.Blocked ||
           mapping.Target is null || mapping.Target.IsAir && !validInvisibleEncoding ||
           mapping.LegacyEncoding is null || mapping.LegacyEncoding.Value.NumericId == 0 && !validInvisibleEncoding)
        {
            throw new InvalidDataException(
                $"非空气方块 {state.CanonicalKey} 没有安全、可见的 1.12.2 ID:data " +
                $"(rule={mapping.RuleId}, quality={mapping.Quality})。");
        }
        cache.Add(state, mapping);
        return mapping;
    }

    private static List<MinecraftDiagnostic> BuildDiagnostics(
        InputScan scan,
        IReadOnlyDictionary<BlockState, MinecraftBlockMapping> mappings,
        IReadOnlyDictionary<BlockState, long> mappingCounts)
    {
        var diagnostics = new List<MinecraftDiagnostic>();
        if(scan.CroppedNonAirBlockCount > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.export.bounds.cropped",
                MinecraftDiagnosticSeverity.Warning,
                $"有 {scan.CroppedNonAirBlockCount:N0} 个非空气方块位于指定 Bounds 外，未写入结构。"));
        }
        if(scan.OpaqueFragmentCount > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.export.opaque.omitted",
                MinecraftDiagnosticSeverity.Warning,
                $"Section 中有 {scan.OpaqueFragmentCount:N0} 个版本专属不透明片段；Legacy .schematic 无法承载，已明确舍弃。"));
        }
        long substituted = mappings
            .Where(static pair => pair.Value.Quality != MinecraftBlockMappingQuality.Exact)
            .Sum(pair => mappingCounts[pair.Key]);
        if(substituted > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.export.blocks.substituted",
                MinecraftDiagnosticSeverity.Information,
                $"有 {substituted:N0} 个方块使用 1.12.2 相近或可见兜底方块编码，没有变为空气。"));
        }
        return diagnostics;
    }

    private static IReadOnlyList<byte[]> EncodeBlockEntities(
        IReadOnlyList<NormalizedMinecraftBlockEntity> entities,
        BoxSelection bounds,
        ICollection<MinecraftDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var encoded = new List<byte[]>(entities.Count);
        int cropped = 0;
        foreach(NormalizedMinecraftBlockEntity entity in entities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!bounds.Contains(entity.Position))
            {
                cropped++;
                continue;
            }

            var localPosition = new BlockPosition(
                checked(entity.Position.X - bounds.Min.X),
                checked(entity.Position.Y - bounds.Min.Y),
                checked(entity.Position.Z - bounds.Min.Z));
            LegacyBlockEntityRewriteResult rewritten = LegacyBlockEntityPayloadRewriter.Rewrite(
                entity with { Position = localPosition });
            if(!rewritten.PreservedUnknownData)
            {
                diagnostics.Add(new MinecraftDiagnostic(
                    "schematic.export.block_entity.reduced",
                    MinecraftDiagnosticSeverity.Warning,
                    $"方块实体 {entity.TypeId} @ {entity.Position} 的完整 NBT 无法安全重写；" +
                    $"已保留类型和坐标，但其余字段可能丢失：{rewritten.ReductionReason ?? "未知原因"}",
                    NbtPath: entity.OriginalNbt.NbtPath));
            }
            encoded.Add(rewritten.CompoundPayload);
        }
        if(cropped > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "schematic.export.block_entities.cropped",
                MinecraftDiagnosticSeverity.Warning,
                $"有 {cropped:N0} 个方块实体位于导出 Bounds 外，未写入结构。"));
        }
        return encoded;
    }

    private static void WriteNbt(
        Stream output,
        SchematicDimensions dimensions,
        BlockPosition clipboardOrigin,
        BlockPosition worldEditOffset,
        ReadOnlySpan<byte> blocks,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> addBlocks,
        IReadOnlyList<byte[]> blockEntities)
    {
        var writer = new LegacyNbtWriter(output);
        writer.WriteRootCompoundStart("Schematic");
        writer.WriteShort("Width", checked((short)dimensions.Width));
        writer.WriteShort("Height", checked((short)dimensions.Height));
        writer.WriteShort("Length", checked((short)dimensions.Length));
        writer.WriteString("Materials", "Alpha");
        writer.WriteInt("WEOriginX", clipboardOrigin.X);
        writer.WriteInt("WEOriginY", clipboardOrigin.Y);
        writer.WriteInt("WEOriginZ", clipboardOrigin.Z);
        writer.WriteInt("WEOffsetX", worldEditOffset.X);
        writer.WriteInt("WEOffsetY", worldEditOffset.Y);
        writer.WriteInt("WEOffsetZ", worldEditOffset.Z);
        writer.WriteByteArray("Blocks", blocks);
        writer.WriteByteArray("Data", data);
        writer.WriteByteArray("AddBlocks", addBlocks);
        writer.WriteListStart("Entities", 10, 0);
        writer.WriteListStart("TileEntities", 10, blockEntities.Count);
        foreach(byte[] blockEntity in blockEntities)
            writer.WriteRawCompoundListElement(blockEntity);
        writer.WriteCompoundEnd();
    }

    private static SchematicDimensions ValidateBounds(BoxSelection bounds)
    {
        long width = (long)bounds.MaxExclusive.X - bounds.Min.X;
        long height = (long)bounds.MaxExclusive.Y - bounds.Min.Y;
        long length = (long)bounds.MaxExclusive.Z - bounds.Min.Z;
        if(width > short.MaxValue || height > short.MaxValue || length > short.MaxValue)
            throw new InvalidDataException("Legacy .schematic 的单轴尺寸不能超过 32,767 方块。");
        long volume;
        try
        {
            volume = checked(width * height * length);
        }
        catch(OverflowException exception)
        {
            throw new InvalidDataException("结构体积超出安全范围。", exception);
        }
        if(volume > MaximumVolume)
            throw new InvalidDataException($"结构体积 {volume:N0} 超过安全上限 {MaximumVolume:N0} 方块。");
        return new SchematicDimensions((int)width, (int)height, (int)length, (int)volume);
    }

    private static BoxSelection FullSectionBounds(SectionCoordinate coordinate)
    {
        var minimum = new BlockPosition(
            checked(coordinate.X * 16),
            checked(coordinate.Y * 16),
            checked(coordinate.Z * 16));
        return BoxSelection.FromMinAndSize(minimum, 16, 16, 16);
    }

    private static void SetNibble(Span<byte> values, int index, byte value)
    {
        int packedIndex = index >> 1;
        if((index & 1) == 0)
            values[packedIndex] = (byte)(values[packedIndex] & 0xf0 | value & 0x0f);
        else
            values[packedIndex] = (byte)(values[packedIndex] & 0x0f | (value & 0x0f) << 4);
    }

    private static BlockPosition Min(BlockPosition left, BlockPosition right) => new(
        Math.Min(left.X, right.X),
        Math.Min(left.Y, right.Y),
        Math.Min(left.Z, right.Z));

    private static BlockPosition Max(BlockPosition left, BlockPosition right) => new(
        Math.Max(left.X, right.X),
        Math.Max(left.Y, right.Y),
        Math.Max(left.Z, right.Z));

    private static void TryDelete(string path)
    {
        try
        {
            if(File.Exists(path)) File.Delete(path);
        }
        catch(IOException)
        {
        }
        catch(UnauthorizedAccessException)
        {
        }
    }

    private sealed record InputScan(
        string Dimension,
        BoxSelection? OccupiedBounds,
        long CroppedNonAirBlockCount,
        long OpaqueFragmentCount);

    private readonly record struct SchematicDimensions(int Width, int Height, int Length, int Volume);
}
