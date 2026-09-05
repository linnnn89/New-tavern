using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Group;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteGroupMemoryRepository : IGroupMemoryRepository
{
    private readonly SqliteDatabase _database;

    public SqliteGroupMemoryRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<GroupMemoryBank?> GetBankAsync(
        string conversationId,
        GroupMemoryScope scope,
        string? characterId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCharacterId = NormalizeCharacterId(scope, characterId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, scope, character_id, body,
                   target_tokens, source_through_message_sequence,
                   prompt_version, revision, updated_at
            FROM group_memory_banks
            WHERE conversation_id = $conversationId
              AND scope = $scope
              AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$characterId", normalizedCharacterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadBank(reader)
            : null;
    }

    public async Task<IReadOnlyList<GroupMemoryBank>> ListBanksAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var result = new List<GroupMemoryBank>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, scope, character_id, body,
                   target_tokens, source_through_message_sequence,
                   prompt_version, revision, updated_at
            FROM group_memory_banks
            WHERE conversation_id = $conversationId
            ORDER BY scope, character_id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadBank(reader));
        }

        return result;
    }

    public async Task<GroupMemoryCheckpoint?> GetCheckpointAsync(
        string conversationId,
        GroupMemoryScope scope,
        string? characterId = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedCharacterId = NormalizeCharacterId(scope, characterId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, scope, character_id,
                   last_message_sequence, processed_messages,
                   source_digest, revision, updated_at
            FROM group_memory_checkpoints
            WHERE conversation_id = $conversationId
              AND scope = $scope
              AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$characterId", normalizedCharacterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCheckpoint(reader)
            : null;
    }

    public async Task<IReadOnlyList<GroupMemoryCheckpoint>> ListCheckpointsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var result = new List<GroupMemoryCheckpoint>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id, scope, character_id,
                   last_message_sequence, processed_messages,
                   source_digest, revision, updated_at
            FROM group_memory_checkpoints
            WHERE conversation_id = $conversationId
            ORDER BY scope, character_id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCheckpoint(reader));
        }

        return result;
    }

    public async Task SaveBatchAsync(
        IReadOnlyList<GroupMemoryBank> banks,
        IReadOnlyList<GroupMemoryCheckpoint> checkpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(banks);
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (banks.Count == 0 && checkpoints.Count == 0)
        {
            return;
        }

        await SaveBatchCoreAsync(
            banks,
            checkpoints,
            expectations: null,
            validateSource: false,
            cancellationToken);
    }

    public async Task<bool> TrySaveBatchAsync(
        IReadOnlyList<GroupMemoryBank> banks,
        IReadOnlyList<GroupMemoryCheckpoint> checkpoints,
        IReadOnlyList<GroupMemoryWriteExpectation> expectations,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        return await SaveBatchCoreAsync(
            banks,
            checkpoints,
            expectations,
            validateSource: true,
            cancellationToken);
    }

    public async Task<bool> ClearIfConversationHasNoMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureGroupConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationId,
                cancellationToken);
            // Check emptiness and delete derived memory under one write
            // transaction; otherwise a concurrent message could be committed
            // between the check and the cleanup.
            await using (var count = connection.CreateCommand())
            {
                count.Transaction = (SqliteTransaction)transaction;
                count.CommandText = """
                    SELECT COUNT(*)
                    FROM messages
                    WHERE conversation_id = $conversationId
                      AND is_deleted = 0
                      AND LENGTH(TRIM(content)) > 0;
                    """;
                count.Parameters.AddWithValue("$conversationId", conversationId);
                if (Convert.ToInt32(
                        await count.ExecuteScalarAsync(cancellationToken)) > 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return false;
                }
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = """
                DELETE FROM group_memory_checkpoints
                WHERE conversation_id = $conversationId;
                DELETE FROM group_memory_banks
                WHERE conversation_id = $conversationId;
                """;
            delete.Parameters.AddWithValue("$conversationId", conversationId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task InvalidateAsync(
        string conversationId,
        GroupMemoryScopeMask scopes = GroupMemoryScopeMask.All,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        if (scopes == GroupMemoryScopeMask.None)
        {
            return;
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureGroupConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationId,
                cancellationToken);
            var now = DateTimeOffset.Now.ToString("O");
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO group_memory_checkpoints(
                    conversation_id, scope, character_id,
                    last_message_sequence, processed_messages,
                    source_digest, revision, updated_at)
                SELECT conversation_id, scope, character_id,
                       source_through_message_sequence, 0,
                       '', 1, $updatedAt
                FROM group_memory_banks
                WHERE conversation_id = $conversationId
                  AND (($shared = 1 AND scope = $sharedScope)
                       OR ($members = 1 AND scope = $memberScope))
                ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                    source_digest = '',
                    revision = group_memory_checkpoints.revision + 1,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$conversationId", conversationId);
            command.Parameters.AddWithValue(
                "$shared",
                scopes.HasFlag(GroupMemoryScopeMask.Shared));
            command.Parameters.AddWithValue(
                "$members",
                scopes.HasFlag(GroupMemoryScopeMask.Members));
            command.Parameters.AddWithValue("$sharedScope", (int)GroupMemoryScope.Shared);
            command.Parameters.AddWithValue("$memberScope", (int)GroupMemoryScope.Member);
            command.Parameters.AddWithValue("$updatedAt", now);
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<bool> SaveBatchCoreAsync(
        IReadOnlyList<GroupMemoryBank> banks,
        IReadOnlyList<GroupMemoryCheckpoint> checkpoints,
        IReadOnlyList<GroupMemoryWriteExpectation>? expectations,
        bool validateSource,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(banks);
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (banks.Count == 0 && checkpoints.Count == 0)
        {
            return true;
        }

        var conversationIds = banks.Select(item => item.ConversationId)
            .Concat(checkpoints.Select(item => item.ConversationId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (conversationIds.Length != 1
            || string.IsNullOrWhiteSpace(conversationIds[0]))
        {
            throw new ArgumentException(
                "一次群聊记忆保存只能包含同一个群聊。",
                nameof(banks));
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsureGroupConversationAsync(
                connection,
                (SqliteTransaction)transaction,
                conversationIds[0],
                cancellationToken);

            var memberIds = banks
                .Where(item => item.Scope == GroupMemoryScope.Member)
                .Select(item => NormalizeCharacterId(item.Scope, item.CharacterId))
                .Concat(checkpoints
                    .Where(item => item.Scope == GroupMemoryScope.Member)
                    .Select(item => NormalizeCharacterId(item.Scope, item.CharacterId)))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            foreach (var memberId in memberIds)
            {
                if (expectations is not null
                    && !await IsMemberMemoryEnabledAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        conversationIds[0],
                        memberId,
                        cancellationToken))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return false;
                }

                await EnsureGroupMemberAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    conversationIds[0],
                    memberId,
                    cancellationToken);
            }

            // Expectations are a compare-and-swap fence over both bank and
            // checkpoint revisions. A stale provider result returns false instead
            // of overwriting a newer memory update.
            if (expectations is not null)
            {
                foreach (var expectation in expectations)
                {
                    if (!await MatchesExpectationAsync(
                            connection,
                            (SqliteTransaction)transaction,
                            conversationIds[0],
                            expectation,
                            cancellationToken))
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return false;
                    }
                }
            }

            // Revision equality alone cannot detect edits, deletions, or candidate
            // switches inside an already processed range; verify its count+digest
            // before advancing any checkpoint.
            if (validateSource)
            {
                foreach (var checkpoint in checkpoints)
                {
                    var source = await ReadSourceAsync(
                        connection,
                        (SqliteTransaction)transaction,
                        checkpoint.ConversationId,
                        checkpoint.LastMessageSequence,
                        cancellationToken);
                    if (source.Count != checkpoint.ProcessedMessages
                        || !string.Equals(
                            source.Digest,
                            checkpoint.SourceDigest,
                            StringComparison.Ordinal))
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                        return false;
                    }
                }
            }

            // Banks and checkpoints describe one derived snapshot and must become
            // visible together; partial publication would skip or double-process
            // transcript ranges on the next update.
            foreach (var bank in banks)
            {
                await UpsertBankAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    bank,
                    cancellationToken);
            }

            foreach (var checkpoint in checkpoints)
            {
                await UpsertCheckpointAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    checkpoint,
                    cancellationToken);
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

    private static async Task<bool> IsMemberMemoryEnabledAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM group_chat_settings settings
            INNER JOIN group_chat_members members
                ON members.conversation_id = settings.conversation_id
            WHERE settings.conversation_id = $conversationId
              AND settings.member_memory_enabled = 1
              AND members.character_id = $characterId
              AND members.is_enabled = 1;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$characterId", characterId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> MatchesExpectationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        GroupMemoryWriteExpectation expectation,
        CancellationToken cancellationToken)
    {
        var characterId = NormalizeCharacterId(
            expectation.Scope,
            expectation.CharacterId);
        var currentBank = await ReadRevisionAsync(
            connection,
            transaction,
            "group_memory_banks",
            conversationId,
            expectation.Scope,
            characterId,
            cancellationToken);
        var currentCheckpoint = await ReadRevisionAsync(
            connection,
            transaction,
            "group_memory_checkpoints",
            conversationId,
            expectation.Scope,
            characterId,
            cancellationToken);
        return SameVersion(currentBank, expectation.BankRevision)
               && SameVersion(
                   currentCheckpoint,
                   expectation.CheckpointRevision);
    }

    private static async Task<long?> ReadRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string conversationId,
        GroupMemoryScope scope,
        string characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT revision
            FROM {table}
            WHERE conversation_id = $conversationId
              AND scope = $scope
              AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        command.Parameters.AddWithValue("$characterId", characterId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static bool SameVersion(
        long? actual,
        long? expected) =>
        actual is null
            ? expected is null
            : expected is not null && actual.Value.Equals(expected.Value);

    private static async Task<(int Count, string Digest)> ReadSourceAsync(
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

        return (messages.Count, GroupMemorySourceFingerprint.Compute(messages));
    }

    private static async Task UpsertBankAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GroupMemoryBank bank,
        CancellationToken cancellationToken)
    {
        var characterId = NormalizeCharacterId(bank.Scope, bank.CharacterId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO group_memory_banks(
                id, conversation_id, scope, character_id, body,
                target_tokens, source_through_message_sequence,
                prompt_version, revision, updated_at)
            VALUES(
                $id, $conversationId, $scope, $characterId, $body,
                $targetTokens, $sourceThroughMessageSequence,
                $promptVersion, $revision, $updatedAt)
            ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                body = excluded.body,
                target_tokens = excluded.target_tokens,
                source_through_message_sequence = excluded.source_through_message_sequence,
                prompt_version = excluded.prompt_version,
                revision = group_memory_banks.revision + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", bank.Id);
        command.Parameters.AddWithValue("$conversationId", bank.ConversationId);
        command.Parameters.AddWithValue("$scope", (int)bank.Scope);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$body", bank.Body.Trim());
        command.Parameters.AddWithValue(
            "$targetTokens",
            Math.Clamp(bank.TargetTokens, 1000, 20000));
        command.Parameters.AddWithValue(
            "$sourceThroughMessageSequence",
            Math.Max(0, bank.SourceThroughMessageSequence));
        command.Parameters.AddWithValue("$promptVersion", bank.PromptVersion);
        command.Parameters.AddWithValue("$revision", Math.Max(1, bank.Revision));
        command.Parameters.AddWithValue("$updatedAt", bank.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GroupMemoryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        var characterId = NormalizeCharacterId(
            checkpoint.Scope,
            checkpoint.CharacterId);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO group_memory_checkpoints(
                conversation_id, scope, character_id,
                last_message_sequence, processed_messages,
                source_digest, revision, updated_at)
            VALUES(
                $conversationId, $scope, $characterId,
                $lastMessageSequence, $processedMessages,
                $sourceDigest, $revision, $updatedAt)
            ON CONFLICT(conversation_id, scope, character_id) DO UPDATE SET
                last_message_sequence = excluded.last_message_sequence,
                processed_messages = excluded.processed_messages,
                source_digest = excluded.source_digest,
                revision = group_memory_checkpoints.revision + 1,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", checkpoint.ConversationId);
        command.Parameters.AddWithValue("$scope", (int)checkpoint.Scope);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue(
            "$lastMessageSequence",
            Math.Max(0, checkpoint.LastMessageSequence));
        command.Parameters.AddWithValue(
            "$processedMessages",
            Math.Max(0, checkpoint.ProcessedMessages));
        command.Parameters.AddWithValue("$sourceDigest", checkpoint.SourceDigest);
        command.Parameters.AddWithValue("$revision", Math.Max(1, checkpoint.Revision));
        command.Parameters.AddWithValue("$updatedAt", checkpoint.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureGroupConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM conversations
            WHERE id = $conversationId AND mode = $groupMode;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$groupMode", (int)ConversationMode.Group);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            throw new InvalidOperationException("群聊记忆引用的群聊不存在。");
        }
    }

    private static async Task EnsureGroupMemberAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string characterId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM group_chat_members
            WHERE conversation_id = $conversationId
              AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$characterId", characterId);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
        {
            throw new InvalidOperationException("角色独立群聊记忆引用的成员不存在。");
        }
    }

    private static string NormalizeCharacterId(
        GroupMemoryScope scope,
        string? characterId)
    {
        if (scope == GroupMemoryScope.Shared)
        {
            if (!string.IsNullOrWhiteSpace(characterId))
            {
                throw new ArgumentException("共同群聊记忆不能绑定角色。", nameof(characterId));
            }

            return string.Empty;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        return characterId;
    }

    private static GroupMemoryBank ReadBank(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            ConversationId = reader.GetString(1),
            Scope = (GroupMemoryScope)reader.GetInt32(2),
            CharacterId = reader.GetString(3) is { Length: > 0 } value
                ? value
                : null,
            Body = reader.GetString(4),
            TargetTokens = reader.GetInt32(5),
            SourceThroughMessageSequence = reader.GetInt64(6),
            PromptVersion = reader.GetString(7),
            Revision = reader.GetInt64(8),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(9))
        };

    private static GroupMemoryCheckpoint ReadCheckpoint(SqliteDataReader reader) =>
        new()
        {
            ConversationId = reader.GetString(0),
            Scope = (GroupMemoryScope)reader.GetInt32(1),
            CharacterId = reader.GetString(2) is { Length: > 0 } value
                ? value
                : null,
            LastMessageSequence = reader.GetInt64(3),
            ProcessedMessages = reader.GetInt32(4),
            SourceDigest = reader.GetString(5),
            Revision = reader.GetInt64(6),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
        };
}
