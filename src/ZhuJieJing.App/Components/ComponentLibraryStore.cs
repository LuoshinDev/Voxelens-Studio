using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZhuJieJing.Core;
using ZhuJieJing.Core.Components;

namespace ZhuJieJing.App.Components;

public sealed record ComponentLibraryMetadata(string Name, string Category, string[] Tags, string Notes, string SourceDescription);

public sealed record ComponentLibraryEntry
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid ComponentId { get; init; }
    public required int Version { get; init; }
    public required ComponentLibraryMetadata Metadata { get; init; }
    public required string PlanSha256 { get; init; }
    public required string SourceSha256 { get; init; }
    public required string SourcePath { get; init; }
    public required string SourcePlanId { get; init; }
    public required DateTimeOffset ApprovedAtUtc { get; init; }
    public required int BlockCount { get; init; }
    public required BoxSelection Bounds { get; init; }
    public string Approval { get; init; } = "user-confirmed";
    [JsonIgnore] public string DisplayTitle => $"{Metadata.Name} · v{Version}";
    [JsonIgnore] public string Subtitle => UiText.Format($"{Metadata.Category} · {BlockCount:N0} 方块 · {Bounds.MaxExclusive.X} × {Bounds.MaxExclusive.Y} × {Bounds.MaxExclusive.Z}");
    [JsonIgnore] public string SearchText => $"{Metadata.Name} {Metadata.Category} {string.Join(' ', Metadata.Tags)} {Metadata.Notes}";
}

public sealed class ComponentCandidate
{
    private readonly byte[] sourceBytes;
    internal ComponentCandidate(byte[] bytes, string path, ZjjPlan plan, CompilationResult compilation)
    {
        sourceBytes = bytes.ToArray();
        SourcePath = path;
        SourceSha256 = ComponentLibraryStore.Hash(bytes);
        Plan = plan;
        Compilation = compilation;
        Bounds = ComponentPlanBuilder.OccupiedBounds(compilation.Delta);
    }
    public string SourcePath { get; }
    public string SourceSha256 { get; }
    public ZjjPlan Plan { get; }
    public CompilationResult Compilation { get; }
    public BoxSelection Bounds { get; }
    internal byte[] CopySourceBytes() => sourceBytes.ToArray();
}

public sealed record ComponentLibraryListing(IReadOnlyList<ComponentLibraryEntry> Entries, IReadOnlyList<string> Errors);
public sealed record ComponentPlacementRequest(ComponentLibraryEntry Entry, ZjjPlan Plan, string DraftPath, BlockPosition Anchor, int QuarterTurns);

/// <summary>Pending files persist across sessions; only explicit approval publishes an immutable version.</summary>
public sealed partial class ComponentLibraryStore
{
    public const int MaximumCandidateBytes = 8 * 1024 * 1024;
    public const int MaximumStoredPlanBytes = 32 * 1024 * 1024;
    private const int MaximumMetadataBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public ComponentLibraryStore(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);
    public string RootDirectory { get; }
    public static ComponentLibraryStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "筑界镜 Studio", "component-library"));

    public Task<ComponentCandidate> ImportCandidateAsync(string path, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        string fullPath = Path.GetFullPath(path);
        if(!string.Equals(Path.GetExtension(fullPath), ".zz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请选择独立的 .zz 建筑组件候选。");
        byte[] bytes = await ReadBoundedAsync(fullPath, MaximumCandidateBytes, cancellationToken).ConfigureAwait(false);
        return ParseCandidate(bytes, fullPath, cancellationToken);
    }, cancellationToken);

    private static ComponentCandidate ParseCandidate(byte[] bytes, string sourcePath, CancellationToken cancellationToken)
    {
        RejectDuplicateProperties(bytes);
        ZjjPlan plan = ZjjPlanJson.DeserializeStrict(bytes);
        CompilationResult compilation = ComponentPlanBuilder.CompileCandidate(plan, cancellationToken);
        return new ComponentCandidate(bytes, sourcePath, plan, compilation);
    }

    public async Task<ComponentLibraryEntry> ApproveAsync(ComponentCandidate candidate, ComponentLibraryMetadata metadata,
        bool userConfirmed, ComponentLibraryEntry? previousVersion = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if(!userConfirmed) throw new InvalidOperationException("候选必须经过用户明确确认，才能进入已批准组件库。");
        ComponentLibraryMetadata normalizedMetadata = NormalizeMetadata(metadata);
        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? stagingDirectory = null;
        try
        {
            // Rebuild from the frozen source, never from UI-exposed mutable plan collections.
            byte[] sourceBytes = candidate.CopySourceBytes();
            if(IsPendingPath(candidate.SourcePath))
            {
                byte[] current = await ReadBoundedAsync(candidate.SourcePath, MaximumCandidateBytes, cancellationToken).ConfigureAwait(false);
                if(Hash(current) != candidate.SourceSha256)
                    throw new InvalidOperationException("待批准文件已被修改，请重新选择并检查最新 3D 预览后再批准。");
            }
            ComponentCandidate verified = await Task.Run(() => ParseCandidate(sourceBytes, candidate.SourcePath, cancellationToken), cancellationToken).ConfigureAwait(false);
            ZjjPlan normalized = await Task.Run(() => ComponentPlanBuilder.CreatePlacement(verified.Plan,
                verified.Compilation.Delta, new(0, 0, 0), 0, cancellationToken: cancellationToken), cancellationToken).ConfigureAwait(false);
            byte[] planBytes = Encoding.UTF8.GetBytes(ZjjPlanJson.Serialize(normalized));
            if(planBytes.Length > MaximumStoredPlanBytes) throw new InvalidDataException("组件规范化结果过大，请拆分后保存。");
            Guid componentId = previousVersion?.ComponentId ?? Guid.NewGuid();
            if(previousVersion is not null) await LoadApprovedPlanAsync(previousVersion, cancellationToken).ConfigureAwait(false);
            string componentDirectory = Path.Combine(RootDirectory, "approved", componentId.ToString("N"));
            EnsurePlainDirectory(componentDirectory);
            int version = 1;
            if(Directory.Exists(componentDirectory))
            {
                foreach(string directory in Directory.EnumerateDirectories(componentDirectory, "v*"))
                    if(int.TryParse(Path.GetFileName(directory).AsSpan(1), out int found)) version = Math.Max(version, checked(found + 1));
            }
            ComponentLibraryEntry entry = new()
            {
                ComponentId = componentId, Version = version, Metadata = normalizedMetadata,
                PlanSha256 = Hash(planBytes), SourceSha256 = Hash(sourceBytes), SourcePath = verified.SourcePath,
                SourcePlanId = verified.Plan.PlanId, ApprovedAtUtc = DateTimeOffset.UtcNow,
                BlockCount = verified.Compilation.Delta.ChangedVoxelCount,
                Bounds = normalized.Selections["component"],
            };
            Directory.CreateDirectory(componentDirectory);
            stagingDirectory = Path.Combine(componentDirectory, $".pending-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingDirectory);
            await WriteNewFileAsync(Path.Combine(stagingDirectory, "component.zz"), planBytes, cancellationToken).ConfigureAwait(false);
            await WriteNewFileAsync(Path.Combine(stagingDirectory, "source.zz"), sourceBytes, cancellationToken).ConfigureAwait(false);
            await WriteNewFileAsync(Path.Combine(stagingDirectory, "approval.json"), JsonSerializer.SerializeToUtf8Bytes(entry, JsonOptions), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await PublishVersionDirectoryAsync(stagingDirectory, Path.Combine(componentDirectory, $"v{version:D6}"), cancellationToken).ConfigureAwait(false);
            stagingDirectory = null;
            // The immutable approved source is the recovery copy. Never remove a newer AI edit.
            if(IsPendingPath(candidate.SourcePath))
            {
                try
                {
                    using FileStream pending = new(candidate.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Delete);
                    if(Hash(ReadPendingBytes(pending)) == candidate.SourceSha256)
                    {
                        File.Delete(candidate.SourcePath);
                        File.Delete(candidate.SourcePath + ".metadata.json");
                    }
                }
                catch(IOException) { }
                catch(UnauthorizedAccessException) { }
            }
            return entry;
        }
        finally
        {
            if(stagingDirectory is not null && Path.GetFullPath(stagingDirectory).StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(stagingDirectory, recursive: true); }
                catch(IOException) { }
                catch(UnauthorizedAccessException) { }
            }
            saveGate.Release();
        }
    }

    public Task<ComponentLibraryListing> ListAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var entries = new List<ComponentLibraryEntry>();
        var errors = new List<string>();
        string directory = Path.Combine(RootDirectory, "approved");
        EnsurePlainDirectory(directory);
        if(!Directory.Exists(directory)) return new ComponentLibraryListing(entries, errors);
        foreach(string component in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!Guid.TryParseExact(Path.GetFileName(component), "N", out Guid id)) continue;
            try
            {
                EnsurePlainDirectory(component);
                foreach(string version in Directory.EnumerateDirectories(component, "v*"))
                {
                    try
                    {
                        EnsurePlainDirectory(version);
                        byte[] bytes = await ReadBoundedAsync(Path.Combine(version, "approval.json"), MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
                        ComponentLibraryEntry entry = JsonSerializer.Deserialize<ComponentLibraryEntry>(bytes, JsonOptions)
                            ?? throw new InvalidDataException("组件批准记录为空。");
                        ValidateEntry(entry);
                        if(entry.ComponentId != id || Path.GetFileName(version) != $"v{entry.Version:D6}") throw new InvalidDataException("组件版本目录与批准记录不一致。");
                        entries.Add(entry);
                    }
                    catch(Exception exception) when(IsDataError(exception)) { errors.Add($"{Path.GetFileName(component)}/{Path.GetFileName(version)}：{exception.Message}"); }
                }
            }
            catch(Exception exception) when(IsDataError(exception)) { errors.Add($"{Path.GetFileName(component)}：{exception.Message}"); }
        }
        return new ComponentLibraryListing(entries.OrderByDescending(entry => entry.ApprovedAtUtc).ToArray(), errors);
    }, cancellationToken);

    public async Task<ZjjPlan> LoadApprovedPlanAsync(ComponentLibraryEntry entry, CancellationToken cancellationToken = default)
    {
        ValidateEntry(entry);
        string directory = GetVersionDirectory(entry);
        EnsurePlainDirectory(directory);
        byte[] metadata = await ReadBoundedAsync(Path.Combine(directory, "approval.json"), MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
        ComponentLibraryEntry persisted = JsonSerializer.Deserialize<ComponentLibraryEntry>(metadata, JsonOptions)
            ?? throw new InvalidDataException("组件批准记录为空。");
        ValidateEntry(persisted);
        if(persisted.ComponentId != entry.ComponentId || persisted.Version != entry.Version || persisted.PlanSha256 != entry.PlanSha256 || persisted.SourceSha256 != entry.SourceSha256)
            throw new InvalidDataException("组件批准记录已变化，请重新载入组件库。");
        byte[] bytes = await ReadBoundedAsync(Path.Combine(directory, "component.zz"), MaximumStoredPlanBytes, cancellationToken).ConfigureAwait(false);
        byte[] source = await ReadBoundedAsync(Path.Combine(directory, "source.zz"), MaximumCandidateBytes, cancellationToken).ConfigureAwait(false);
        if(Hash(bytes) != entry.PlanSha256 || Hash(source) != entry.SourceSha256) throw new InvalidDataException("组件文件校验失败：内容与用户批准版本不一致，已阻止预览和复用。");
        return await Task.Run(() =>
        {
            RejectDuplicateProperties(bytes);
            ZjjPlan plan = ZjjPlanJson.DeserializeStrict(bytes);
            _ = ComponentPlanBuilder.CompileCandidate(plan, cancellationToken);
            return plan;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ComponentPlacementRequest> CreatePlacementDraftAsync(ComponentLibraryEntry entry, BlockPosition anchor,
        int quarterTurns, string? dimension = null, CancellationToken cancellationToken = default)
    {
        ZjjPlan source = await LoadApprovedPlanAsync(entry, cancellationToken).ConfigureAwait(false);
        ZjjPlan plan = await Task.Run(() =>
        {
            CompilationResult compilation = ComponentPlanBuilder.CompileCandidate(source, cancellationToken);
            return ComponentPlanBuilder.CreatePlacement(source, compilation.Delta, anchor, quarterTurns, dimension, cancellationToken)
                with { PlanId = $"component-{entry.ComponentId:N}-v{entry.Version}-{Guid.NewGuid():N}",
                    Module = new() { Id = $"component-{entry.ComponentId:N}", DisplayName = entry.Metadata.Name } };
        }, cancellationToken).ConfigureAwait(false);
        string directory = Path.Combine(RootDirectory, "drafts");
        EnsurePlainDirectory(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, plan.PlanId + ".zz");
        await WriteNewFileAsync(path, Encoding.UTF8.GetBytes(ZjjPlanJson.Serialize(plan)), cancellationToken).ConfigureAwait(false);
        return new ComponentPlacementRequest(entry, plan, path, anchor, quarterTurns);
    }

    public string GetVersionDirectory(ComponentLibraryEntry entry) => Path.Combine(RootDirectory, "approved", entry.ComponentId.ToString("N"), $"v{entry.Version:D6}");
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsDataError(Exception e) => e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException;

    private static ComponentLibraryMetadata NormalizeMetadata(ComponentLibraryMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string Clean(string? text, int maximum, string field, bool required = false)
        {
            string value = (text ?? "").Trim();
            if(required && value.Length == 0 || value.Length > maximum || value.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t'))
                throw new ArgumentException($"{field}必须为{(required ? " 1 至" : "不超过")} {maximum} 个有效字符。");
            return value;
        }
        string[] tags = (metadata.Tags ?? []).Select(tag => Clean(tag, 32, "标签")).Where(tag => tag.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if(tags.Length > 12) throw new ArgumentException("最多填写 12 个标签。");
        return new(Clean(metadata.Name, 80, "组件名称", true), Clean(metadata.Category, 40, "分类", true), tags,
            Clean(metadata.Notes, 2000, "备注"), Clean(metadata.SourceDescription, 500, "来源说明"));
    }

    private static void ValidateEntry(ComponentLibraryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if(entry.SchemaVersion != 1 || entry.ComponentId == Guid.Empty || entry.Version < 1 || entry.Approval != "user-confirmed" ||
            entry.BlockCount is < 1 or > ComponentPlanBuilder.MaximumComponentBlocks || entry.PlanSha256?.Length != 64 || entry.SourceSha256?.Length != 64 ||
            entry.Bounds.Min != new BlockPosition(0, 0, 0) || entry.Bounds.MaxExclusive.X <= 0 || entry.Bounds.MaxExclusive.Y <= 0 || entry.Bounds.MaxExclusive.Z <= 0)
            throw new InvalidDataException("组件批准记录格式不合法。");
        _ = NormalizeMetadata(entry.Metadata);
    }

    private static void EnsurePlainDirectory(string path)
    {
        DirectoryInfo? current = new(Path.GetFullPath(path));
        while(current is not null)
        {
            if(current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("组件库不接受符号链接或目录联接。");
            current = current.Parent;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        if((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("组件文件不能是符号链接。");
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if(stream.Length is <= 0 || stream.Length > maximum) throw new InvalidDataException($"文件为空或超过 {maximum / 1024 / 1024} MiB 限制。");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteNewFileAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task PublishVersionDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        for(int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch(IOException exception) when(attempt < 4 && !Directory.Exists(destination) &&
                (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                // Defender/indexers may briefly keep a just-written file open on
                // Windows. Retry the same atomic move; never replace a version.
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void RejectDuplicateProperties(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        Visit(document.RootElement);
        static void Visit(JsonElement element)
        {
            if(element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach(JsonProperty property in element.EnumerateObject())
                {
                    if(!names.Add(property.Name)) throw new JsonException($"蓝图出现重复字段：{property.Name}。");
                    Visit(property.Value);
                }
            }
            else if(element.ValueKind == JsonValueKind.Array)
                foreach(JsonElement item in element.EnumerateArray()) Visit(item);
        }
    }
}
