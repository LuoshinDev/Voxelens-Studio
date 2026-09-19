using System.Globalization;
using System.Security.Cryptography;

namespace ZhuJieJing.Core.Collaboration;

public sealed class ZhujieProjectWorkspace
{
    public const long MaximumPlanBytes = 256L * 1024 * 1024;
    public const long MaximumProtocolDocumentBytes = 1024L * 1024;

    private readonly SemaphoreSlim previewReceiptWriteGate = new(1, 1);

    private ZhujieProjectWorkspace(ZhujieProjectLayout layout, ZhujieProjectManifest manifest)
    {
        Layout = layout;
        Manifest = manifest;
    }

    public ZhujieProjectLayout Layout { get; }

    public ZhujieProjectManifest Manifest { get; }

    public static async Task<ZhujieProjectWorkspace> CreateAsync(
        string projectPath,
        string? displayName = null,
        ZhujieProjectTarget? target = null,
        string? projectsRoot = null,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectLayout layout = ZhujieProjectLayout.Resolve(projectPath, projectsRoot);
        if(File.Exists(layout.ProjectDirectory) || Directory.Exists(layout.ProjectDirectory))
            throw new IOException($"筑界镜工程目标已经存在：{layout.ProjectDirectory}");
        Directory.CreateDirectory(layout.ProjectsRoot);
        ZhujieProjectManifest manifest = new()
        {
            ProjectId = Guid.NewGuid(),
            DisplayName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(layout.ProjectDirectory)
                : displayName.Trim(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Target = target ?? new ZhujieProjectTarget
            {
                Dimension = "minecraft:overworld",
                VisualProfile = null,
            },
        };
        string stagingName = $".{Path.GetFileName(layout.ProjectDirectory)}.zjj-init-{Guid.NewGuid():N}";
        ZhujieProjectLayout staging = ZhujieProjectLayout.Resolve(stagingName, layout.ProjectsRoot);
        try
        {
            staging.CreateDirectories();
            await AtomicProjectFile.WriteAsync(
                staging.ProjectBriefPath,
                CreateProjectBrief(manifest),
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
            await AtomicProjectFile.WriteAsync(
                staging.AgentsPath,
                CreateProjectAgents(),
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
            await AtomicProjectFile.WriteAsync(
                staging.ManifestPath,
                ZhujieProjectJson.Serialize(manifest),
                overwrite: false,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging.ProjectDirectory, layout.ProjectDirectory);
            return new ZhujieProjectWorkspace(layout, manifest);
        }
        catch
        {
            TryDeleteInitializationStaging(staging);
            throw;
        }
    }

    public static async Task<ZhujieProjectWorkspace> OpenAsync(
        string projectPath,
        string? projectsRoot = null,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectLayout layout = ZhujieProjectLayout.Resolve(projectPath, projectsRoot);
        layout.EnsureDirectoryStructure();
        byte[] json = await AtomicProjectFile.ReadAsync(
            layout.ManifestPath,
            MaximumProtocolDocumentBytes,
            cancellationToken).ConfigureAwait(false);
        ZhujieProjectManifest manifest = ZhujieProjectJson.DeserializeManifest(json);
        _ = layout.ResolveProjectPath(manifest.SceneStore);
        _ = layout.ResolveProjectPath(manifest.BatchRoot);
        _ = layout.ResolveProjectPath(manifest.ModuleRoot);
        return new ZhujieProjectWorkspace(layout, manifest);
    }

    public async Task<ZhujiePatchDraft> ReadPatchDraftAsync(
        string draftPath,
        CancellationToken cancellationToken = default)
    {
        string draft = Layout.ResolveContainedInputPath(draftPath);
        if(!string.Equals(Path.GetDirectoryName(draft), Layout.StagingDirectory, PathComparison))
            throw new ArgumentException("发布输入必须是当前工程 .zhujie/staging 下的直系普通文件。", nameof(draftPath));
        if(!File.Exists(draft) || (File.GetAttributes(draft) & FileAttributes.Directory) != 0)
            throw new ArgumentException("发布输入必须是普通文件。", nameof(draftPath));
        if(!Path.GetExtension(draft).Equals(".zz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("施工补丁必须使用 .zz 扩展名。", nameof(draftPath));
        byte[] bytes = await AtomicProjectFile.ReadAsync(
            draft,
            MaximumPlanBytes,
            cancellationToken).ConfigureAwait(false);
        ZjjPlan plan = ParseAndValidatePatch(bytes);
        return new ZhujiePatchDraft(draft, plan, ComputeSha256(bytes), bytes);
    }

    public ZjjPlan ParseAndValidatePatch(ReadOnlySpan<byte> utf8Json) => ReadAndValidatePatch(utf8Json);

    public async Task<IReadOnlyList<ZhujieBatchRecord>> ListBatchesAsync(
        CancellationToken cancellationToken = default)
    {
        var batches = new List<ZhujieBatchRecord>();
        foreach(string path in Directory.EnumerateFiles(Layout.BatchesDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await AtomicProjectFile.ReadAsync(path, MaximumProtocolDocumentBytes, cancellationToken).ConfigureAwait(false);
            ZhujieBatchRecord batch = ZhujieProjectJson.DeserializeBatch(bytes);
            EnsureProjectId(batch.ProjectId, $"批次 {path}");
            _ = Layout.ResolveProjectPath(batch.PlanFile);
            batches.Add(batch);
        }
        ZhujieBatchRecord[] ordered = batches.OrderBy(static batch => batch.Sequence).ToArray();
        for(int index = 0; index < ordered.Length; index++)
        {
            if(ordered[index].Sequence != index + 1)
                throw new InvalidDataException($"批次序号不连续：期望 {index + 1}，实际 {ordered[index].Sequence}。");
        }
        return ordered;
    }

    public async Task<string> WriteBatchAsync(
        ZhujieBatchRecord batch,
        ReadOnlyMemory<byte> planBytes,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectValidator.EnsureValid(batch);
        EnsureProjectId(batch.ProjectId, "施工批次");
        string planPath = Layout.ResolveProjectPath(batch.PlanFile);
        string expectedPrefix = Path.GetFullPath(Path.Combine(Layout.ModulesDirectory, batch.Module.Id)) + Path.DirectorySeparatorChar;
        if(!planPath.StartsWith(expectedPrefix, PathComparison))
            throw new InvalidDataException("批次补丁必须保存在其所属模块目录中。");
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        string batchPath = Path.Combine(Layout.BatchesDirectory, $"{batch.Sequence:D8}-{batch.BatchId:D}.json");
        await AtomicProjectFile.WriteAsync(planPath, planBytes, overwrite: false, cancellationToken).ConfigureAwait(false);
        await AtomicProjectFile.WriteAsync(batchPath, ZhujieProjectJson.Serialize(batch), overwrite: false, cancellationToken).ConfigureAwait(false);
        return batchPath;
    }

    public async Task WriteArchitectStatusAsync(
        ZhujieArchitectStatus status,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectValidator.EnsureValid(status);
        EnsureProjectId(status.ProjectId, "建筑师状态");
        if(status.State == ZhujieArchitectState.Completed)
            await EnsureArchitectCompletionAsync(status, cancellationToken).ConfigureAwait(false);
        await AtomicProjectFile.WriteAsync(
            Layout.ArchitectStatusPath,
            ZhujieProjectJson.Serialize(status),
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ZhujieArchitectStatus?> ReadArchitectStatusAsync(CancellationToken cancellationToken = default)
    {
        if(!File.Exists(Layout.ArchitectStatusPath)) return null;
        byte[] bytes = await AtomicProjectFile.ReadAsync(
            Layout.ArchitectStatusPath,
            MaximumProtocolDocumentBytes,
            cancellationToken).ConfigureAwait(false);
        ZhujieArchitectStatus status = ZhujieProjectJson.DeserializeArchitectStatus(bytes);
        EnsureProjectId(status.ProjectId, "建筑师状态");
        if(status.State == ZhujieArchitectState.Completed)
            await EnsureArchitectCompletionAsync(status, cancellationToken).ConfigureAwait(false);
        return status;
    }

    public async Task<string> EnqueueInstructionAsync(
        ZhujieInstruction instruction,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectValidator.EnsureValid(instruction);
        EnsureProjectId(instruction.ProjectId, "指令");
        string duplicatePattern = $"*-{instruction.InstructionId:D}.json";
        if(Directory.EnumerateFiles(Layout.InstructionsDirectory, duplicatePattern, SearchOption.TopDirectoryOnly).Any())
            throw new IOException($"指令 {instruction.InstructionId:D} 已经存在。");
        string timestamp = instruction.CreatedAtUtc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        string path = Path.Combine(Layout.InstructionsDirectory, $"{timestamp}-{instruction.InstructionId:D}.json");
        await AtomicProjectFile.WriteAsync(
            path,
            ZhujieProjectJson.Serialize(instruction),
            overwrite: false,
            cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task<IReadOnlyList<ZhujieInstruction>> ListInstructionsAsync(
        bool pendingOnly = false,
        CancellationToken cancellationToken = default)
    {
        var instructions = new List<ZhujieInstruction>();
        var instructionIds = new HashSet<Guid>();
        foreach(string path in Directory.EnumerateFiles(Layout.InstructionsDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = await AtomicProjectFile.ReadAsync(path, MaximumProtocolDocumentBytes, cancellationToken).ConfigureAwait(false);
            ZhujieInstruction instruction = ZhujieProjectJson.DeserializeInstruction(bytes);
            EnsureProjectId(instruction.ProjectId, $"指令 {path}");
            if(!instructionIds.Add(instruction.InstructionId))
                throw new InvalidDataException($"工程中存在重复指令 ID：{instruction.InstructionId:D}");
            if(pendingOnly && await IsTerminallyAcknowledgedAsync(instruction, cancellationToken).ConfigureAwait(false))
                continue;
            instructions.Add(instruction);
        }
        return instructions
            .OrderBy(static instruction => instruction.CreatedAtUtc)
            .ThenBy(static instruction => instruction.InstructionId)
            .ToArray();
    }

    public Task<bool> DeleteQueuedInstructionAsync(
        Guid instructionId,
        CancellationToken cancellationToken = default)
    {
        if(instructionId == Guid.Empty) throw new ArgumentException("指令 ID 不能是空 GUID。", nameof(instructionId));
        cancellationToken.ThrowIfCancellationRequested();
        string acknowledgementPath = Path.Combine(Layout.AcknowledgementsDirectory, $"{instructionId:D}.json");
        if(File.Exists(acknowledgementPath)) return Task.FromResult(false);

        string[] paths = Directory.EnumerateFiles(
                Layout.InstructionsDirectory,
                $"*-{instructionId:D}.json",
                SearchOption.TopDirectoryOnly)
            .Take(2)
            .ToArray();
        if(paths.Length == 0) return Task.FromResult(false);
        if(paths.Length > 1) throw new InvalidDataException($"工程中存在重复指令 ID：{instructionId:D}");

        cancellationToken.ThrowIfCancellationRequested();
        if(File.Exists(acknowledgementPath)) return Task.FromResult(false);
        File.Delete(paths[0]);
        return Task.FromResult(true);
    }

    public async Task WriteInstructionAcknowledgementAsync(
        ZhujieInstructionAcknowledgement acknowledgement,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectValidator.EnsureValid(acknowledgement);
        EnsureProjectId(acknowledgement.ProjectId, "指令回执");
        ZhujieInstruction instruction = await ReadInstructionByIdAsync(
            acknowledgement.InstructionId,
            cancellationToken).ConfigureAwait(false);
        await EnsureCompletedInstructionAcknowledgementAsync(
            acknowledgement,
            instruction,
            cancellationToken).ConfigureAwait(false);
        string path = Path.Combine(Layout.AcknowledgementsDirectory, $"{acknowledgement.InstructionId:D}.json");
        await AtomicProjectFile.WriteAsync(
            path,
            ZhujieProjectJson.Serialize(acknowledgement),
            overwrite: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ZhujieInstructionAcknowledgement?> ReadInstructionAcknowledgementAsync(
        Guid instructionId,
        CancellationToken cancellationToken = default)
    {
        if(instructionId == Guid.Empty) throw new ArgumentException("指令 ID 不能是空 GUID。", nameof(instructionId));
        return await ReadInstructionAcknowledgementCoreAsync(
            instructionId,
            knownInstruction: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ZhujieInstructionAcknowledgement?> ReadInstructionAcknowledgementCoreAsync(
        Guid instructionId,
        ZhujieInstruction? knownInstruction,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(Layout.AcknowledgementsDirectory, $"{instructionId:D}.json");
        if(!File.Exists(path)) return null;
        byte[] bytes = await AtomicProjectFile.ReadAsync(path, MaximumProtocolDocumentBytes, cancellationToken).ConfigureAwait(false);
        ZhujieInstructionAcknowledgement acknowledgement = ZhujieProjectJson.DeserializeInstructionAcknowledgement(bytes);
        EnsureProjectId(acknowledgement.ProjectId, "指令回执");
        if(acknowledgement.InstructionId != instructionId)
            throw new InvalidDataException($"回执文件名与 instructionId 不一致：{path}");
        ZhujieInstruction instruction = knownInstruction ?? await ReadInstructionByIdAsync(
            acknowledgement.InstructionId,
            cancellationToken).ConfigureAwait(false);
        if(instruction.InstructionId != acknowledgement.InstructionId)
            throw new InvalidDataException($"回执 instructionId 与指令不一致：{path}");
        await EnsureCompletedInstructionAcknowledgementAsync(
            acknowledgement,
            instruction,
            cancellationToken).ConfigureAwait(false);
        return acknowledgement;
    }

    /// <summary>
    /// Returns true when the requested receipt is created, already satisfied, or monotonically upgraded. Returns
    /// false without changing the file when the request conflicts with an existing result or would downgrade it.
    /// </summary>
    public async Task<bool> WritePreviewReceiptAsync(
        ZhujiePreviewReceipt receipt,
        CancellationToken cancellationToken = default)
    {
        ZhujieProjectValidator.EnsureValid(receipt);
        EnsureProjectId(receipt.ProjectId, "预览回执");
        await previewReceiptWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(Layout.ReceiptsDirectory, $"{receipt.PlanSha256}.json");
            await using FileStream processLock = await AcquireReceiptWriteLockAsync(
                $"{path}.lock",
                cancellationToken).ConfigureAwait(false);
            if(!File.Exists(path))
            {
                try
                {
                    await AtomicProjectFile.WriteAsync(
                        path,
                        ZhujieProjectJson.Serialize(receipt),
                        overwrite: false,
                        cancellationToken).ConfigureAwait(false);
                    return true;
                }
                catch(IOException) when(File.Exists(path))
                {
                    // Another observer won the create race. Resolve it using the same transition rules below.
                }
            }

            ZhujiePreviewReceipt existing = await ReadPreviewReceiptAsync(
                receipt.PlanSha256,
                cancellationToken).ConfigureAwait(false) ??
                throw new IOException($"预览回执在并发写入期间消失：{path}");
            if(existing.Status == receipt.Status)
                return ReceiptPayloadMatches(existing, receipt);

            bool mayUpgrade =
                (existing.Status == ZhujiePreviewReceiptState.Superseded &&
                 receipt.Status is ZhujiePreviewReceiptState.Invalid or ZhujiePreviewReceiptState.Previewed) ||
                (existing.Status == ZhujiePreviewReceiptState.Invalid &&
                 receipt.Status == ZhujiePreviewReceiptState.Previewed);
            if(!mayUpgrade) return false;

            await AtomicProjectFile.WriteAsync(
                path,
                ZhujieProjectJson.Serialize(receipt),
                overwrite: true,
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            previewReceiptWriteGate.Release();
        }
    }

    public async Task<ZhujiePreviewReceipt?> ReadPreviewReceiptAsync(
        string planSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateSha256(planSha256, nameof(planSha256));
        string path = Path.Combine(Layout.ReceiptsDirectory, $"{planSha256}.json");
        if(!File.Exists(path)) return null;
        byte[] bytes = await AtomicProjectFile.ReadAsync(path, MaximumProtocolDocumentBytes, cancellationToken).ConfigureAwait(false);
        ZhujiePreviewReceipt receipt = ZhujieProjectJson.DeserializePreviewReceipt(bytes);
        EnsureProjectId(receipt.ProjectId, "预览回执");
        if(!string.Equals(receipt.PlanSha256, planSha256, StringComparison.Ordinal))
            throw new InvalidDataException($"预览回执文件名与 planSha256 不一致：{path}");
        return receipt;
    }

    private ZjjPlan ReadAndValidatePatch(ReadOnlySpan<byte> bytes)
    {
        ZjjPlan plan = ZjjPlanJson.DeserializeStrict(bytes);
        ZjjPlanValidator.EnsureValid(plan);
        if(plan.Base is null)
            throw new PlanValidationException(new PlanValidationResult([
                new ValidationIssue(
                    "project.patch_base",
                    "base",
                    "增量施工补丁必须声明 base.scene 与 base.revision。")
            ]));
        if(!string.Equals(plan.Base.Scene, Manifest.SceneStore, StringComparison.Ordinal))
            throw new PlanValidationException(new PlanValidationResult([
                new ValidationIssue("project.scene", "base.scene", $"补丁基础场景必须是 {Manifest.SceneStore}。")
            ]));
        if(!string.Equals(plan.Dimension, Manifest.Target.Dimension, StringComparison.Ordinal))
            throw new PlanValidationException(new PlanValidationResult([
                new ValidationIssue(
                    "project.dimension",
                    "dimension",
                    $"蓝图维度 {plan.Dimension} 与工程目标 {Manifest.Target.Dimension} 不一致。")
            ]));
        if(Manifest.Target.VisualProfile is not null && !VisualProfilesEqual(plan.VisualProfile, Manifest.Target.VisualProfile))
            throw new PlanValidationException(new PlanValidationResult([
                new ValidationIssue(
                    "project.visual_profile",
                    "visualProfile",
                    "蓝图材质身份与 project.json 的工程目标不一致。")
            ]));
        return plan;
    }

    private async Task<bool> IsTerminallyAcknowledgedAsync(
        ZhujieInstruction instruction,
        CancellationToken cancellationToken)
    {
        ZhujieInstructionAcknowledgement? acknowledgement = await ReadInstructionAcknowledgementCoreAsync(
            instruction.InstructionId,
            instruction,
            cancellationToken).ConfigureAwait(false);
        return acknowledgement?.State is ZhujieInstructionAcknowledgementState.Completed or
            ZhujieInstructionAcknowledgementState.Superseded;
    }

    private async Task<ZhujieInstruction> ReadInstructionByIdAsync(
        Guid instructionId,
        CancellationToken cancellationToken)
    {
        string[] paths = Directory.EnumerateFiles(
                Layout.InstructionsDirectory,
                $"*-{instructionId:D}.json",
                SearchOption.TopDirectoryOnly)
            .Take(2)
            .ToArray();
        if(paths.Length == 0)
            throw new FileNotFoundException($"找不到指令 {instructionId:D}，不能写入回执。");
        if(paths.Length > 1)
            throw new InvalidDataException($"工程中存在重复指令 ID：{instructionId:D}");
        byte[] bytes = await AtomicProjectFile.ReadAsync(
            paths[0],
            MaximumProtocolDocumentBytes,
            cancellationToken).ConfigureAwait(false);
        ZhujieInstruction instruction = ZhujieProjectJson.DeserializeInstruction(bytes);
        EnsureProjectId(instruction.ProjectId, $"指令 {paths[0]}");
        if(instruction.InstructionId != instructionId)
            throw new InvalidDataException($"指令文件名与 instructionId 不一致：{paths[0]}");
        return instruction;
    }

    private async Task EnsureArchitectCompletionAsync(
        ZhujieArchitectStatus status,
        CancellationToken cancellationToken)
    {
        if(string.IsNullOrWhiteSpace(status.PublishedPlanSha256))
            throw new InvalidDataException("建筑师状态只能在绑定当前 publishedPlanSha256 后标记 completed。");
        ZhujieBatchRecord? latest = (await ListBatchesAsync(cancellationToken).ConfigureAwait(false)).LastOrDefault();
        if(latest is null ||
           !string.Equals(latest.PlanSha256, status.PublishedPlanSha256, StringComparison.Ordinal) ||
           !string.Equals(latest.Revision, status.PublishedRevision, StringComparison.Ordinal))
            throw new InvalidDataException("建筑师 completed 状态必须绑定当前最新批次的 plan SHA 与 revision。");
        ZhujiePreviewReceipt? receipt = await ReadPreviewReceiptAsync(
            status.PublishedPlanSha256,
            cancellationToken).ConfigureAwait(false);
        if(receipt?.Status != ZhujiePreviewReceiptState.Previewed)
            throw new InvalidDataException("建筑师只能在当前蓝图已获得 previewed 回执后标记 completed。");
        if(!string.Equals(receipt.Revision, latest.Revision, StringComparison.Ordinal))
            throw new InvalidDataException("建筑师 completed 状态引用的预览回执 revision 不匹配。");
        IReadOnlyList<ZhujieInstruction> pending = await ListInstructionsAsync(
            pendingOnly: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if(pending.Count != 0)
            throw new InvalidDataException($"建筑师不能在仍有 {pending.Count:N0} 条未终态指令时标记 completed。");
    }

    private async Task EnsureCompletedInstructionAcknowledgementAsync(
        ZhujieInstructionAcknowledgement acknowledgement,
        ZhujieInstruction instruction,
        CancellationToken cancellationToken)
    {
        if(acknowledgement.State != ZhujieInstructionAcknowledgementState.Completed ||
           instruction.Kind != ZhujieInstructionKind.Instruction)
            return;
        if(string.IsNullOrWhiteSpace(acknowledgement.PublishedPlanSha256))
            throw new InvalidDataException("普通施工指令只能在绑定已预览的 publishedPlanSha256 后标记 completed。");
        if(string.IsNullOrWhiteSpace(acknowledgement.PublishedRevision))
            throw new InvalidDataException("普通施工指令只能在绑定已预览的 publishedRevision 后标记 completed。");
        ZhujiePreviewReceipt? receipt = await ReadPreviewReceiptAsync(
            acknowledgement.PublishedPlanSha256,
            cancellationToken).ConfigureAwait(false);
        if(receipt?.Status != ZhujiePreviewReceiptState.Previewed)
            throw new InvalidDataException("普通施工指令的 completed 回执必须引用同哈希的 previewed 预览回执。");
        if(!string.Equals(receipt.Revision, acknowledgement.PublishedRevision, StringComparison.Ordinal))
            throw new InvalidDataException("普通施工指令的 completed 回执 revision 不匹配。");
    }

    private static bool ReceiptPayloadMatches(
        ZhujiePreviewReceipt existing,
        ZhujiePreviewReceipt incoming) =>
        string.Equals(existing.Format, incoming.Format, StringComparison.Ordinal) &&
        existing.ProjectId == incoming.ProjectId &&
        string.Equals(existing.PlanSha256, incoming.PlanSha256, StringComparison.Ordinal) &&
        string.Equals(existing.Revision, incoming.Revision, StringComparison.Ordinal) &&
        existing.Status == incoming.Status &&
        string.Equals(existing.PlanId, incoming.PlanId, StringComparison.Ordinal) &&
        string.Equals(existing.SceneDeltaHash, incoming.SceneDeltaHash, StringComparison.Ordinal) &&
        existing.ChangedVoxelCount == incoming.ChangedVoxelCount &&
        existing.SectionCount == incoming.SectionCount &&
        existing.Errors.SequenceEqual(incoming.Errors);

    private static async Task<FileStream> AcquireReceiptWriteLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        const int maximumAttempts = 120;
        for(int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch(IOException) when(attempt < maximumAttempts - 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void EnsureProjectId(Guid projectId, string documentName)
    {
        if(projectId != Manifest.ProjectId)
            throw new InvalidDataException($"{documentName} 的 projectId 不属于当前工程。");
    }

    private static bool VisualProfilesEqual(ZjjVisualProfile? plan, ZhujieProjectVisualProfile target) =>
        plan is not null &&
        string.Equals(plan.Edition, target.Edition, StringComparison.Ordinal) &&
        string.Equals(plan.VersionName, target.VersionName, StringComparison.Ordinal) &&
        plan.DataVersion == target.DataVersion &&
        string.Equals(plan.StorageFamily, target.StorageFamily, StringComparison.Ordinal);

    private static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] CreateProjectBrief(ZhujieProjectManifest manifest)
    {
        string text = $"""
            # {manifest.DisplayName}

            > 这是筑界镜工程的长期施工说明。用户负责维护约束，AI 建筑师每个施工批次开始前必须重新阅读。

            ## 建设目标

            - 待填写工程要实现的核心用途、规模与体验。

            ## 美术风格

            - 待填写主题、时代、色彩、材质与参考方向。

            ## 坐标与施工范围

            - 目标维度：`{manifest.Target.Dimension}`
            - 默认出生点：`0, 32, 0`（首个施工批次必须设置并同步到筑界镜）
            - 允许施工范围：待填写。
            - 重要锚点与朝向：待填写。

            ## 禁区与不可变内容

            - 待填写禁止改动的坐标、建筑、地形与功能结构。

            ## 当前阶段与验收标准

            - 当前阶段：待填写。
            - 本阶段完成标准：待填写。

            """;
        return System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static byte[] CreateProjectAgents()
    {
        const string text = """
            # 筑界镜工程规则

            当前目录是 Voxelens Studio AI 地图工程。收到创建、继续或修改地图的请求时，先阅读本目录的 `project.json` 和 `PROJECT.md`。
            蓝图格式和版本约束见 Studio 源码仓库的 `docs/AI_WORLD_FORMAT.md`，命令用法见 `zjj help`。
            如果工程工作区已安装 `zhujiejing-architect` 技能，先阅读其 `SKILL.md`；不要假定技能位于固定盘符。

            禁止直接编辑 `.zjjscene`、MCA、NBT、源世界或导出世界；施工内容只写入 `.zhujie/staging` 的 `.zz` 增量补丁，并通过 `zjj` 发布。必须遵守 `project.json` 的目标 Minecraft 版本，不能使用该版本不存在的方块或方块状态。新工程出生点固定为玩家脚部 `(0,32,0)`。
            """;
        return System.Text.Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static void TryDeleteInitializationStaging(ZhujieProjectLayout staging)
    {
        try
        {
            if(!Directory.Exists(staging.ProjectDirectory)) return;
            string parent = Path.GetDirectoryName(staging.ProjectDirectory)!;
            string name = Path.GetFileName(staging.ProjectDirectory);
            if(!string.Equals(parent, staging.ProjectsRoot, PathComparison) ||
               !name.StartsWith(".", StringComparison.Ordinal) ||
               !name.Contains(".zjj-init-", StringComparison.Ordinal))
                return;
            Directory.Delete(staging.ProjectDirectory, recursive: true);
        }
        catch(Exception exception) when(exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if(value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("必须是 64 位小写 SHA-256。", parameterName);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
