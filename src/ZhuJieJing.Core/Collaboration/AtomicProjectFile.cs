using System.Security.Cryptography;

namespace ZhuJieJing.Core.Collaboration;

internal static class AtomicProjectFile
{
    private const int ReplaceAttempts = 6;

    public static async Task WriteAsync(
        string destinationPath,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("目标文件必须具有父目录。", nameof(destinationPath));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using(FileStream stream = new(
                            temporary,
                            FileMode.CreateNew,
                            FileAccess.Write,
                            FileShare.None,
                            64 * 1024,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await MoveWithRetryAsync(temporary, destination, overwrite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if(File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    public static async Task<byte[]> ReadAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if(maximumBytes <= 0 || maximumBytes > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        string fullPath = Path.GetFullPath(path);
        for(int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long initialLength = stream.Length;
                if(initialLength > maximumBytes)
                    throw new InvalidDataException($"协议文件超过 {maximumBytes:N0} 字节上限：{fullPath}");
                byte[] result = GC.AllocateUninitializedArray<byte>(checked((int)initialLength));
                await stream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
                if(stream.Length != initialLength)
                    throw new IOException($"读取期间文件发生变化：{fullPath}");
                return result;
            }
            catch(IOException) when(attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static async Task<string> ComputeSha256Async(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if(maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        string fullPath = Path.GetFullPath(path);
        for(int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                long initialLength = stream.Length;
                if(initialLength > maximumBytes)
                    throw new InvalidDataException($"协议文件超过 {maximumBytes:N0} 字节上限：{fullPath}");
                byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                if(stream.Length != initialLength)
                    throw new IOException($"计算哈希期间文件发生变化：{fullPath}");
                return Convert.ToHexString(hash).ToLowerInvariant();
            }
            catch(IOException) when(attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task MoveWithRetryAsync(
        string source,
        string destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        for(int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch(IOException) when(overwrite && attempt < ReplaceAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
