using System.Security.Cryptography;

namespace ZhuJieJing.Minecraft;

internal enum PreparedWorldDirectoryTransactionState
{
    Prepared,
    CommitFailedNeedsRecovery,
    Committed,
    Finalized,
    RolledBack,
}

/// <summary>
/// Shared same-volume directory transaction used by destructive world rewrites. Its rollback directory is
/// temporary crash-safety state, not a persistent user backup.
/// </summary>
internal sealed class PreparedWorldDirectoryTransaction : IAsyncDisposable
{
    private readonly object transitionGate = new();
    private readonly string parentDirectory;
    private readonly string rollbackDirectory;
    private readonly string stagingMarker;
    private readonly string rollbackMarker;
    private readonly string operationName;
    private readonly IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles;
    private PreparedWorldDirectoryTransactionState state = PreparedWorldDirectoryTransactionState.Prepared;

    internal PreparedWorldDirectoryTransaction(
        string sourceDirectory,
        string stagingDirectory,
        string rollbackDirectory,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles,
        string stagingMarker,
        string rollbackMarker,
        string operationName)
    {
        SourceDirectory = sourceDirectory;
        StagingDirectory = stagingDirectory;
        this.rollbackDirectory = rollbackDirectory;
        this.sourceFiles = sourceFiles;
        this.stagingMarker = stagingMarker;
        this.rollbackMarker = rollbackMarker;
        this.operationName = operationName;
        parentDirectory = Path.GetDirectoryName(sourceDirectory) ??
                          throw new InvalidDataException($"{operationName}源世界必须有父目录。");
    }

    internal string SourceDirectory { get; }

    internal string StagingDirectory { get; }

    internal PreparedWorldDirectoryTransactionState State
    {
        get
        {
            lock(transitionGate) return state;
        }
    }

    internal string? CleanupWarning { get; private set; }

    internal string? ResidualDirectoryPath { get; private set; }

    internal void Commit()
    {
        lock(transitionGate)
        {
            EnsureState(PreparedWorldDirectoryTransactionState.Prepared);
            EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
            EnsureSafeTransactionPath(StagingDirectory, parentDirectory, stagingMarker, mustExist: true);
            EnsureSafeTransactionPath(rollbackDirectory, parentDirectory, rollbackMarker, mustExist: false);
            EnsureDirectoryTreeHasNoReparsePoints(SourceDirectory, operationName);
            EnsureSourceContentStillMatches(SourceDirectory, sourceFiles, operationName);
            EnsureWorldIsNotActivelyLocked(SourceDirectory, operationName);

            WindowsDirectoryMove.Move(SourceDirectory, rollbackDirectory);
            state = PreparedWorldDirectoryTransactionState.CommitFailedNeedsRecovery;
            try
            {
                WindowsDirectoryMove.Move(StagingDirectory, SourceDirectory);
                state = PreparedWorldDirectoryTransactionState.Committed;
            }
            catch(Exception commitFailure)
            {
                try
                {
                    WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
                    state = PreparedWorldDirectoryTransactionState.Prepared;
                }
                catch(Exception restoreFailure)
                {
                    throw new AggregateException(
                        $"{operationName}提交失败；原世界仍位于临时回滚目录 {rollbackDirectory}，自动恢复也失败。",
                        commitFailure,
                        restoreFailure);
                }

                throw;
            }
        }
    }

    internal void FinalizeCommit()
    {
        lock(transitionGate)
        {
            EnsureState(PreparedWorldDirectoryTransactionState.Committed);
            EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
            state = PreparedWorldDirectoryTransactionState.Finalized;
            try
            {
                DeleteVerifiedTransactionDirectory(rollbackDirectory, parentDirectory, rollbackMarker);
            }
            catch(Exception exception)
            {
                ResidualDirectoryPath = rollbackDirectory;
                CleanupWarning =
                    $"{operationName}结果已经生效，但临时回滚目录未能完整清理：{rollbackDirectory}；{exception.Message}";
                throw new IOException(
                    $"{operationName}结果已经生效，但临时回滚目录未能完整清理：{rollbackDirectory}",
                    exception);
            }
        }
    }

    internal void Rollback()
    {
        lock(transitionGate)
        {
            if(state == PreparedWorldDirectoryTransactionState.RolledBack) return;
            if(state == PreparedWorldDirectoryTransactionState.Finalized)
                throw new InvalidOperationException($"{operationName}事务已经完成，临时回滚目录已删除。");

            if(state == PreparedWorldDirectoryTransactionState.Prepared)
            {
                DeleteVerifiedTransactionDirectory(StagingDirectory, parentDirectory, stagingMarker);
                state = PreparedWorldDirectoryTransactionState.RolledBack;
                return;
            }

            if(state == PreparedWorldDirectoryTransactionState.CommitFailedNeedsRecovery)
            {
                EnsureSafeTransactionPath(rollbackDirectory, parentDirectory, rollbackMarker, mustExist: true);
                if(Directory.Exists(SourceDirectory) || File.Exists(SourceDirectory))
                {
                    throw new IOException(
                        $"提交失败后世界路径已被占用，未自动覆盖；原世界仍位于 {rollbackDirectory}。");
                }
                WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
                state = PreparedWorldDirectoryTransactionState.RolledBack;
                TryCleanupAfterWorldIsSafe(StagingDirectory, stagingMarker);
                return;
            }

            EnsureSafeTransactionPath(rollbackDirectory, parentDirectory, rollbackMarker, mustExist: true);
            string failedOutput = StagingDirectory;
            EnsureSafeTransactionPath(failedOutput, parentDirectory, stagingMarker, mustExist: false);

            bool movedFailedOutput = false;
            if(Directory.Exists(SourceDirectory))
            {
                EnsureSafeTransactionPath(SourceDirectory, parentDirectory, expectedName: null, mustExist: true);
                WindowsDirectoryMove.Move(SourceDirectory, failedOutput);
                movedFailedOutput = true;
            }
            else if(File.Exists(SourceDirectory))
            {
                throw new IOException($"世界路径被文件占用，无法回滚：{SourceDirectory}");
            }

            try
            {
                WindowsDirectoryMove.Move(rollbackDirectory, SourceDirectory);
            }
            catch(Exception restoreFailure)
            {
                if(movedFailedOutput && !Directory.Exists(SourceDirectory))
                {
                    try
                    {
                        WindowsDirectoryMove.Move(failedOutput, SourceDirectory);
                    }
                    catch(Exception republishFailure)
                    {
                        throw new AggregateException(
                            $"恢复原世界失败；原世界仍位于 {rollbackDirectory}，新世界位于 {failedOutput}。",
                            restoreFailure,
                            republishFailure);
                    }
                }
                throw;
            }

            state = PreparedWorldDirectoryTransactionState.RolledBack;
            if(movedFailedOutput) TryCleanupAfterWorldIsSafe(failedOutput, stagingMarker);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock(transitionGate)
        {
            if(state == PreparedWorldDirectoryTransactionState.Prepared)
            {
                DeleteVerifiedTransactionDirectory(StagingDirectory, parentDirectory, stagingMarker);
                state = PreparedWorldDirectoryTransactionState.RolledBack;
            }
        }
        return ValueTask.CompletedTask;
    }

    internal static void EnsureDirectoryTreeHasNoReparsePoints(string rootPath, string operationName)
    {
        string root = Path.GetFullPath(rootPath);
        Stack<string> directories = new();
        directories.Push(root);
        while(directories.Count > 0)
        {
            string directory = directories.Pop();
            FileAttributes directoryAttributes = File.GetAttributes(directory);
            if((directoryAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"{operationName}不接受重解析点目录：{directory}");
            foreach(string entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"{operationName}不接受目录树内的重解析点：{entry}");
                if((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
            }
        }
    }

    internal static void DeleteDirectoryTreeWithoutFollowingReparsePoints(string rootPath)
    {
        FileAttributes rootAttributes = File.GetAttributes(rootPath);
        if((rootAttributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"拒绝递归删除重解析点根目录：{rootPath}");
        foreach(string entry in Directory.EnumerateFileSystemEntries(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if((attributes & FileAttributes.Directory) != 0)
            {
                if((attributes & FileAttributes.ReparsePoint) != 0)
                    Directory.Delete(entry, recursive: false);
                else
                    DeleteDirectoryTreeWithoutFollowingReparsePoints(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(rootPath, recursive: false);
    }

    private void EnsureState(PreparedWorldDirectoryTransactionState expected)
    {
        if(state != expected)
            throw new InvalidOperationException($"{operationName}事务当前为 {state}，要求状态为 {expected}。");
    }

    private void TryCleanupAfterWorldIsSafe(string path, string expectedName)
    {
        try
        {
            DeleteVerifiedTransactionDirectory(path, parentDirectory, expectedName);
        }
        catch(Exception exception)
        {
            ResidualDirectoryPath = path;
            CleanupWarning = $"原世界已经恢复，但临时目录未能清理：{path}；{exception.Message}";
        }
    }

    private static void EnsureSourceContentStillMatches(
        string sourceDirectory,
        IReadOnlyDictionary<string, MinecraftFileFingerprint> sourceFiles,
        string operationName)
    {
        IReadOnlyList<string> currentPaths = MinecraftSourceFiles.EnumerateWorldFiles(sourceDirectory);
        HashSet<string> currentRelative = currentPaths
            .Select(path => MinecraftSourceFiles.NormalizeRelativePath(sourceDirectory, path))
            .Where(static path => !IsSessionLock(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expectedRelative = sourceFiles.Keys.Where(static path => !IsSessionLock(path)).ToArray();
        if(currentRelative.Count != expectedRelative.Length || expectedRelative.Any(path => !currentRelative.Contains(path)))
            throw new IOException($"源世界文件集合在准备完成后发生变化，拒绝提交{operationName}结果。");

        foreach(MinecraftFileFingerprint fingerprint in sourceFiles.Values.Where(
                    static file => !IsSessionLock(file.RelativePath)))
        {
            if(!MinecraftSourceFiles.MatchesFingerprintMetadata(sourceDirectory, fingerprint))
                throw new IOException($"源世界文件在准备完成后发生变化：{fingerprint.RelativePath}");
            if(string.IsNullOrWhiteSpace(fingerprint.ContentHash))
                throw new InvalidDataException($"源世界文件缺少内容哈希：{fingerprint.RelativePath}");

            byte[] expected;
            try
            {
                expected = Convert.FromHexString(fingerprint.ContentHash);
            }
            catch(FormatException exception)
            {
                throw new InvalidDataException($"源世界文件内容哈希无效：{fingerprint.RelativePath}", exception);
            }
            string fullPath = MinecraftSourceFiles.ResolveRelativePath(sourceDirectory, fingerprint.RelativePath);
            using FileStream stream = MinecraftSourceFiles.OpenRead(fullPath, FileOptions.SequentialScan);
            byte[] actual = SHA256.HashData(stream);
            if(!CryptographicOperations.FixedTimeEquals(expected, actual) ||
               !MinecraftSourceFiles.MatchesFingerprintMetadata(sourceDirectory, fingerprint))
            {
                throw new IOException($"源世界文件内容在准备完成后发生变化：{fingerprint.RelativePath}");
            }
        }
    }

    private static void EnsureWorldIsNotActivelyLocked(string sourceDirectory, string operationName)
    {
        string path = Path.Combine(sourceDirectory, "session.lock");
        if(!File.Exists(path)) return;
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                1,
                FileOptions.RandomAccess);
            _ = stream.Length;
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"世界可能仍被 Minecraft 打开，已拒绝提交{operationName}结果。", exception);
        }
    }

    private static bool IsSessionLock(string relativePath) =>
        relativePath.Equals("session.lock", StringComparison.OrdinalIgnoreCase);

    private void EnsureSafeTransactionPath(
        string path,
        string expectedParent,
        string? expectedName,
        bool mustExist)
    {
        string resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string parent = Path.GetDirectoryName(resolved) ?? string.Empty;
        string name = Path.GetFileName(resolved);
        if(!parent.Equals(Path.GetFullPath(expectedParent), StringComparison.OrdinalIgnoreCase) ||
           name.Length == 0 ||
           expectedName is not null && !name.Contains(expectedName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"拒绝操作未验证的{operationName}事务路径：{resolved}");
        }
        if(mustExist)
        {
            if(!Directory.Exists(resolved) || File.Exists(resolved))
                throw new DirectoryNotFoundException($"{operationName}事务目录不存在：{resolved}");
            if((File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"{operationName}事务目录不能是重解析点：{resolved}");
        }
        else if(Directory.Exists(resolved) || File.Exists(resolved))
        {
            throw new IOException($"{operationName}事务路径已存在：{resolved}");
        }
    }

    private void DeleteVerifiedTransactionDirectory(
        string path,
        string expectedParent,
        string expectedName)
    {
        if(!Directory.Exists(path)) return;
        EnsureSafeTransactionPath(path, expectedParent, expectedName, mustExist: true);
        DeleteDirectoryTreeWithoutFollowingReparsePoints(path);
    }
}
