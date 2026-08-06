using System.Text.Json;
using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteWorldbookRepository : IWorldbookRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly SqliteDatabase _database;
    private readonly AppDataPaths _paths;

    public SqliteWorldbookRepository(
        SqliteDatabase database,
        AppDataPaths paths)
    {
        _database = database;
        _paths = paths;
    }

    public async Task<IReadOnlyList<Worldbook>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<Worldbook>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = WorldbookSelectSql + " ORDER BY w.name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadWorldbook(reader));
        }

        return result;
    }

    public async Task<Worldbook?> GetAsync(
        string worldbookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = WorldbookSelectSql + " WHERE w.id = $id;";
        command.Parameters.AddWithValue("$id", worldbookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadWorldbook(reader)
            : null;
    }

    public async Task<IReadOnlyList<Worldbook>> ListEnabledForCharacterAsync(
        string? characterId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<Worldbook>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT w.id, w.name, w.description, w.source_kind,
                   w.source_path, w.source_file_name, w.source_sha256,
                   w.raw_json, w.is_enabled, w.scan_depth, w.token_budget,
                   w.recursive_scanning, w.revision, w.created_at, w.updated_at,
                   0, 0
            FROM worldbooks AS w
            INNER JOIN (
                SELECT worldbook_id,
                       MAX(scope_kind) AS scope_kind,
                       MIN(sort_index) AS sort_index
                FROM worldbook_mounts
                WHERE is_enabled = 1
                  AND (
                        (scope_kind = $globalScope AND scope_id = '')
                        OR (
                            scope_kind = $characterScope
                            AND $characterId <> ''
                            AND scope_id = $characterId
                        )
                      )
                GROUP BY worldbook_id
            ) AS m
                ON m.worldbook_id = w.id
            WHERE w.is_enabled = 1
            ORDER BY m.scope_kind DESC, m.sort_index, w.name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$globalScope", (int)WorldbookScopeKind.Global);
        command.Parameters.AddWithValue("$characterScope", (int)WorldbookScopeKind.Character);
        command.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadWorldbook(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<WorldbookMount>> ListMountsAsync(
        string worldbookId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<WorldbookMount>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT worldbook_id, scope_kind, scope_id, sort_index,
                   is_enabled, mounted_revision
            FROM worldbook_mounts
            WHERE worldbook_id = $worldbookId
            ORDER BY scope_kind, sort_index, scope_id;
            """;
        command.Parameters.AddWithValue("$worldbookId", worldbookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorldbookMount
            {
                WorldbookId = reader.GetString(0),
                ScopeKind = (WorldbookScopeKind)reader.GetInt32(1),
                ScopeId = reader.GetString(2),
                SortIndex = reader.GetInt32(3),
                IsEnabled = reader.GetInt32(4) != 0,
                MountedRevision = reader.GetInt32(5)
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<WorldbookEntry>> ListEntriesAsync(
        string worldbookId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<WorldbookEntry>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT worldbook_id, entry_id, title, comment, content,
                   keys_json, secondary_keys_json, content_type, visibility,
                   semantic_enabled, enabled, constant, case_sensitive,
                   match_whole_words, selective_logic, insertion_order,
                   position, depth, provider_role, probability, use_probability,
                   inclusion_group, group_weight, exclude_recursion,
                   original_index, content_hash, extensions_json
            FROM worldbook_entries
            WHERE worldbook_id = $worldbookId
            ORDER BY original_index, insertion_order, entry_id;
            """;
        command.Parameters.AddWithValue("$worldbookId", worldbookId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public async Task UpdateEntryTitleAsync(
        string worldbookId,
        string entryId,
        string title,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldbookId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var updateEntry = connection.CreateCommand())
            {
                updateEntry.Transaction = (SqliteTransaction)transaction;
                updateEntry.CommandText = """
                    UPDATE worldbook_entries
                    SET title = $title
                    WHERE worldbook_id = $worldbookId
                      AND entry_id = $entryId;
                    """;
                updateEntry.Parameters.AddWithValue("$title", title.Trim());
                updateEntry.Parameters.AddWithValue("$worldbookId", worldbookId);
                updateEntry.Parameters.AddWithValue("$entryId", entryId);
                if (await updateEntry.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new KeyNotFoundException("世界书条目不存在，无法保存词条名。");
                }
            }

            await using (var updateBook = connection.CreateCommand())
            {
                updateBook.Transaction = (SqliteTransaction)transaction;
                updateBook.CommandText = """
                    UPDATE worldbooks
                    SET revision = revision + 1,
                        updated_at = $updatedAt
                    WHERE id = $worldbookId;
                    """;
                updateBook.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
                updateBook.Parameters.AddWithValue("$worldbookId", worldbookId);
                await updateBook.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpsertAsync(
        Worldbook worldbook,
        IReadOnlyList<WorldbookEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldbook.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(worldbook.Name);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var book = connection.CreateCommand())
            {
                book.Transaction = (SqliteTransaction)transaction;
                book.CommandText = """
                    INSERT INTO worldbooks(
                        id, name, description, source_kind, source_path,
                        source_file_name, source_sha256, raw_json, is_enabled,
                        scan_depth, token_budget, recursive_scanning, revision,
                        created_at, updated_at)
                    VALUES(
                        $id, $name, $description, $sourceKind, $sourcePath,
                        $sourceFileName, $sourceSha256, $rawJson, $isEnabled,
                        $scanDepth, $tokenBudget, $recursiveScanning, $revision,
                        $createdAt, $updatedAt)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name,
                        description = excluded.description,
                        source_kind = excluded.source_kind,
                        source_path = excluded.source_path,
                        source_file_name = excluded.source_file_name,
                        source_sha256 = excluded.source_sha256,
                        raw_json = excluded.raw_json,
                        is_enabled = excluded.is_enabled,
                        scan_depth = excluded.scan_depth,
                        token_budget = excluded.token_budget,
                        recursive_scanning = excluded.recursive_scanning,
                        revision = excluded.revision,
                        updated_at = excluded.updated_at;
                    """;
                book.Parameters.AddWithValue("$id", worldbook.Id);
                book.Parameters.AddWithValue("$name", worldbook.Name);
                book.Parameters.AddWithValue("$description", worldbook.Description);
                book.Parameters.AddWithValue("$sourceKind", (int)worldbook.SourceKind);
                book.Parameters.AddWithValue(
                    "$sourcePath",
                    _paths.ToStoredPath(worldbook.SourcePath));
                book.Parameters.AddWithValue("$sourceFileName", worldbook.SourceFileName);
                book.Parameters.AddWithValue("$sourceSha256", worldbook.SourceSha256);
                book.Parameters.AddWithValue("$rawJson", worldbook.RawJson);
                book.Parameters.AddWithValue("$isEnabled", worldbook.IsEnabled ? 1 : 0);
                book.Parameters.AddWithValue("$scanDepth", worldbook.ScanDepth);
                book.Parameters.AddWithValue("$tokenBudget", worldbook.TokenBudget);
                book.Parameters.AddWithValue(
                    "$recursiveScanning",
                    worldbook.RecursiveScanning ? 1 : 0);
                book.Parameters.AddWithValue("$revision", worldbook.Revision);
                book.Parameters.AddWithValue("$createdAt", worldbook.CreatedAt.ToString("O"));
                book.Parameters.AddWithValue("$updatedAt", worldbook.UpdatedAt.ToString("O"));
                await book.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var source = connection.CreateCommand())
            {
                source.Transaction = (SqliteTransaction)transaction;
                source.CommandText = """
                    DELETE FROM worldbook_sources
                    WHERE worldbook_id = $worldbookId;

                    INSERT INTO worldbook_sources(
                        id, worldbook_id, file_name, source_format,
                        source_sha256, raw_json, parser_version, imported_at)
                    VALUES(
                        $sourceId, $worldbookId, $fileName, $sourceFormat,
                        $sourceSha256, $rawJson, $parserVersion, $importedAt);
                    """;
                source.Parameters.AddWithValue("$sourceId", Guid.NewGuid().ToString("N"));
                source.Parameters.AddWithValue("$worldbookId", worldbook.Id);
                source.Parameters.AddWithValue("$fileName", worldbook.SourceFileName);
                source.Parameters.AddWithValue(
                    "$sourceFormat",
                    worldbook.SourceKind == WorldbookSourceKind.CharacterCardEmbedded
                        ? "character-card"
                        : "world-info-json");
                source.Parameters.AddWithValue("$sourceSha256", worldbook.SourceSha256);
                source.Parameters.AddWithValue("$rawJson", worldbook.RawJson);
                source.Parameters.AddWithValue("$parserVersion", "worldbook-v1");
                source.Parameters.AddWithValue("$importedAt", DateTimeOffset.Now.ToString("O"));
                await source.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteFts = connection.CreateCommand())
            {
                deleteFts.Transaction = (SqliteTransaction)transaction;
                deleteFts.CommandText =
                    "DELETE FROM worldbook_chunks_fts WHERE worldbook_id = $id;";
                deleteFts.Parameters.AddWithValue("$id", worldbook.Id);
                await deleteFts.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteEntries = connection.CreateCommand())
            {
                deleteEntries.Transaction = (SqliteTransaction)transaction;
                deleteEntries.CommandText = "DELETE FROM worldbook_entries WHERE worldbook_id = $id;";
                deleteEntries.Parameters.AddWithValue("$id", worldbook.Id);
                await deleteEntries.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var entry in entries)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO worldbook_entries(
                        worldbook_id, entry_id, title, comment, content,
                        keys_json, secondary_keys_json, content_type, visibility,
                        semantic_enabled, enabled, constant, case_sensitive,
                        match_whole_words, selective_logic, insertion_order,
                        position, depth, provider_role, probability, use_probability,
                        inclusion_group, group_weight, exclude_recursion,
                        original_index, content_hash, extensions_json)
                    VALUES(
                        $worldbookId, $entryId, $title, $comment, $content,
                        $keys, $secondaryKeys, $contentType, $visibility,
                        $semanticEnabled, $enabled, $constant, $caseSensitive,
                        $wholeWords, $logic, $order, $position, $depth, $role,
                        $probability, $useProbability, $group, $groupWeight,
                        $excludeRecursion, $originalIndex, $contentHash, $extensions);
                    """;
                command.Parameters.AddWithValue("$worldbookId", worldbook.Id);
                command.Parameters.AddWithValue("$entryId", entry.Id);
                command.Parameters.AddWithValue("$title", entry.Title);
                command.Parameters.AddWithValue("$comment", entry.Comment);
                command.Parameters.AddWithValue("$content", entry.Content);
                command.Parameters.AddWithValue("$keys", Serialize(entry.Keys));
                command.Parameters.AddWithValue("$secondaryKeys", Serialize(entry.SecondaryKeys));
                command.Parameters.AddWithValue("$contentType", (int)entry.ContentType);
                command.Parameters.AddWithValue("$visibility", (int)entry.Visibility);
                command.Parameters.AddWithValue("$semanticEnabled", entry.SemanticEnabled ? 1 : 0);
                command.Parameters.AddWithValue("$enabled", entry.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$constant", entry.Constant ? 1 : 0);
                command.Parameters.AddWithValue("$caseSensitive", entry.CaseSensitive ? 1 : 0);
                command.Parameters.AddWithValue("$wholeWords", entry.MatchWholeWords ? 1 : 0);
                command.Parameters.AddWithValue("$logic", (int)entry.SelectiveLogic);
                command.Parameters.AddWithValue("$order", entry.InsertionOrder);
                command.Parameters.AddWithValue("$position", (int)entry.Position);
                command.Parameters.AddWithValue("$depth", entry.Depth);
                command.Parameters.AddWithValue("$role", entry.ProviderRole);
                command.Parameters.AddWithValue("$probability", entry.Probability);
                command.Parameters.AddWithValue("$useProbability", entry.UseProbability ? 1 : 0);
                command.Parameters.AddWithValue("$group", entry.InclusionGroup);
                command.Parameters.AddWithValue("$groupWeight", entry.GroupWeight);
                command.Parameters.AddWithValue("$excludeRecursion", entry.ExcludeRecursion ? 1 : 0);
                command.Parameters.AddWithValue("$originalIndex", entry.OriginalIndex);
                command.Parameters.AddWithValue("$contentHash", entry.ContentHash);
                command.Parameters.AddWithValue("$extensions", entry.ExtensionsJson);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpsertMountAsync(
        WorldbookMount mount,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO worldbook_mounts(
                worldbook_id, scope_kind, scope_id, sort_index,
                is_enabled, mounted_revision)
            VALUES(
                $worldbookId, $scopeKind, $scopeId, $sortIndex,
                $isEnabled, $revision)
            ON CONFLICT(worldbook_id, scope_kind, scope_id) DO UPDATE SET
                sort_index = excluded.sort_index,
                is_enabled = excluded.is_enabled,
                mounted_revision = excluded.mounted_revision;
            """;
        command.Parameters.AddWithValue("$worldbookId", mount.WorldbookId);
        command.Parameters.AddWithValue("$scopeKind", (int)mount.ScopeKind);
        command.Parameters.AddWithValue("$scopeId", mount.ScopeId);
        command.Parameters.AddWithValue("$sortIndex", mount.SortIndex);
        command.Parameters.AddWithValue("$isEnabled", mount.IsEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$revision", mount.MountedRevision);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveMountAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM worldbook_mounts
            WHERE worldbook_id = $worldbookId
              AND scope_kind = $scopeKind
              AND scope_id = $scopeId;
            """;
        command.Parameters.AddWithValue("$worldbookId", worldbookId);
        command.Parameters.AddWithValue("$scopeKind", (int)scopeKind);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task ReplaceCharacterMountsAsync(
        string worldbookId,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default)
        => ReplaceScopeMountsAsync(
            worldbookId,
            WorldbookScopeKind.Character,
            mounts,
            cancellationToken);

    public async Task ReplaceScopeMountsAsync(
        string worldbookId,
        WorldbookScopeKind scopeKind,
        IReadOnlyList<WorldbookMount> mounts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worldbookId);
        var scopeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mount in mounts)
        {
            if (!string.Equals(mount.WorldbookId, worldbookId, StringComparison.Ordinal)
                || mount.ScopeKind != scopeKind
                || string.IsNullOrWhiteSpace(mount.ScopeId))
            {
                throw new ArgumentException(
                    "挂载集合只能包含同一本世界书的有效范围挂载。",
                    nameof(mounts));
            }

            if (!scopeIds.Add(mount.ScopeId))
            {
                throw new ArgumentException(
                    "挂载集合不能包含重复范围。",
                    nameof(mounts));
            }
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = """
                    DELETE FROM worldbook_mounts
                    WHERE worldbook_id = $worldbookId
                      AND scope_kind = $scopeKind;
                    """;
                delete.Parameters.AddWithValue("$worldbookId", worldbookId);
                delete.Parameters.AddWithValue("$scopeKind", (int)scopeKind);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var mount in mounts)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO worldbook_mounts(
                        worldbook_id, scope_kind, scope_id, sort_index,
                        is_enabled, mounted_revision)
                    VALUES(
                        $worldbookId, $scopeKind, $scopeId, $sortIndex,
                        $isEnabled, $revision);
                    """;
                insert.Parameters.AddWithValue("$worldbookId", worldbookId);
                insert.Parameters.AddWithValue("$scopeKind", (int)scopeKind);
                insert.Parameters.AddWithValue("$scopeId", mount.ScopeId);
                insert.Parameters.AddWithValue("$sortIndex", mount.SortIndex);
                insert.Parameters.AddWithValue("$isEnabled", mount.IsEnabled ? 1 : 0);
                insert.Parameters.AddWithValue("$revision", mount.MountedRevision);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task DeleteAsync(
        string worldbookId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteFts = connection.CreateCommand())
            {
                deleteFts.Transaction = (SqliteTransaction)transaction;
                deleteFts.CommandText =
                    "DELETE FROM worldbook_chunks_fts WHERE worldbook_id = $id;";
                deleteFts.Parameters.AddWithValue("$id", worldbookId);
                await deleteFts.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM worldbooks WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", worldbookId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<WorldbookChunk>> ListChunksAsync(
        IReadOnlySet<string> worldbookIds,
        CancellationToken cancellationToken = default)
    {
        if (worldbookIds.Count == 0)
        {
            return [];
        }

        var result = new List<WorldbookChunk>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = worldbookIds.Select((id, index) =>
        {
            var name = "$book" + index;
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT id, worldbook_id, entry_id, chunk_index, content,
                   normalized_content, token_count, source_locator,
                   content_hash, updated_at
            FROM worldbook_chunks
            WHERE worldbook_id IN ({string.Join(",", parameters)})
            ORDER BY worldbook_id, entry_id, chunk_index;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new WorldbookChunk
            {
                Id = reader.GetString(0),
                WorldbookId = reader.GetString(1),
                EntryId = reader.GetString(2),
                ChunkIndex = reader.GetInt32(3),
                Content = reader.GetString(4),
                NormalizedContent = reader.GetString(5),
                TokenCount = reader.GetInt32(6),
                SourceLocator = reader.GetString(7),
                ContentHash = reader.GetString(8),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(9))
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<WorldbookTextHit>> SearchTextAsync(
        IReadOnlySet<string> worldbookIds,
        string queryText,
        int maximumResults,
        CancellationToken cancellationToken = default)
    {
        if (worldbookIds.Count == 0 || string.IsNullOrWhiteSpace(queryText))
        {
            return [];
        }

        var rawTerms = queryText
            .Split(
                [' ', '\t', '\r', '\n', ',', '，', '。', '！', '？', '、',
                 '：', ':', '；', ';', '（', '）', '(', ')', '[', ']',
                 '{', '}', '"', '\'', '“', '”'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var terms = rawTerms
            .SelectMany(ExpandSearchTerm)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        var ftsTerms = terms
            .Select(term => '"' + term.Replace("\"", "\"\"") + '"')
            .ToArray();

        var result = new List<WorldbookTextHit>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var parameters = worldbookIds.Select((id, index) =>
        {
            var name = "$book" + index;
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        command.CommandText = $"""
            SELECT worldbook_chunks_fts.chunk_id,
                   worldbook_chunks_fts.worldbook_id,
                   worldbook_chunks_fts.entry_id,
                   bm25(worldbook_chunks_fts)
            FROM worldbook_chunks_fts
            INNER JOIN worldbook_chunks AS c
                ON c.id = worldbook_chunks_fts.chunk_id
            INNER JOIN worldbook_entries AS e
                ON e.worldbook_id = c.worldbook_id
               AND e.entry_id = c.entry_id
            WHERE worldbook_chunks_fts MATCH $query
              AND worldbook_chunks_fts.worldbook_id IN ({string.Join(",", parameters)})
              AND e.enabled = 1
              AND e.semantic_enabled = 1
              AND e.visibility = $publicVisibility
            ORDER BY bm25(worldbook_chunks_fts)
            LIMIT $maximumResults;
            """;
        command.Parameters.AddWithValue("$query", string.Join(" OR ", ftsTerms));
        command.Parameters.AddWithValue("$maximumResults", Math.Clamp(maximumResults, 1, 100));
        command.Parameters.AddWithValue(
            "$publicVisibility",
            (int)WorldbookVisibility.Public);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var bm25 = reader.IsDBNull(3) ? 0d : reader.GetDouble(3);
                result.Add(new WorldbookTextHit(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    1d / (1d + Math.Abs(bm25))));
            }
        }

        var fallbackLimit = Math.Clamp(
            maximumResults - result.Count,
            0,
            100);
        if (fallbackLimit == 0)
        {
            return result.Take(Math.Clamp(maximumResults, 1, 100)).ToArray();
        }

        // unicode61 does not split CJK text consistently on every SQLite build.
        // Keep FTS5 as the primary path, then use an exact local substring
        // fallback so Chinese world_info files remain searchable offline.
        command.Parameters.Clear();
        var fallbackBookParameters = worldbookIds.Select((id, index) =>
        {
            var name = "$book" + index;
            command.Parameters.AddWithValue(name, id);
            return name;
        }).ToArray();
        var fallbackTermParameters = terms.Take(24).Select((term, index) =>
        {
            var name = "$term" + index;
            command.Parameters.AddWithValue(name, term);
            return name;
        }).ToArray();
        command.Parameters.AddWithValue("$fallbackLimit", fallbackLimit);
        command.Parameters.AddWithValue(
            "$publicVisibility",
            (int)WorldbookVisibility.Public);
        command.CommandText = $"""
            SELECT worldbook_chunks.id,
                   worldbook_chunks.worldbook_id,
                   worldbook_chunks.entry_id,
                   0.0
            FROM worldbook_chunks
            INNER JOIN worldbook_entries AS e
                ON e.worldbook_id = worldbook_chunks.worldbook_id
               AND e.entry_id = worldbook_chunks.entry_id
            WHERE worldbook_chunks.worldbook_id IN ({string.Join(",", fallbackBookParameters)})
              AND e.enabled = 1
              AND e.semantic_enabled = 1
              AND e.visibility = $publicVisibility
              AND ({string.Join(
                  " OR ",
                  fallbackTermParameters.Select(item =>
                      $"worldbook_chunks.content LIKE '%' || {item} || '%'"))})
            ORDER BY worldbook_chunks.chunk_index
            LIMIT $fallbackLimit;
            """;
        await using var fallbackReader = await command.ExecuteReaderAsync(cancellationToken);
        var existingChunkIds = result
            .Select(hit => hit.ChunkId)
            .ToHashSet(StringComparer.Ordinal);
        while (await fallbackReader.ReadAsync(cancellationToken))
        {
            var chunkId = fallbackReader.GetString(0);
            if (existingChunkIds.Add(chunkId))
            {
                result.Add(new WorldbookTextHit(
                    chunkId,
                    fallbackReader.GetString(1),
                    fallbackReader.GetString(2),
                    1d));
            }
        }

        return result.Take(Math.Clamp(maximumResults, 1, 100)).ToArray();
    }

    private static IEnumerable<string> ExpandSearchTerm(string term)
    {
        var cjk = new string(term
            .Where(character => character is >= '\u4E00' and <= '\u9FFF')
            .ToArray());
        if (cjk.Length > 0)
        {
            var terms = new List<string> { cjk };
            for (var index = 0; index + 1 < cjk.Length; index++)
            {
                terms.Add(cjk.Substring(index, 2));
            }

            return terms;
        }

        return [term];
    }

    public async Task ReplaceChunksAsync(
        string worldbookId,
        IReadOnlyList<WorldbookChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteFts = connection.CreateCommand())
            {
                deleteFts.Transaction = (SqliteTransaction)transaction;
                deleteFts.CommandText = """
                    DELETE FROM worldbook_chunks_fts
                    WHERE chunk_id IN (
                        SELECT id FROM worldbook_chunks WHERE worldbook_id = $worldbookId
                    );
                    """;
                deleteFts.Parameters.AddWithValue("$worldbookId", worldbookId);
                await deleteFts.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM worldbook_chunks WHERE worldbook_id = $worldbookId;";
                delete.Parameters.AddWithValue("$worldbookId", worldbookId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var chunk in chunks)
            {
                await using var insert = connection.CreateCommand();
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO worldbook_chunks(
                        id, worldbook_id, entry_id, chunk_index, content,
                        normalized_content, token_count, source_locator,
                        content_hash, updated_at)
                    VALUES(
                        $id, $worldbookId, $entryId, $chunkIndex, $content,
                        $normalizedContent, $tokenCount, $sourceLocator,
                        $contentHash, $updatedAt);
                    """;
                insert.Parameters.AddWithValue("$id", chunk.Id);
                insert.Parameters.AddWithValue("$worldbookId", worldbookId);
                insert.Parameters.AddWithValue("$entryId", chunk.EntryId);
                insert.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
                insert.Parameters.AddWithValue("$content", chunk.Content);
                insert.Parameters.AddWithValue("$normalizedContent", chunk.NormalizedContent);
                insert.Parameters.AddWithValue("$tokenCount", chunk.TokenCount);
                insert.Parameters.AddWithValue("$sourceLocator", chunk.SourceLocator);
                insert.Parameters.AddWithValue("$contentHash", chunk.ContentHash);
                insert.Parameters.AddWithValue("$updatedAt", chunk.UpdatedAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);

                await using var fts = connection.CreateCommand();
                fts.Transaction = (SqliteTransaction)transaction;
                fts.CommandText = """
                    INSERT INTO worldbook_chunks_fts(
                        chunk_id, worldbook_id, entry_id, content, normalized_content)
                    VALUES($id, $worldbookId, $entryId, $content, $normalizedContent);
                    """;
                fts.Parameters.AddWithValue("$id", chunk.Id);
                fts.Parameters.AddWithValue("$worldbookId", worldbookId);
                fts.Parameters.AddWithValue("$entryId", chunk.EntryId);
                fts.Parameters.AddWithValue("$content", chunk.Content);
                fts.Parameters.AddWithValue("$normalizedContent", chunk.NormalizedContent);
                await fts.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpsertEmbeddingProfileAsync(
        EmbeddingProfile profile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO embedding_profiles(
                id, provider_id, model_id, dimension, normalize, batch_size, updated_at)
            VALUES($id, $providerId, $modelId, $dimension, $normalize, $batchSize, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                provider_id = excluded.provider_id,
                model_id = excluded.model_id,
                dimension = excluded.dimension,
                normalize = excluded.normalize,
                batch_size = excluded.batch_size,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$providerId", profile.ProviderId);
        command.Parameters.AddWithValue("$modelId", profile.ModelId);
        command.Parameters.AddWithValue("$dimension", profile.Dimension is null
            ? DBNull.Value
            : profile.Dimension.Value);
        command.Parameters.AddWithValue("$normalize", profile.Normalize ? 1 : 0);
        command.Parameters.AddWithValue("$batchSize", profile.BatchSize);
        command.Parameters.AddWithValue("$updatedAt", profile.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceIndexedChunksAsync(
        string worldbookId,
        IReadOnlyList<WorldbookChunk> chunks,
        EmbeddingProfile profile,
        IReadOnlyList<WorldbookEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        var chunkIds = chunks
            .Select(chunk => chunk.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (embeddings.Count != chunks.Count
            || embeddings.Any(embedding => !chunkIds.Contains(embedding.ChunkId)))
        {
            throw new InvalidDataException(
                "世界书块与 Embedding 数量或归属不一致，拒绝写入索引。" );
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteFts = connection.CreateCommand())
            {
                deleteFts.Transaction = (SqliteTransaction)transaction;
                deleteFts.CommandText =
                    "DELETE FROM worldbook_chunks_fts WHERE worldbook_id = $worldbookId;";
                deleteFts.Parameters.AddWithValue("$worldbookId", worldbookId);
                await deleteFts.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteChunks = connection.CreateCommand())
            {
                deleteChunks.Transaction = (SqliteTransaction)transaction;
                deleteChunks.CommandText =
                    "DELETE FROM worldbook_chunks WHERE worldbook_id = $worldbookId;";
                deleteChunks.Parameters.AddWithValue("$worldbookId", worldbookId);
                await deleteChunks.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var chunk in chunks)
            {
                await InsertChunkAndFtsAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    chunk,
                    cancellationToken);
            }

            await using (var upsertProfile = connection.CreateCommand())
            {
                upsertProfile.Transaction = (SqliteTransaction)transaction;
                upsertProfile.CommandText = """
                    INSERT INTO embedding_profiles(
                        id, provider_id, model_id, dimension, normalize, batch_size, updated_at)
                    VALUES($id, $providerId, $modelId, $dimension, $normalize, $batchSize, $updatedAt)
                    ON CONFLICT(id) DO UPDATE SET
                        provider_id = excluded.provider_id,
                        model_id = excluded.model_id,
                        dimension = excluded.dimension,
                        normalize = excluded.normalize,
                        batch_size = excluded.batch_size,
                        updated_at = excluded.updated_at;
                    """;
                upsertProfile.Parameters.AddWithValue("$id", profile.Id);
                upsertProfile.Parameters.AddWithValue("$providerId", profile.ProviderId);
                upsertProfile.Parameters.AddWithValue("$modelId", profile.ModelId);
                upsertProfile.Parameters.AddWithValue("$dimension", profile.Dimension is null
                    ? DBNull.Value
                    : profile.Dimension.Value);
                upsertProfile.Parameters.AddWithValue("$normalize", profile.Normalize ? 1 : 0);
                upsertProfile.Parameters.AddWithValue("$batchSize", profile.BatchSize);
                upsertProfile.Parameters.AddWithValue("$updatedAt", profile.UpdatedAt.ToString("O"));
                await upsertProfile.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var embedding in embeddings)
            {
                await using var insertEmbedding = connection.CreateCommand();
                insertEmbedding.Transaction = (SqliteTransaction)transaction;
                insertEmbedding.CommandText = """
                    INSERT INTO worldbook_embeddings(
                        chunk_id, profile_id, model_id, dimension,
                        vector_blob, content_hash, updated_at)
                    VALUES(
                        $chunkId, $profileId, $modelId, $dimension,
                        $vector, $contentHash, $updatedAt);
                    """;
                insertEmbedding.Parameters.AddWithValue("$chunkId", embedding.ChunkId);
                insertEmbedding.Parameters.AddWithValue("$profileId", embedding.ProfileId);
                insertEmbedding.Parameters.AddWithValue("$modelId", embedding.ModelId);
                insertEmbedding.Parameters.AddWithValue("$dimension", embedding.Dimension);
                insertEmbedding.Parameters.AddWithValue("$vector", embedding.VectorBlob);
                insertEmbedding.Parameters.AddWithValue("$contentHash", embedding.ContentHash);
                insertEmbedding.Parameters.AddWithValue("$updatedAt", embedding.UpdatedAt.ToString("O"));
                await insertEmbedding.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task InsertChunkAndFtsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        WorldbookChunk chunk,
        CancellationToken cancellationToken)
    {
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO worldbook_chunks(
                    id, worldbook_id, entry_id, chunk_index, content,
                    normalized_content, token_count, source_locator,
                    content_hash, updated_at)
                VALUES(
                    $id, $worldbookId, $entryId, $chunkIndex, $content,
                    $normalizedContent, $tokenCount, $sourceLocator,
                    $contentHash, $updatedAt);
                """;
            insert.Parameters.AddWithValue("$id", chunk.Id);
            insert.Parameters.AddWithValue("$worldbookId", chunk.WorldbookId);
            insert.Parameters.AddWithValue("$entryId", chunk.EntryId);
            insert.Parameters.AddWithValue("$chunkIndex", chunk.ChunkIndex);
            insert.Parameters.AddWithValue("$content", chunk.Content);
            insert.Parameters.AddWithValue("$normalizedContent", chunk.NormalizedContent);
            insert.Parameters.AddWithValue("$tokenCount", chunk.TokenCount);
            insert.Parameters.AddWithValue("$sourceLocator", chunk.SourceLocator);
            insert.Parameters.AddWithValue("$contentHash", chunk.ContentHash);
            insert.Parameters.AddWithValue("$updatedAt", chunk.UpdatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var fts = connection.CreateCommand())
        {
            fts.Transaction = transaction;
            fts.CommandText = """
                INSERT INTO worldbook_chunks_fts(
                    chunk_id, worldbook_id, entry_id, content, normalized_content)
                VALUES($id, $worldbookId, $entryId, $content, $normalizedContent);
                """;
            fts.Parameters.AddWithValue("$id", chunk.Id);
            fts.Parameters.AddWithValue("$worldbookId", chunk.WorldbookId);
            fts.Parameters.AddWithValue("$entryId", chunk.EntryId);
            fts.Parameters.AddWithValue("$content", chunk.Content);
            fts.Parameters.AddWithValue("$normalizedContent", chunk.NormalizedContent);
            await fts.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<EmbeddingProfile?> GetEmbeddingProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, provider_id, model_id, dimension, normalize,
                   batch_size, updated_at
            FROM embedding_profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", profileId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new EmbeddingProfile
        {
            Id = reader.GetString(0),
            ProviderId = reader.GetString(1),
            ModelId = reader.GetString(2),
            Dimension = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            Normalize = reader.GetInt32(4) != 0,
            BatchSize = reader.GetInt32(5),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async Task<IReadOnlyList<WorldbookEmbedding>> ListEmbeddingsAsync(
        IReadOnlySet<string> chunkIds,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (chunkIds.Count == 0)
        {
            return [];
        }

        var result = new List<WorldbookEmbedding>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        var ids = chunkIds.ToArray();
        const int batchSize = 400;
        for (var offset = 0; offset < ids.Length; offset += batchSize)
        {
            await using var command = connection.CreateCommand();
            var batch = ids.Skip(offset).Take(batchSize).ToArray();
            var parameters = batch.Select((id, index) =>
            {
                var name = "$chunk" + index;
                command.Parameters.AddWithValue(name, id);
                return name;
            }).ToArray();
            command.Parameters.AddWithValue("$profileId", profileId);
            command.CommandText = $"""
                SELECT chunk_id, profile_id, model_id, dimension,
                       vector_blob, content_hash, updated_at
                FROM worldbook_embeddings
                WHERE profile_id = $profileId
                  AND chunk_id IN ({string.Join(",", parameters)});
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new WorldbookEmbedding
                {
                    ChunkId = reader.GetString(0),
                    ProfileId = reader.GetString(1),
                    ModelId = reader.GetString(2),
                    Dimension = reader.GetInt32(3),
                    VectorBlob = reader.GetFieldValue<byte[]>(4),
                    ContentHash = reader.GetString(5),
                    UpdatedAt = DateTimeOffset.Parse(reader.GetString(6))
                });
            }
        }

        return result;
    }

    public async Task ReplaceEmbeddingsAsync(
        string profileId,
        IReadOnlyList<WorldbookEmbedding> embeddings,
        CancellationToken cancellationToken = default)
    {
        if (embeddings.Count == 0)
        {
            return;
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var embedding in embeddings)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO worldbook_embeddings(
                        chunk_id, profile_id, model_id, dimension,
                        vector_blob, content_hash, updated_at)
                    VALUES(
                        $chunkId, $profileId, $modelId, $dimension,
                        $vector, $contentHash, $updatedAt)
                    ON CONFLICT(chunk_id, profile_id) DO UPDATE SET
                        model_id = excluded.model_id,
                        dimension = excluded.dimension,
                        vector_blob = excluded.vector_blob,
                        content_hash = excluded.content_hash,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$chunkId", embedding.ChunkId);
                command.Parameters.AddWithValue("$profileId", profileId);
                command.Parameters.AddWithValue("$modelId", embedding.ModelId);
                command.Parameters.AddWithValue("$dimension", embedding.Dimension);
                command.Parameters.AddWithValue("$vector", embedding.VectorBlob);
                command.Parameters.AddWithValue("$contentHash", embedding.ContentHash);
                command.Parameters.AddWithValue("$updatedAt", embedding.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private const string WorldbookSelectSql = """
        SELECT w.id, w.name, w.description, w.source_kind,
               w.source_path, w.source_file_name, w.source_sha256,
               w.raw_json, w.is_enabled, w.scan_depth, w.token_budget,
               w.recursive_scanning, w.revision, w.created_at, w.updated_at,
               (SELECT COUNT(*) FROM worldbook_entries e WHERE e.worldbook_id = w.id),
               (SELECT COUNT(*)
                FROM worldbook_embeddings em
                INNER JOIN worldbook_chunks c ON c.id = em.chunk_id
                WHERE c.worldbook_id = w.id)
        FROM worldbooks w
        """;

    private Worldbook ReadWorldbook(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            SourceKind = (WorldbookSourceKind)reader.GetInt32(3),
            SourcePath = _paths.ResolveStoredPath(reader.GetString(4)),
            SourceFileName = reader.GetString(5),
            SourceSha256 = reader.GetString(6),
            RawJson = reader.GetString(7),
            IsEnabled = reader.GetInt32(8) != 0,
            ScanDepth = reader.GetInt32(9),
            TokenBudget = reader.GetInt32(10),
            RecursiveScanning = reader.GetInt32(11) != 0,
            Revision = reader.GetInt32(12),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(14)),
            EntryCount = reader.GetInt32(15),
            IndexedChunkCount = reader.GetInt32(16)
        };

    private static WorldbookEntry ReadEntry(SqliteDataReader reader) =>
        new()
        {
            WorldbookId = reader.GetString(0),
            Id = reader.GetString(1),
            Title = reader.GetString(2),
            Comment = reader.GetString(3),
            Content = reader.GetString(4),
            Keys = Deserialize(reader.GetString(5)),
            SecondaryKeys = Deserialize(reader.GetString(6)),
            ContentType = (WorldbookContentType)reader.GetInt32(7),
            Visibility = (WorldbookVisibility)reader.GetInt32(8),
            SemanticEnabled = reader.GetInt32(9) != 0,
            Enabled = reader.GetInt32(10) != 0,
            Constant = reader.GetInt32(11) != 0,
            CaseSensitive = reader.GetInt32(12) != 0,
            MatchWholeWords = reader.GetInt32(13) != 0,
            SelectiveLogic = (WorldbookSelectiveLogic)reader.GetInt32(14),
            InsertionOrder = reader.GetInt32(15),
            Position = (WorldbookInsertionPosition)reader.GetInt32(16),
            Depth = reader.GetInt32(17),
            ProviderRole = reader.GetString(18),
            Probability = reader.GetInt32(19),
            UseProbability = reader.GetInt32(20) != 0,
            InclusionGroup = reader.GetString(21),
            GroupWeight = reader.GetInt32(22),
            ExcludeRecursion = reader.GetInt32(23) != 0,
            OriginalIndex = reader.GetInt32(24),
            ContentHash = reader.GetString(25),
            ExtensionsJson = reader.GetString(26)
        };

    private static string Serialize(IReadOnlyList<string> values) =>
        JsonSerializer.Serialize(values, JsonOptions);

    private static IReadOnlyList<string> Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
