using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

internal sealed record LevelDatReadResult(
    string? LevelName,
    MinecraftVersionDescriptor Version,
    BlockPosition? SpawnLocation,
    MinecraftOpaquePayload OriginalPayload,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>
/// Reads only the small metadata subset needed to identify a world. It never rewrites or normalizes the
/// remaining level.dat tree; the complete source file is retained as an opaque payload.
/// </summary>
internal static class MinimalLevelDatReader
{
    private const int MaximumStoredBytes = 64 * 1024 * 1024;
    private const int MaximumNbtBytes = 64 * 1024 * 1024;

    public static async ValueTask<LevelDatReadResult> ReadAsync(
        string rootPath,
        string levelDatPath,
        CancellationToken cancellationToken)
    {
        byte[] storedBytes = await MinecraftSourceFiles.ReadAllBoundedAsync(
            levelDatPath,
            MaximumStoredBytes,
            cancellationToken).ConfigureAwait(false);
        string relativePath = MinecraftSourceFiles.NormalizeRelativePath(rootPath, levelDatPath);
        MinecraftOpaquePayload originalPayload = new(
            MinecraftOpaquePayloadFormat.RawFile,
            storedBytes,
            relativePath,
            null,
            Convert.ToHexString(SHA256.HashData(storedBytes)));
        List<MinecraftDiagnostic> diagnostics = new();
        Dictionary<string, object> fields = new(StringComparer.Ordinal);

        try
        {
            byte[] nbtBytes = storedBytes.Length >= 2 && storedBytes[0] == 0x1f && storedBytes[1] == 0x8b
                ? await DecompressGZipAsync(storedBytes, cancellationToken).ConfigureAwait(false)
                : storedBytes;
            fields = ParseLevelFields(nbtBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.nbt.invalid",
                MinecraftDiagnosticSeverity.Error,
                $"level.dat 的 NBT 无法安全读取：{exception.Message}",
                NbtPath: "Data"));
        }

        string? levelName = Get<string>(fields, "Data.LevelName");
        int? dataVersion = Get<int?>(fields, "Data.DataVersion");
        string? versionName = Get<string>(fields, "Data.Version.Name");
        int? spawnX = Get<int?>(fields, "Data.SpawnX");
        int? spawnY = Get<int?>(fields, "Data.SpawnY");
        int? spawnZ = Get<int?>(fields, "Data.SpawnZ");

        AddMissingDiagnostic(levelName is null, "level.name.missing", "Data.LevelName", diagnostics);
        AddMissingDiagnostic(dataVersion is null, "level.data_version.missing", "Data.DataVersion", diagnostics);
        AddMissingDiagnostic(versionName is null, "level.version_name.missing", "Data.Version.Name", diagnostics);
        AddMissingDiagnostic(
            spawnX is null || spawnY is null || spawnZ is null,
            "level.spawn.missing",
            "Data.SpawnX/Y/Z",
            diagnostics);

        BlockPosition? spawnLocation = spawnX is not null && spawnY is not null && spawnZ is not null
            ? new BlockPosition(spawnX.Value, spawnY.Value, spawnZ.Value)
            : null;
        MinecraftVersionDescriptor version = new(
            dataVersion,
            versionName,
            InferStorageFamily(dataVersion));

        return new LevelDatReadResult(levelName, version, spawnLocation, originalPayload, diagnostics);
    }

    private static T? Get<T>(IReadOnlyDictionary<string, object> fields, string path) =>
        fields.TryGetValue(path, out object? value) && value is T typed ? typed : default;

    private static Dictionary<string, object> ParseLevelFields(ReadOnlySpan<byte> nbtBytes)
    {
        SafeNbtReader reader = new(nbtBytes);
        return reader.ReadLevelFields();
    }

    private static void AddMissingDiagnostic(
        bool missing,
        string code,
        string path,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        if (!missing)
            return;

        diagnostics.Add(new MinecraftDiagnostic(
            code,
            MinecraftDiagnosticSeverity.Warning,
            $"level.dat 未提供 {path}；其余原始数据仍会完整保留。",
            NbtPath: path));
    }

    private static MinecraftStorageFamily InferStorageFamily(int? dataVersion) => dataVersion switch
    {
        null => MinecraftStorageFamily.Unknown,
        <= 1343 => MinecraftStorageFamily.LegacyNumericAnvil,
        < 2844 => MinecraftStorageFamily.FlattenedPalette,
        _ => MinecraftStorageFamily.ModernSectionPalette,
    };

    private static async ValueTask<byte[]> DecompressGZipAsync(
        byte[] sourceBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream source = new(sourceBytes, writable: false);
        using GZipStream gzip = new(source, CompressionMode.Decompress);
        using MemoryStream destination = new(Math.Min(sourceBytes.Length * 2, 256 * 1024));
        byte[] buffer = new byte[64 * 1024];

        while (true)
        {
            int read = await gzip.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaximumNbtBytes)
                throw new InvalidDataException(
                    $"level.dat 解压后超过安全上限 {MaximumNbtBytes:N0} 字节。");

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return destination.ToArray();
    }

    private ref struct SafeNbtReader
    {
        private const int MaximumDepth = 64;
        private const int MaximumCollectionElements = 16 * 1024 * 1024;

        private readonly ReadOnlySpan<byte> source;
        private int position;

        public SafeNbtReader(ReadOnlySpan<byte> source)
        {
            this.source = source;
            position = 0;
        }

        public Dictionary<string, object> ReadLevelFields()
        {
            Dictionary<string, object> fields = new(StringComparer.Ordinal);
            byte rootType = ReadByte();
            if (rootType != 10)
                throw new InvalidDataException($"NBT 根标签必须是 TAG_Compound，实际类型为 {rootType}。");

            _ = ReadModifiedUtf8();
            ReadCompound(string.Empty, 0, fields);
            if (position != source.Length)
                throw new InvalidDataException("NBT 根标签结束后仍有多余字节。");
            return fields;
        }

        private void ReadCompound(string parentPath, int depth, IDictionary<string, object> fields)
        {
            EnsureDepth(depth);
            while (true)
            {
                byte tagType = ReadByte();
                if (tagType == 0)
                    return;

                string name = ReadModifiedUtf8();
                string path = parentPath.Length == 0 ? name : $"{parentPath}.{name}";
                ReadPayload(tagType, path, depth + 1, fields);
            }
        }

        private void ReadPayload(
            byte tagType,
            string path,
            int depth,
            IDictionary<string, object> fields)
        {
            EnsureDepth(depth);
            switch (tagType)
            {
                case 1:
                    Skip(1);
                    break;
                case 2:
                    Skip(2);
                    break;
                case 3:
                    int intValue = ReadInt32();
                    if (path is "Data.DataVersion" or "Data.SpawnX" or "Data.SpawnY" or "Data.SpawnZ")
                        fields[path] = intValue;
                    break;
                case 4:
                    Skip(8);
                    break;
                case 5:
                    Skip(4);
                    break;
                case 6:
                    Skip(8);
                    break;
                case 7:
                    SkipCollection(elementSize: 1);
                    break;
                case 8:
                    string stringValue = ReadModifiedUtf8();
                    if (path is "Data.LevelName" or "Data.Version.Name")
                        fields[path] = stringValue;
                    break;
                case 9:
                    ReadList(path, depth, fields);
                    break;
                case 10:
                    ReadCompound(path, depth, fields);
                    break;
                case 11:
                    SkipCollection(elementSize: 4);
                    break;
                case 12:
                    SkipCollection(elementSize: 8);
                    break;
                default:
                    throw new InvalidDataException($"NBT 包含未知标签类型 {tagType}，路径为 {path}。");
            }
        }

        private void ReadList(string path, int depth, IDictionary<string, object> fields)
        {
            byte elementType = ReadByte();
            int count = ReadCollectionLength();
            if (count > 0 && elementType == 0)
                throw new InvalidDataException($"NBT 列表 {path} 使用了无效的 TAG_End 元素类型。");

            for (int index = 0; index < count; index++)
                ReadPayload(elementType, $"{path}[{index}]", depth + 1, fields);
        }

        private void SkipCollection(int elementSize)
        {
            int count = ReadCollectionLength();
            long byteCount = checked((long)count * elementSize);
            if (byteCount > int.MaxValue)
                throw new InvalidDataException("NBT 数组长度超出安全范围。");
            Skip((int)byteCount);
        }

        private int ReadCollectionLength()
        {
            int count = ReadInt32();
            if (count < 0 || count > MaximumCollectionElements)
                throw new InvalidDataException($"NBT 集合长度 {count} 超出安全范围。");
            return count;
        }

        private int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            int value = BinaryPrimitives.ReadInt32BigEndian(source.Slice(position, sizeof(int)));
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
            ushort byteLength = BinaryPrimitives.ReadUInt16BigEndian(source.Slice(position, sizeof(ushort)));
            position += sizeof(ushort);
            EnsureAvailable(byteLength);
            ReadOnlySpan<byte> bytes = source.Slice(position, byteLength);
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
                        throw new InvalidDataException("Modified UTF-8 字符串包含原始 NUL 字节。");
                    characters[characterCount++] = (char)first;
                    continue;
                }

                if ((first & 0xe0) == 0xc0)
                {
                    if (index >= bytes.Length)
                        throw new InvalidDataException("Modified UTF-8 双字节字符被截断。");
                    byte second = bytes[index++];
                    if ((second & 0xc0) != 0x80)
                        throw new InvalidDataException("Modified UTF-8 包含无效续字节。");
                    int codeUnit = (first & 0x1f) << 6 | second & 0x3f;
                    if (codeUnit != 0 && codeUnit < 0x80)
                        throw new InvalidDataException("Modified UTF-8 包含过长双字节编码。");
                    characters[characterCount++] = (char)codeUnit;
                    continue;
                }

                if ((first & 0xf0) == 0xe0)
                {
                    if (index + 1 >= bytes.Length)
                        throw new InvalidDataException("Modified UTF-8 三字节字符被截断。");
                    byte second = bytes[index++];
                    byte third = bytes[index++];
                    if ((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                        throw new InvalidDataException("Modified UTF-8 包含无效续字节。");
                    int codeUnit = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                    if (codeUnit < 0x800)
                        throw new InvalidDataException("Modified UTF-8 包含过长三字节编码。");
                    characters[characterCount++] = (char)codeUnit;
                    continue;
                }

                throw new InvalidDataException("Modified UTF-8 包含不受支持的四字节编码。");
            }

            return new string(characters, 0, characterCount);
        }

        private void Skip(int count)
        {
            EnsureAvailable(count);
            position += count;
        }

        private void EnsureAvailable(int count)
        {
            if (count < 0 || position > source.Length - count)
                throw new InvalidDataException("NBT 数据被截断或长度字段越界。");
        }

        private static void EnsureDepth(int depth)
        {
            if (depth > MaximumDepth)
                throw new InvalidDataException($"NBT 嵌套深度超过安全上限 {MaximumDepth}。");
        }
    }
}
