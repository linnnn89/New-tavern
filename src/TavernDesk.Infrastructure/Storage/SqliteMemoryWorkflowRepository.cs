using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

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
                update_system_prompt, update_user_template,
                compression_system_prompt, compression_user_template, updated_at)
            VALUES(
                $ownerId, $autoGenerateEnabled, $updateIntervalTurns,
                $updateSystemPrompt, $updateUserTemplate,
                $compressionSystemPrompt, $compressionUserTemplate, $updatedAt)
            ON CONFLICT(owner_id) DO UPDATE SET
                auto_generate_enabled = excluded.auto_generate_enabled,
                update_interval_turns = excluded.update_interval_turns,
                update_system_prompt = excluded.update_system_prompt,
                update_user_template = excluded.update_user_template,
                compression_system_prompt = excluded.compression_system_prompt,
                compression_user_template = excluded.compression_user_template,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$ownerId", settings.OwnerId);
        command.Parameters.AddWithValue("$autoGenerateEnabled", settings.AutoGenerateEnabled);
        command.Parameters.AddWithValue("$updateIntervalTurns", settings.UpdateIntervalTurns);
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
                   source_user_turns, created_at, updated_at
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
                   source_user_turns, created_at, updated_at
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
                source_user_turns, created_at, updated_at)
            VALUES(
                $id, $targetOwnerId, $sourceConversationId, $kind,
                $body, $requestPreview, $targetTokens, $sourceThroughSequenceNo,
                $sourceUserTurns, $createdAt, $updatedAt)
            ON CONFLICT(target_owner_id, source_conversation_id, draft_kind)
            DO UPDATE SET
                id = excluded.id,
                body = excluded.body,
                request_preview = excluded.request_preview,
                target_tokens = excluded.target_tokens,
                source_through_sequence_no = excluded.source_through_sequence_no,
                source_user_turns = excluded.source_user_turns,
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
                           source_user_turns, created_at, updated_at
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
            await using (var saveBank = connection.CreateCommand())
            {
                saveBank.Transaction = (SqliteTransaction)transaction;
                saveBank.CommandText = """
                    INSERT INTO memory_banks(id, owner_id, body, target_tokens, updated_at)
                    VALUES($id, $ownerId, $body, $targetTokens, $updatedAt)
                    ON CONFLICT(owner_id) DO UPDATE SET
                        body = excluded.body,
                        target_tokens = excluded.target_tokens,
                        updated_at = excluded.updated_at;
                    """;
                saveBank.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
                saveBank.Parameters.AddWithValue("$ownerId", draft.TargetOwnerId);
                saveBank.Parameters.AddWithValue("$body", normalized);
                saveBank.Parameters.AddWithValue("$targetTokens", targetTokens);
                saveBank.Parameters.AddWithValue("$updatedAt", updatedAt);
                await saveBank.ExecuteNonQueryAsync(cancellationToken);
            }

            if (draft.Kind == MemoryDraftKind.Update)
            {
                await using var checkpoint = connection.CreateCommand();
                checkpoint.Transaction = (SqliteTransaction)transaction;
                checkpoint.CommandText = """
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
                checkpoint.Parameters.AddWithValue("$ownerId", draft.TargetOwnerId);
                checkpoint.Parameters.AddWithValue(
                    "$conversationId",
                    draft.SourceConversationId);
                checkpoint.Parameters.AddWithValue(
                    "$lastSequenceNo",
                    draft.SourceThroughSequenceNo);
                checkpoint.Parameters.AddWithValue(
                    "$processedUserTurns",
                    draft.SourceUserTurns);
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

    private static void ValidateSettings(MemoryWorkflowSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.OwnerId);
        if (settings.UpdateIntervalTurns is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings.UpdateIntervalTurns),
                "自动生成阈值必须在 1–10000 个用户轮次之间。");
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
            UpdateSystemPrompt = reader.GetString(3),
            UpdateUserTemplate = reader.GetString(4),
            CompressionSystemPrompt = reader.GetString(5),
            CompressionUserTemplate = reader.GetString(6),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
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
            CreatedAt = DateTimeOffset.Parse(reader.GetString(9)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(10))
        };
}
