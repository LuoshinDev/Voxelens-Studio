using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using ZhuJieJing.Core;

namespace ZhuJieJing.Minecraft;

/// <summary>Result of rebuilding source world metadata into a bounded Java 1.12.2 level.dat.</summary>
public sealed record Minecraft1122LevelDatTranscodeResult(
    ReadOnlyMemory<byte> GZipBytes,
    IReadOnlyList<MinecraftDiagnostic> Diagnostics);

/// <summary>
/// Selectively carries version-neutral world settings into a newly generated Java 1.12.2 level.dat. Unknown
/// source tags are parsed only for structural safety and are never copied into the legacy output.
/// </summary>
public static class Minecraft1122LevelDatTranscoder
{
    public const int MaximumStoredBytes = 64 * 1024 * 1024;
    public const int MaximumDecompressedBytes = 64 * 1024 * 1024;

    private const int MaximumNbtDepth = 64;
    private const int MaximumCollectionElements = 1_048_576;
    private const int MaximumVisitedTags = 1_048_576;
    private const double DefaultBorderSize = 60_000_000D;
    private const string VoidFlatGeneratorOptions = "3;minecraft:air;1;";

    private static readonly HashSet<string> SupportedScalarPaths = new(StringComparer.Ordinal)
    {
        "Data.DataVersion",
        "Data.RandomSeed",
        "Data.WorldGenSettings",
        "Data.WorldGenSettings.seed",
        "Data.MapFeatures",
        "Data.WorldGenSettings.generate_features",
        "Data.generatorName",
        "Data.generatorVersion",
        "Data.generatorOptions",
        "Data.GameType",
        "Data.hardcore",
        "Data.allowCommands",
        "Data.Difficulty",
        "Data.DifficultyLocked",
        "Data.Time",
        "Data.DayTime",
        "Data.clearWeatherTime",
        "Data.rainTime",
        "Data.raining",
        "Data.thunderTime",
        "Data.thundering",
        "Data.BorderCenterX",
        "Data.BorderCenterZ",
        "Data.BorderSize",
        "Data.BorderSizeLerpTime",
        "Data.BorderSizeLerpTarget",
        "Data.BorderSafeZone",
        "Data.BorderDamagePerBlock",
        "Data.BorderWarningBlocks",
        "Data.BorderWarningTime",
        "Data.GameRules",
    };

    private static readonly IReadOnlyDictionary<string, string> DefaultGameRules =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doFireTick"] = "true",
            ["mobGriefing"] = "true",
            ["keepInventory"] = "false",
            ["doMobSpawning"] = "true",
            ["doMobLoot"] = "true",
            ["doTileDrops"] = "true",
            ["doEntityDrops"] = "true",
            ["commandBlockOutput"] = "true",
            ["naturalRegeneration"] = "true",
            ["doDaylightCycle"] = "true",
            ["logAdminCommands"] = "true",
            ["showDeathMessages"] = "true",
            ["randomTickSpeed"] = "3",
            ["sendCommandFeedback"] = "true",
            ["reducedDebugInfo"] = "false",
            ["spectatorsGenerateChunks"] = "true",
            ["spawnRadius"] = "10",
            ["disableElytraMovementCheck"] = "false",
            ["maxEntityCramming"] = "24",
            ["doWeatherCycle"] = "true",
            ["doLimitedCrafting"] = "false",
            ["maxCommandChainLength"] = "65536",
            ["announceAdvancements"] = "true",
            ["gameLoopFunction"] = "-",
        };

    private static readonly HashSet<string> BooleanGameRules = new(StringComparer.Ordinal)
    {
        "doFireTick",
        "mobGriefing",
        "keepInventory",
        "doMobSpawning",
        "doMobLoot",
        "doTileDrops",
        "doEntityDrops",
        "commandBlockOutput",
        "naturalRegeneration",
        "doDaylightCycle",
        "logAdminCommands",
        "showDeathMessages",
        "sendCommandFeedback",
        "reducedDebugInfo",
        "spectatorsGenerateChunks",
        "disableElytraMovementCheck",
        "doWeatherCycle",
        "doLimitedCrafting",
        "announceAdvancements",
    };

    private static readonly HashSet<string> NumericalGameRules = new(StringComparer.Ordinal)
    {
        "randomTickSpeed",
        "spawnRadius",
        "maxEntityCramming",
        "maxCommandChainLength",
    };

    private static readonly HashSet<string> LegacyGeneratorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "default",
        "flat",
        "largeBiomes",
        "amplified",
        "customized",
        "debug_all_block_states",
        "default_1_1",
    };

    public static Minecraft1122LevelDatTranscodeResult Transcode(
        MinecraftOpaquePayload sourceLevelMetadata,
        string targetLevelName,
        BlockPosition targetSpawn)
    {
        ArgumentNullException.ThrowIfNull(sourceLevelMetadata);
        if(string.IsNullOrWhiteSpace(targetLevelName))
            throw new ArgumentException("Target level name must not be blank.", nameof(targetLevelName));

        List<MinecraftDiagnostic> diagnostics = new();
        CompatibleSettings settings = ReadCompatibleSettings(sourceLevelMetadata, diagnostics);
        settings.GeneratorName = "flat";
        settings.GeneratorVersion = 0;
        settings.GeneratorOptions = VoidFlatGeneratorOptions;
        settings.MapFeatures = false;
        MinecraftWorldExportProtection.ApplyBaselineGameRules(settings.GameRules);
        diagnostics.Add(new MinecraftDiagnostic(
            "level.transcode.void_generator",
            MinecraftDiagnosticSeverity.Information,
            "输出世界已固定为 Java 1.12.2 纯空气超平坦生成器；未导出的新区块不会生成原版地形。",
            NbtPath: "Data.generatorName"));
        diagnostics.Add(new MinecraftDiagnostic(
            "level.transcode.building_protection",
            MinecraftDiagnosticSeverity.Information,
            "已写入建筑展示保护规则：关闭随机刻、火焰更新、生物生成与生物破坏。",
            NbtPath: "Data.GameRules"));
        byte[] nbt = BuildLevelNbt(targetLevelName, targetSpawn, settings);
        byte[] compressed = CompressGZip(nbt);
        return new Minecraft1122LevelDatTranscodeResult(compressed, diagnostics.ToArray());
    }

    private static CompatibleSettings ReadCompatibleSettings(
        MinecraftOpaquePayload source,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        CompatibleSettings settings = CompatibleSettings.CreateDefault();
        try
        {
            ReadOnlyMemory<byte> stored = source.Bytes;
            if(stored.IsEmpty)
                throw new InvalidDataException("level.dat payload is empty.");
            if(stored.Length > MaximumStoredBytes)
                throw new InvalidDataException($"level.dat exceeds the {MaximumStoredBytes:N0}-byte stored limit.");
            ValidateContentHash(source, stored.Span);

            byte[] decoded = DecodePayload(source.Format, stored.Span);
            Dictionary<string, NbtScalar> fields = new SafeSelectiveNbtReader(decoded).ReadFields();
            ApplyFields(fields, settings, diagnostics);
        }
        catch(Exception exception) when(exception is InvalidDataException or IOException or OverflowException or FormatException)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.source.invalid",
                MinecraftDiagnosticSeverity.Warning,
                $"源 level.dat 无法安全提取兼容字段，已使用 Java 1.12.2 默认元数据：{exception.Message}",
                NbtPath: "Data"));
        }
        return settings;
    }

    private static void ValidateContentHash(MinecraftOpaquePayload source, ReadOnlySpan<byte> bytes)
    {
        if(string.IsNullOrWhiteSpace(source.ContentHash))
            return;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(source.ContentHash);
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException("level.dat content hash is not hexadecimal.", exception);
        }
        if(expected.Length != SHA256.HashSizeInBytes)
            throw new InvalidDataException("level.dat content hash is not a SHA-256 digest.");
        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, actual);
        if(!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("level.dat content hash does not match its payload.");
    }

    private static byte[] DecodePayload(MinecraftOpaquePayloadFormat format, ReadOnlySpan<byte> bytes)
    {
        if(format is not MinecraftOpaquePayloadFormat.RawFile and
           not MinecraftOpaquePayloadFormat.DecompressedNbtDocument)
        {
            throw new InvalidDataException($"Unsupported level.dat payload format {format}.");
        }

        bool isGZip = bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;
        if(format == MinecraftOpaquePayloadFormat.DecompressedNbtDocument && isGZip)
            throw new InvalidDataException("A decompressed NBT payload unexpectedly contains a GZip stream.");
        if(!isGZip)
            return bytes.ToArray();

        using MemoryStream source = new(bytes.ToArray(), writable: false);
        using GZipStream gzip = new(source, CompressionMode.Decompress);
        using MemoryStream destination = new(Math.Min(bytes.Length * 2, 256 * 1024));
        byte[] buffer = new byte[64 * 1024];
        while(true)
        {
            int read = gzip.Read(buffer, 0, buffer.Length);
            if(read == 0)
                break;
            if(destination.Length + read > MaximumDecompressedBytes)
            {
                throw new InvalidDataException(
                    $"level.dat decompressed data exceeds the {MaximumDecompressedBytes:N0}-byte limit.");
            }
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static void ApplyFields(
        IReadOnlyDictionary<string, NbtScalar> fields,
        CompatibleSettings settings,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        bool hasModernWorldGen = ValidateCompound(fields, "Data.WorldGenSettings", diagnostics);
        _ = ValidateCompound(fields, "Data.GameRules", diagnostics);
        long? legacySeed = ReadLong(fields, "Data.RandomSeed", diagnostics);
        long? modernSeed = ReadLong(fields, "Data.WorldGenSettings.seed", diagnostics);
        if(legacySeed is not null && modernSeed is not null && legacySeed != modernSeed)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.seed.conflict",
                MinecraftDiagnosticSeverity.Warning,
                "源 level.dat 同时含有不同的 RandomSeed 与 WorldGenSettings.seed；已采用现代 WorldGenSettings.seed。",
                NbtPath: "Data.WorldGenSettings.seed"));
        }
        settings.Seed = modernSeed ?? legacySeed ?? settings.Seed;
        if(modernSeed is not null)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.seed.modern",
                MinecraftDiagnosticSeverity.Information,
                "已将现代 Data.WorldGenSettings.seed 转为 Java 1.12.2 Data.RandomSeed。",
                NbtPath: "Data.WorldGenSettings.seed"));
        }
        if(hasModernWorldGen)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.worldgen.modern_projected",
                MinecraftDiagnosticSeverity.Information,
                "现代 WorldGenSettings 仅投影 seed 与 generate_features；生成器结构无法无损降级，1.12.2 输出使用 default 生成器。",
                NbtPath: "Data.WorldGenSettings"));
        }

        bool? legacyFeatures = ReadBooleanByte(fields, "Data.MapFeatures", diagnostics);
        bool? modernFeatures = ReadBooleanByte(fields, "Data.WorldGenSettings.generate_features", diagnostics);
        if(legacyFeatures is not null && modernFeatures is not null && legacyFeatures != modernFeatures)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.map_features.conflict",
                MinecraftDiagnosticSeverity.Warning,
                "源 level.dat 的 MapFeatures 与 WorldGenSettings.generate_features 不一致；已采用现代值。",
                NbtPath: "Data.WorldGenSettings.generate_features"));
        }
        settings.MapFeatures = modernFeatures ?? legacyFeatures ?? settings.MapFeatures;

        int? gameType = ReadInt(fields, "Data.GameType", diagnostics);
        if(gameType is >= 0 and <= 3)
            settings.GameType = gameType.Value;
        else if(gameType is not null)
            AddInvalidValue(diagnostics, "Data.GameType", "游戏模式必须在 0–3 之间");

        settings.Hardcore = ReadBooleanByte(fields, "Data.hardcore", diagnostics) ?? settings.Hardcore;
        settings.AllowCommands = ReadBooleanByte(fields, "Data.allowCommands", diagnostics) ?? settings.AllowCommands;
        settings.DifficultyLocked = ReadBooleanByte(fields, "Data.DifficultyLocked", diagnostics) ?? settings.DifficultyLocked;

        byte? difficulty = ReadByte(fields, "Data.Difficulty", diagnostics);
        if(difficulty is <= 3)
            settings.Difficulty = difficulty.Value;
        else if(difficulty is not null)
            AddInvalidValue(diagnostics, "Data.Difficulty", "难度必须在 0–3 之间");

        settings.Time = ReadLong(fields, "Data.Time", diagnostics) ?? settings.Time;
        settings.DayTime = ReadLong(fields, "Data.DayTime", diagnostics) ?? settings.DayTime;
        settings.Raining = ReadBooleanByte(fields, "Data.raining", diagnostics) ?? settings.Raining;
        settings.Thundering = ReadBooleanByte(fields, "Data.thundering", diagnostics) ?? settings.Thundering;
        settings.ClearWeatherTime = ReadNonNegativeInt(fields, "Data.clearWeatherTime", diagnostics) ?? settings.ClearWeatherTime;
        settings.RainTime = ReadNonNegativeInt(fields, "Data.rainTime", diagnostics) ?? settings.RainTime;
        settings.ThunderTime = ReadNonNegativeInt(fields, "Data.thunderTime", diagnostics) ?? settings.ThunderTime;

        settings.BorderCenterX = ReadFiniteDouble(fields, "Data.BorderCenterX", diagnostics) ?? settings.BorderCenterX;
        settings.BorderCenterZ = ReadFiniteDouble(fields, "Data.BorderCenterZ", diagnostics) ?? settings.BorderCenterZ;
        settings.BorderSize = ReadNonNegativeDouble(fields, "Data.BorderSize", diagnostics) ?? settings.BorderSize;
        settings.BorderSizeLerpTime = ReadNonNegativeLong(fields, "Data.BorderSizeLerpTime", diagnostics) ?? settings.BorderSizeLerpTime;
        settings.BorderSizeLerpTarget = ReadNonNegativeDouble(fields, "Data.BorderSizeLerpTarget", diagnostics) ?? settings.BorderSizeLerpTarget;
        settings.BorderSafeZone = ReadNonNegativeDouble(fields, "Data.BorderSafeZone", diagnostics) ?? settings.BorderSafeZone;
        settings.BorderDamagePerBlock = ReadNonNegativeDouble(fields, "Data.BorderDamagePerBlock", diagnostics) ?? settings.BorderDamagePerBlock;
        settings.BorderWarningBlocks = ReadNonNegativeDouble(fields, "Data.BorderWarningBlocks", diagnostics) ?? settings.BorderWarningBlocks;
        settings.BorderWarningTime = ReadNonNegativeDouble(fields, "Data.BorderWarningTime", diagnostics) ?? settings.BorderWarningTime;

        ApplyLegacyGenerator(fields, settings, diagnostics);
        ApplyGameRules(fields, settings, diagnostics);
    }

    private static void ApplyLegacyGenerator(
        IReadOnlyDictionary<string, NbtScalar> fields,
        CompatibleSettings settings,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        int? dataVersion = ReadInt(fields, "Data.DataVersion", diagnostics);
        bool hasModernWorldGen = fields.ContainsKey("Data.WorldGenSettings") ||
                                 fields.ContainsKey("Data.WorldGenSettings.seed") ||
                                 fields.ContainsKey("Data.WorldGenSettings.generate_features");
        bool mayUseLegacyGenerator = !hasModernWorldGen && (dataVersion is <= 1343 || dataVersion is null);
        string? generatorName = ReadString(fields, "Data.generatorName", diagnostics);
        if(generatorName is null)
            return;
        if(!mayUseLegacyGenerator)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.generator.modern_ignored",
                MinecraftDiagnosticSeverity.Information,
                "现代世界的 legacy generator 字段未继承；1.12.2 输出使用 default 生成器。",
                NbtPath: "Data.generatorName"));
            return;
        }
        if(!LegacyGeneratorNames.TryGetValue(generatorName, out string? canonicalName))
        {
            AddInvalidValue(diagnostics, "Data.generatorName", $"不受支持的 1.12.2 生成器 {generatorName}");
            return;
        }

        settings.GeneratorName = canonicalName;
        settings.GeneratorVersion = ReadInt(fields, "Data.generatorVersion", diagnostics) ?? settings.GeneratorVersion;
        settings.GeneratorOptions = ReadString(fields, "Data.generatorOptions", diagnostics) ?? settings.GeneratorOptions;
    }

    private static void ApplyGameRules(
        IReadOnlyDictionary<string, NbtScalar> fields,
        CompatibleSettings settings,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        int filtered = 0;
        foreach((string path, NbtScalar scalar) in fields)
        {
            const string prefix = "Data.GameRules.";
            if(!path.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            string name = path[prefix.Length..];
            if(!DefaultGameRules.ContainsKey(name))
            {
                filtered++;
                continue;
            }
            if(scalar.Type != NbtTagType.String || scalar.Value is not string value)
            {
                AddTypeMismatch(diagnostics, path, NbtTagType.String, scalar.Type);
                continue;
            }

            string? normalized = NormalizeGameRule(name, value);
            if(normalized is null)
            {
                AddInvalidValue(diagnostics, path, $"值 {value} 不兼容 Java 1.12.2");
                continue;
            }
            settings.GameRules[name] = normalized;
        }
        if(filtered > 0)
        {
            diagnostics.Add(new MinecraftDiagnostic(
                "level.transcode.gamerules.filtered",
                MinecraftDiagnosticSeverity.Information,
                $"已忽略 {filtered:N0} 个不属于 Java 1.12.2 白名单的 GameRule。",
                NbtPath: "Data.GameRules"));
        }
    }

    private static string? NormalizeGameRule(string name, string value)
    {
        if(BooleanGameRules.Contains(name))
            return bool.TryParse(value, out bool parsed) ? parsed ? "true" : "false" : null;
        if(NumericalGameRules.Contains(name))
        {
            return int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
                ? parsed.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        if(name == "gameLoopFunction")
            return IsLegacyFunctionIdentifier(value) ? value : null;
        return null;
    }

    private static bool IsLegacyFunctionIdentifier(string value)
    {
        if(value == "-")
            return true;
        if(value.Length is 0 or > 512 || value != value.ToLowerInvariant())
            return false;
        int separator = value.IndexOf(':');
        if(separator == 0 || separator == value.Length - 1 || value.LastIndexOf(':') != separator)
            return false;
        ReadOnlySpan<char> nameSpace = separator < 0 ? ReadOnlySpan<char>.Empty : value.AsSpan(0, separator);
        ReadOnlySpan<char> path = separator < 0 ? value.AsSpan() : value.AsSpan(separator + 1);
        return (nameSpace.IsEmpty || nameSpace.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_.-") < 0) &&
               path.IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789/._-") < 0;
    }

    private static byte[] BuildLevelNbt(string levelName, BlockPosition spawn, CompatibleSettings settings)
    {
        using MemoryStream stream = new(8192);
        LevelNbtWriter writer = new(stream);
        writer.WriteRootCompoundStart();
        writer.WriteCompoundStart("Data");
        writer.WriteInt("DataVersion", 1343);
        writer.WriteInt("version", 19133);
        writer.WriteCompoundStart("Version");
        writer.WriteInt("Id", 1343);
        writer.WriteString("Name", "1.12.2");
        writer.WriteByte("Snapshot", 0);
        writer.WriteCompoundEnd();
        writer.WriteString("LevelName", levelName);
        writer.WriteLong("RandomSeed", settings.Seed);
        writer.WriteString("generatorName", settings.GeneratorName);
        writer.WriteInt("generatorVersion", settings.GeneratorVersion);
        writer.WriteString("generatorOptions", settings.GeneratorOptions);
        writer.WriteInt("GameType", settings.GameType);
        writer.WriteByte("MapFeatures", ToNbtBoolean(settings.MapFeatures));
        writer.WriteByte("hardcore", ToNbtBoolean(settings.Hardcore));
        writer.WriteByte("allowCommands", ToNbtBoolean(settings.AllowCommands));
        writer.WriteByte("initialized", 1);
        writer.WriteByte("Difficulty", settings.Difficulty);
        writer.WriteByte("DifficultyLocked", ToNbtBoolean(settings.DifficultyLocked));
        writer.WriteLong("Time", settings.Time);
        writer.WriteLong("DayTime", settings.DayTime);
        writer.WriteLong("LastPlayed", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        writer.WriteLong("SizeOnDisk", 0);
        writer.WriteInt("SpawnX", spawn.X);
        writer.WriteInt("SpawnY", spawn.Y);
        writer.WriteInt("SpawnZ", spawn.Z);
        writer.WriteInt("clearWeatherTime", settings.ClearWeatherTime);
        writer.WriteInt("rainTime", settings.RainTime);
        writer.WriteByte("raining", ToNbtBoolean(settings.Raining));
        writer.WriteInt("thunderTime", settings.ThunderTime);
        writer.WriteByte("thundering", ToNbtBoolean(settings.Thundering));
        writer.WriteDouble("BorderCenterX", settings.BorderCenterX);
        writer.WriteDouble("BorderCenterZ", settings.BorderCenterZ);
        writer.WriteDouble("BorderSize", settings.BorderSize);
        writer.WriteLong("BorderSizeLerpTime", settings.BorderSizeLerpTime);
        writer.WriteDouble("BorderSizeLerpTarget", settings.BorderSizeLerpTarget);
        writer.WriteDouble("BorderSafeZone", settings.BorderSafeZone);
        writer.WriteDouble("BorderDamagePerBlock", settings.BorderDamagePerBlock);
        writer.WriteDouble("BorderWarningBlocks", settings.BorderWarningBlocks);
        writer.WriteDouble("BorderWarningTime", settings.BorderWarningTime);
        writer.WriteCompoundStart("GameRules");
        foreach((string name, string value) in settings.GameRules.OrderBy(static item => item.Key, StringComparer.Ordinal))
            writer.WriteString(name, value);
        writer.WriteCompoundEnd();
        writer.WriteCompoundEnd();
        writer.WriteCompoundEnd();
        return stream.ToArray();
    }

    private static byte[] CompressGZip(ReadOnlySpan<byte> nbt)
    {
        using MemoryStream stored = new();
        using(GZipStream gzip = new(stored, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(nbt);
        return stored.ToArray();
    }

    private static byte ToNbtBoolean(bool value) => value ? (byte)1 : (byte)0;

    private static NbtScalar? Find(
        IReadOnlyDictionary<string, NbtScalar> fields,
        string path,
        NbtTagType expected,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        if(!fields.TryGetValue(path, out NbtScalar scalar))
            return null;
        if(scalar.Type == expected)
            return scalar;
        AddTypeMismatch(diagnostics, path, expected, scalar.Type);
        return null;
    }

    private static bool ValidateCompound(
        IReadOnlyDictionary<string, NbtScalar> fields,
        string path,
        ICollection<MinecraftDiagnostic> diagnostics)
    {
        if(!fields.TryGetValue(path, out NbtScalar scalar))
            return false;
        if(scalar.Type == NbtTagType.Compound)
            return true;
        AddTypeMismatch(diagnostics, path, NbtTagType.Compound, scalar.Type);
        return false;
    }

    private static byte? ReadByte(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics) =>
        Find(fields, path, NbtTagType.Byte, diagnostics)?.Value as byte?;

    private static int? ReadInt(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics) =>
        Find(fields, path, NbtTagType.Int, diagnostics)?.Value as int?;

    private static long? ReadLong(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics) =>
        Find(fields, path, NbtTagType.Long, diagnostics)?.Value as long?;

    private static double? ReadDouble(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics) =>
        Find(fields, path, NbtTagType.Double, diagnostics)?.Value as double?;

    private static string? ReadString(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics) =>
        Find(fields, path, NbtTagType.String, diagnostics)?.Value as string;

    private static bool? ReadBooleanByte(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics)
    {
        byte? value = ReadByte(fields, path, diagnostics);
        if(value is null)
            return null;
        if(value is 0 or 1)
            return value == 1;
        AddInvalidValue(diagnostics, path, "布尔 TAG_Byte 必须为 0 或 1");
        return null;
    }

    private static int? ReadNonNegativeInt(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics)
    {
        int? value = ReadInt(fields, path, diagnostics);
        if(value is null || value >= 0)
            return value;
        AddInvalidValue(diagnostics, path, "值不能为负数");
        return null;
    }

    private static long? ReadNonNegativeLong(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics)
    {
        long? value = ReadLong(fields, path, diagnostics);
        if(value is null || value >= 0)
            return value;
        AddInvalidValue(diagnostics, path, "值不能为负数");
        return null;
    }

    private static double? ReadFiniteDouble(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics)
    {
        double? value = ReadDouble(fields, path, diagnostics);
        if(value is null || double.IsFinite(value.Value))
            return value;
        AddInvalidValue(diagnostics, path, "值必须为有限 double");
        return null;
    }

    private static double? ReadNonNegativeDouble(IReadOnlyDictionary<string, NbtScalar> fields, string path, ICollection<MinecraftDiagnostic> diagnostics)
    {
        double? value = ReadFiniteDouble(fields, path, diagnostics);
        if(value is null || value >= 0)
            return value;
        AddInvalidValue(diagnostics, path, "值不能为负数");
        return null;
    }

    private static void AddTypeMismatch(
        ICollection<MinecraftDiagnostic> diagnostics,
        string path,
        NbtTagType expected,
        NbtTagType actual) => diagnostics.Add(new MinecraftDiagnostic(
        "level.transcode.field.type_mismatch",
        MinecraftDiagnosticSeverity.Warning,
        $"{path} 应为 {expected}，实际为 {actual}；已使用安全默认值。",
        NbtPath: path));

    private static void AddInvalidValue(ICollection<MinecraftDiagnostic> diagnostics, string path, string reason) =>
        diagnostics.Add(new MinecraftDiagnostic(
            "level.transcode.field.invalid_value",
            MinecraftDiagnosticSeverity.Warning,
            $"{path} 的值无效（{reason}）；已使用安全默认值。",
            NbtPath: path));

    private enum NbtTagType : byte
    {
        End = 0,
        Byte = 1,
        Short = 2,
        Int = 3,
        Long = 4,
        Float = 5,
        Double = 6,
        ByteArray = 7,
        String = 8,
        List = 9,
        Compound = 10,
        IntArray = 11,
        LongArray = 12,
    }

    private readonly record struct NbtScalar(NbtTagType Type, object? Value);

    private sealed class CompatibleSettings
    {
        public long Seed { get; set; }
        public string GeneratorName { get; set; } = "default";
        public int GeneratorVersion { get; set; } = 1;
        public string GeneratorOptions { get; set; } = string.Empty;
        public int GameType { get; set; } = 1;
        public bool MapFeatures { get; set; } = true;
        public bool Hardcore { get; set; }
        public bool AllowCommands { get; set; } = true;
        public byte Difficulty { get; set; } = 2;
        public bool DifficultyLocked { get; set; }
        public long Time { get; set; }
        public long DayTime { get; set; } = 6000;
        public int ClearWeatherTime { get; set; }
        public int RainTime { get; set; }
        public bool Raining { get; set; }
        public int ThunderTime { get; set; }
        public bool Thundering { get; set; }
        public double BorderCenterX { get; set; }
        public double BorderCenterZ { get; set; }
        public double BorderSize { get; set; } = DefaultBorderSize;
        public long BorderSizeLerpTime { get; set; }
        public double BorderSizeLerpTarget { get; set; } = DefaultBorderSize;
        public double BorderSafeZone { get; set; } = 5D;
        public double BorderDamagePerBlock { get; set; } = 0.2D;
        public double BorderWarningBlocks { get; set; } = 5D;
        public double BorderWarningTime { get; set; } = 15D;
        public Dictionary<string, string> GameRules { get; } = new(DefaultGameRules, StringComparer.Ordinal);

        public static CompatibleSettings CreateDefault() => new();
    }

    private ref struct SafeSelectiveNbtReader
    {
        private readonly ReadOnlySpan<byte> source;
        private readonly Dictionary<string, NbtScalar> fields;
        private int position;
        private int visitedTags;

        public SafeSelectiveNbtReader(ReadOnlySpan<byte> source)
        {
            this.source = source;
            fields = new Dictionary<string, NbtScalar>(StringComparer.Ordinal);
            position = 0;
            visitedTags = 0;
        }

        public Dictionary<string, NbtScalar> ReadFields()
        {
            NbtTagType rootType = ReadTagType();
            if(rootType != NbtTagType.Compound)
                throw new InvalidDataException($"NBT root must be TAG_Compound, found {rootType}.");
            _ = ReadModifiedUtf8();
            ReadCompound(string.Empty, 0);
            if(position != source.Length)
                throw new InvalidDataException("Trailing bytes follow the NBT root compound.");
            return fields;
        }

        private void ReadCompound(string parentPath, int depth)
        {
            EnsureDepth(depth);
            while(true)
            {
                NbtTagType type = ReadTagType();
                if(type == NbtTagType.End)
                    return;
                CountTag();
                string name = ReadModifiedUtf8();
                string path = parentPath.Length == 0 ? name : $"{parentPath}.{name}";
                ReadPayload(type, path, depth + 1, capture: ShouldCapture(parentPath, name));
            }
        }

        private void ReadPayload(NbtTagType type, string path, int depth, bool capture)
        {
            EnsureDepth(depth);
            switch(type)
            {
                case NbtTagType.Byte:
                    Store(path, type, ReadByte(), capture);
                    break;
                case NbtTagType.Short:
                    short shortValue = ReadInt16();
                    Store(path, type, shortValue, capture);
                    break;
                case NbtTagType.Int:
                    int intValue = ReadInt32();
                    Store(path, type, intValue, capture);
                    break;
                case NbtTagType.Long:
                    long longValue = ReadInt64();
                    Store(path, type, longValue, capture);
                    break;
                case NbtTagType.Float:
                    int floatBits = ReadInt32();
                    Store(path, type, BitConverter.Int32BitsToSingle(floatBits), capture);
                    break;
                case NbtTagType.Double:
                    long doubleBits = ReadInt64();
                    Store(path, type, BitConverter.Int64BitsToDouble(doubleBits), capture);
                    break;
                case NbtTagType.ByteArray:
                    Store(path, type, null, capture);
                    SkipCollection(1);
                    break;
                case NbtTagType.String:
                    string stringValue = ReadModifiedUtf8();
                    Store(path, type, stringValue, capture);
                    break;
                case NbtTagType.List:
                    Store(path, type, null, capture);
                    ReadList(path, depth);
                    break;
                case NbtTagType.Compound:
                    Store(path, type, null, capture);
                    ReadCompound(path, depth);
                    break;
                case NbtTagType.IntArray:
                    Store(path, type, null, capture);
                    SkipCollection(sizeof(int));
                    break;
                case NbtTagType.LongArray:
                    Store(path, type, null, capture);
                    SkipCollection(sizeof(long));
                    break;
                default:
                    throw new InvalidDataException($"Unknown NBT tag type {(byte)type} at {path}.");
            }
        }

        private void ReadList(string path, int depth)
        {
            NbtTagType elementType = ReadTagType();
            int count = ReadCollectionLength();
            if(count > 0 && elementType == NbtTagType.End)
                throw new InvalidDataException($"Non-empty NBT list {path} uses TAG_End elements.");
            for(int index = 0; index < count; index++)
            {
                CountTag();
                ReadPayload(elementType, $"{path}[{index}]", depth + 1, capture: false);
            }
        }

        private void Store(string path, NbtTagType type, object? value, bool capture)
        {
            if(!capture)
                return;
            if(!fields.TryAdd(path, new NbtScalar(type, value)))
                throw new InvalidDataException($"Duplicate supported NBT field {path}.");
        }

        private static bool ShouldCapture(string parentPath, string name)
        {
            string path = parentPath.Length == 0 ? name : $"{parentPath}.{name}";
            return SupportedScalarPaths.Contains(path) || parentPath == "Data.GameRules";
        }

        private void SkipCollection(int elementSize)
        {
            int count = ReadCollectionLength();
            long bytes = checked((long)count * elementSize);
            if(bytes > int.MaxValue)
                throw new InvalidDataException("NBT array byte length exceeds Int32.");
            Skip((int)bytes);
        }

        private int ReadCollectionLength()
        {
            int count = ReadInt32();
            if(count < 0 || count > MaximumCollectionElements)
                throw new InvalidDataException($"NBT collection length {count:N0} exceeds its safe limit.");
            return count;
        }

        private NbtTagType ReadTagType()
        {
            byte raw = ReadByte();
            if(raw > (byte)NbtTagType.LongArray)
                throw new InvalidDataException($"Unknown NBT tag type {raw}.");
            return (NbtTagType)raw;
        }

        private byte ReadByte()
        {
            EnsureAvailable(1);
            return source[position++];
        }

        private short ReadInt16()
        {
            EnsureAvailable(sizeof(short));
            short value = BinaryPrimitives.ReadInt16BigEndian(source.Slice(position, sizeof(short)));
            position += sizeof(short);
            return value;
        }

        private int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            int value = BinaryPrimitives.ReadInt32BigEndian(source.Slice(position, sizeof(int)));
            position += sizeof(int);
            return value;
        }

        private long ReadInt64()
        {
            EnsureAvailable(sizeof(long));
            long value = BinaryPrimitives.ReadInt64BigEndian(source.Slice(position, sizeof(long)));
            position += sizeof(long);
            return value;
        }

        private string ReadModifiedUtf8()
        {
            ushort byteLength = unchecked((ushort)ReadInt16());
            EnsureAvailable(byteLength);
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
                    if(first == 0)
                        throw new InvalidDataException("Modified UTF-8 contains a raw NUL byte.");
                    characters[characterCount++] = (char)first;
                    continue;
                }
                if((first & 0xe0) == 0xc0)
                {
                    if(index >= bytes.Length)
                        throw new InvalidDataException("Truncated two-byte Modified UTF-8 sequence.");
                    byte second = bytes[index++];
                    if((second & 0xc0) != 0x80)
                        throw new InvalidDataException("Invalid Modified UTF-8 continuation byte.");
                    int value = (first & 0x1f) << 6 | second & 0x3f;
                    if(value != 0 && value < 0x80)
                        throw new InvalidDataException("Overlong Modified UTF-8 sequence.");
                    characters[characterCount++] = (char)value;
                    continue;
                }
                if((first & 0xf0) == 0xe0)
                {
                    if(index + 1 >= bytes.Length)
                        throw new InvalidDataException("Truncated three-byte Modified UTF-8 sequence.");
                    byte second = bytes[index++];
                    byte third = bytes[index++];
                    if((second & 0xc0) != 0x80 || (third & 0xc0) != 0x80)
                        throw new InvalidDataException("Invalid Modified UTF-8 continuation byte.");
                    int value = (first & 0x0f) << 12 | (second & 0x3f) << 6 | third & 0x3f;
                    if(value < 0x800)
                        throw new InvalidDataException("Overlong Modified UTF-8 sequence.");
                    characters[characterCount++] = (char)value;
                    continue;
                }
                throw new InvalidDataException("Unsupported four-byte Modified UTF-8 sequence.");
            }
            return new string(characters, 0, characterCount);
        }

        private void CountTag()
        {
            visitedTags++;
            if(visitedTags > MaximumVisitedTags)
                throw new InvalidDataException($"NBT tag count exceeds the {MaximumVisitedTags:N0}-tag limit.");
        }

        private void Skip(int count)
        {
            EnsureAvailable(count);
            position += count;
        }

        private void EnsureAvailable(int count)
        {
            if(count < 0 || position > source.Length - count)
                throw new InvalidDataException("NBT data is truncated or has an invalid length.");
        }

        private static void EnsureDepth(int depth)
        {
            if(depth > MaximumNbtDepth)
                throw new InvalidDataException($"NBT depth exceeds the {MaximumNbtDepth}-level limit.");
        }
    }

    private sealed class LevelNbtWriter
    {
        private readonly Stream stream;

        public LevelNbtWriter(Stream stream) => this.stream = stream;

        public void WriteRootCompoundStart()
        {
            stream.WriteByte((byte)NbtTagType.Compound);
            WriteStringPayload(string.Empty);
        }

        public void WriteCompoundStart(string name) => WriteHeader(NbtTagType.Compound, name);

        public void WriteCompoundEnd() => stream.WriteByte((byte)NbtTagType.End);

        public void WriteByte(string name, byte value)
        {
            WriteHeader(NbtTagType.Byte, name);
            stream.WriteByte(value);
        }

        public void WriteInt(string name, int value)
        {
            WriteHeader(NbtTagType.Int, name);
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void WriteLong(string name, long value)
        {
            WriteHeader(NbtTagType.Long, name);
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        public void WriteDouble(string name, double value) => WriteLongWithType(name, BitConverter.DoubleToInt64Bits(value), NbtTagType.Double);

        public void WriteString(string name, string value)
        {
            WriteHeader(NbtTagType.String, name);
            WriteStringPayload(value);
        }

        private void WriteLongWithType(string name, long value, NbtTagType type)
        {
            WriteHeader(type, name);
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            stream.Write(bytes);
        }

        private void WriteHeader(NbtTagType type, string name)
        {
            stream.WriteByte((byte)type);
            WriteStringPayload(name);
        }

        private void WriteStringPayload(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            byte[] encoded = EncodeModifiedUtf8(value);
            if(encoded.Length > ushort.MaxValue)
                throw new InvalidDataException("NBT string exceeds the 65,535-byte Modified UTF-8 limit.");
            Span<byte> length = stackalloc byte[sizeof(ushort)];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)encoded.Length);
            stream.Write(length);
            stream.Write(encoded);
        }

        private static byte[] EncodeModifiedUtf8(string value)
        {
            using MemoryStream encoded = new(value.Length * 3);
            foreach(char character in value)
            {
                if(character is >= '\u0001' and <= '\u007f')
                {
                    encoded.WriteByte((byte)character);
                }
                else if(character <= '\u07ff')
                {
                    encoded.WriteByte((byte)(0xc0 | character >> 6 & 0x1f));
                    encoded.WriteByte((byte)(0x80 | character & 0x3f));
                }
                else
                {
                    encoded.WriteByte((byte)(0xe0 | character >> 12 & 0x0f));
                    encoded.WriteByte((byte)(0x80 | character >> 6 & 0x3f));
                    encoded.WriteByte((byte)(0x80 | character & 0x3f));
                }
            }
            return encoded.ToArray();
        }
    }
}
