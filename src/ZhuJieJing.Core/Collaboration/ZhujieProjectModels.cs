using System.Text.Json.Serialization;

namespace ZhuJieJing.Core.Collaboration;

public enum ZhujieProjectMode
{
    IncrementalPatches,
}

public enum ZhujieBatchKind
{
    Construction,
    Revert,
}

public enum ZhujieInstructionKind
{
    Instruction,
    Pause,
    Resume,
    CancelCurrent,
}

public enum ZhujieArchitectState
{
    Idle,
    Planning,
    Building,
    Validating,
    WaitingForUser,
    Paused,
    Completed,
    Error,
}

public enum ZhujieInstructionAcknowledgementState
{
    Accepted,
    Working,
    Completed,
    Blocked,
    Superseded,
}

public enum ZhujiePreviewReceiptState
{
    Previewed,
    Invalid,
    Superseded,
}

public sealed record ZhujieProjectVisualProfile
{
    [JsonRequired]
    public required string Edition { get; init; }

    [JsonRequired]
    public string? VersionName { get; init; }

    [JsonRequired]
    public int? DataVersion { get; init; }

    [JsonRequired]
    public required string StorageFamily { get; init; }

    public ZjjVisualProfile ToPlanProfile() => new()
    {
        Edition = Edition,
        VersionName = VersionName,
        DataVersion = DataVersion,
        StorageFamily = StorageFamily,
    };

    public static ZhujieProjectVisualProfile FromPlanProfile(ZjjVisualProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ZhujieProjectVisualProfile
        {
            Edition = profile.Edition,
            VersionName = profile.VersionName,
            DataVersion = profile.DataVersion,
            StorageFamily = profile.StorageFamily,
        };
    }
}

public sealed record ZhujieProjectTarget
{
    [JsonRequired]
    public required string Dimension { get; init; }

    [JsonRequired]
    public ZhujieProjectVisualProfile? VisualProfile { get; init; }
}

public sealed record ZhujieProjectManifest
{
    public const string CurrentFormat = "zhujie.project/2";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public required string DisplayName { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public ZhujieProjectMode Mode { get; init; } = ZhujieProjectMode.IncrementalPatches;

    [JsonRequired]
    public string SceneStore { get; init; } = ZhujieProjectLayout.SceneStoreRelativePath;

    [JsonRequired]
    public string BatchRoot { get; init; } = ZhujieProjectLayout.BatchesRelativePath;

    [JsonRequired]
    public string ModuleRoot { get; init; } = ZhujieProjectLayout.ModulesRelativePath;

    [JsonRequired]
    public required ZhujieProjectTarget Target { get; init; }
}

public sealed record ZhujieBatchRecord
{
    public const string CurrentFormat = "zhujie.batch/1";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid BatchId { get; init; }

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public long Sequence { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public ZhujieBatchKind Kind { get; init; }

    [JsonRequired]
    public required string Revision { get; init; }

    [JsonRequired]
    public required string BaseRevision { get; init; }

    [JsonRequired]
    public required string PlanSha256 { get; init; }

    [JsonRequired]
    public required string PlanId { get; init; }

    [JsonRequired]
    public required ZjjPlanModule Module { get; init; }

    [JsonRequired]
    public required string PlanFile { get; init; }

    public string? RevertedRevision { get; init; }

    [JsonRequired]
    public required string SceneDeltaHash { get; init; }

    [JsonRequired]
    public int ChangedVoxelCount { get; init; }

    [JsonRequired]
    public int SectionCount { get; init; }

    [JsonRequired]
    public required string Dimension { get; init; }

    [JsonRequired]
    public ZjjVisualProfile? VisualProfile { get; init; }

    [JsonRequired]
    public required BlockPosition SpawnPoint { get; init; }
}

public sealed record ZhujieCameraPose
{
    [JsonRequired]
    public double X { get; init; }

    [JsonRequired]
    public double Y { get; init; }

    [JsonRequired]
    public double Z { get; init; }

    public double? Yaw { get; init; }

    public double? Pitch { get; init; }
}

public sealed record ZhujieInstructionContext
{
    [JsonRequired]
    public required string Dimension { get; init; }

    public BoxSelection? Selection { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ZhujieCameraPose? Camera { get; init; }
}

public sealed record ZhujieInstruction
{
    public const string CurrentFormat = "zhujie.instruction/2";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid InstructionId { get; init; }

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public DateTimeOffset CreatedAtUtc { get; init; }

    [JsonRequired]
    public ZhujieInstructionKind Kind { get; init; } = ZhujieInstructionKind.Instruction;

    [JsonRequired]
    public required string Text { get; init; }

    public string? BasedOnRevision { get; init; }

    [JsonRequired]
    public required ZhujieInstructionContext Context { get; init; }
}

public sealed record ZhujieArchitectProgress
{
    [JsonRequired]
    public long Completed { get; init; }

    [JsonRequired]
    public long Total { get; init; }

    [JsonRequired]
    public required string Unit { get; init; }
}

public sealed record ZhujieArchitectStatus
{
    public const string CurrentFormat = "zhujie.architect-status/2";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public required string SessionId { get; init; }

    [JsonRequired]
    public required string Agent { get; init; }

    [JsonRequired]
    public ZhujieArchitectState State { get; init; }

    [JsonRequired]
    public required string Phase { get; init; }

    [JsonRequired]
    public required string Message { get; init; }

    public ZhujieArchitectProgress? Progress { get; init; }

    public Guid? CurrentInstructionId { get; init; }

    public string? PublishedPlanSha256 { get; init; }

    public string? PublishedRevision { get; init; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ZhujieInstructionAcknowledgement
{
    public const string CurrentFormat = "zhujie.instruction-ack/2";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public Guid InstructionId { get; init; }

    [JsonRequired]
    public required string SessionId { get; init; }

    [JsonRequired]
    public ZhujieInstructionAcknowledgementState State { get; init; }

    [JsonRequired]
    public required string Message { get; init; }

    public string? PublishedPlanSha256 { get; init; }

    public string? PublishedRevision { get; init; }

    [JsonRequired]
    public DateTimeOffset UpdatedAtUtc { get; init; }
}

public sealed record ZhujiePreviewReceiptError
{
    [JsonRequired]
    public required string Code { get; init; }

    [JsonRequired]
    public required string Path { get; init; }

    [JsonRequired]
    public required string Message { get; init; }
}

public sealed record ZhujiePreviewReceipt
{
    public const string CurrentFormat = "zhujie.preview-receipt/2";

    [JsonRequired]
    public string Format { get; init; } = CurrentFormat;

    [JsonRequired]
    public Guid ProjectId { get; init; }

    [JsonRequired]
    public required string PlanSha256 { get; init; }

    [JsonRequired]
    public required string Revision { get; init; }

    [JsonRequired]
    public DateTimeOffset ObservedAtUtc { get; init; }

    [JsonRequired]
    public ZhujiePreviewReceiptState Status { get; init; }

    public string? PlanId { get; init; }

    public string? SceneDeltaHash { get; init; }

    public int? ChangedVoxelCount { get; init; }

    public int? SectionCount { get; init; }

    [JsonRequired]
    public IReadOnlyList<ZhujiePreviewReceiptError> Errors { get; init; } = [];
}

public sealed record ZhujiePatchDraft(string Path, ZjjPlan Plan, string PlanSha256, ReadOnlyMemory<byte> Utf8Json);

public sealed record ZhujiePatchPublishResult(
    ZhujieBatchRecord Batch,
    string PlanFilePath,
    string PlanSha256,
    string Revision,
    string BaseRevision,
    string SceneDeltaHash,
    int ChangedVoxelCount,
    int SectionCount,
    int AttemptedWriteCount,
    int AppliedWriteCount,
    int SkippedWriteCount);
