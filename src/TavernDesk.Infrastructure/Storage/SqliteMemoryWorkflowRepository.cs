using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Group;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteMemoryWorkflowRepository : IMemoryWorkflowRepository
{
    private readonly SqliteDatabase _database;

    public SqliteMemoryWorkflowRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<MemoryWorkflowSettings> GetSettingsAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_id, auto_generate_enabled, update_interval_turns,
                   maximum_source_user_turns, send_only_new_messages,
                   update_system_prompt, update_user_template,
                   compression_system_prompt, compression_user_template, updated_at
            FROM memory_workflow_settings
            WHERE owner_id = $ownerId;
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadSettings(reader)
            : new MemoryWorkflowSettings { OwnerId = ownerId };
    }

    public async Task SaveSettingsAsync(
        MemoryWorkflowSettings settings,
        CancellationToken cancellationToken = default)
    {
        ValidateSettings(settings);
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_workflow_settings(
                owner_id, auto_generate_enabled, update_interval_turns,
                maximum_source_user_turns, send_only_new_messages,
                update_system_prompt, update_user_template,
                compression_system_prompt, compression_user_template, updated_at)
            VALUES(
                $ownerId, $autoGenerateEnabled, $updateIntervalTurns,
                $maximumSourceUserTurns, $sendOnlyNewMessages,
                $updateSystemPrompt, $updateUserTemplate,
                $compressionSystemPrompt, $compressionUserTemplate, $updatedAt)
            ON CONFLICT(owner_id) DO UPDATE SET
                auto_generate_enabled = excluded.auto_generate_enabled,
                update_interval_turns = excluded.update_interval_turns,
                maximum_source_user_turns = excluded.maximum_source_user_turns,
                send_only_new_messages = excluded.send_only_new_messages,
                update_system_prompt = excluded.update_system_prompt,
                update_user_template = excluded.update_user_template,
                compression_system_prompt = excluded.compression_system_prompt,
                compression_user_template = excluded.compression_user_template,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$ownerId", settings.OwnerId);
        command.Parameters.AddWithValue("$autoGenerateEnabled", settings.AutoGenerateEnabled);
        command.Parameters.AddWithValue("$updateIntervalTurns", settings.UpdateIntervalTurns);
        command.Parameters.AddWithValue(
            "$maximumSourceUserTurns",
            settings.MaximumSourceUserTurns);
        command.Parameters.AddWithValue(
            "$sendOnlyNewMessages",
            settings.SendOnlyNewMessages);
        command.Parameters.AddWithValue("$updateSystemPrompt", settings.UpdateSystemPrompt);
        command.Parameters.AddWithValue("$updateUserTemplate", settings.UpdateUserTemplate);
        command.Parameters.AddWithValue(
            "$compressionSystemPrompt",
            settings.CompressionSystemPrompt);
        command.Parameters.AddWithValue(
            "$compressionUserTemplate",
            settings.CompressionUserTemplate);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MemoryCheckpoint?> GetCheckpointAsync(
        string ownerId,
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        if (MemoryOwnerIds.TryParseGroup(
                ownerId,
                out var groupConversationId,
                out var characterId))
        {
            if (!string.Equals(
                    groupConversationId,
                    conversationId,
                    StringComparison.Ordinal))
            {
                return null;
            }

            return await GetGroupCheckpointAsync(
                ownerId,
                conversationId,
                characterId,
                cancellationToken);
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT owner_id, conversation_id, last_sequence_no,
                   processed_user_turns, updated_at
            FROM memory_checkpoints
            WHERE owner_id = $ownerId AND conversation_id = $conversationId;
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCheckpoint(reader)
            : null;
    }

    public async Task<MemoryUpdateDraft?> GetDraftAsync(
        string targetOwnerId,
        string sourceConversationId,
        MemoryDraftKind kind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_owner_id, source_conversation_id, draft_kind,
                   body, request_preview, target_tokens, source_through_sequence_no,
                   source_user_turns, source_message_count, source_digest,
                   target_bank_revision, source_bank_revision, created_at, updated_at
            FROM memory_update_drafts
            WHERE target_owner_id = $targetOwnerId
              AND source_conversation_id = $sourceConversationId
              AND draft_kind = $kind;
            """;
        command.Parameters.AddWithValue("$targetOwnerId", targetOwnerId);
        command.Parameters.AddWithValue("$sourceConversationId", sourceConversationId);
        command.Parameters.AddWithValue("$kind", (int)kind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadDraft(reader)
            : null;
    }

    public async Task<IReadOnlyList<MemoryUpdateDraft>> ListDraftsAsync(
        string sourceConversationId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MemoryUpdateDraft>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, target_owner_id, source_conversation_id, draft_kind,
                   body, request_preview, target_tokens, source_through_sequence_no,
                   source_user_turns, source_message_count, source_digest,
                   target_bank_revision, source_bank_revision, created_at, updated_at
            FROM memory_update_drafts
            WHERE source_conversation_id = $sourceConversationId
            ORDER BY updated_at DESC;
            """;
        command.Parameters.AddWithValue("$sourceConversationId", sourceConversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadDraft(reader));
        }

        return result;
    }

    public async Task SaveDraftAsync(
        MemoryUpdateDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(draft.Body))
        {
            throw new ArgumentException("记忆草稿正文不能为空。", nameof(draft));
        }

        draft.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_update_drafts(
                id, target_owner_id, source_conversation_id, draft_kind,
                body, request_preview, target_tokens, source_through_sequence_no,
                source_user_turns, source_message_count, source_digest,
                target_bank_revision, source_bank_revision, created_at, updated_at)
            VALUES(
                $id, $targetOwnerId, $sourceConversationId, $kind,
                $body, $requestPreview, $targetTokens, $sourceThroughSequenceNo,
                $sourceUserTurns, $sourceMessageCount, $sourceDigest,
                $targetBankRevision, $sourceBankRevision, $createdAt, $updatedAt)
            ON CONFLICT(target_owner_id, source_conversation_id, draft_kind)
            DO UPDATE SET
                id = excluded.id,
                body = excluded.body,
                request_preview = excluded.request_preview,
                target_tokens = excluded.target_tokens,
                source_through_sequence_no = excluded.source_through_sequence_no,
                source_user_turns = excluded.source_user_turns,
                source_message_count = excluded.source_message_count,
                source_digest = excluded.source_digest,
                target_bank_revision = excluded.target_bank_revision,
                source_bank_revision = excluded.source_bank_revision,
                created_at = excluded.created_at,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", draft.Id);
        command.Parameters.AddWithValue("$targetOwnerId", draft.TargetOwnerId);
        command.Parameters.AddWithValue("$sourceConversationId", draft.SourceConversationId);
        command.Parameters.AddWithValue("$kind", (int)draft.Kind);
        command.Parameters.AddWithValue("$body", draft.Body.Trim());
        command.Parameters.AddWithValue("$requestPreview", draft.RequestPreview);
        command.Parameters.AddWithValue("$targetTokens", draft.TargetTokens);
        command.Parameters.AddWithValue(
            "$sourceThroughSequenceNo",
            draft.SourceThroughSequenceNo);
        command.Parameters.AddWithValue("$sourceUserTurns", draft.SourceUserTurns);
        command.Parameters.AddWithValue("$sourceMessageCount", draft.SourceMessageCount);
        command.Parameters.AddWithValue("$sourceDigest", draft.SourceDigest);
        command.Parameters.AddWithValue(
            "$targetBankRevision",
            (object?)draft.TargetBankRevision ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sourceBankRevision",
            (object?)draft.SourceBankRevision ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", draft.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", draft.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CommitDraftAsync(
        string draftId,
        string editedBody,
        int targetTokens,
        CancellationToken cancellationToken = default)
    {
        var normalized = editedBody.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("记忆正文不能为空。", nameof(editedBody));
        }

        if (targetTokens is < 1000 or > 20000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTokens),
                "记忆目标必须在 1000–20000 tokens 之间。");
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            MemoryUpdateDraft draft;
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = (SqliteTransaction)transaction;
                query.CommandText = """
                    SELECT id, target_owner_id, source_conversation_id, draft_kind,
                           body, request_preview, target_tokens, source_through_sequence_no,
                           source_user_turns, source_message_count, source_digest,
                           target_bank_revision, source_bank_revision, created_at, updated_at
                    FROM memory_update_drafts
                    WHERE id = $id;
                    """;
                query.Parameters.AddWithValue("$id", draftId);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException("待保存的记忆草稿不存在或已处理。");
                }

                draft = ReadDraft(reader);
            }

            var updatedAt = DateTimeOffset.Now.ToString("O");
            var isGroupMemory = MemoryOwnerIds.TryParseGroup(
                draft.TargetOwnerId,
                out var groupConversationId,
                out var groupCharacterId);
            if (isGroupMemory
                && !string.Equals(
                    groupConversationId,
                    draft.SourceConversationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "群聊记忆草稿的目标和来源群聊不一致。");
            }

            if (isGroupMemory)
            {
                await EnsureGroupDraftIsCurrentAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    draft,
                    groupConversationId,
                    groupCharacterId,
                    cancellationToken);
            }
            else
            {
                await EnsurePersonalTargetIsCurrentAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    draft,
                    cancellationToken);
            }

            if (draft.Kind == MemoryDraftKind.GroupMerge)
            {
                await EnsureGroupMergeSourceIsCurrentAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    draft,
                    cancellationToken);
            }

            await using (var saveBank = connection.CreateCommand())
            {
                saveBank.Transaction = (SqliteTransaction)transaction;
                saveBank.CommandText = isGroupMemory
                    ? """
                      INSERT INTO group_memory_banks(
                          id, conversation_id, scope, character_id, body,
                          target_tokens, source_through_message_sequence,
                          prompt_version, revision, updated_at)
                      VALUES(
                          $id, $conversationId, $scope, $characterId, $body,
                          $targetTokens, $sourceThroughSequenceNo,
                          $promptVersion, 1, $updatedAt)
                      ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                          body = excluded.body,
                          target_tokens = excluded.target_tokens,
                          source_through_message_sequence =
                              excluded.source_through_message_sequence,
                          prompt_version = excluded.prompt_version,
                          revision = group_memory_banks.revision + 1,
                          updated_at = excluded.updated_at;
                      """
                    : """
                      INSERT INTO memory_banks(
                          id, owner_id, body, target_tokens, revision, updated_at)
                      VALUES($id, $ownerId, $body, $targetTokens, 1, $updatedAt)
                      ON CONFLICT(owner_id) DO UPDATE SET
                          body = excluded.body,
                          target_tokens = excluded.target_tokens,
                          revision = memory_banks.revision + 1,
                          updated_at = excluded.updated_at;
                      """;
                saveBank.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                if (isGroupMemory)
                {
                    saveBank.Parameters.AddWithValue(
                        "$conversationId",
                        groupConversationId);
                    saveBank.Parameters.AddWithValue(
                        "$scope",
                        (int)(groupCharacterId is null
                            ? GroupMemoryScope.Shared
                            : GroupMemoryScope.Member));
                    saveBank.Parameters.AddWithValue(
                        "$characterId",
                        groupCharacterId ?? string.Empty);
                    saveBank.Parameters.AddWithValue(
                        "$sourceThroughSequenceNo",
                        Math.Max(0, draft.SourceThroughSequenceNo));
                    saveBank.Parameters.AddWithValue(
                        "$promptVersion",
                        "reviewed-group-memory-v1");
                }
                else
                {
                    saveBank.Parameters.AddWithValue("$ownerId", draft.TargetOwnerId);
                }

                saveBank.Parameters.AddWithValue("$body", normalized);
                saveBank.Parameters.AddWithValue("$targetTokens", targetTokens);
                saveBank.Parameters.AddWithValue("$updatedAt", updatedAt);
                await saveBank.ExecuteNonQueryAsync(cancellationToken);
            }

            if (draft.Kind == MemoryDraftKind.Update)
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.Transaction = (SqliteTransaction)transaction;
                checkpoint.CommandText = isGroupMemory
                    ? """
                      INSERT INTO group_memory_checkpoints(
                          conversation_id, scope, character_id,
                          last_message_sequence, processed_messages,
                          source_digest, revision, updated_at)
                      VALUES(
                          $conversationId, $scope, $characterId,
                          $lastSequenceNo,
                          $processedMessages,
                          $sourceDigest,
                          1, $updatedAt)
                      ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                          last_message_sequence = excluded.last_message_sequence,
                          processed_messages = excluded.processed_messages,
                          source_digest = excluded.source_digest,
                          revision = group_memory_checkpoints.revision + 1,
                          updated_at = excluded.updated_at;
                      """
                    : """
                      INSERT INTO memory_checkpoints(
                          owner_id, conversation_id, last_sequence_no,
                          processed_user_turns, updated_at)
                      VALUES(
                          $ownerId, $conversationId, $lastSequenceNo,
                          $processedUserTurns, $updatedAt)
                      ON CONFLICT(owner_id, conversation_id) DO UPDATE SET
                          last_sequence_no = MAX(
                              memory_checkpoints.last_sequence_no,
                              excluded.last_sequence_no),
                          processed_user_turns =
                              memory_checkpoints.processed_user_turns
                              + excluded.processed_user_turns,
                          updated_at = excluded.updated_at;
                      """;
                if (isGroupMemory)
                {
                    checkpoint.Parameters.AddWithValue(
                        "$scope",
                        (int)(groupCharacterId is null
                            ? GroupMemoryScope.Shared
                            : GroupMemoryScope.Member));
                    checkpoint.Parameters.AddWithValue(
                        "$characterId",
                        groupCharacterId ?? string.Empty);
                    checkpoint.Parameters.AddWithValue(
                        "$processedMessages",
                        draft.SourceMessageCount);
                    checkpoint.Parameters.AddWithValue(
                        "$sourceDigest",
                        draft.SourceDigest);
                }
                else
                {
                    checkpoint.Parameters.AddWithValue(
                        "$ownerId",
                        draft.TargetOwnerId);
                    checkpoint.Parameters.AddWithValue(
                        "$processedUserTurns",
                        draft.SourceUserTurns);
                }

                checkpoint.Parameters.AddWithValue(
                    "$conversationId",
                    draft.SourceConversationId);
                checkpoint.Parameters.AddWithValue(
                    "$lastSequenceNo",
                    draft.SourceThroughSequenceNo);
                checkpoint.Parameters.AddWithValue("$updatedAt", updatedAt);
                await checkpoint.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText = "DELETE FROM memory_update_drafts WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", draft.Id);
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

    public async Task DeleteDraftAsync(
        string draftId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM memory_update_drafts WHERE id = $id;";
        command.Parameters.AddWithValue("$id", draftId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(int ProcessedMessages, string SourceDigest)>
        ReadGroupCheckpointSourceAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string conversationId,
            long throughSequence,
            CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence_no, sender_kind, sender_id, content
            FROM messages
            WHERE conversation_id = $conversationId
              AND is_deleted = 0
              AND LENGTH(TRIM(content)) > 0
              AND sequence_no <= $throughSequence
            ORDER BY sequence_no;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$throughSequence", throughSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            messages.Add(new ChatMessage
            {
                ConversationId = conversationId,
                SequenceNo = reader.GetInt64(0),
                SenderKind = (MessageSenderKind)reader.GetInt32(1),
                SenderId = reader.GetString(2),
                Content = reader.GetString(3)
            });
        }

        return (
            messages.Count,
            GroupMemorySourceFingerprint.Compute(messages));
    }

    private static async Task EnsureGroupDraftIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryUpdateDraft draft,
        string conversationId,
        string? characterId,
        CancellationToken cancellationToken)
    {
        var scope = characterId is null
            ? GroupMemoryScope.Shared
            : GroupMemoryScope.Member;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT revision
                 FROM group_memory_banks
                 WHERE conversation_id = $conversationId
                   AND scope = $scope
                   AND character_id = $characterId),
                (SELECT updated_at
                 FROM group_memory_banks
                 WHERE conversation_id = $conversationId
                   AND scope = $scope
                   AND character_id = $characterId),
                (SELECT last_message_sequence
                 FROM group_memory_checkpoints
                 WHERE conversation_id = $conversationId
                   AND scope = $scope
                   AND character_id = $characterId);
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var currentRevision = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        var bankChanged = draft.TargetBankRevision.HasValue
            ? currentRevision != draft.TargetBankRevision.Value
            : !reader.IsDBNull(1)
              && DateTimeOffset.Parse(reader.GetString(1)) > draft.CreatedAt;
        var checkpointAhead = draft.Kind == MemoryDraftKind.Update
                              && string.IsNullOrWhiteSpace(draft.SourceDigest)
                              && !reader.IsDBNull(2)
                              && reader.GetInt64(2) > draft.SourceThroughSequenceNo;
        await reader.DisposeAsync();

        var sourceChanged = false;
        if (draft.Kind == MemoryDraftKind.Update
            && !string.IsNullOrWhiteSpace(draft.SourceDigest))
        {
            var currentSource = await ReadGroupCheckpointSourceAsync(
                connection,
                transaction,
                draft.SourceConversationId,
                draft.SourceThroughSequenceNo,
                cancellationToken);
            sourceChanged = currentSource.ProcessedMessages != draft.SourceMessageCount
                            || !string.Equals(
                                currentSource.SourceDigest,
                                draft.SourceDigest,
                                StringComparison.Ordinal);
        }

        if (bankChanged || checkpointAhead || sourceChanged)
        {
            throw new InvalidOperationException(
                "群聊记忆已在草稿生成后发生变化，请重新生成草稿后再保存。");
        }
    }

    private static async Task EnsurePersonalTargetIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryUpdateDraft draft,
        CancellationToken cancellationToken)
    {
        if (!draft.TargetBankRevision.HasValue)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT revision FROM memory_banks WHERE owner_id = $ownerId;";
        command.Parameters.AddWithValue("$ownerId", draft.TargetOwnerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var currentRevision = value is null or DBNull ? 0 : Convert.ToInt64(value);
        if (currentRevision != draft.TargetBankRevision.Value)
        {
            throw new InvalidOperationException(
                "目标记忆已在草稿生成后发生变化，请重新生成草稿后再保存。");
        }
    }

    private static async Task EnsureGroupMergeSourceIsCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MemoryUpdateDraft draft,
        CancellationToken cancellationToken)
    {
        if (!draft.SourceBankRevision.HasValue)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT revision
            FROM group_memory_banks
            WHERE conversation_id = $conversationId
              AND scope = $scope
              AND character_id = '';
            """;
        command.Parameters.AddWithValue("$conversationId", draft.SourceConversationId);
        command.Parameters.AddWithValue("$scope", (int)GroupMemoryScope.Shared);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var currentRevision = value is null or DBNull ? 0 : Convert.ToInt64(value);
        if (currentRevision != draft.SourceBankRevision.Value)
        {
            throw new InvalidOperationException(
                "群聊记忆已在合并草稿生成后发生变化，请重新生成草稿后再保存。");
        }
    }

    private async Task<MemoryCheckpoint?> GetGroupCheckpointAsync(
        string ownerId,
        string conversationId,
        string? characterId,
        CancellationToken cancellationToken)
    {
        var scope = characterId is null
            ? GroupMemoryScope.Shared
            : GroupMemoryScope.Member;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_message_sequence, processed_messages, source_digest, updated_at
            FROM group_memory_checkpoints
            WHERE conversation_id = $conversationId
              AND scope = $scope
              AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(reader.GetString(2)))
        {
            return null;
        }

        return new MemoryCheckpoint
        {
            OwnerId = ownerId,
            ConversationId = conversationId,
            LastSequenceNo = reader.GetInt64(0),
            ProcessedUserTurns = reader.GetInt32(1),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(3))
        };
    }

    private static void ValidateSettings(MemoryWorkflowSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OwnerId);
        if (settings.UpdateIntervalTurns is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.UpdateIntervalTurns),
                "自动生成阈值必须在 1–10000 个用户轮次之间。");
        }

        if (settings.MaximumSourceUserTurns is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.MaximumSourceUserTurns),
                "单次发送上限必须在 1–10000 个用户轮次之间。");
        }

        if (string.IsNullOrWhiteSpace(settings.UpdateSystemPrompt)
            || string.IsNullOrWhiteSpace(settings.UpdateUserTemplate)
            || string.IsNullOrWhiteSpace(settings.CompressionSystemPrompt)
            || string.IsNullOrWhiteSpace(settings.CompressionUserTemplate))
        {
            throw new ArgumentException("记忆更新和压缩提示词不能为空。", nameof(settings));
        }
    }

    private static MemoryWorkflowSettings ReadSettings(SqliteDataReader reader) =>
        new()
        {
            OwnerId = reader.GetString(0),
            AutoGenerateEnabled = reader.GetBoolean(1),
            UpdateIntervalTurns = reader.GetInt32(2),
            MaximumSourceUserTurns = reader.GetInt32(3),
            SendOnlyNewMessages = reader.GetBoolean(4),
            UpdateSystemPrompt = reader.GetString(5),
            UpdateUserTemplate = reader.GetString(6),
            CompressionSystemPrompt = reader.GetString(7),
            CompressionUserTemplate = reader.GetString(8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9))
        };

    private static MemoryCheckpoint ReadCheckpoint(SqliteDataReader reader) =>
        new()
        {
            OwnerId = reader.GetString(0),
            ConversationId = reader.GetString(1),
            LastSequenceNo = reader.GetInt64(2),
            ProcessedUserTurns = reader.GetInt32(3),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4))
        };

    private static MemoryUpdateDraft ReadDraft(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            TargetOwnerId = reader.GetString(1),
            SourceConversationId = reader.GetString(2),
            Kind = (MemoryDraftKind)reader.GetInt32(3),
            Body = reader.GetString(4),
            RequestPreview = reader.GetString(5),
            TargetTokens = reader.GetInt32(6),
            SourceThroughSequenceNo = reader.GetInt64(7),
            SourceUserTurns = reader.GetInt32(8),
            SourceMessageCount = reader.GetInt32(9),
            SourceDigest = reader.GetString(10),
            TargetBankRevision = reader.IsDBNull(11) ? null : reader.GetInt64(11),
            SourceBankRevision = reader.IsDBNull(12) ? null : reader.GetInt64(12),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(14))
        };
}
