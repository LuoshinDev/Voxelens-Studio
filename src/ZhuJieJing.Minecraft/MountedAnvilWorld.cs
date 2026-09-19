namespace ZhuJieJing.Minecraft;

internal sealed class MountedAnvilWorld : IReadOnlyMinecraftWorld, IReadOnlyMinecraftWorldFileSource
{
    private readonly string rootPath;
    private readonly IReadOnlyDictionary<string, MinecraftFileFingerprint> fingerprints;
    private readonly IReadOnlyDictionary<string, MinecraftAuxiliaryFile> auxiliaryFiles;
    private readonly MinecraftOpaquePayload levelMetadata;
    private bool disposed;

    public MountedAnvilWorld(
        string rootPath,
        MinecraftWorldDescriptor descriptor,
        AnvilChunkIndex chunkIndex,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> fingerprints,
        IEnumerable<MinecraftAuxiliaryFile> auxiliaryFiles,
        MinecraftOpaquePayload levelMetadata,
        bool forceFingerprintVerification)
    {
        this.rootPath = rootPath;
        Descriptor = descriptor;
        ChunkIndex = chunkIndex;
        this.fingerprints = fingerprints;
        this.auxiliaryFiles = auxiliaryFiles.ToDictionary(
            static file => file.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        this.levelMetadata = levelMetadata;
        ChunkReader = new AnvilChunkReader(
            rootPath,
            chunkIndex,
            fingerprints,
            forceFingerprintVerification);
    }

    public MinecraftWorldDescriptor Descriptor { get; }

    public IMinecraftChunkIndex ChunkIndex { get; }

    public IMinecraftChunkReader ChunkReader { get; }

    public ValueTask<MinecraftOpaquePayload> ReadLevelMetadataAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(levelMetadata);
    }

    public async ValueTask<WorldSourceValidationResult> ValidateSourceAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> currentFiles;
        try
        {
            currentFiles = MinecraftSourceFiles.EnumerateWorldFiles(rootPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MinecraftDiagnostic failure = new(
                "source.enumeration.failed",
                MinecraftDiagnosticSeverity.Error,
                $"无法重新枚举源世界：{exception.Message}");
            return new WorldSourceValidationResult(false, Array.Empty<string>(), new[] { failure });
        }

        HashSet<string> currentRelativePaths = new(
            currentFiles.Select(path => MinecraftSourceFiles.NormalizeRelativePath(rootPath, path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string currentPath in currentRelativePaths)
        {
            if (!fingerprints.ContainsKey(currentPath))
                changed.Add(currentPath);
        }

        foreach (MinecraftFileFingerprint fingerprint in fingerprints.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!currentRelativePaths.Contains(fingerprint.RelativePath) ||
                !await MinecraftSourceFiles.MatchesFingerprintAsync(
                    rootPath,
                    fingerprint,
                    cancellationToken).ConfigureAwait(false))
            {
                changed.Add(fingerprint.RelativePath);
            }
        }

        string[] changedPaths = changed.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        MinecraftDiagnostic[] diagnostics = changedPaths.Select(path => new MinecraftDiagnostic(
            "source.file.changed",
            MinecraftDiagnosticSeverity.Error,
            $"挂载后的源文件已发生变化：{path}")).ToArray();
        return new WorldSourceValidationResult(changedPaths.Length == 0, changedPaths, diagnostics);
    }

    public async IAsyncEnumerable<MinecraftAuxiliaryFile> EnumerateAuxiliaryFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Task.CompletedTask.ConfigureAwait(false);
        foreach (MinecraftAuxiliaryFile file in auxiliaryFiles.Values.OrderBy(
                     static item => item.RelativePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    public ValueTask<Stream> OpenAuxiliaryFileAsync(
        MinecraftAuxiliaryFile file,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if (!auxiliaryFiles.TryGetValue(file.RelativePath, out MinecraftAuxiliaryFile? mountedFile) ||
            mountedFile != file)
        {
            throw new InvalidOperationException("The auxiliary file descriptor does not belong to this mount.");
        }

        string fullPath = MinecraftSourceFiles.ResolveRelativePath(rootPath, mountedFile.RelativePath);
        Stream stream = MinecraftSourceFiles.OpenRead(fullPath, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public async IAsyncEnumerable<MinecraftFileFingerprint> EnumerateWorldFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await Task.CompletedTask.ConfigureAwait(false);
        foreach(MinecraftFileFingerprint file in fingerprints.Values.OrderBy(
                    static item => item.RelativePath,
                    StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    public ValueTask<Stream> OpenWorldFileAsync(
        MinecraftFileFingerprint file,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(file);
        cancellationToken.ThrowIfCancellationRequested();
        if(!fingerprints.TryGetValue(file.RelativePath, out MinecraftFileFingerprint? mountedFile) ||
           mountedFile != file)
        {
            throw new InvalidOperationException("The world file descriptor does not belong to this mount.");
        }

        string fullPath = MinecraftSourceFiles.ResolveRelativePath(rootPath, mountedFile.RelativePath);
        Stream stream = MinecraftSourceFiles.OpenRead(fullPath, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        return ((AnvilChunkReader)ChunkReader).DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
