using System.Security.Cryptography;
using System.Text;

namespace ZhuJieJing.Minecraft;

internal static class MinecraftSourceFiles
{
    internal const FileShare ReadShare = FileShare.ReadWrite | FileShare.Delete;

    public static FileStream OpenRead(string path, FileOptions options = FileOptions.Asynchronous | FileOptions.RandomAccess) =>
        new(path, FileMode.Open, FileAccess.Read, ReadShare, 64 * 1024, options);

    public static string NormalizeRelativePath(string rootPath, string fullPath)
    {
        string root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        string resolved = Path.GetFullPath(fullPath);

        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Source path escapes the mounted world: {fullPath}");

        string relative = Path.GetRelativePath(rootPath, resolved).Replace('\\', '/');
        if (relative is "." or ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new InvalidDataException($"Source path escapes the mounted world: {fullPath}");

        return relative;
    }

    public static string ResolveRelativePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException("A mounted source path must be non-empty and relative.");

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string resolved = Path.GetFullPath(Path.Combine(rootPath, normalized));
        _ = NormalizeRelativePath(rootPath, resolved);
        return resolved;
    }

    public static IReadOnlyList<string> EnumerateWorldFiles(string rootPath)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        return Directory.EnumerateFiles(rootPath, "*", options)
            .Select(Path.GetFullPath)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static async ValueTask<MinecraftFileFingerprint> CaptureFingerprintAsync(
        string rootPath,
        string fullPath,
        bool includeContentHash,
        CancellationToken cancellationToken)
    {
        string relativePath = NormalizeRelativePath(rootPath, fullPath);
        FileSnapshot before = Snapshot(fullPath);
        string? hash = null;

        if (includeContentHash)
        {
            await using FileStream stream = OpenRead(fullPath, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            hash = Convert.ToHexString(digest);
        }

        FileSnapshot after = Snapshot(fullPath);
        if (before != after)
            throw new IOException($"Source file changed while it was being fingerprinted: {relativePath}");

        return new MinecraftFileFingerprint(relativePath, before.Length, before.LastWriteTimeUtc, hash);
    }

    public static async ValueTask<bool> MatchesFingerprintAsync(
        string rootPath,
        MinecraftFileFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = ResolveRelativePath(rootPath, fingerprint.RelativePath);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (!File.Exists(fullPath))
            return false;

        FileSnapshot snapshot;
        try
        {
            snapshot = Snapshot(fullPath);
        }
        catch (IOException)
        {
            return false;
        }

        if (snapshot.Length != fingerprint.Length || snapshot.LastWriteTimeUtc != fingerprint.LastWriteTimeUtc)
            return false;

        if (fingerprint.ContentHash is null)
            return true;

        try
        {
            await using FileStream stream = OpenRead(fullPath, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(fingerprint.ContentHash),
                digest);
        }
        catch (IOException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Fast guard for the chunk hot path. The mount already captured the optional content hash; unchanged
    /// length and write time prove that normal filesystem writes have not invalidated the indexed offsets.
    /// A full content-hash audit remains available through ValidateSourceAsync.
    /// </summary>
    public static bool MatchesFingerprintMetadata(string rootPath, MinecraftFileFingerprint fingerprint)
    {
        string fullPath;
        try
        {
            fullPath = ResolveRelativePath(rootPath, fingerprint.RelativePath);
            if (!File.Exists(fullPath))
                return false;

            FileSnapshot snapshot = Snapshot(fullPath);
            return snapshot.Length == fingerprint.Length &&
                   snapshot.LastWriteTimeUtc == fingerprint.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    public static string ComputeRevision(IEnumerable<MinecraftFileFingerprint> fingerprints)
    {
        StringBuilder source = new();
        foreach (MinecraftFileFingerprint fingerprint in fingerprints.OrderBy(
                     static item => item.RelativePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            source.Append(fingerprint.RelativePath)
                .Append('\0')
                .Append(fingerprint.Length)
                .Append('\0')
                .Append(fingerprint.LastWriteTimeUtc.UtcTicks)
                .Append('\0')
                .Append(fingerprint.ContentHash ?? "-")
                .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    public static async ValueTask<byte[]> ReadAllBoundedAsync(
        string path,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        FileInfo information = new(path);
        if (!information.Exists)
            throw new FileNotFoundException("Minecraft source file was not found.", path);
        if (information.Length > maximumBytes)
            throw new InvalidDataException($"Source file exceeds the safe {maximumBytes:N0}-byte limit: {path}");

        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)information.Length));
        await using FileStream stream = OpenRead(path, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static FileSnapshot Snapshot(string path)
    {
        FileInfo information = new(path);
        information.Refresh();
        if (!information.Exists)
            throw new FileNotFoundException("Minecraft source file was not found.", path);

        DateTime utc = DateTime.SpecifyKind(information.LastWriteTimeUtc, DateTimeKind.Utc);
        return new FileSnapshot(information.Length, new DateTimeOffset(utc));
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private readonly record struct FileSnapshot(long Length, DateTimeOffset LastWriteTimeUtc);
}
