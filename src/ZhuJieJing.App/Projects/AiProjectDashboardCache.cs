using System.IO;
using ZhuJieJing.Core.Collaboration;

namespace ZhuJieJing.App.Projects;

internal enum AiProjectFileKind { None, Status, Instruction, Acknowledgement, Batch, Receipt, Scene }

/// <summary>Event-driven protocol cache. Integrity probes compare metadata; unchanged JSON is never deserialized again.</summary>
internal sealed class AiProjectDashboardCache(ZhujieProjectWorkspace workspace)
{
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object changeGate = new();
    private readonly HashSet<string> changedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedDocument<ZhujieInstruction>> instructions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedDocument<ZhujieBatchRecord>> batches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, CachedDocument<ZhujieInstructionAcknowledgement>> acknowledgements = [];
    private readonly Dictionary<string, FileStamp?> receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly CachedDocument<ZhujieArchitectStatus> status = new();
    private AiProjectDashboard? dashboard;
    private bool requestFullRescan = true;
    internal long JsonReadCount { get; private set; }

    public void Invalidate(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if(ClassifyPath(workspace.Layout.ProjectDirectory, fullPath) == AiProjectFileKind.None) return;
        lock(changeGate) changedPaths.Add(fullPath);
    }

    public void RequestIntegrityProbe() { lock(changeGate) requestFullRescan = true; }

    internal static AiProjectFileKind ClassifyPath(string projectDirectory, string path)
    {
        string relative = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/').ToLowerInvariant();
        return relative switch
        {
            "live/architect-status.json" => AiProjectFileKind.Status,
            "live/scene.zjjscene" or "live/scene.zjjscene-wal" or "live/scene.zjjscene-shm" => AiProjectFileKind.Scene,
            "instructions" => AiProjectFileKind.Instruction,
            "acknowledgements" => AiProjectFileKind.Acknowledgement,
            "batches" => AiProjectFileKind.Batch,
            ".zhujie/receipts" => AiProjectFileKind.Receipt,
            _ when IsDirectJson(relative, "instructions/") => AiProjectFileKind.Instruction,
            _ when IsDirectJson(relative, "acknowledgements/") => AiProjectFileKind.Acknowledgement,
            _ when IsDirectJson(relative, "batches/") => AiProjectFileKind.Batch,
            _ when IsDirectJson(relative, ".zhujie/receipts/") => AiProjectFileKind.Receipt,
            _ => AiProjectFileKind.None,
        };
    }

    private static bool IsDirectJson(string relative, string prefix) => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !relative[prefix.Length..].Contains('/') && !relative[prefix.Length..].StartsWith('.');

    public async Task<AiProjectDashboard> ReadAsync(AiProjectRefreshKind kind, CancellationToken cancellationToken = default)
    {
        await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string[] dirty;
            bool full;
            lock(changeGate)
            {
                full = requestFullRescan || dashboard is null || (kind & AiProjectRefreshKind.RevisionIntegrityProbe) != 0;
                requestFullRescan = false;
                dirty = changedPaths.ToArray();
                changedPaths.Clear();
            }
            if(!full && dirty.Length == 0 && dashboard is not null) return dashboard;
            HashSet<string> forced = dirty.Where(File.Exists).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var categories = dirty.Select(path => ClassifyPath(workspace.Layout.ProjectDirectory, path)).ToHashSet();
            bool instructionsChanged = false;
            bool batchesChanged = false;
            bool acknowledgementsChanged = false;
            bool receiptDependenciesChanged = false;
            var errors = new List<string>();
            Dictionary<Guid, ZhujieInstruction> previousInstructions = instructions.Values.Where(item => item.Value is not null)
                .Select(item => item.Value!).DistinctBy(instruction => instruction.InstructionId).ToDictionary(instruction => instruction.InstructionId);

            if(full || categories.Contains(AiProjectFileKind.Instruction))
                instructionsChanged = await RefreshCollectionAsync(instructions, workspace.Layout.InstructionsDirectory,
                    full || dirty.Contains(workspace.Layout.InstructionsDirectory, StringComparer.OrdinalIgnoreCase), dirty, forced,
                    AiProjectFileKind.Instruction, async (path, token) =>
                    {
                        ZhujieInstruction instruction = ZhujieProjectJson.DeserializeInstruction(await ReadProtocolAsync(path, token).ConfigureAwait(false));
                        EnsureProject(instruction.ProjectId);
                        return instruction;
                    }, cancellationToken).ConfigureAwait(false);

            if(full || categories.Contains(AiProjectFileKind.Batch))
                batchesChanged = await RefreshCollectionAsync(batches, workspace.Layout.BatchesDirectory,
                    full || dirty.Contains(workspace.Layout.BatchesDirectory, StringComparer.OrdinalIgnoreCase), dirty, forced,
                    AiProjectFileKind.Batch, async (path, token) =>
                    {
                        ZhujieBatchRecord batch = ZhujieProjectJson.DeserializeBatch(await ReadProtocolAsync(path, token).ConfigureAwait(false));
                        EnsureProject(batch.ProjectId);
                        _ = workspace.Layout.ResolveProjectPath(batch.PlanFile);
                        return batch;
                    }, cancellationToken).ConfigureAwait(false);

            HashSet<string> changedReceiptHashes = new(StringComparer.OrdinalIgnoreCase);
            if(full || categories.Contains(AiProjectFileKind.Receipt))
            {
                IEnumerable<string> paths = full || dirty.Contains(workspace.Layout.ReceiptsDirectory, StringComparer.OrdinalIgnoreCase)
                    ? Directory.EnumerateFiles(workspace.Layout.ReceiptsDirectory, "*.json", SearchOption.TopDirectoryOnly).Concat(receipts.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : dirty.Where(path => ClassifyPath(workspace.Layout.ProjectDirectory, path) == AiProjectFileKind.Receipt && Path.GetExtension(path) == ".json");
                foreach(string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileStamp? current = FileStamp.Read(path);
                    if(!receipts.TryGetValue(path, out FileStamp? previous) || current != previous || forced.Contains(path))
                    {
                        receipts[path] = current;
                        changedReceiptHashes.Add(Path.GetFileNameWithoutExtension(path));
                        receiptDependenciesChanged = true;
                    }
                }
            }

            ZhujieInstruction[] orderedInstructions = instructions.Values.Where(item => item.Value is not null).Select(item => item.Value!)
                .OrderBy(instruction => instruction.CreatedAtUtc).ThenBy(instruction => instruction.InstructionId).ToArray();
            bool instructionSetValid = orderedInstructions.Select(instruction => instruction.InstructionId).Distinct().Count() == orderedInstructions.Length;
            if(!instructionSetValid) errors.Add("指令队列暂不可读：工程中存在重复指令 ID。");
            AddDocumentErrors(instructions, "指令队列暂不可读", errors);
            AddDocumentErrors(batches, "批次记录暂不可读", errors);
            HashSet<Guid> instructionIds = orderedInstructions.Select(instruction => instruction.InstructionId).ToHashSet();
            foreach(Guid id in acknowledgements.Keys.Where(id => !instructionIds.Contains(id)).ToArray())
            {
                acknowledgements.Remove(id);
                acknowledgementsChanged = true;
            }
            foreach(ZhujieInstruction instruction in orderedInstructions.DistinctBy(instruction => instruction.InstructionId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = Path.Combine(workspace.Layout.AcknowledgementsDirectory, $"{instruction.InstructionId:D}.json");
                if(!acknowledgements.TryGetValue(instruction.InstructionId, out var item))
                {
                    item = new();
                    acknowledgements.Add(instruction.InstructionId, item);
                }
                bool instructionChanged = !previousInstructions.TryGetValue(instruction.InstructionId, out ZhujieInstruction? previousInstruction) ||
                    !ReferenceEquals(previousInstruction, instruction);
                bool dependencyChanged = instructionChanged || receiptDependenciesChanged && (item.Error is not null ||
                    item.Value?.PublishedPlanSha256 is string hash && changedReceiptHashes.Contains(hash));
                if(full || !item.Initialized || forced.Contains(path) || dirty.Contains(path, StringComparer.OrdinalIgnoreCase) || dependencyChanged ||
                    dirty.Contains(workspace.Layout.AcknowledgementsDirectory, StringComparer.OrdinalIgnoreCase))
                {
                    acknowledgementsChanged |= await item.RefreshAsync(path, forced.Contains(path) || dependencyChanged || full && item.Error is not null,
                        async (_, token) =>
                        {
                            JsonReadCount++;
                            return await workspace.ReadInstructionAcknowledgementAsync(instruction.InstructionId, token).ConfigureAwait(false);
                        }, cancellationToken).ConfigureAwait(false);
                }
                if(item.Error is not null) errors.Add($"指令 {instruction.InstructionId:D} 回执暂不可读：{item.Error}");
            }

            bool statusDependencyChanged = instructionsChanged || batchesChanged || acknowledgementsChanged || receiptDependenciesChanged;
            bool statusChanged = false;
            if(full || categories.Contains(AiProjectFileKind.Status) || statusDependencyChanged)
                statusChanged = await status.RefreshAsync(workspace.Layout.ArchitectStatusPath,
                    categories.Contains(AiProjectFileKind.Status) || statusDependencyChanged || full && status.Error is not null,
                    async (_, token) => { JsonReadCount++; return await workspace.ReadArchitectStatusAsync(token).ConfigureAwait(false); }, cancellationToken).ConfigureAwait(false);
            if(status.Error is not null) errors.Add($"AI 状态暂不可读：{status.Error}");

            ZhujieBatchRecord[] orderedBatches = batches.Values.Where(item => item.Value is not null).Select(item => item.Value!).OrderBy(batch => batch.Sequence).ToArray();
            bool batchesValid = !batches.Values.Any(item => item.Error is not null);
            for(int index = 0; index < orderedBatches.Length; index++)
                if(orderedBatches[index].Sequence != index + 1)
                {
                    errors.Add($"批次记录暂不可读：批次序号不连续，期望 {index + 1}，实际 {orderedBatches[index].Sequence}。");
                    batchesValid = false;
                    break;
                }
            if(dashboard is not null && !instructionsChanged && !batchesChanged && !acknowledgementsChanged && !statusChanged &&
                string.Equals(dashboard.ReadError, errors.Count == 0 ? null : string.Join("；", errors), StringComparison.Ordinal)) return dashboard;
            IReadOnlyList<AiInstructionHistoryItem> history = instructionSetValid && !instructions.Values.Any(item => item.Error is not null)
                ? orderedInstructions.Select(instruction => CreateHistory(instruction, acknowledgements[instruction.InstructionId].Value)).ToArray()
                : [];
            return dashboard = new AiProjectDashboard(status.Value, history, batchesValid ? orderedBatches : [], errors.Count == 0 ? null : string.Join("；", errors));
        }
        catch
        {
            lock(changeGate) requestFullRescan = true;
            throw;
        }
        finally { readGate.Release(); }
    }

    private async Task<bool> RefreshCollectionAsync<T>(Dictionary<string, CachedDocument<T>> collection, string directory,
        bool scanDirectory, string[] dirty, HashSet<string> forced, AiProjectFileKind category,
        Func<string, CancellationToken, Task<T?>> loader, CancellationToken cancellationToken) where T : class
    {
        string[] paths = scanDirectory
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Concat(collection.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : dirty.Where(path => ClassifyPath(workspace.Layout.ProjectDirectory, path) == category && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        bool changed = false;
        foreach(string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!collection.TryGetValue(path, out var item)) { item = new(); collection.Add(path, item); }
            changed |= await item.RefreshAsync(path, forced.Contains(path) || scanDirectory && item.Error is not null, loader, cancellationToken).ConfigureAwait(false);
            if(item.Initialized && item.Stamp is null && item.Error is null) collection.Remove(path);
        }
        return changed;
    }

    private void EnsureProject(Guid projectId)
    {
        if(projectId != workspace.Manifest.ProjectId) throw new InvalidDataException("协议文件的 projectId 与当前工程不一致。");
    }

    private async Task<byte[]> ReadProtocolAsync(string path, CancellationToken cancellationToken)
    {
        _ = workspace.Layout.ResolveContainedInputPath(path);
        JsonReadCount++;
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long length = stream.Length;
        if(length > ZhujieProjectWorkspace.MaximumProtocolDocumentBytes) throw new InvalidDataException("协议文件超过大小上限。");
        byte[] bytes = new byte[checked((int)length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if(stream.Length != length) throw new IOException("读取期间协议文件发生变化，将在稳定后重试。");
        return bytes;
    }

    private static void AddDocumentErrors<T>(Dictionary<string, CachedDocument<T>> collection, string prefix, List<string> errors) where T : class
    {
        foreach(var pair in collection)
            if(pair.Value.Error is not null) errors.Add($"{prefix}：{Path.GetFileName(pair.Key)} · {pair.Value.Error}");
    }

    private static AiInstructionHistoryItem CreateHistory(ZhujieInstruction instruction, ZhujieInstructionAcknowledgement? acknowledgement) => new(
        instruction.InstructionId,
        instruction.Kind switch { ZhujieInstructionKind.Pause => "暂停施工", ZhujieInstructionKind.Resume => "继续施工", ZhujieInstructionKind.CancelCurrent => "取消当前批次", _ => instruction.Text },
        acknowledgement?.Message ?? "等待 AI 读取",
        acknowledgement?.State switch { ZhujieInstructionAcknowledgementState.Accepted => "已读取", ZhujieInstructionAcknowledgementState.Working => "执行中", ZhujieInstructionAcknowledgementState.Completed => "已完成", ZhujieInstructionAcknowledgementState.Blocked => "受阻", ZhujieInstructionAcknowledgementState.Superseded => "已替代", _ => "排队中" },
        acknowledgement is null, instruction.CreatedAtUtc, acknowledgement?.UpdatedAtUtc);

    private readonly record struct FileStamp(long Length, long LastWriteTicks, long CreationTicks)
    {
        public static FileStamp? Read(string path)
        {
            FileInfo info = new(path);
            return info.Exists ? new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
        }
    }

    private sealed class CachedDocument<T> where T : class
    {
        public bool Initialized { get; private set; }
        public FileStamp? Stamp { get; private set; }
        public T? Value { get; private set; }
        public string? Error { get; private set; }
        public async Task<bool> RefreshAsync(string path, bool force, Func<string, CancellationToken, Task<T?>> loader, CancellationToken cancellationToken)
        {
            FileStamp? stamp = FileStamp.Read(path);
            if(Initialized && !force && stamp == Stamp) return false;
            T? value = null;
            string? error = null;
            try
            {
                if(stamp is not null)
                {
                    value = await loader(path, cancellationToken).ConfigureAwait(false);
                    if(FileStamp.Read(path) != stamp) throw new IOException("读取期间协议文件发生变化，稍后将重试。");
                }
            }
            catch(Exception exception) when(exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException or ZhujieProtocolValidationException or System.Text.Json.JsonException)
            { error = exception.Message; }
            bool changed = !Initialized || !EqualityComparer<T?>.Default.Equals(Value, value) || Error != error || Stamp != stamp;
            Value = value; Error = error; Stamp = stamp; Initialized = true;
            return changed;
        }
    }
}
