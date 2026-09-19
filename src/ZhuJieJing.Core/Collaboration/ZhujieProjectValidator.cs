using System.Text.RegularExpressions;

namespace ZhuJieJing.Core.Collaboration;

public sealed class ZhujieProtocolValidationResult
{
    public ZhujieProtocolValidationResult(IEnumerable<ValidationIssue> issues)
    {
        Issues = issues.ToArray();
    }

    public IReadOnlyList<ValidationIssue> Issues { get; }

    public bool IsValid => Issues.Count == 0;
}

public sealed class ZhujieProtocolValidationException : Exception
{
    public ZhujieProtocolValidationException(ZhujieProtocolValidationResult result)
        : base(string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        Result = result;
    }

    public ZhujieProtocolValidationResult Result { get; }
}

public static class ZhujieProjectValidator
{
    public const int MaximumDisplayNameLength = 256;
    public const int MaximumAgentNameLength = 256;
    public const int MaximumStatusTextLength = 4096;
    public const int MaximumInstructionTextLength = 32_768;

    private static readonly Regex NamespacedIdPattern = new(
        "^[a-z0-9_.-]+:[a-z0-9_./-]+$",
        RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex RevisionPattern = new("^(root|sha256:[0-9a-f]{64})$", RegexOptions.CultureInvariant);
    private static readonly Regex RelativeProtocolPathPattern = new("^[a-zA-Z0-9_.-]+(?:/[a-zA-Z0-9_.-]+)*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> VisualStorageFamilies = new(StringComparer.Ordinal)
    {
        ZjjVisualProfile.UnknownStorageFamily,
        ZjjVisualProfile.LegacyNumericAnvilStorageFamily,
        ZjjVisualProfile.FlattenedPaletteStorageFamily,
        ZjjVisualProfile.ModernSectionPaletteStorageFamily,
    };

    public static ZhujieProtocolValidationResult Validate(ZhujieProjectManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        List<ValidationIssue> issues = [];
        RequireFormat(manifest.Format, ZhujieProjectManifest.CurrentFormat, issues);
        RequireGuid(manifest.ProjectId, "projectId", issues);
        RequireText(manifest.DisplayName, "displayName", MaximumDisplayNameLength, issues);
        RequireUtcTimestamp(manifest.CreatedAtUtc, "createdAtUtc", issues);
        if(manifest.Mode != ZhujieProjectMode.IncrementalPatches)
            issues.Add(new ValidationIssue("project.mode", "mode", "当前仅支持 incrementalPatches 工程模式。"));
        RequireProtocolPath(manifest.SceneStore, "sceneStore", ZhujieProjectLayout.SceneStoreRelativePath, issues);
        RequireProtocolPath(manifest.BatchRoot, "batchRoot", ZhujieProjectLayout.BatchesRelativePath, issues);
        RequireProtocolPath(manifest.ModuleRoot, "moduleRoot", ZhujieProjectLayout.ModulesRelativePath, issues);
        if(manifest.Target is null)
        {
            issues.Add(new ValidationIssue("project.target", "target", "工程目标不能为空。"));
        }
        else
        {
            RequireNamespacedId(manifest.Target.Dimension, "target.dimension", issues);
            ValidateVisualProfile(manifest.Target.VisualProfile, "target.visualProfile", issues);
        }
        return new ZhujieProtocolValidationResult(issues);
    }

    public static ZhujieProtocolValidationResult Validate(ZhujieBatchRecord batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        List<ValidationIssue> issues = [];
        RequireFormat(batch.Format, ZhujieBatchRecord.CurrentFormat, issues);
        RequireGuid(batch.BatchId, "batchId", issues);
        RequireGuid(batch.ProjectId, "projectId", issues);
        if(batch.Sequence <= 0) issues.Add(new ValidationIssue("batch.sequence", "sequence", "批次序号必须为正数。"));
        RequireUtcTimestamp(batch.CreatedAtUtc, "createdAtUtc", issues);
        RequireRevision(batch.Revision, "revision", issues);
        RequireRevision(batch.BaseRevision, "baseRevision", issues);
        RequireSha256(batch.PlanSha256, "planSha256", issues);
        RequireText(batch.PlanId, "planId", MaximumDisplayNameLength, issues);
        if(batch.Module is null)
            issues.Add(new ValidationIssue("batch.module", "module", "施工模块不能为空。"));
        else
            ZjjPlanValidator.ValidateModule(batch.Module, "module", issues);
        RequireProtocolPath(batch.PlanFile, "planFile", expected: null, issues);
        RequireSha256(batch.SceneDeltaHash, "sceneDeltaHash", issues);
        if(batch.ChangedVoxelCount < 0) issues.Add(new ValidationIssue("batch.changed_voxels", "changedVoxelCount", "变化方块数不能为负数。"));
        if(batch.SectionCount < 0) issues.Add(new ValidationIssue("batch.sections", "sectionCount", "Section 数不能为负数。"));
        RequireNamespacedId(batch.Dimension, "dimension", issues);
        if(batch.RevertedRevision is not null) RequireRevision(batch.RevertedRevision, "revertedRevision", issues);
        return new ZhujieProtocolValidationResult(issues);
    }

    public static ZhujieProtocolValidationResult Validate(ZhujieInstruction instruction)
    {
        ArgumentNullException.ThrowIfNull(instruction);
        List<ValidationIssue> issues = [];
        RequireFormat(instruction.Format, ZhujieInstruction.CurrentFormat, issues);
        RequireGuid(instruction.InstructionId, "instructionId", issues);
        RequireGuid(instruction.ProjectId, "projectId", issues);
        RequireUtcTimestamp(instruction.CreatedAtUtc, "createdAtUtc", issues);
        if(instruction.Kind == ZhujieInstructionKind.Instruction)
            RequireText(instruction.Text, "text", MaximumInstructionTextLength, issues);
        else if(instruction.Text?.Length > MaximumInstructionTextLength)
            issues.Add(new ValidationIssue("protocol.text_length", "text", $"文字不能超过 {MaximumInstructionTextLength:N0} 个字符。"));
        RequireOptionalRevision(instruction.BasedOnRevision, "basedOnRevision", issues);
        if(instruction.Context is null)
        {
            issues.Add(new ValidationIssue("instruction.context", "context", "指令上下文不能为空。"));
        }
        else
        {
            RequireNamespacedId(instruction.Context.Dimension, "context.dimension", issues);
            ValidateCamera(instruction.Context.Camera, issues);
        }
        return new ZhujieProtocolValidationResult(issues);
    }

    public static ZhujieProtocolValidationResult Validate(ZhujieArchitectStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        List<ValidationIssue> issues = [];
        RequireFormat(status.Format, ZhujieArchitectStatus.CurrentFormat, issues);
        RequireGuid(status.ProjectId, "projectId", issues);
        RequireText(status.SessionId, "sessionId", MaximumAgentNameLength, issues);
        RequireText(status.Agent, "agent", MaximumAgentNameLength, issues);
        RequireText(status.Phase, "phase", MaximumDisplayNameLength, issues);
        RequireText(status.Message, "message", MaximumStatusTextLength, issues);
        RequireUtcTimestamp(status.UpdatedAtUtc, "updatedAtUtc", issues);
        RequireOptionalSha256(status.PublishedPlanSha256, "publishedPlanSha256", issues);
        RequireOptionalRevision(status.PublishedRevision, "publishedRevision", issues);
        if(status.CurrentInstructionId == Guid.Empty)
            issues.Add(new ValidationIssue("protocol.guid", "currentInstructionId", "指令 ID 不能是空 GUID。"));
        if(status.Progress is not null)
        {
            if(status.Progress.Total <= 0)
                issues.Add(new ValidationIssue("status.progress_total", "progress.total", "进度总量必须为正数。"));
            if(status.Progress.Completed < 0 || status.Progress.Completed > status.Progress.Total)
                issues.Add(new ValidationIssue("status.progress_completed", "progress.completed", "已完成量必须位于 0 与总量之间。"));
            RequireText(status.Progress.Unit, "progress.unit", 64, issues);
        }
        return new ZhujieProtocolValidationResult(issues);
    }

    public static ZhujieProtocolValidationResult Validate(ZhujieInstructionAcknowledgement acknowledgement)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        List<ValidationIssue> issues = [];
        RequireFormat(acknowledgement.Format, ZhujieInstructionAcknowledgement.CurrentFormat, issues);
        RequireGuid(acknowledgement.ProjectId, "projectId", issues);
        RequireGuid(acknowledgement.InstructionId, "instructionId", issues);
        RequireText(acknowledgement.SessionId, "sessionId", MaximumAgentNameLength, issues);
        RequireText(acknowledgement.Message, "message", MaximumStatusTextLength, issues);
        RequireOptionalSha256(acknowledgement.PublishedPlanSha256, "publishedPlanSha256", issues);
        RequireOptionalRevision(acknowledgement.PublishedRevision, "publishedRevision", issues);
        RequireUtcTimestamp(acknowledgement.UpdatedAtUtc, "updatedAtUtc", issues);
        return new ZhujieProtocolValidationResult(issues);
    }

    public static ZhujieProtocolValidationResult Validate(ZhujiePreviewReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        List<ValidationIssue> issues = [];
        RequireFormat(receipt.Format, ZhujiePreviewReceipt.CurrentFormat, issues);
        RequireGuid(receipt.ProjectId, "projectId", issues);
        RequireSha256(receipt.PlanSha256, "planSha256", issues);
        RequireRevision(receipt.Revision, "revision", issues);
        RequireUtcTimestamp(receipt.ObservedAtUtc, "observedAtUtc", issues);
        if(receipt.Errors is null)
        {
            issues.Add(new ValidationIssue("receipt.errors", "errors", "错误集合不能为空。"));
        }
        else
        {
            for(int index = 0; index < receipt.Errors.Count; index++)
            {
                ZhujiePreviewReceiptError? error = receipt.Errors[index];
                if(error is null)
                {
                    issues.Add(new ValidationIssue("receipt.error", $"errors[{index}]", "错误项不能为空。"));
                    continue;
                }
                RequireText(error.Code, $"errors[{index}].code", 256, issues);
                RequireText(error.Path, $"errors[{index}].path", 2048, issues);
                RequireText(error.Message, $"errors[{index}].message", MaximumStatusTextLength, issues);
            }
        }
        if(receipt.Status == ZhujiePreviewReceiptState.Previewed)
        {
            RequireText(receipt.PlanId, "planId", MaximumDisplayNameLength, issues);
            RequireSha256(receipt.SceneDeltaHash, "sceneDeltaHash", issues);
            if(receipt.ChangedVoxelCount is null or < 0)
                issues.Add(new ValidationIssue("receipt.changed_voxels", "changedVoxelCount", "成功回执必须包含非负变化方块数。"));
            if(receipt.SectionCount is null or < 0)
                issues.Add(new ValidationIssue("receipt.sections", "sectionCount", "成功回执必须包含非负 Section 数。"));
            if(receipt.Errors is { Count: > 0 })
                issues.Add(new ValidationIssue("receipt.success_errors", "errors", "成功回执不能包含错误。"));
        }
        if(receipt.Status == ZhujiePreviewReceiptState.Invalid && receipt.Errors is not { Count: > 0 })
            issues.Add(new ValidationIssue("receipt.invalid_errors", "errors", "无效回执必须说明至少一个错误。"));
        return new ZhujieProtocolValidationResult(issues);
    }

    public static void EnsureValid(ZhujieProjectManifest manifest) => Ensure(Validate(manifest));

    public static void EnsureValid(ZhujieBatchRecord batch) => Ensure(Validate(batch));

    public static void EnsureValid(ZhujieInstruction instruction) => Ensure(Validate(instruction));

    public static void EnsureValid(ZhujieArchitectStatus status) => Ensure(Validate(status));

    public static void EnsureValid(ZhujieInstructionAcknowledgement acknowledgement) => Ensure(Validate(acknowledgement));

    public static void EnsureValid(ZhujiePreviewReceipt receipt) => Ensure(Validate(receipt));

    private static void ValidateVisualProfile(
        ZhujieProjectVisualProfile? profile,
        string path,
        List<ValidationIssue> issues)
    {
        if(profile is null) return;
        if(!string.Equals(profile.Edition, ZjjVisualProfile.JavaEdition, StringComparison.Ordinal))
            issues.Add(new ValidationIssue("visual_profile.edition", $"{path}.edition", "当前仅支持 Minecraft Java 材质身份。"));
        if(profile.VersionName is not null && (string.IsNullOrWhiteSpace(profile.VersionName) || profile.VersionName.Length > 128))
            issues.Add(new ValidationIssue("visual_profile.version", $"{path}.versionName", "版本名必须为 1–128 个字符。"));
        if(profile.DataVersion is < 0)
            issues.Add(new ValidationIssue("visual_profile.data_version", $"{path}.dataVersion", "DataVersion 不能为负数。"));
        if(!VisualStorageFamilies.Contains(profile.StorageFamily))
            issues.Add(new ValidationIssue("visual_profile.storage_family", $"{path}.storageFamily", "不支持该存储格式族。"));
        if(profile.VersionName is null && profile.DataVersion is null &&
           string.Equals(profile.StorageFamily, ZjjVisualProfile.UnknownStorageFamily, StringComparison.Ordinal))
            issues.Add(new ValidationIssue("visual_profile.identity", path, "材质身份必须提供版本名、DataVersion 或明确存储格式族。"));
    }

    private static void ValidateCamera(ZhujieCameraPose? camera, List<ValidationIssue> issues)
    {
        if(camera is null) return;
        if(!double.IsFinite(camera.X) || !double.IsFinite(camera.Y) || !double.IsFinite(camera.Z) ||
           camera.Yaw is double yaw && !double.IsFinite(yaw) ||
           camera.Pitch is double pitch && !double.IsFinite(pitch))
            issues.Add(new ValidationIssue("instruction.camera", "context.camera", "镜头坐标和角度必须是有限数值。"));
        if(camera.Pitch is < -90 or > 90)
            issues.Add(new ValidationIssue("instruction.camera_pitch", "context.camera.pitch", "镜头俯仰角必须位于 -90 到 90 度。"));
    }

    private static void RequireFormat(string? actual, string expected, List<ValidationIssue> issues)
    {
        if(!string.Equals(actual, expected, StringComparison.Ordinal))
            issues.Add(new ValidationIssue("protocol.format", "format", $"仅支持 {expected}。"));
    }

    private static void RequireGuid(Guid value, string path, List<ValidationIssue> issues)
    {
        if(value == Guid.Empty) issues.Add(new ValidationIssue("protocol.guid", path, "ID 不能是空 GUID。"));
    }

    private static void RequireUtcTimestamp(DateTimeOffset value, string path, List<ValidationIssue> issues)
    {
        if(value == default || value.Offset != TimeSpan.Zero)
            issues.Add(new ValidationIssue("protocol.utc_timestamp", path, "时间必须是带 UTC 时区的有效时间。"));
    }

    private static void RequireNamespacedId(string? value, string path, List<ValidationIssue> issues)
    {
        if(string.IsNullOrWhiteSpace(value) || !NamespacedIdPattern.IsMatch(value))
            issues.Add(new ValidationIssue("protocol.namespaced_id", path, "必须使用小写命名空间 ID。"));
    }

    private static void RequireText(string? value, string path, int maximumLength, List<ValidationIssue> issues)
    {
        if(string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new ValidationIssue("protocol.text", path, "文字不能为空。"));
            return;
        }
        if(value.Length > maximumLength)
            issues.Add(new ValidationIssue("protocol.text_length", path, $"文字不能超过 {maximumLength:N0} 个字符。"));
    }

    private static void RequireOptionalSha256(string? value, string path, List<ValidationIssue> issues)
    {
        if(value is not null) RequireSha256(value, path, issues);
    }

    private static void RequireOptionalRevision(string? value, string path, List<ValidationIssue> issues)
    {
        if(value is not null) RequireRevision(value, path, issues);
    }

    private static void RequireRevision(string? value, string path, List<ValidationIssue> issues)
    {
        if(value is null || !RevisionPattern.IsMatch(value))
            issues.Add(new ValidationIssue("protocol.revision", path, "必须是 root 或 sha256: 开头的 64 位 revision。"));
    }

    private static void RequireProtocolPath(
        string? value,
        string path,
        string? expected,
        List<ValidationIssue> issues)
    {
        if(string.IsNullOrWhiteSpace(value) || !RelativeProtocolPathPattern.IsMatch(value) ||
           value.Contains("..", StringComparison.Ordinal))
        {
            issues.Add(new ValidationIssue("protocol.path", path, "必须是工程内的正斜杠相对路径。"));
            return;
        }
        if(expected is not null && !string.Equals(value, expected, StringComparison.Ordinal))
            issues.Add(new ValidationIssue("protocol.path", path, $"协议路径必须是 {expected}。"));
    }

    private static void RequireSha256(string? value, string path, List<ValidationIssue> issues)
    {
        if(value is null || !Sha256Pattern.IsMatch(value))
            issues.Add(new ValidationIssue("protocol.sha256", path, "必须是 64 位小写 SHA-256。"));
    }

    private static void Ensure(ZhujieProtocolValidationResult result)
    {
        if(!result.IsValid) throw new ZhujieProtocolValidationException(result);
    }
}
