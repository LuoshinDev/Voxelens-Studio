namespace ZhuJieJing.Core;

public interface ISceneSnapshot
{
    string Revision { get; }

    BlockState GetBlock(string dimension, BlockPosition position);
}

public sealed record SceneBlock(string Dimension, BlockPosition Position, BlockState State);

public sealed class EmptySceneSnapshot : ISceneSnapshot
{
    public EmptySceneSnapshot(string revision = "empty")
    {
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("Revision 不能为空。", nameof(revision));
        Revision = revision;
    }

    public string Revision { get; }

    public BlockState GetBlock(string dimension, BlockPosition position) => BlockState.Air;
}

public sealed class InMemorySceneSnapshot : ISceneSnapshot
{
    private readonly Dictionary<SceneBlockKey, BlockState> _blocks;

    public InMemorySceneSnapshot(string revision, IEnumerable<SceneBlock>? blocks = null)
    {
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("Revision 不能为空。", nameof(revision));
        Revision = revision;
        _blocks = new Dictionary<SceneBlockKey, BlockState>();

        if (blocks is null) return;
        foreach (var block in blocks)
        {
            if (block.State.IsAir) continue;
            _blocks[new SceneBlockKey(block.Dimension, block.Position)] = block.State;
        }
    }

    public string Revision { get; }

    public BlockState GetBlock(string dimension, BlockPosition position) =>
        _blocks.TryGetValue(new SceneBlockKey(dimension, position), out var state) ? state : BlockState.Air;

    public InMemorySceneSnapshot Apply(SceneDelta delta, string newRevision)
    {
        if (!string.Equals(delta.BaseRevision, Revision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"SceneDelta 基于 {delta.BaseRevision}，当前场景为 {Revision}。");
        }

        var result = new Dictionary<SceneBlockKey, BlockState>(_blocks);
        foreach (var section in delta.Sections)
        {
            foreach (var change in section.Changes)
            {
                var position = section.Section.ToBlockPosition(change.LocalIndex);
                var key = new SceneBlockKey(section.Section.Dimension, position);
                var current = result.TryGetValue(key, out var state) ? state : BlockState.Air;
                if (!current.Equals(change.Before)) throw new InvalidOperationException($"体素 {position} 的前置状态不匹配。 ");

                if (change.After.IsAir) result.Remove(key);
                else result[key] = change.After;
            }
        }

        return new InMemorySceneSnapshot(newRevision, result.Select(pair => new SceneBlock(pair.Key.Dimension, pair.Key.Position, pair.Value)));
    }

    private readonly record struct SceneBlockKey(string Dimension, BlockPosition Position);
}
