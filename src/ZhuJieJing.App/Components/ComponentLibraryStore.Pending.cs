using System.IO;
using System.Text.Json;

namespace ZhuJieJing.App.Components;

public sealed record PendingComponentDetails(ComponentLibraryMetadata Metadata, ComponentLibraryEntry? PreviousVersion = null);
public sealed record PendingComponentEntry(string Path, DateTime ModifiedAtUtc, PendingComponentDetails Details, string? Error = null)
{
    public ComponentLibraryMetadata Metadata => Details.Metadata;
    public string DisplayTitle => Metadata.Name;
    public string Subtitle => Error is null ? UiText.Format($"待批准 · {ModifiedAtUtc.ToLocalTime():MM-dd HH:mm}") : UiText.Format($"信息损坏 · {Error}");
    public string SearchText => $"{Metadata.Name} {Metadata.Category} {string.Join(' ', Metadata.Tags)} {Metadata.Notes} {System.IO.Path.GetFileName(Path)}";
}

public sealed partial class ComponentLibraryStore
{
    public string PendingDirectory => Path.Combine(RootDirectory, "pending");
    public string ApprovedDirectory => Path.Combine(RootDirectory, "approved");

    public void EnsureDirectories()
    {
        EnsurePlainDirectory(PendingDirectory);
        EnsurePlainDirectory(ApprovedDirectory);
        Directory.CreateDirectory(PendingDirectory);
        Directory.CreateDirectory(ApprovedDirectory);
    }

    public bool IsPendingPath(string path) => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), PendingDirectory, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetExtension(path), ".zz", StringComparison.OrdinalIgnoreCase);

    public async Task<ComponentCandidate> StageCandidateAsync(string path, ComponentLibraryEntry? previousVersion = null, CancellationToken cancellationToken = default)
    {
        ComponentCandidate imported = await ImportCandidateAsync(path, cancellationToken).ConfigureAwait(false);
        EnsureDirectories();
        if(IsPendingPath(imported.SourcePath))
        {
            if(previousVersion is not null)
                await SavePendingDetailsAsync(imported.SourcePath, new(previousVersion.Metadata, previousVersion), cancellationToken).ConfigureAwait(false);
            return imported;
        }
        string destination = Path.Combine(PendingDirectory, $"{Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.zz");
        string temporary = destination + ".tmp";
        try
        {
            await WriteNewFileAsync(temporary, imported.CopySourceBytes(), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
        var details = new PendingComponentDetails(previousVersion?.Metadata ?? new(
            imported.Plan.Module?.DisplayName ?? Path.GetFileNameWithoutExtension(path), "建筑", [], "", $"导入自：{imported.SourcePath}"), previousVersion);
        await SavePendingDetailsAsync(destination, details, cancellationToken).ConfigureAwait(false);
        return await ImportCandidateAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<PendingComponentEntry>> ListPendingAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        EnsureDirectories();
        var result = new List<PendingComponentEntry>();
        foreach(string path in Directory.EnumerateFiles(PendingDirectory, "*.zz"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PendingComponentDetails details = new(new(Path.GetFileNameWithoutExtension(path), "建筑", [], "", "AI 制作候选"));
            string? error = null;
            try
            {
                if(File.Exists(path + ".metadata.json"))
                {
                    byte[] bytes = await ReadBoundedAsync(path + ".metadata.json", MaximumMetadataBytes, cancellationToken).ConfigureAwait(false);
                    var persisted = JsonSerializer.Deserialize<PendingComponentDetails>(bytes, JsonOptions) ?? throw new InvalidDataException("候选信息为空");
                    details = persisted with { Metadata = NormalizeMetadata(persisted.Metadata) };
                }
            }
            catch(Exception exception) when(IsDataError(exception)) { error = exception.Message; }
            // Listing never compiles every candidate: malformed and unfinished files remain selectable/removable.
            result.Add(new(path, File.GetLastWriteTimeUtc(path), details, error));
        }
        return (IReadOnlyList<PendingComponentEntry>)result.OrderByDescending(entry => entry.ModifiedAtUtc).ToArray();
    }, cancellationToken);

    public async Task SavePendingDetailsAsync(string path, PendingComponentDetails details, CancellationToken cancellationToken = default)
    {
        if(!IsPendingPath(path)) throw new InvalidOperationException("只能保存待批准目录内的候选信息。");
        EnsurePlainDirectory(PendingDirectory);
        if(!File.Exists(path)) throw new FileNotFoundException("候选已被移除，请刷新列表。", path);
        details = details with { Metadata = NormalizeMetadata(details.Metadata) };
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteNewFileAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(details, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path + ".metadata.json", overwrite: true);
        }
        finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }

    public void RecyclePending(string path)
    {
        if(!IsPendingPath(path)) throw new InvalidOperationException("只能移除待批准目录内的候选。");
        EnsurePlainDirectory(PendingDirectory);
        foreach(string file in new[] { path, path + ".metadata.json" })
            if(File.Exists(file)) Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(file,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private static byte[] ReadPendingBytes(FileStream stream)
    {
        if(stream.Length > MaximumCandidateBytes) return [];
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
