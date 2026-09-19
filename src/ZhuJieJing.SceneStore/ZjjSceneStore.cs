using System.Data;
using System.Text.Json;
using ZhuJieJing.Core;
using Microsoft.Data.Sqlite;

namespace ZhuJieJing.SceneStore;

public sealed partial class ZjjSceneStore : IAsyncDisposable
{
    public const int CurrentSchemaVersion = 2;
    public const string SceneFormat = "zhujie.scene/2";

    private static readonly HashSet<string> ReservedMetaKeys = new(StringComparer.Ordinal)
    {
        "scene_format",
        "current_revision",
    };

    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    private ZjjSceneStore(string filePath, SqliteConnection connection)
    {
        FilePath = filePath;
        _connection = connection;
    }

    public string FilePath { get; }

    public int SchemaVersion => CurrentSchemaVersion;

    public static async Task<ZjjSceneStore> CreateAsync(
        string filePath,
        string initialRevision = "root",
        CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(filePath);
        if (string.IsNullOrWhiteSpace(initialRevision)) throw new ArgumentException("初始 revision 不能为空。", nameof(initialRevision));
        if (File.Exists(fullPath)) throw new IOException($"场景文件已经存在：{fullPath}");

        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        SqliteConnection? stagingConnection = null;
        try
        {
            stagingConnection = await OpenConnectionAsync(
                temporaryPath,
                SqliteOpenMode.ReadWriteCreate,
                cancellationToken);
            await InitializeSchemaAsync(stagingConnection, initialRevision, cancellationToken);
            await VerifySchemaAsync(stagingConnection, cancellationToken);
            await CheckpointAsync(stagingConnection, cancellationToken, requireComplete: true);
            await stagingConnection.DisposeAsync();
            stagingConnection = null;
            DeleteTemporaryDatabaseSidecars(temporaryPath);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: false);

            var connection = await OpenConnectionAsync(fullPath, SqliteOpenMode.ReadWrite, CancellationToken.None);
            try
            {
                await VerifySchemaAsync(connection, CancellationToken.None);
                await EnsurePerformanceTablesAsync(connection, CancellationToken.None);
                return new ZjjSceneStore(fullPath, connection);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
        catch
        {
            if (stagingConnection is not null)
            {
                try
                {
                    await stagingConnection.DisposeAsync();
                }
                catch
                {
                    // Preserve the creation failure and continue cleaning only our staging files.
                }
            }

            DeleteTemporaryDatabaseFiles(temporaryPath);
            throw;
        }
    }

    public static async Task<ZjjSceneStore> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = ValidatePath(filePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("场景文件不存在。", fullPath);

        var connection = await OpenConnectionAsync(fullPath, SqliteOpenMode.ReadWrite, cancellationToken);
        try
        {
            await VerifySchemaAsync(connection, cancellationToken);
            await EnsurePerformanceTablesAsync(connection, cancellationToken);
            return new ZjjSceneStore(fullPath, connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public Task<string> GetCurrentRevisionAsync(CancellationToken cancellationToken = default) =>
        UseConnectionAsync((connection, token) => ReadRequiredMetaAsync(connection, "current_revision", token), cancellationToken);

    public Task<string> GetJournalModeAsync(CancellationToken cancellationToken = default) =>
        UseConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            var value = await command.ExecuteScalarAsync(token);
            return Convert.ToString(value) ?? string.Empty;
        }, cancellationToken);

    public Task<string?> GetMetadataAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateMetadataKey(key, allowReserved: true);
        return UseConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key;";
            command.Parameters.AddWithValue("$key", key);
            var value = await command.ExecuteScalarAsync(token);
            return value is null or DBNull ? null : Convert.ToString(value);
        }, cancellationToken);
    }

    public Task SetMetadataAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ValidateMetadataKey(key, allowReserved: false);
        ArgumentNullException.ThrowIfNull(value);
        return UseConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO meta(key, value) VALUES($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            await command.ExecuteNonQueryAsync(token);
        }, cancellationToken);
    }

    public Task<SceneCommitResult> CommitAsync(SceneDelta delta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return UseConnectionAsync((connection, token) => CommitCoreAsync(connection, delta, token), cancellationToken);
    }

    public Task<StoredSceneRevision> ReadRevisionAsync(string revisionId, CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync((connection, token) => ReadRevisionCoreAsync(connection, revisionId, token), cancellationToken);
    }

    public Task<SceneDelta> ReadDeltaAsync(string revisionId, CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync((connection, token) => ReadDeltaCoreAsync(connection, revisionId, token), cancellationToken);
    }

    public Task<SectionDelta?> ReadSectionDeltaAsync(
        string revisionId,
        SectionCoordinate section,
        CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync(async (connection, token) =>
        {
            await ReadRevisionCoreAsync(connection, revisionId, token);
            var integrity = await ReadSectionIntegrityCoreAsync(connection, revisionId, section, token);
            var changes = await ReadChangesCoreAsync(connection, revisionId, section, token);
            if (integrity.Count == 0)
            {
                if (changes.Count != 0)
                {
                    throw new SceneStoreCorruptionException(
                        $"Revision {revisionId} 的 Section {section} 缺少完整性记录。");
                }

                return null;
            }

            if (changes.Count == 0)
            {
                throw new SceneStoreCorruptionException(
                    $"Revision {revisionId} 的 Section {section} 有完整性记录但没有变化。");
            }

            var result = new SectionDelta(section, changes.Select(static change => change.Change));
            ValidateSectionIntegrity(revisionId, result, integrity[0]);
            return result;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<StoredSceneRevision>> ListRevisionsAsync(
        CancellationToken cancellationToken = default) =>
        UseConnectionAsync(async (connection, token) =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT revision_sequence, revision_id, parent_revision_id, delta_hash, change_count
                FROM revisions
                ORDER BY revision_sequence;
                """;
            var result = new List<StoredSceneRevision>();
            await using var reader = await command.ExecuteReaderAsync(token);
            while(await reader.ReadAsync(token))
            {
                result.Add(new StoredSceneRevision(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt32(4)));
            }
            return (IReadOnlyList<StoredSceneRevision>)result;
        }, cancellationToken);

    public Task<StoredSceneSnapshot> ReadHeadSnapshotAsync(
        IReadOnlyCollection<SectionCoordinate>? sections = null,
        CancellationToken cancellationToken = default) =>
        UseConnectionAsync((connection, token) => InReadTransactionAsync(connection, async () =>
        {
            string revision = await ReadRequiredMetaAsync(connection, "current_revision", token);
            return await ReadHeadSnapshotCoreAsync(connection, revision, sections, token);
        }, token), cancellationToken);

    public Task<StoredSceneSnapshot> ReadSnapshotAsync(
        string revisionId,
        IReadOnlyCollection<SectionCoordinate>? sections = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync((connection, token) => InReadTransactionAsync(connection, async () =>
        {
            string current = await ReadRequiredMetaAsync(connection, "current_revision", token);
            if(string.Equals(current, revisionId, StringComparison.Ordinal))
                return await ReadHeadSnapshotCoreAsync(connection, revisionId, sections, token);
            return await ReadHistoricalSnapshotAsync(connection, revisionId, sections, token);
        }, token), cancellationToken);
    }

    public Task<SceneDelta> CreateSelectiveInverseDeltaAsync(
        string revisionId,
        CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync(async (connection, token) =>
        {
            string current = await ReadRequiredMetaAsync(connection, "current_revision", token);
            SceneDelta original = await ReadDeltaCoreAsync(connection, revisionId, token);
            StoredSceneSnapshot head = await ReadHeadSnapshotCoreAsync(
                connection,
                current,
                original.Sections.Select(static section => section.Section).ToArray(),
                token);
            var reverted = new List<SectionDelta>();
            foreach(SectionDelta section in original.Sections)
            {
                var changes = new List<VoxelChange>();
                foreach(VoxelChange change in section.Changes)
                {
                    BlockPosition position = section.Section.ToBlockPosition(change.LocalIndex);
                    BlockState actual = head.GetBlock(section.Section.Dimension, position);
                    if(!actual.Equals(change.After) || actual.Equals(change.Before)) continue;
                    changes.Add(new VoxelChange(
                        change.LocalIndex,
                        actual,
                        change.Before,
                        $"revert:{revisionId}:{change.OperationId}"));
                }
                if(changes.Count > 0) reverted.Add(new SectionDelta(section.Section, changes));
            }
            return new SceneDelta(current, reverted);
        }, cancellationToken);
    }

    public Task<SceneDelta> CreateInverseDeltaAsync(string revisionId, CancellationToken cancellationToken = default)
    {
        ValidateRevisionId(revisionId);
        return UseConnectionAsync(async (connection, token) =>
        {
            var currentRevision = await ReadRequiredMetaAsync(connection, "current_revision", token);
            if (!string.Equals(currentRevision, revisionId, StringComparison.Ordinal))
            {
                throw new SceneRevisionConflictException(revisionId, currentRevision);
            }

            var delta = await ReadDeltaCoreAsync(connection, revisionId, token);
            return delta.Invert(currentRevision);
        }, cancellationToken);
    }

    private static async Task<StoredSceneSnapshot> ReadHeadSnapshotCoreAsync(
        SqliteConnection connection,
        string revision,
        IReadOnlyCollection<SectionCoordinate>? selectedSections,
        CancellationToken cancellationToken)
    {
        var state = new Dictionary<SectionCoordinate, Dictionary<int, BlockState>>();
        if(selectedSections is not null)
        {
            foreach(SectionCoordinate section in selectedSections.Distinct())
                await ReadHeadSectionAsync(connection, section, state, cancellationToken);
            return BuildSnapshot(revision, state);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.dimension, h.section_x, h.section_y, h.section_z, h.local_index,
                   s.name, s.properties_json
            FROM head_voxels h
            JOIN block_states s ON s.state_id = h.state_id
            ORDER BY h.dimension, h.section_x, h.section_y, h.section_z, h.local_index;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while(await reader.ReadAsync(cancellationToken))
        {
            var coordinate = new SectionCoordinate(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
            if(!state.TryGetValue(coordinate, out Dictionary<int, BlockState>? blocks))
            {
                blocks = [];
                state.Add(coordinate, blocks);
            }
            blocks.Add(reader.GetInt32(4), ReadBlockStateCached(connection, reader.GetString(5), reader.GetString(6)));
        }
        return BuildSnapshot(revision, state);
    }

    private static async Task ReadHeadSectionAsync(
        SqliteConnection connection,
        SectionCoordinate section,
        IDictionary<SectionCoordinate, Dictionary<int, BlockState>> state,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT h.local_index, s.name, s.properties_json
            FROM head_voxels h
            JOIN block_states s ON s.state_id = h.state_id
            WHERE h.dimension = $dimension
              AND h.section_x = $section_x
              AND h.section_y = $section_y
              AND h.section_z = $section_z
            ORDER BY h.local_index;
            """;
        command.Parameters.AddWithValue("$dimension", section.Dimension);
        command.Parameters.AddWithValue("$section_x", section.X);
        command.Parameters.AddWithValue("$section_y", section.Y);
        command.Parameters.AddWithValue("$section_z", section.Z);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Dictionary<int, BlockState>? blocks = null;
        while(await reader.ReadAsync(cancellationToken))
        {
            blocks ??= [];
            blocks.Add(reader.GetInt32(0), ReadBlockStateCached(connection, reader.GetString(1), reader.GetString(2)));
        }
        if(blocks is not null) state.Add(section, blocks);
    }

    private static StoredSceneSnapshot BuildSnapshot(
        string revision,
        Dictionary<SectionCoordinate, Dictionary<int, BlockState>> state) =>
        new(revision, state.Select(static pair => new StoredSceneSection(pair.Key, pair.Value)));

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                await CheckpointAsync(_connection, CancellationToken.None);
            }
            finally
            {
                await _connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> UseConnectionAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await action(_connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UseConnectionAsync(
        Func<SqliteConnection, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await action(_connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static async Task<SceneCommitResult> CommitCoreAsync(
        SqliteConnection connection,
        SceneDelta delta,
        CancellationToken cancellationToken)
    {
        var revisionId = $"sha256:{delta.StableHash}";
        await ExecuteControlStatementAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
        var transactionOpen = true;
        try
        {
            var currentRevision = await ReadRequiredMetaAsync(connection, "current_revision", cancellationToken);
            if (string.Equals(currentRevision, revisionId, StringComparison.Ordinal))
            {
                var idempotentResult = await ReadIdempotentCommitAsync(
                    connection,
                    revisionId,
                    delta,
                    cancellationToken);
                await ExecuteControlStatementAsync(connection, "COMMIT;", CancellationToken.None);
                transactionOpen = false;
                return idempotentResult;
            }

            if (!string.Equals(currentRevision, delta.BaseRevision, StringComparison.Ordinal))
            {
                throw new SceneRevisionConflictException(delta.BaseRevision, currentRevision);
            }

            var stateCache = await ResolveBlockStateIdsAsync(connection, delta, cancellationToken);
            await StageCommitChangesAsync(connection, delta, stateCache, cancellationToken);
            await ValidateStagedHeadAsync(connection, cancellationToken);
            await InsertRevisionAsync(connection, revisionId, delta, cancellationToken);
            await InsertSectionIntegrityAsync(connection, revisionId, delta.Sections, cancellationToken);
            await ApplyStagedChangesAsync(connection, revisionId, cancellationToken);
            await SavePeriodicSnapshotAsync(connection, revisionId, cancellationToken);

            await SetRequiredMetaAsync(connection, "current_revision", revisionId, cancellationToken);
            await ExecuteControlStatementAsync(connection, "COMMIT;", CancellationToken.None);
            transactionOpen = false;
            return new SceneCommitResult(revisionId, delta.BaseRevision, delta.StableHash, delta.ChangedVoxelCount);
        }
        catch
        {
            if (transactionOpen)
            {
                try
                {
                    await ExecuteControlStatementAsync(connection, "ROLLBACK;", CancellationToken.None);
                }
                catch
                {
                    // Preserve the original transaction failure.
                }
            }

            throw;
        }
    }

    private static async Task<SceneCommitResult> ReadIdempotentCommitAsync(
        SqliteConnection connection,
        string revisionId,
        SceneDelta delta,
        CancellationToken cancellationToken)
    {
        var revision = await ReadRevisionCoreAsync(connection, revisionId, cancellationToken);
        if (!string.Equals(revision.ParentRevisionId, delta.BaseRevision, StringComparison.Ordinal) ||
            !string.Equals(revision.DeltaHash, delta.StableHash, StringComparison.Ordinal) ||
            revision.ChangeCount != delta.ChangedVoxelCount)
        {
            throw new SceneStoreCorruptionException($"当前 revision {revisionId} 与幂等提交内容不一致。");
        }

        var storedDelta = await ReadDeltaCoreAsync(connection, revisionId, cancellationToken);
        if (storedDelta.Sections.Count != delta.Sections.Count ||
            storedDelta.Sections.Where((section, index) =>
                section.Section != delta.Sections[index].Section ||
                !string.Equals(section.StableHash, delta.Sections[index].StableHash, StringComparison.Ordinal)).Any())
        {
            throw new SceneStoreCorruptionException($"当前 revision {revisionId} 与幂等提交的 Section 不一致。");
        }

        return new SceneCommitResult(revisionId, delta.BaseRevision, delta.StableHash, delta.ChangedVoxelCount);
    }

    private static async Task InsertRevisionAsync(
        SqliteConnection connection,
        string revisionId,
        SceneDelta delta,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO revisions(revision_id, parent_revision_id, delta_hash, change_count)
            VALUES($revision_id, $parent_revision_id, $delta_hash, $change_count);
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        command.Parameters.AddWithValue("$parent_revision_id", delta.BaseRevision);
        command.Parameters.AddWithValue("$delta_hash", delta.StableHash);
        command.Parameters.AddWithValue("$change_count", delta.ChangedVoxelCount);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, CachedBlockState>> ResolveBlockStateIdsAsync(
        SqliteConnection connection,
        SceneDelta delta,
        CancellationToken cancellationToken)
    {
        var states = new Dictionary<string, PendingBlockState>(StringComparer.Ordinal);
        foreach (var change in delta.Sections.SelectMany(static section => section.Changes))
        {
            AddPendingState(states, change.Before);
            AddPendingState(states, change.After);
        }

        var cache = new Dictionary<string, CachedBlockState>(states.Count, StringComparer.Ordinal);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO block_states(canonical_key, name, properties_json)
            VALUES($canonical_key, $name, $properties_json)
            ON CONFLICT(canonical_key) DO NOTHING;
            """;
        var insertCanonicalKey = insert.Parameters.Add("$canonical_key", SqliteType.Text);
        var insertName = insert.Parameters.Add("$name", SqliteType.Text);
        var insertProperties = insert.Parameters.Add("$properties_json", SqliteType.Text);
        insert.Prepare();

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT state_id, name, properties_json
            FROM block_states
            WHERE canonical_key = $canonical_key;
            """;
        var selectCanonicalKey = select.Parameters.Add("$canonical_key", SqliteType.Text);
        select.Prepare();

        foreach (var pending in states.Values.OrderBy(static state => state.State.CanonicalKey, StringComparer.Ordinal))
        {
            insertCanonicalKey.Value = pending.State.CanonicalKey;
            insertName.Value = pending.State.Name;
            insertProperties.Value = pending.PropertiesJson;
            await insert.ExecuteNonQueryAsync(cancellationToken);

            selectCanonicalKey.Value = pending.State.CanonicalKey;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new SceneStoreCorruptionException($"无法读取方块状态 {pending.State.CanonicalKey}。");
            }

            var stored = new CachedBlockState(reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
            EnsureSameState(pending.State, pending.PropertiesJson, stored.Name, stored.PropertiesJson);
            cache.Add(pending.State.CanonicalKey, stored);
        }

        return cache;
    }

    private static void AddPendingState(IDictionary<string, PendingBlockState> states, BlockState state)
    {
        var propertiesJson = JsonSerializer.Serialize(state.Properties);
        if (states.TryGetValue(state.CanonicalKey, out var existing))
        {
            EnsureSameState(state, propertiesJson, existing.State.Name, existing.PropertiesJson);
            return;
        }

        states.Add(state.CanonicalKey, new PendingBlockState(state, propertiesJson));
    }

    private static void EnsureSameState(BlockState state, string propertiesJson, string storedName, string storedPropertiesJson)
    {
        if (!string.Equals(state.Name, storedName, StringComparison.Ordinal) ||
           !string.Equals(propertiesJson, storedPropertiesJson, StringComparison.Ordinal))
        {
            throw new SceneStoreCorruptionException($"规范方块键 {state.CanonicalKey} 对应了不同状态。");
        }
    }

    private static async Task InsertSectionIntegrityAsync(
        SqliteConnection connection,
        string revisionId,
        IReadOnlyList<SectionDelta> sections,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO revision_sections(
                revision_id, dimension, section_x, section_y, section_z, section_hash, change_count)
            VALUES(
                $revision_id, $dimension, $section_x, $section_y, $section_z, $section_hash, $change_count);
            """;
        var revision = command.Parameters.Add("$revision_id", SqliteType.Text);
        var dimension = command.Parameters.Add("$dimension", SqliteType.Text);
        var sectionX = command.Parameters.Add("$section_x", SqliteType.Integer);
        var sectionY = command.Parameters.Add("$section_y", SqliteType.Integer);
        var sectionZ = command.Parameters.Add("$section_z", SqliteType.Integer);
        var sectionHash = command.Parameters.Add("$section_hash", SqliteType.Text);
        var changeCount = command.Parameters.Add("$change_count", SqliteType.Integer);
        command.Prepare();

        revision.Value = revisionId;
        foreach (var section in sections)
        {
            dimension.Value = section.Section.Dimension;
            sectionX.Value = section.Section.X;
            sectionY.Value = section.Section.Y;
            sectionZ.Value = section.Section.Z;
            sectionHash.Value = section.StableHash;
            changeCount.Value = section.Changes.Count;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<StoredSceneRevision> ReadRevisionCoreAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision_sequence, revision_id, parent_revision_id, delta_hash, change_count
            FROM revisions
            WHERE revision_id = $revision_id;
            """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new KeyNotFoundException($"Revision {revisionId} 不存在。");

        return new StoredSceneRevision(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4));
    }

    private static async Task<SceneDelta> ReadDeltaCoreAsync(
        SqliteConnection connection,
        string revisionId,
        CancellationToken cancellationToken)
    {
        var revision = await ReadRevisionCoreAsync(connection, revisionId, cancellationToken);
        if (revision.ParentRevisionId is null || revision.DeltaHash is null)
        {
            throw new InvalidOperationException("根 revision 没有可读取的 SceneDelta。");
        }

        var changes = await ReadChangesCoreAsync(connection, revisionId, null, cancellationToken);
        var sections = changes
            .GroupBy(change => change.Section)
            .Select(group => new SectionDelta(group.Key, group.Select(change => change.Change)))
            .ToArray();
        var integrity = await ReadSectionIntegrityCoreAsync(connection, revisionId, null, cancellationToken);
        if (integrity.Count != sections.Length)
        {
            throw new SceneStoreCorruptionException(
                $"Revision {revisionId} 的 Section 完整性记录数量不匹配。");
        }

        var integrityBySection = integrity.ToDictionary(static item => item.Section);
        foreach (var section in sections)
        {
            if (!integrityBySection.TryGetValue(section.Section, out var stored))
            {
                throw new SceneStoreCorruptionException(
                    $"Revision {revisionId} 的 Section {section.Section} 缺少完整性记录。");
            }

            ValidateSectionIntegrity(revisionId, section, stored);
        }

        var delta = new SceneDelta(revision.ParentRevisionId, sections);
        if (delta.ChangedVoxelCount != revision.ChangeCount ||
           !string.Equals(delta.StableHash, revision.DeltaHash, StringComparison.Ordinal))
        {
            throw new SceneStoreCorruptionException($"Revision {revisionId} 的变化记录与稳定哈希不匹配。");
        }

        return delta;
    }

    private static void ValidateSectionIntegrity(
        string revisionId,
        SectionDelta section,
        StoredSectionIntegrity integrity)
    {
        if (section.Changes.Count != integrity.ChangeCount ||
            !string.Equals(section.StableHash, integrity.SectionHash, StringComparison.Ordinal))
        {
            throw new SceneStoreCorruptionException(
                $"Revision {revisionId} 的 Section {section.Section} 与稳定哈希不匹配。");
        }
    }

    private static async Task<List<StoredSectionIntegrity>> ReadSectionIntegrityCoreAsync(
        SqliteConnection connection,
        string revisionId,
        SectionCoordinate? section,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = section is null
            ? """
                SELECT dimension, section_x, section_y, section_z, section_hash, change_count
                FROM revision_sections
                WHERE revision_id = $revision_id
                ORDER BY dimension, section_x, section_y, section_z;
                """
            : """
                SELECT dimension, section_x, section_y, section_z, section_hash, change_count
                FROM revision_sections
                WHERE revision_id = $revision_id
                  AND dimension = $dimension
                  AND section_x = $section_x
                  AND section_y = $section_y
                  AND section_z = $section_z;
                """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        if (section is not null)
        {
            command.Parameters.AddWithValue("$dimension", section.Value.Dimension);
            command.Parameters.AddWithValue("$section_x", section.Value.X);
            command.Parameters.AddWithValue("$section_y", section.Value.Y);
            command.Parameters.AddWithValue("$section_z", section.Value.Z);
        }

        var result = new List<StoredSectionIntegrity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new StoredSectionIntegrity(
                new SectionCoordinate(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3)),
                reader.GetString(4),
                reader.GetInt32(5)));
        }

        return result;
    }

    private static async Task<List<StoredChange>> ReadChangesCoreAsync(
        SqliteConnection connection,
        string revisionId,
        SectionCoordinate? section,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = section is null
            ? """
                SELECT c.dimension, c.section_x, c.section_y, c.section_z, c.local_index,
                       before_state.name, before_state.properties_json,
                       after_state.name, after_state.properties_json, c.operation_id
                FROM revision_changes c
                JOIN block_states before_state ON before_state.state_id = c.before_state_id
                JOIN block_states after_state ON after_state.state_id = c.after_state_id
                WHERE c.revision_id = $revision_id
                ORDER BY c.dimension, c.section_x, c.section_y, c.section_z, c.local_index;
                """
            : """
                SELECT c.dimension, c.section_x, c.section_y, c.section_z, c.local_index,
                       before_state.name, before_state.properties_json,
                       after_state.name, after_state.properties_json, c.operation_id
                FROM revision_changes c
                JOIN block_states before_state ON before_state.state_id = c.before_state_id
                JOIN block_states after_state ON after_state.state_id = c.after_state_id
                WHERE c.revision_id = $revision_id
                  AND c.dimension = $dimension
                  AND c.section_x = $section_x
                  AND c.section_y = $section_y
                  AND c.section_z = $section_z
                ORDER BY c.local_index;
                """;
        command.Parameters.AddWithValue("$revision_id", revisionId);
        if (section is not null)
        {
            command.Parameters.AddWithValue("$dimension", section.Value.Dimension);
            command.Parameters.AddWithValue("$section_x", section.Value.X);
            command.Parameters.AddWithValue("$section_y", section.Value.Y);
            command.Parameters.AddWithValue("$section_z", section.Value.Z);
        }

        var result = new List<StoredChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var coordinate = new SectionCoordinate(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3));
            var change = new VoxelChange(
                reader.GetInt32(4),
                ReadBlockStateCached(connection, reader.GetString(5), reader.GetString(6)),
                ReadBlockStateCached(connection, reader.GetString(7), reader.GetString(8)),
                reader.GetString(9));
            result.Add(new StoredChange(coordinate, change));
        }

        return result;
    }

    private static BlockState ReadBlockState(string name, string propertiesJson)
    {
        try
        {
            var properties = JsonSerializer.Deserialize<Dictionary<string, string>>(propertiesJson)
                ?? throw new SceneStoreCorruptionException($"方块状态 {name} 的 properties 无效。");
            return new BlockState(name, properties);
        }
        catch (JsonException exception)
        {
            throw new SceneStoreCorruptionException($"方块状态 {name} 的 properties 无效：{exception.Message}");
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(
        string fullPath,
        SqliteOpenMode mode,
        CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await ExecuteControlStatementAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
            await ExecuteControlStatementAsync(connection, "PRAGMA busy_timeout=5000;", cancellationToken);
            await ExecuteControlStatementAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
            await ExecuteControlStatementAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task InitializeSchemaAsync(
        SqliteConnection connection,
        string initialRevision,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var schema = connection.CreateCommand())
        {
            schema.Transaction = (SqliteTransaction)transaction;
            schema.CommandText = """
                CREATE TABLE schema_version(
                    singleton INTEGER PRIMARY KEY CHECK(singleton = 1),
                    version INTEGER NOT NULL
                );

                CREATE TABLE meta(
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE TABLE block_states(
                    state_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    canonical_key TEXT NOT NULL UNIQUE,
                    name TEXT NOT NULL,
                    properties_json TEXT NOT NULL
                );

                CREATE TABLE revisions(
                    revision_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    revision_id TEXT NOT NULL UNIQUE,
                    parent_revision_id TEXT NULL REFERENCES revisions(revision_id),
                    delta_hash TEXT NULL,
                    change_count INTEGER NOT NULL CHECK(change_count >= 0)
                );

                CREATE TABLE revision_sections(
                    revision_id TEXT NOT NULL REFERENCES revisions(revision_id) ON DELETE CASCADE,
                    dimension TEXT NOT NULL,
                    section_x INTEGER NOT NULL,
                    section_y INTEGER NOT NULL,
                    section_z INTEGER NOT NULL,
                    section_hash TEXT NOT NULL,
                    change_count INTEGER NOT NULL CHECK(change_count > 0),
                    PRIMARY KEY(revision_id, dimension, section_x, section_y, section_z)
                );

                CREATE TABLE revision_changes(
                    revision_id TEXT NOT NULL REFERENCES revisions(revision_id) ON DELETE CASCADE,
                    dimension TEXT NOT NULL,
                    section_x INTEGER NOT NULL,
                    section_y INTEGER NOT NULL,
                    section_z INTEGER NOT NULL,
                    local_index INTEGER NOT NULL CHECK(local_index >= 0 AND local_index < 4096),
                    before_state_id INTEGER NOT NULL REFERENCES block_states(state_id),
                    after_state_id INTEGER NOT NULL REFERENCES block_states(state_id),
                    operation_id TEXT NOT NULL,
                    PRIMARY KEY(revision_id, dimension, section_x, section_y, section_z, local_index),
                    FOREIGN KEY(revision_id, dimension, section_x, section_y, section_z)
                        REFERENCES revision_sections(revision_id, dimension, section_x, section_y, section_z)
                        ON DELETE CASCADE
                );

                CREATE TABLE head_voxels(
                    dimension TEXT NOT NULL,
                    section_x INTEGER NOT NULL,
                    section_y INTEGER NOT NULL,
                    section_z INTEGER NOT NULL,
                    local_index INTEGER NOT NULL CHECK(local_index >= 0 AND local_index < 4096),
                    state_id INTEGER NOT NULL REFERENCES block_states(state_id),
                    PRIMARY KEY(dimension, section_x, section_y, section_z, local_index)
                );

                """;
            await schema.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var version = connection.CreateCommand())
        {
            version.Transaction = (SqliteTransaction)transaction;
            version.CommandText = "INSERT INTO schema_version(singleton, version) VALUES(1, $version);";
            version.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            await version.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var root = connection.CreateCommand())
        {
            root.Transaction = (SqliteTransaction)transaction;
            root.CommandText = """
                INSERT INTO revisions(revision_id, parent_revision_id, delta_hash, change_count)
                VALUES($revision_id, NULL, NULL, 0);
                """;
            root.Parameters.AddWithValue("$revision_id", initialRevision);
            await root.ExecuteNonQueryAsync(cancellationToken);
        }

        await SetRequiredMetaAsync(connection, "scene_format", SceneFormat, cancellationToken, (SqliteTransaction)transaction);
        await SetRequiredMetaAsync(connection, "current_revision", initialRevision, cancellationToken, (SqliteTransaction)transaction);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task VerifySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var version = connection.CreateCommand())
        {
            version.CommandText = "SELECT version FROM schema_version WHERE singleton = 1;";
            var value = await version.ExecuteScalarAsync(cancellationToken);
            if (value is null || Convert.ToInt32(value) != CurrentSchemaVersion)
            {
                throw new NotSupportedException($"不支持的 .zjjscene schema：{value ?? "missing"}。");
            }
        }

        var format = await ReadRequiredMetaAsync(connection, "scene_format", cancellationToken);
        if (!string.Equals(format, SceneFormat, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"不支持的场景格式：{format}。");
        }

        var currentRevision = await ReadRequiredMetaAsync(connection, "current_revision", cancellationToken);
        await ReadRevisionCoreAsync(connection, currentRevision, cancellationToken);
    }

    private static async Task<string> ReadRequiredMetaAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new SceneStoreCorruptionException($"场景缺少 meta.{key}。")
            : Convert.ToString(value)!;
    }

    private static async Task SetRequiredMetaAsync(
        SqliteConnection connection,
        string key,
        string value,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteControlStatementAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CheckpointAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken,
        bool requireComplete = false)
    {
        await using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken) && reader.GetInt32(0) != 0 && requireComplete)
        {
            throw new IOException("SQLite WAL checkpoint 因活动事务未能完成。");
        }
    }

    private static void DeleteTemporaryDatabaseSidecars(string temporaryPath)
    {
        foreach (var path in TemporaryDatabaseSidecars(temporaryPath))
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void DeleteTemporaryDatabaseFiles(string temporaryPath)
    {
        foreach (var path in new[] { temporaryPath }.Concat(TemporaryDatabaseSidecars(temporaryPath)))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Cleanup must not replace the initialization failure.
            }
        }
    }

    private static IEnumerable<string> TemporaryDatabaseSidecars(string temporaryPath)
    {
        yield return temporaryPath + "-wal";
        yield return temporaryPath + "-shm";
        yield return temporaryPath + "-journal";
    }

    private static string ValidatePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("场景路径不能为空。", nameof(filePath));
        return Path.GetFullPath(filePath);
    }

    private static void ValidateRevisionId(string revisionId)
    {
        if (string.IsNullOrWhiteSpace(revisionId)) throw new ArgumentException("Revision ID 不能为空。", nameof(revisionId));
    }

    private static void ValidateMetadataKey(string key, bool allowReserved)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Meta key 不能为空。", nameof(key));
        if (!allowReserved && ReservedMetaKeys.Contains(key)) throw new ArgumentException($"{key} 是保留的 Meta key。", nameof(key));
    }

    private sealed record CachedBlockState(long Id, string Name, string PropertiesJson);

    private sealed record PendingBlockState(BlockState State, string PropertiesJson);

    private sealed record StoredChange(SectionCoordinate Section, VoxelChange Change);

    private sealed record StoredSectionIntegrity(SectionCoordinate Section, string SectionHash, int ChangeCount);
}
