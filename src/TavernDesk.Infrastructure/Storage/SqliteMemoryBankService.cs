using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Group;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteMemoryBankService : IMemoryBankService
{
    private readonly SqliteDatabase _database;

    public SqliteMemoryBankService(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<MemoryBank?> GetAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        if (MemoryOwnerIds.TryParseGroup(
                ownerId,
                out var conversationId,
                out var characterId))
        {
            return await GetGroupMemoryAsync(
                ownerId,
                conversationId,
                characterId,
                cancellationToken);
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_id, body, target_tokens, revision, updated_at
            FROM memory_banks
            WHERE owner_id = $ownerId;
            """;
        command.Parameters.AddWithValue("$ownerId", ownerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MemoryBank
        {
            Id = reader.GetString(0),
            OwnerId = reader.GetString(1),
            Body = reader.GetString(2),
            TargetTokens = reader.GetInt32(3),
            Revision = reader.GetInt64(4),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    public async Task<string?> GetBodyAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        return (await GetAsync(ownerId, cancellationToken))?.Body;
    }

    public async Task SaveBodyAsync(
        string ownerId,
        string body,
        int targetTokens,
        CancellationToken cancellationToken = default)
    {
        if (!await TrySaveBodyAsync(
                ownerId,
                body,
                targetTokens,
                expectedRevision: null,
                cancellationToken))
        {
            throw new InvalidOperationException("记忆正文在保存前已发生变化，请重新载入后再保存。");
        }
    }

    public async Task<bool> TrySaveBodyAsync(
        string ownerId,
        string body,
        int targetTokens,
        long? expectedRevision,
        CancellationToken cancellationToken = default)
    {
        if (MemoryOwnerIds.TryParseGroup(
                ownerId,
                out var conversationId,
                out var characterId))
        {
            return await TrySaveGroupMemoryAsync(
                conversationId,
                characterId,
                body,
                targetTokens,
                expectedRevision,
                cancellationToken);
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        var sqliteTransaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
        try
        {
            long? currentRevision;
            await using (var current = connection.CreateCommand())
            {
                current.Transaction = sqliteTransaction;
                current.CommandText = """
                    SELECT revision
                    FROM memory_banks
                    WHERE owner_id = $ownerId;
                    """;
                current.Parameters.AddWithValue("$ownerId", ownerId);
                var value = await current.ExecuteScalarAsync(cancellationToken);
                currentRevision = value is null or DBNull ? null : Convert.ToInt64(value);
            }

            if (expectedRevision is not null
                && (currentRevision ?? 0) != expectedRevision.Value)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = sqliteTransaction;
            command.CommandText = """
                INSERT INTO memory_banks(
                    id, owner_id, body, target_tokens, revision, updated_at)
                VALUES($id, $ownerId, $body, $targetTokens, 1, $updatedAt)
                ON CONFLICT(owner_id) DO UPDATE SET
                    body = excluded.body,
                    target_tokens = excluded.target_tokens,
                    revision = memory_banks.revision + 1,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$ownerId", ownerId);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue("$targetTokens", Math.Clamp(targetTokens, 1000, 20000));
            command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<MemoryBank?> GetGroupMemoryAsync(
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
            SELECT id, body, target_tokens, revision, updated_at
            FROM group_memory_banks
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

        return new MemoryBank
        {
            Id = reader.GetString(0),
            OwnerId = ownerId,
            Body = reader.GetString(1),
            TargetTokens = reader.GetInt32(2),
            Revision = reader.GetInt64(3),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4))
        };
    }

    private async Task<bool> TrySaveGroupMemoryAsync(
        string conversationId,
        string? characterId,
        string body,
        int targetTokens,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var scope = characterId is null
            ? GroupMemoryScope.Shared
            : GroupMemoryScope.Member;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var validate = connection.CreateCommand())
            {
                validate.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                validate.CommandText = characterId is null
                    ? """
                      SELECT COUNT(*)
                      FROM conversations
                      WHERE id = $conversationId AND mode = $groupMode;
                      """
                    : """
                      SELECT COUNT(*)
                      FROM group_chat_members
                      INNER JOIN conversations
                          ON conversations.id = group_chat_members.conversation_id
                      WHERE group_chat_members.conversation_id = $conversationId
                        AND group_chat_members.character_id = $characterId
                        AND conversations.mode = $groupMode;
                      """;
                validate.Parameters.AddWithValue("$conversationId", conversationId);
                validate.Parameters.AddWithValue("$groupMode", (int)ConversationMode.Group);
                if (characterId is not null)
                {
                    validate.Parameters.AddWithValue("$characterId", characterId);
                }

                if (Convert.ToInt32(
                        await validate.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    throw new InvalidOperationException(
                        characterId is null
                            ? "群聊记忆引用的群聊不存在。"
                            : "角色独立群聊记忆引用的成员不存在。");
                }
            }

            string bankId;
            long sourceThrough;
            long? currentRevision;
            await using (var current = connection.CreateCommand())
            {
                current.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                current.CommandText = """
                    SELECT id, source_through_message_sequence, revision
                    FROM group_memory_banks
                    WHERE conversation_id = $conversationId
                      AND scope = $scope
                      AND character_id = $characterId;
                    """;
                current.Parameters.AddWithValue("$conversationId", conversationId);
                current.Parameters.AddWithValue("$scope", (int)scope);
                current.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
                await using var reader = await current.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    bankId = reader.GetString(0);
                    sourceThrough = reader.GetInt64(1);
                    currentRevision = reader.GetInt64(2);
                }
                else
                {
                    bankId = Guid.NewGuid().ToString("N");
                    sourceThrough = 0;
                    currentRevision = null;
                }
            }

            if (expectedRevision is not null
                && (currentRevision ?? 0) != expectedRevision.Value)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            var updatedAt = DateTimeOffset.Now.ToString("O");
            await using var command = connection.CreateCommand();
            command.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO group_memory_banks(
                    id, conversation_id, scope, character_id, body,
                    target_tokens, source_through_message_sequence,
                    prompt_version, revision, updated_at)
                VALUES(
                    $id, $conversationId, $scope, $characterId, $body,
                    $targetTokens, $sourceThrough, $promptVersion, 1, $updatedAt)
                ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                    body = excluded.body,
                    target_tokens = excluded.target_tokens,
                    source_through_message_sequence = excluded.source_through_message_sequence,
                    prompt_version = excluded.prompt_version,
                    revision = group_memory_banks.revision + 1,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$id", bankId);
            command.Parameters.AddWithValue("$conversationId", conversationId);
            command.Parameters.AddWithValue("$scope", (int)scope);
            command.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
            command.Parameters.AddWithValue("$body", body);
            command.Parameters.AddWithValue(
                "$targetTokens",
                Math.Clamp(targetTokens, 1000, 20000));
            command.Parameters.AddWithValue("$promptVersion", "manual-group-memory-v1");
            command.Parameters.AddWithValue("$sourceThrough", sourceThrough);
            command.Parameters.AddWithValue("$updatedAt", updatedAt);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using (var checkpointExists = connection.CreateCommand())
            {
                checkpointExists.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                checkpointExists.CommandText = """
                    SELECT COUNT(*)
                    FROM group_memory_checkpoints
                    WHERE conversation_id = $conversationId
                      AND scope = $scope
                      AND character_id = $characterId;
                    """;
                checkpointExists.Parameters.AddWithValue("$conversationId", conversationId);
                checkpointExists.Parameters.AddWithValue("$scope", (int)scope);
                checkpointExists.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
                if (Convert.ToInt32(await checkpointExists.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    await using var checkpoint = connection.CreateCommand();
                    checkpoint.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)transaction;
                    checkpoint.CommandText = """
                        INSERT INTO group_memory_checkpoints(
                            conversation_id, scope, character_id,
                            last_message_sequence, processed_messages,
                            source_digest, revision, updated_at)
                        VALUES(
                            $conversationId, $scope, $characterId,
                            0, 0, $sourceDigest, 1, $updatedAt);
                        """;
                    checkpoint.Parameters.AddWithValue("$conversationId", conversationId);
                    checkpoint.Parameters.AddWithValue("$scope", (int)scope);
                    checkpoint.Parameters.AddWithValue("$characterId", characterId ?? string.Empty);
                    checkpoint.Parameters.AddWithValue(
                        "$sourceDigest",
                        GroupMemorySourceFingerprint.Compute(Array.Empty<ChatMessage>()));
                    checkpoint.Parameters.AddWithValue("$updatedAt", updatedAt);
                    await checkpoint.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
