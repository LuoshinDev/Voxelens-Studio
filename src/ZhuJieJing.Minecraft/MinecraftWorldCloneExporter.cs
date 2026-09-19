using System.Buffers;
using System.Security.Cryptography;

namespace ZhuJieJing.Minecraft;

/// <summary>Request for a verified original-version export of one mounted Java world.</summary>
public sealed record MinecraftWorldCloneRequest(
    string DestinationDirectory,
    string ExpectedSourceRevision,
    IReadOnlyMinecraftWorld SourceWorld);

public enum MinecraftWorldCloneStage
{
    ValidatingSource,
    PreparingStagingDirectory,
    CopyingFiles,
    ApplyingWorldProtection,
    CommittingDirectory,
    Complete,
}

public sealed record MinecraftWorldCloneProgress(
    MinecraftWorldCloneStage Stage,
    long FilesCopied,
    long BytesCopied,
    string? CurrentRelativePath,
    string Message);

public sealed record MinecraftWorldCloneResult(
    string OutputDirectory,
    string SourceRevision,
    long FileCount,
    long ByteCount,
    long SkippedSessionLockCount,
    MinecraftWorldExportProtectionResult Protection);

/// <summary>
/// Copies a content-hashed Java world mount into a new sibling staging directory, applies the export-only building
/// protection pass there, and publishes it with one directory move. The mounted source is never rewritten and an
/// existing destination is never replaced.
/// </summary>
public sealed class MinecraftWorldCloneExporter
{
    private const int CopyBufferBytes = 128 * 1024;

    public Task<MinecraftWorldCloneResult> ExportAsync(
        MinecraftWorldCloneRequest request,
        IProgress<MinecraftWorldCloneProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => ExportCoreAsync(request, progress, cancellationToken, null);

    internal Task<MinecraftWorldCloneResult> ExportTransformedAsync(MinecraftWorldCloneRequest request, Func<string, CancellationToken, Task> transform, IProgress<MinecraftWorldCloneProgress>? progress, CancellationToken cancellationToken)
        => ExportCoreAsync(request, progress, cancellationToken, transform);

    private async Task<MinecraftWorldCloneResult> ExportCoreAsync(MinecraftWorldCloneRequest request, IProgress<MinecraftWorldCloneProgress>? progress, CancellationToken cancellationToken, Func<string, CancellationToken, Task>? transform)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyMinecraftWorld sourceWorld = request.SourceWorld;
        if(sourceWorld is not IReadOnlyMinecraftWorldFileSource fileSource)
        {
            throw new NotSupportedException(
                "The mounted world does not expose the complete read-only file source required for cloning.");
        }
        if(sourceWorld.Descriptor.SourceRevisionStrength != MinecraftSourceRevisionStrength.ContentHash)
        {
            throw new InvalidOperationException(
                "A complete world clone requires a mount created with content fingerprints enabled.");
        }
        if(!string.Equals(
               request.ExpectedSourceRevision,
               sourceWorld.Descriptor.SourceRevision,
               StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected source revision {request.ExpectedSourceRevision}, but the mounted world is " +
                $"{sourceWorld.Descriptor.SourceRevision}.");
        }

        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceWorld.Descriptor.RootPath));
        string destination = ValidateDestination(request.DestinationDirectory, sourceRoot);
        string parent = Path.GetDirectoryName(destination)!;
        string outputName = Path.GetFileName(destination);

        Report(progress, MinecraftWorldCloneStage.ValidatingSource, 0, 0, null, "正在验证源世界");
        await EnsureSourceStableAsync(sourceWorld, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(parent);
        string stagingDirectory = Path.Combine(
            parent,
            $".{outputName}.zjj-clone-staging-{Guid.NewGuid():N}");
        if(Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
            throw new IOException($"Generated clone staging path already exists: {stagingDirectory}");

        long filesCopied = 0;
        long bytesCopied = 0;
        long skippedSessionLocks = 0;
        bool foundLevelDat = false;
        MinecraftWorldExportProtectionResult? protection = null;
        HashSet<string> copiedPaths = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            Report(
                progress,
                MinecraftWorldCloneStage.PreparingStagingDirectory,
                filesCopied,
                bytesCopied,
                null,
                "正在创建临时世界目录");
            Directory.CreateDirectory(stagingDirectory);

            await foreach(MinecraftFileFingerprint file in fileSource
                               .EnumerateWorldFilesAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidateFileDescriptor(file);
                if(IsSessionLock(file.RelativePath))
                {
                    skippedSessionLocks = checked(skippedSessionLocks + 1);
                    continue;
                }
                if(!copiedPaths.Add(file.RelativePath))
                    throw new InvalidDataException($"Duplicate mounted world file path: {file.RelativePath}");

                string outputPath = ResolveClonePath(stagingDirectory, file.RelativePath);
                string? outputParent = Path.GetDirectoryName(outputPath);
                if(outputParent is null)
                    throw new InvalidDataException($"World file has no safe destination parent: {file.RelativePath}");
                Directory.CreateDirectory(outputParent);

                Report(
                    progress,
                    MinecraftWorldCloneStage.CopyingFiles,
                    filesCopied,
                    bytesCopied,
                    file.RelativePath,
                    "正在复制世界文件");
                long copied = await CopyAndVerifyAsync(
                    fileSource,
                    file,
                    outputPath,
                    cancellationToken).ConfigureAwait(false);
                File.SetLastWriteTimeUtc(outputPath, file.LastWriteTimeUtc.UtcDateTime);
                filesCopied = checked(filesCopied + 1);
                bytesCopied = checked(bytesCopied + copied);
                foundLevelDat |= file.RelativePath.Equals("level.dat", StringComparison.OrdinalIgnoreCase);
            }

            if(!foundLevelDat)
                throw new InvalidDataException("The mounted world file source did not contain level.dat.");

            Report(
                progress,
                MinecraftWorldCloneStage.ApplyingWorldProtection,
                filesCopied,
                bytesCopied,
                null,
                "正在写入建筑展示保护规则并清理流体调度刻");
            if(transform is null)
                protection = await MinecraftWorldExportProtection.ApplyAsync(stagingDirectory, cancellationToken).ConfigureAwait(false);
            else
            {
                await transform(stagingDirectory, cancellationToken).ConfigureAwait(false);
                protection = new MinecraftWorldExportProtectionResult(0, 0, 0, 0, 0, 0);
            }

            Report(
                progress,
                MinecraftWorldCloneStage.ValidatingSource,
                filesCopied,
                bytesCopied,
                null,
                "正在复核源世界");
            await EnsureSourceStableAsync(sourceWorld, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Report(
                progress,
                MinecraftWorldCloneStage.CommittingDirectory,
                filesCopied,
                bytesCopied,
                null,
                "正在提交世界目录");
            if(Directory.Exists(destination) || File.Exists(destination))
                throw new IOException($"World clone destination appeared during export: {destination}");
            WindowsDirectoryMove.Move(stagingDirectory, destination);

            Report(
                progress,
                MinecraftWorldCloneStage.Complete,
                filesCopied,
                bytesCopied,
                null,
                "世界文件夹克隆完成");
            return new MinecraftWorldCloneResult(
                destination,
                sourceWorld.Descriptor.SourceRevision,
                filesCopied,
                bytesCopied,
                skippedSessionLocks,
                protection ?? throw new InvalidOperationException("World protection result is missing."));
        }
        catch(Exception exception)
        {
            Exception? cleanupFailure = TryDeleteStagingDirectory(stagingDirectory, parent);
            if(cleanupFailure is not null)
            {
                throw new AggregateException(
                    "World clone failed and its staging directory could not be removed.",
                    exception,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static void ValidateRequest(MinecraftWorldCloneRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.SourceWorld);
        if(string.IsNullOrWhiteSpace(request.DestinationDirectory))
            throw new ArgumentException("DestinationDirectory cannot be empty.", nameof(request));
        if(string.IsNullOrWhiteSpace(request.ExpectedSourceRevision))
            throw new ArgumentException("ExpectedSourceRevision cannot be empty.", nameof(request));
    }

    private static string ValidateDestination(string requestedPath, string sourceRoot)
    {
        string destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        string? parent = Path.GetDirectoryName(destination);
        string name = Path.GetFileName(destination);
        if(parent is null || name.Length == 0 ||
           string.Equals(destination, Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A world clone destination cannot be a filesystem root.");
        }
        if(IsSameOrDescendant(destination, sourceRoot) || IsSameOrDescendant(sourceRoot, destination))
            throw new InvalidOperationException("A world clone destination must be outside the source world tree.");
        if(Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"World clone destination already exists: {destination}");
        if(Directory.Exists(sourceRoot) && IsReparsePoint(sourceRoot))
            throw new IOException("A reparse-point source root cannot be cloned safely.");
        if(Directory.Exists(parent) && IsReparsePoint(parent))
            throw new IOException("A reparse-point destination parent cannot be used safely.");
        return destination;
    }

    private static void ValidateFileDescriptor(MinecraftFileFingerprint file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if(string.IsNullOrWhiteSpace(file.RelativePath) || Path.IsPathRooted(file.RelativePath))
            throw new InvalidDataException("A mounted world file path must be non-empty and relative.");
        if(file.Length < 0)
            throw new InvalidDataException($"World file has a negative length: {file.RelativePath}");
        if(IsSessionLock(file.RelativePath))
            return;
        if(string.IsNullOrWhiteSpace(file.ContentHash))
            throw new InvalidDataException($"World file has no content fingerprint: {file.RelativePath}");
        try
        {
            if(Convert.FromHexString(file.ContentHash).Length != SHA256.HashSizeInBytes)
                throw new InvalidDataException($"World file has a non-SHA-256 fingerprint: {file.RelativePath}");
        }
        catch(FormatException exception)
        {
            throw new InvalidDataException($"World file has an invalid content fingerprint: {file.RelativePath}", exception);
        }
    }

    private static async Task<long> CopyAndVerifyAsync(
        IReadOnlyMinecraftWorldFileSource source,
        MinecraftFileFingerprint file,
        string outputPath,
        CancellationToken cancellationToken)
    {
        byte[] expectedHash = Convert.FromHexString(file.ContentHash!);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream input = await source.OpenWorldFileAsync(file, cancellationToken).ConfigureAwait(false);
            await using FileStream output = new(
                outputPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long total = 0;
            while(true)
            {
                int read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if(read == 0) break;
                total = checked(total + read);
                if(total > file.Length)
                    throw new IOException($"Source world file grew while cloning: {file.RelativePath}");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if(total != file.Length)
                throw new IOException($"Source world file length changed while cloning: {file.RelativePath}");
            byte[] actualHash = hash.GetHashAndReset();
            if(!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
                throw new IOException($"Source world file content changed while cloning: {file.RelativePath}");
            if(output.Length != total)
                throw new IOException($"Cloned world file length does not match its source: {file.RelativePath}");
            return total;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task EnsureSourceStableAsync(
        IReadOnlyMinecraftWorld source,
        CancellationToken cancellationToken)
    {
        WorldSourceValidationResult validation = await source.ValidateSourceAsync(cancellationToken).ConfigureAwait(false);
        if(validation.IsStable) return;
        string changed = validation.ChangedRelativePaths.Count == 0
            ? "源文件无法验证"
            : string.Join(", ", validation.ChangedRelativePaths.Take(8));
        throw new IOException($"Mounted source world changed before clone commit: {changed}");
    }

    private static string ResolveClonePath(string stagingDirectory, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string outputPath = MinecraftSourceFiles.ResolveRelativePath(stagingDirectory, normalized);
        string safeRelative = MinecraftSourceFiles.NormalizeRelativePath(stagingDirectory, outputPath);
        if(!safeRelative.Equals(normalized, StringComparison.Ordinal))
            throw new InvalidDataException($"World file path is not canonical: {relativePath}");
        return outputPath;
    }

    private static bool IsSessionLock(string relativePath) =>
        Path.GetFileName(relativePath).Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrDescendant(string candidate, string ancestor)
    {
        string relative = Path.GetRelativePath(ancestor, candidate);
        return relative == "." ||
               !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static Exception? TryDeleteStagingDirectory(string stagingDirectory, string expectedParent)
    {
        try
        {
            if(!Directory.Exists(stagingDirectory)) return null;
            string resolved = Path.GetFullPath(stagingDirectory);
            string? parent = Path.GetDirectoryName(resolved);
            string name = Path.GetFileName(resolved);
            if(!string.Equals(parent, Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
               !name.Contains(".zjj-clone-staging-", StringComparison.Ordinal))
            {
                return new InvalidOperationException($"Refused to recursively delete an unverified staging path: {resolved}");
            }
            Directory.Delete(resolved, recursive: true);
            return null;
        }
        catch(Exception exception)
        {
            return exception;
        }
    }

    private static void Report(
        IProgress<MinecraftWorldCloneProgress>? progress,
        MinecraftWorldCloneStage stage,
        long filesCopied,
        long bytesCopied,
        string? currentRelativePath,
        string message)
    {
        if(progress is null) return;
        try
        {
            progress.Report(new MinecraftWorldCloneProgress(
                stage,
                filesCopied,
                bytesCopied,
                currentRelativePath,
                message));
        }
        catch
        {
            // Progress is observational; a broken observer must not turn an already committed clone into failure.
        }
    }
}
