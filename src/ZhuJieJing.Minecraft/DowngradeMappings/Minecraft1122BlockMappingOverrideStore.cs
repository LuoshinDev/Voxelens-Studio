using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZhuJieJing.Minecraft;

/// <summary>Atomic JSON persistence for user-authored modern-to-1.12.2 replacements.</summary>
public sealed class Minecraft1122BlockMappingOverrideStore
{
    private const int CurrentFormatVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public Minecraft1122BlockMappingOverrideStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = Path.GetFullPath(filePath);
    }

    public event EventHandler? Changed;

    public string FilePath { get; }

    public static Minecraft1122BlockMappingOverrideStore CreateDefault()
    {
        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new Minecraft1122BlockMappingOverrideStore(
            Path.Combine(applicationData, "筑界镜", "minecraft-1.12.2-block-mappings.json"));
    }

    public async ValueTask<Minecraft1122BlockMappingOverrideSet> LoadAsync(CancellationToken cancellationToken = default)
    {
        if(!File.Exists(FilePath)) return Minecraft1122BlockMappingOverrideSet.Empty;
        await using FileStream stream = new(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        MappingDocument? document = await JsonSerializer.DeserializeAsync<MappingDocument>(stream, JsonOptions, cancellationToken);
        if(document is null) throw new InvalidDataException($"方块映射文件为空：{FilePath}");
        if(document.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"不支持的方块映射格式版本 {document.FormatVersion}，当前支持 {CurrentFormatVersion}。");
        if(!string.Equals(document.TargetVersion, "1.12.2", StringComparison.Ordinal))
            throw new InvalidDataException($"方块映射目标必须是 Java 1.12.2，实际为 {document.TargetVersion}。");
        return new Minecraft1122BlockMappingOverrideSet(document.Mappings ?? []);
    }

    public async ValueTask SaveAsync(Minecraft1122BlockMappingOverrideSet mappings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        string directory = Path.GetDirectoryName(FilePath)
            ?? throw new InvalidOperationException($"方块映射路径没有父目录：{FilePath}");
        Directory.CreateDirectory(directory);
        string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using(var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var document = new MappingDocument(CurrentFormatVersion, "1.12.2", mappings.Entries);
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if(File.Exists(temporary)) File.Delete(temporary);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask<Minecraft1122BlockMappingOverrideSet> RemoveAsync(string sourceKey, CancellationToken cancellationToken = default)
    {
        Minecraft1122BlockMappingOverrideSet current = await LoadAsync(cancellationToken);
        Minecraft1122BlockMappingOverrideSet updated = current.Without(sourceKey);
        if(updated.Count != current.Count) await SaveAsync(updated, cancellationToken);
        return updated;
    }

    private sealed record MappingDocument(
        int FormatVersion,
        string TargetVersion,
        IReadOnlyList<Minecraft1122BlockMappingOverride>? Mappings);
}
