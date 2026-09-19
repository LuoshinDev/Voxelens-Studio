namespace ZhuJieJing.Core;

public sealed record CompilationResult(SceneDelta Delta, int AttemptedWriteCount, int AppliedWriteCount, int SkippedWriteCount);

public sealed class PlanCompilationException : Exception
{
    public PlanCompilationException(string message, string? operationId = null, BlockPosition? position = null)
        : base(message)
    {
        OperationId = operationId;
        Position = position;
    }

    public string? OperationId { get; }

    public BlockPosition? Position { get; }
}

public sealed class ZjjPlanCompiler
{
    public CompilationResult Compile(
        ZjjPlan plan,
        ISceneSnapshot baseScene,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(baseScene);
        ZjjPlanValidator.EnsureValid(plan);

        if (plan.Base is not null && !string.Equals(plan.Base.Revision, baseScene.Revision, StringComparison.Ordinal))
        {
            throw new PlanCompilationException($"蓝图基于 {plan.Base.Revision}，当前场景为 {baseScene.Revision}。");
        }

        var resolvedMaterials = plan.Materials.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Resolve() ?? throw new PlanCompilationException($"材质 {pair.Key} 无法解析，禁止使用空气兜底。"),
            StringComparer.Ordinal);
        var staged = new Dictionary<BlockPosition, StagedWrite>();
        var attemptedWrites = 0;
        var appliedWrites = 0;
        var skippedWrites = 0;

        foreach (var operation in plan.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetState = resolvedMaterials[operation.MaterialId];
            switch (operation)
            {
                case FillBoxOperation fillBox:
                    foreach (var position in fillBox.Bounds.EnumeratePositions()) Write(position, operation, targetState);
                    break;
                case ColumnsOperation columns:
                    foreach (var origin in columns.Origins)
                    {
                        for (var y = 0; y < columns.Height; y++) Write(origin.Offset(0, y, 0), operation, targetState);
                    }
                    break;
                default:
                    throw new PlanCompilationException($"不支持操作类型 {operation.GetType().Name}。", operation.Id);
            }
        }

        var sectionChanges = new Dictionary<SectionCoordinate, List<VoxelChange>>();
        foreach (var write in staged)
        {
            var section = SectionCoordinate.FromBlock(plan.Dimension, write.Key);
            if (!sectionChanges.TryGetValue(section, out var changes))
            {
                changes = [];
                sectionChanges.Add(section, changes);
            }

            changes.Add(new VoxelChange(
                SectionCoordinate.LocalIndex(write.Key),
                write.Value.Before,
                write.Value.After,
                write.Value.OperationId));
        }

        var delta = new SceneDelta(baseScene.Revision, sectionChanges.Select(pair => new SectionDelta(pair.Key, pair.Value)));
        return new CompilationResult(delta, attemptedWrites, appliedWrites, skippedWrites);

        void Write(BlockPosition position, PlanOperation operation, BlockState targetState)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedWrites++;
            var before = baseScene.GetBlock(plan.Dimension, position);
            var current = staged.TryGetValue(position, out var previousWrite) ? previousWrite.After : before;

            switch (operation.WriteStrategy)
            {
                case WriteStrategy.OnlyAir when !current.IsAir:
                case WriteStrategy.ReplaceMatching when !current.Equals(operation.MatchState):
                    skippedWrites++;
                    return;
                case WriteStrategy.FailOnConflict when !current.IsAir && !current.Equals(targetState):
                    throw new PlanCompilationException(
                        $"操作 {operation.Id} 在 {position} 遇到非空气方块 {current.CanonicalKey}。",
                        operation.Id,
                        position);
            }

            if (current.Equals(targetState)) return;
            appliedWrites++;
            if (before.Equals(targetState)) staged.Remove(position);
            else staged[position] = new StagedWrite(before, targetState, operation.Id);
        }
    }

    private sealed record StagedWrite(BlockState Before, BlockState After, string OperationId);
}
