using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using ZhuJieJing.Core;

namespace ZhuJieJing.SceneStore;

public sealed partial class ZjjSceneStore
{
    private const int ChangeInsertBatchSize = 128;
    private const int SnapshotCheckpointInterval = 32;
    private const int MaximumSnapshotCheckpoints = 3;
    private static readonly ConditionalWeakTable<SqliteConnection, Dictionary<(string Name, string Properties), BlockState>> MaterializedStates = new();

    private static BlockState ReadBlockStateCached(SqliteConnection connection, string name, string properties)
    {
        var cache = MaterializedStates.GetOrCreateValue(connection);
        if(cache.TryGetValue((name, properties), out BlockState? state)) return state;
        state = ReadBlockState(name, properties);
        if(cache.Count >= 65_536) cache.Clear();
        cache[(name, properties)] = state;
        return state;
    }

    private static Task EnsurePerformanceTablesAsync(SqliteConnection connection, CancellationToken token) =>
        ExecuteControlStatementAsync(connection, """
            CREATE TABLE IF NOT EXISTS cache_snapshot_checkpoints(
                revision_id TEXT PRIMARY KEY REFERENCES revisions(revision_id),
                revision_sequence INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cache_snapshot_voxels(
                revision_id TEXT NOT NULL REFERENCES cache_snapshot_checkpoints(revision_id) ON DELETE CASCADE,
                dimension TEXT NOT NULL, section_x INTEGER NOT NULL, section_y INTEGER NOT NULL,
                section_z INTEGER NOT NULL, local_index INTEGER NOT NULL, state_id INTEGER NOT NULL REFERENCES block_states(state_id),
                PRIMARY KEY(revision_id, dimension, section_x, section_y, section_z, local_index)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS idx_revision_changes_section
                ON revision_changes(dimension, section_x, section_y, section_z, revision_id, local_index);
            """, token);

    private static async Task StageCommitChangesAsync(SqliteConnection connection, SceneDelta delta,
        IReadOnlyDictionary<string, CachedBlockState> states, CancellationToken token)
    {
        await ExecuteControlStatementAsync(connection, """
            CREATE TEMP TABLE IF NOT EXISTS pending_changes(
                dimension TEXT NOT NULL, section_x INTEGER NOT NULL, section_y INTEGER NOT NULL, section_z INTEGER NOT NULL,
                local_index INTEGER NOT NULL, before_state_id INTEGER NOT NULL, after_state_id INTEGER NOT NULL,
                before_air INTEGER NOT NULL, after_air INTEGER NOT NULL, operation_id TEXT NOT NULL,
                PRIMARY KEY(dimension,section_x,section_y,section_z,local_index)
            ) WITHOUT ROWID;
            DELETE FROM pending_changes;
            """, token);
        // Reuse one prepared multi-row statement for full batches; no SQL or provider call per voxel.
        using SqliteCommand insert = CreateInsert(ChangeInsertBatchSize);
        var pending = new List<(SectionCoordinate Section, VoxelChange Change)>(ChangeInsertBatchSize);
        foreach(SectionDelta section in delta.Sections)
        foreach(VoxelChange change in section.Changes)
        {
            token.ThrowIfCancellationRequested();
            pending.Add((section.Section, change));
            if(pending.Count < ChangeInsertBatchSize) continue;
            await WriteBatchAsync(insert);
            pending.Clear();
        }
        if(pending.Count > 0)
        {
            using SqliteCommand tail = CreateInsert(pending.Count);
            await WriteBatchAsync(tail);
        }

        SqliteCommand CreateInsert(int count)
        {
            SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO pending_changes VALUES " + string.Join(',', Enumerable.Range(0, count)
                .Select(row => "(" + string.Join(',', Enumerable.Range(0, 10).Select(column => $"$p{row * 10 + column}")) + ")"));
            for(int row = 0; row < count; row++)
            for(int column = 0; column < 10; column++)
                command.Parameters.Add($"$p{row * 10 + column}", column is 0 or 9 ? SqliteType.Text : SqliteType.Integer);
            command.Prepare();
            return command;
        }

        async Task WriteBatchAsync(SqliteCommand command)
        {
            int index = 0;
            foreach(var (section, change) in pending)
            {
                command.Parameters[index++].Value = section.Dimension;
                command.Parameters[index++].Value = section.X;
                command.Parameters[index++].Value = section.Y;
                command.Parameters[index++].Value = section.Z;
                command.Parameters[index++].Value = change.LocalIndex;
                command.Parameters[index++].Value = states[change.Before.CanonicalKey].Id;
                command.Parameters[index++].Value = states[change.After.CanonicalKey].Id;
                command.Parameters[index++].Value = change.Before.IsAir ? 1 : 0;
                command.Parameters[index++].Value = change.After.IsAir ? 1 : 0;
                command.Parameters[index++].Value = change.OperationId;
            }
            await command.ExecuteNonQueryAsync(token);
        }
    }

    private static async Task ValidateStagedHeadAsync(SqliteConnection connection, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.dimension,p.section_x,p.section_y,p.section_z,p.local_index,p.operation_id,
                   b.name,b.properties_json,a.name,a.properties_json
            FROM pending_changes p
            JOIN block_states b ON b.state_id=p.before_state_id
            LEFT JOIN head_voxels h ON h.dimension=p.dimension AND h.section_x=p.section_x
                AND h.section_y=p.section_y AND h.section_z=p.section_z AND h.local_index=p.local_index
            LEFT JOIN block_states a ON a.state_id=h.state_id
            WHERE (h.state_id IS NULL AND p.before_air=0)
                OR (h.state_id IS NOT NULL AND (p.before_air=1 OR h.state_id<>p.before_state_id))
            ORDER BY p.dimension,p.section_x,p.section_y,p.section_z,p.local_index LIMIT 1;
            """;
        using var reader = await command.ExecuteReaderAsync(token);
        if(!await reader.ReadAsync(token)) return;
        throw new SceneVoxelConflictException(new SectionCoordinate(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)),
            reader.GetInt32(4), ReadBlockStateCached(connection, reader.GetString(6), reader.GetString(7)),
            reader.IsDBNull(8) ? BlockState.Air : ReadBlockStateCached(connection, reader.GetString(8), reader.GetString(9)), reader.GetString(5));
    }

    private static async Task ApplyStagedChangesAsync(SqliteConnection connection, string revisionId, CancellationToken token)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO revision_changes(revision_id,dimension,section_x,section_y,section_z,local_index,before_state_id,after_state_id,operation_id)
                SELECT $revision,dimension,section_x,section_y,section_z,local_index,before_state_id,after_state_id,operation_id FROM pending_changes;
            DELETE FROM head_voxels WHERE (dimension,section_x,section_y,section_z,local_index) IN
                (SELECT dimension,section_x,section_y,section_z,local_index FROM pending_changes WHERE after_air=1);
            INSERT INTO head_voxels(dimension,section_x,section_y,section_z,local_index,state_id)
                SELECT dimension,section_x,section_y,section_z,local_index,after_state_id FROM pending_changes WHERE after_air=0
                ON CONFLICT(dimension,section_x,section_y,section_z,local_index) DO UPDATE SET state_id=excluded.state_id;
            DELETE FROM pending_changes;
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task SavePeriodicSnapshotAsync(SqliteConnection connection, string revisionId, CancellationToken token)
    {
        StoredSceneRevision revision = await ReadRevisionCoreAsync(connection, revisionId, token);
        if((revision.Sequence - 1) % SnapshotCheckpointInterval != 0) return;
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO cache_snapshot_checkpoints(revision_id,revision_sequence) VALUES($revision,$sequence);
            INSERT INTO cache_snapshot_voxels(revision_id,dimension,section_x,section_y,section_z,local_index,state_id)
                SELECT $revision,dimension,section_x,section_y,section_z,local_index,state_id FROM head_voxels;
            DELETE FROM cache_snapshot_checkpoints WHERE revision_id IN
                (SELECT revision_id FROM cache_snapshot_checkpoints ORDER BY revision_sequence DESC LIMIT -1 OFFSET $keep);
            """;
        command.Parameters.AddWithValue("$revision", revisionId);
        command.Parameters.AddWithValue("$sequence", revision.Sequence);
        command.Parameters.AddWithValue("$keep", MaximumSnapshotCheckpoints);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<T> InReadTransactionAsync<T>(SqliteConnection connection, Func<Task<T>> read, CancellationToken token)
    {
        await ExecuteControlStatementAsync(connection, "BEGIN;", token);
        try
        {
            T result = await read();
            await ExecuteControlStatementAsync(connection, "COMMIT;", CancellationToken.None);
            return result;
        }
        catch
        {
            await ExecuteControlStatementAsync(connection, "ROLLBACK;", CancellationToken.None);
            throw;
        }
    }

    private static async Task<StoredSceneSnapshot> ReadHistoricalSnapshotAsync(SqliteConnection connection, string revisionId,
        IReadOnlyCollection<SectionCoordinate>? selectedSections, CancellationToken token)
    {
        var lineage = new List<StoredSceneRevision>();
        StoredSceneRevision cursor = await ReadRevisionCoreAsync(connection, revisionId, token);
        string? checkpoint = null;
        using SqliteCommand checkpointQuery = connection.CreateCommand();
        checkpointQuery.CommandText = "SELECT 1 FROM cache_snapshot_checkpoints WHERE revision_id=$revision;";
        checkpointQuery.Parameters.Add("$revision", SqliteType.Text);
        while(cursor.ParentRevisionId is not null)
        {
            checkpointQuery.Parameters[0].Value = cursor.RevisionId;
            if(await checkpointQuery.ExecuteScalarAsync(token) is not null)
            {
                checkpoint = cursor.RevisionId;
                break;
            }
            lineage.Add(cursor);
            cursor = await ReadRevisionCoreAsync(connection, cursor.ParentRevisionId, token);
        }
        var state = new Dictionary<SectionCoordinate, Dictionary<int, BlockState>>();
        SectionCoordinate[]? filter = selectedSections?.Distinct().ToArray();
        if(filter is { Length: 0 }) return BuildSnapshot(revisionId, state);
        if(checkpoint is not null)
        {
            if(filter is null) await ReadCheckpointSectionAsync(null);
            else foreach(SectionCoordinate section in filter) await ReadCheckpointSectionAsync(section);
        }
        lineage.Reverse();
        foreach(StoredSceneRevision revision in lineage)
        {
            if(filter is null) Apply(await ReadChangesCoreAsync(connection, revision.RevisionId, null, token));
            else foreach(SectionCoordinate section in filter)
                Apply(await ReadChangesCoreAsync(connection, revision.RevisionId, section, token));
        }
        return BuildSnapshot(revisionId, state);

        void Apply(IEnumerable<StoredChange> changes)
        {
            foreach(StoredChange change in changes)
            {
                token.ThrowIfCancellationRequested();
                if(!state.TryGetValue(change.Section, out var blocks)) state.Add(change.Section, blocks = []);
                if(change.Change.After.IsAir) blocks.Remove(change.Change.LocalIndex);
                else blocks[change.Change.LocalIndex] = change.Change.After;
            }
        }

        async Task ReadCheckpointSectionAsync(SectionCoordinate? section)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT v.dimension,v.section_x,v.section_y,v.section_z,v.local_index,s.name,s.properties_json
                FROM cache_snapshot_voxels v JOIN block_states s ON s.state_id=v.state_id WHERE v.revision_id=$revision
                """ + (section is null ? "" : " AND v.dimension=$dimension AND v.section_x=$x AND v.section_y=$y AND v.section_z=$z") + ";";
            command.Parameters.AddWithValue("$revision", checkpoint!);
            if(section is { } selected)
            {
                command.Parameters.AddWithValue("$dimension", selected.Dimension);
                command.Parameters.AddWithValue("$x", selected.X);
                command.Parameters.AddWithValue("$y", selected.Y);
                command.Parameters.AddWithValue("$z", selected.Z);
            }
            using var reader = await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                var coordinate = new SectionCoordinate(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
                if(!state.TryGetValue(coordinate, out var blocks)) state.Add(coordinate, blocks = []);
                blocks.Add(reader.GetInt32(4), ReadBlockStateCached(connection, reader.GetString(5), reader.GetString(6)));
            }
        }
    }
}
