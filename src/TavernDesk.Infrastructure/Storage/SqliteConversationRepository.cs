using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteConversationRepository : IConversationRepository
{
    private static readonly string SummarySelect = $"""
        SELECT c.id, c.character_id, c.title, c.mode,
               COALESCE(
                 (SELECT m.content
                  FROM messages m
                  WHERE m.conversation_id = c.id
                    AND m.is_deleted = 0
                    AND m.sender_kind IN (
                        {(int)MessageSenderKind.User},
                        {(int)MessageSenderKind.Character}
                    )
                  ORDER BY m.sequence_no DESC
                  LIMIT 1),
                 ''),
               c.updated_at
        FROM conversations c
        """;

    private readonly SqliteDatabase _database;

    public SqliteConversationRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + """

            ORDER BY c.updated_at DESC, c.id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Max(1, limit));
        return await ReadSummariesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + """

            ORDER BY c.updated_at DESC, c.id;
            """;
        return await ReadSummariesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListByCharacterAsync(
        string characterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + """

            WHERE c.character_id = $characterId
              AND c.mode = $mode
            ORDER BY c.updated_at DESC, c.id;
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$mode", (int)ConversationMode.SingleCharacter);
        return await ReadSummariesAsync(command, cancellationToken);
    }

    public async Task<ConversationSummary?> GetLatestForCharacterAsync(
        string characterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(characterId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SummarySelect + """

            WHERE c.character_id = $characterId
              AND c.mode = $mode
            ORDER BY c.updated_at DESC, c.id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$mode", (int)ConversationMode.SingleCharacter);
        return (await ReadSummariesAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<Conversation?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, character_id, title, mode, created_at, updated_at
            FROM conversations
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new Conversation
        {
            Id = reader.GetString(0),
            CharacterId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Title = reader.GetString(2),
            Mode = (ConversationMode)reader.GetInt32(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ChatMessage>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, sequence_no, sender_kind, sender_id, content,
                   active_candidate_index, created_at, updated_at, is_deleted
            FROM messages
            WHERE conversation_id = $conversationId AND is_deleted = 0
            ORDER BY sequence_no;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ChatMessage
            {
                Id = reader.GetString(0),
                ConversationId = reader.GetString(1),
                SequenceNo = reader.GetInt64(2),
                SenderKind = (MessageSenderKind)reader.GetInt32(3),
                SenderId = reader.GetString(4),
                Content = reader.GetString(5),
                ActiveCandidateIndex = reader.GetInt32(6),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(8)),
                IsDeleted = reader.GetBoolean(9)
            });
        }

        return result;
    }

    public async Task UpsertAsync(Conversation conversation, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations(id, character_id, title, mode, created_at, updated_at)
            VALUES($id, $characterId, $title, $mode, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                character_id = excluded.character_id,
                title = excluded.title,
                mode = excluded.mode,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$characterId", (object?)conversation.CharacterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task AddMessageAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        if (message.SequenceNo <= 0)
        {
            await using var sequenceCommand = connection.CreateCommand();
            sequenceCommand.Transaction = (SqliteTransaction)transaction;
            sequenceCommand.CommandText = """
                SELECT COALESCE(MAX(sequence_no), 0) + 1
                FROM messages
                WHERE conversation_id = $conversationId;
                """;
            sequenceCommand.Parameters.AddWithValue("$conversationId", message.ConversationId);
            message.SequenceNo = Convert.ToInt64(
                await sequenceCommand.ExecuteScalarAsync(cancellationToken));
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO messages(
                    id, conversation_id, sequence_no, sender_kind, sender_id, content,
                    active_candidate_index, created_at, updated_at, is_deleted)
                VALUES(
                    $id, $conversationId, $sequenceNo, $senderKind, $senderId, $content,
                    $activeCandidateIndex, $createdAt, $updatedAt, $isDeleted);
                """;
            command.Parameters.AddWithValue("$id", message.Id);
            command.Parameters.AddWithValue("$conversationId", message.ConversationId);
            command.Parameters.AddWithValue("$sequenceNo", message.SequenceNo);
            command.Parameters.AddWithValue("$senderKind", (int)message.SenderKind);
            command.Parameters.AddWithValue("$senderId", message.SenderId);
            command.Parameters.AddWithValue("$content", message.Content);
            command.Parameters.AddWithValue("$activeCandidateIndex", message.ActiveCandidateIndex);
            command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updatedAt", message.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$isDeleted", message.IsDeleted);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertSearchRowAsync(connection, (SqliteTransaction)transaction, message, cancellationToken);

        await using (var updateConversation = connection.CreateCommand())
        {
            updateConversation.Transaction = (SqliteTransaction)transaction;
            updateConversation.CommandText = """
                UPDATE conversations
                SET updated_at = $updatedAt
                WHERE id = $conversationId;
                """;
            updateConversation.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            updateConversation.Parameters.AddWithValue("$conversationId", message.ConversationId);
            await updateConversation.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AddCandidateAsync(
        MessageCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO message_candidates(id, message_id, candidate_index, content, created_at)
            VALUES($id, $messageId, $candidateIndex, $content, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", candidate.Id);
        command.Parameters.AddWithValue("$messageId", candidate.MessageId);
        command.Parameters.AddWithValue("$candidateIndex", candidate.CandidateIndex);
        command.Parameters.AddWithValue("$content", candidate.Content);
        command.Parameters.AddWithValue("$createdAt", candidate.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MessageCandidate>> ListCandidatesAsync(
        string messageId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<MessageCandidate>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, message_id, candidate_index, content, created_at
            FROM message_candidates
            WHERE message_id = $messageId
            ORDER BY candidate_index;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MessageCandidate
            {
                Id = reader.GetString(0),
                MessageId = reader.GetString(1),
                CandidateIndex = reader.GetInt32(2),
                Content = reader.GetString(3),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4))
            });
        }

        return result;
    }

    public async Task AddAndActivateCandidateAsync(
        MessageCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var normalized = candidate.Content.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("候选正文不能为空。", nameof(candidate));
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var updatedAt = DateTimeOffset.Now.ToString("O");
        try
        {
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO message_candidates(
                        id, message_id, candidate_index, content, created_at)
                    VALUES(
                        $id, $messageId, $candidateIndex, $content, $createdAt);
                    """;
                insert.Parameters.AddWithValue("$id", candidate.Id);
                insert.Parameters.AddWithValue("$messageId", candidate.MessageId);
                insert.Parameters.AddWithValue("$candidateIndex", candidate.CandidateIndex);
                insert.Parameters.AddWithValue("$content", normalized);
                insert.Parameters.AddWithValue("$createdAt", candidate.CreatedAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            string conversationId;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE messages
                    SET content = $content,
                        active_candidate_index = $candidateIndex,
                        updated_at = $updatedAt
                    WHERE id = $messageId AND is_deleted = 0
                    RETURNING conversation_id;
                    """;
                update.Parameters.AddWithValue("$content", normalized);
                update.Parameters.AddWithValue("$candidateIndex", candidate.CandidateIndex);
                update.Parameters.AddWithValue("$updatedAt", updatedAt);
                update.Parameters.AddWithValue("$messageId", candidate.MessageId);
                conversationId = Convert.ToString(
                    await update.ExecuteScalarAsync(cancellationToken))
                    ?? throw new InvalidOperationException(
                        "候选所属消息不存在或已经删除。");
            }

            await using (var search = connection.CreateCommand())
            {
                search.Transaction = (SqliteTransaction)transaction;
                search.CommandText = """
                    UPDATE message_search
                    SET content = $content
                    WHERE message_id = $messageId;
                    """;
                search.Parameters.AddWithValue("$content", normalized);
                search.Parameters.AddWithValue("$messageId", candidate.MessageId);
                await search.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var conversation = connection.CreateCommand())
            {
                conversation.Transaction = (SqliteTransaction)transaction;
                conversation.CommandText = """
                    UPDATE conversations
                    SET updated_at = $updatedAt
                    WHERE id = $conversationId;
                    """;
                conversation.Parameters.AddWithValue("$updatedAt", updatedAt);
                conversation.Parameters.AddWithValue("$conversationId", conversationId);
                await conversation.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task UpdateMessageContentAsync(
        string messageId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var normalized = content.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("消息正文不能为空。", nameof(content));
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var updatedAt = DateTimeOffset.Now.ToString("O");

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText = """
                UPDATE messages
                SET content = $content, updated_at = $updatedAt
                WHERE id = $messageId AND is_deleted = 0;
                """;
            update.Parameters.AddWithValue("$content", normalized);
            update.Parameters.AddWithValue("$updatedAt", updatedAt);
            update.Parameters.AddWithValue("$messageId", messageId);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new InvalidOperationException("消息不存在或已经删除。");
            }
        }

        await using (var searchUpdate = connection.CreateCommand())
        {
            searchUpdate.Transaction = (SqliteTransaction)transaction;
            searchUpdate.CommandText = """
                UPDATE message_search
                SET content = $content
                WHERE message_id = $messageId;
                """;
            searchUpdate.Parameters.AddWithValue("$content", normalized);
            searchUpdate.Parameters.AddWithValue("$messageId", messageId);
            await searchUpdate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var candidateUpdate = connection.CreateCommand())
        {
            candidateUpdate.Transaction = (SqliteTransaction)transaction;
            candidateUpdate.CommandText = """
                UPDATE message_candidates
                SET content = $content
                WHERE message_id = $messageId
                  AND candidate_index = (
                      SELECT active_candidate_index
                      FROM messages
                      WHERE id = $messageId
                  );
                """;
            candidateUpdate.Parameters.AddWithValue("$content", normalized);
            candidateUpdate.Parameters.AddWithValue("$messageId", messageId);
            await candidateUpdate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteMessageAsync(
        string messageId,
        bool includeSubsequent,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string conversationId;
        long sequenceNo;
        await using (var lookup = connection.CreateCommand())
        {
            lookup.Transaction = (SqliteTransaction)transaction;
            lookup.CommandText = """
                SELECT conversation_id, sequence_no
                FROM messages
                WHERE id = $messageId AND is_deleted = 0;
                """;
            lookup.Parameters.AddWithValue("$messageId", messageId);
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("消息不存在或已经删除。");
            }

            conversationId = reader.GetString(0);
            sequenceNo = reader.GetInt64(1);
        }

        await using (var searchDelete = connection.CreateCommand())
        {
            searchDelete.Transaction = (SqliteTransaction)transaction;
            searchDelete.CommandText = includeSubsequent
                ? """
                    DELETE FROM message_search
                    WHERE conversation_id = $conversationId
                      AND message_id IN (
                          SELECT id
                          FROM messages
                          WHERE conversation_id = $conversationId
                            AND sequence_no >= $sequenceNo
                      );
                    """
                : "DELETE FROM message_search WHERE message_id = $messageId;";
            searchDelete.Parameters.AddWithValue("$messageId", messageId);
            searchDelete.Parameters.AddWithValue("$conversationId", conversationId);
            searchDelete.Parameters.AddWithValue("$sequenceNo", sequenceNo);
            await searchDelete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = includeSubsequent
                ? """
                    DELETE FROM messages
                    WHERE conversation_id = $conversationId
                      AND sequence_no >= $sequenceNo
                      AND is_deleted = 0;
                    """
                : "DELETE FROM messages WHERE id = $messageId AND is_deleted = 0;";
            delete.Parameters.AddWithValue("$messageId", messageId);
            delete.Parameters.AddWithValue("$conversationId", conversationId);
            delete.Parameters.AddWithValue("$sequenceNo", sequenceNo);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await RecalculateConversationActivityAsync(
            connection,
            (SqliteTransaction)transaction,
            conversationId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<Conversation> ForkThroughMessageAsync(
        string conversationId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var source = await ReadConversationAsync(
            connection,
            (SqliteTransaction)transaction,
            conversationId,
            cancellationToken);
        var cutoffSequence = await ReadCutoffSequenceAsync(
            connection,
            (SqliteTransaction)transaction,
            conversationId,
            messageId,
            cancellationToken);

        var now = DateTimeOffset.Now;
        var fork = new Conversation
        {
            CharacterId = source.CharacterId,
            Title = $"{source.Title} · 分支 {now:MM-dd HH:mm}",
            Mode = source.Mode,
            CreatedAt = now,
            UpdatedAt = now
        };
        await InsertConversationAsync(
            connection,
            (SqliteTransaction)transaction,
            fork,
            cancellationToken);
        if (source.Mode == ConversationMode.Group)
        {
            await CopyGroupConfigurationAsync(
                connection,
                (SqliteTransaction)transaction,
                source.Id,
                fork.Id,
                cancellationToken);
        }

        var copies = await ReadMessagesForForkAsync(
            connection,
            (SqliteTransaction)transaction,
            conversationId,
            fork.Id,
            cutoffSequence,
            now,
            cancellationToken);
        var candidates = await ReadCandidatesForForkAsync(
            connection,
            (SqliteTransaction)transaction,
            conversationId,
            cutoffSequence,
            cancellationToken);

        foreach (var copy in copies)
        {
            var sourceCandidates = candidates
                .Where(candidate => candidate.MessageId == copy.SourceMessageId)
                .ToArray();
            if (copy.Target.ActiveCandidateIndex != 0 && sourceCandidates.Length == 0)
            {
                throw new InvalidOperationException(
                    $"消息 {copy.SourceMessageId} 的当前候选索引无对应候选记录，无法安全创建独立分支。");
            }

            if (sourceCandidates.Length > 0
                && sourceCandidates.All(candidate =>
                    candidate.CandidateIndex != copy.Target.ActiveCandidateIndex))
            {
                throw new InvalidOperationException(
                    $"消息 {copy.SourceMessageId} 的当前候选索引无对应候选记录，无法安全创建独立分支。");
            }

            await InsertForkMessageAsync(
                connection,
                (SqliteTransaction)transaction,
                copy.Target,
                cancellationToken);

            foreach (var sourceCandidate in sourceCandidates)
            {
                await InsertCandidateAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    new MessageCandidate
                    {
                        MessageId = copy.Target.Id,
                        CandidateIndex = sourceCandidate.CandidateIndex,
                        Content = sourceCandidate.Content,
                        CreatedAt = sourceCandidate.CreatedAt
                    },
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return fork;
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM conversations;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<IReadOnlyList<ConversationSummary>> ReadSummariesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<ConversationSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ConversationSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                (ConversationMode)reader.GetInt32(3),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))));
        }

        return result;
    }

    private static async Task InsertSearchRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        await using var searchCommand = connection.CreateCommand();
        searchCommand.Transaction = transaction;
        searchCommand.CommandText = """
            INSERT INTO message_search(message_id, conversation_id, content)
            VALUES($messageId, $conversationId, $content);
            """;
        searchCommand.Parameters.AddWithValue("$messageId", message.Id);
        searchCommand.Parameters.AddWithValue("$conversationId", message.ConversationId);
        searchCommand.Parameters.AddWithValue("$content", message.Content);
        await searchCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task RecalculateConversationActivityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE conversations
            SET updated_at = MAX(
                created_at,
                COALESCE(
                    (SELECT m.created_at
                     FROM messages m
                     WHERE m.conversation_id = conversations.id
                       AND m.is_deleted = 0
                     ORDER BY m.sequence_no DESC
                     LIMIT 1),
                    created_at
                )
            )
            WHERE id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Conversation> ReadConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, character_id, title, mode, created_at, updated_at
            FROM conversations
            WHERE id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("源会话不存在。");
        }

        return new Conversation
        {
            Id = reader.GetString(0),
            CharacterId = reader.IsDBNull(1) ? null : reader.GetString(1),
            Title = reader.GetString(2),
            Mode = (ConversationMode)reader.GetInt32(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    private static async Task<long> ReadCutoffSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sequence_no
            FROM messages
            WHERE id = $messageId
              AND conversation_id = $conversationId
              AND is_deleted = 0;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        command.Parameters.AddWithValue("$conversationId", conversationId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long value
            ? value
            : throw new InvalidOperationException("分支起点不属于当前会话。");
    }

    private static async Task InsertConversationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Conversation conversation,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversations(id, character_id, title, mode, created_at, updated_at)
            VALUES($id, $characterId, $title, $mode, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", conversation.Id);
        command.Parameters.AddWithValue("$characterId", (object?)conversation.CharacterId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", conversation.Title);
        command.Parameters.AddWithValue("$mode", (int)conversation.Mode);
        command.Parameters.AddWithValue("$createdAt", conversation.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", conversation.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<ForkMessageCopy>> ReadMessagesForForkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceConversationId,
        string targetConversationId,
        long cutoffSequence,
        DateTimeOffset copiedAt,
        CancellationToken cancellationToken)
    {
        var result = new List<ForkMessageCopy>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, sequence_no, sender_kind, sender_id, content,
                   active_candidate_index, created_at
            FROM messages
            WHERE conversation_id = $conversationId
              AND is_deleted = 0
              AND sequence_no <= $cutoffSequence
            ORDER BY sequence_no;
            """;
        command.Parameters.AddWithValue("$conversationId", sourceConversationId);
        command.Parameters.AddWithValue("$cutoffSequence", cutoffSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ForkMessageCopy(
                reader.GetString(0),
                new ChatMessage
                {
                    ConversationId = targetConversationId,
                    SequenceNo = reader.GetInt64(1),
                    SenderKind = (MessageSenderKind)reader.GetInt32(2),
                    SenderId = reader.GetString(3),
                    Content = reader.GetString(4),
                    ActiveCandidateIndex = reader.GetInt32(5),
                    CreatedAt = DateTimeOffset.Parse(reader.GetString(6)),
                    UpdatedAt = copiedAt
                }));
        }

        return result;
    }

    private static async Task CopyGroupConfigurationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sourceConversationId,
        string targetConversationId,
        CancellationToken cancellationToken)
    {
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = """
                INSERT INTO group_chat_settings(
                    conversation_id, relay_mode, auto_continue_enabled,
                    maximum_automatic_turns, pause_on_user_mention,
                    group_system_prompt, merge_system_prompt,
                    merge_user_template, updated_at)
                SELECT
                    $targetConversationId, relay_mode, auto_continue_enabled,
                    maximum_automatic_turns, pause_on_user_mention,
                    group_system_prompt, merge_system_prompt,
                    merge_user_template, $updatedAt
                FROM group_chat_settings
                WHERE conversation_id = $sourceConversationId;
                """;
            settings.Parameters.AddWithValue(
                "$sourceConversationId",
                sourceConversationId);
            settings.Parameters.AddWithValue(
                "$targetConversationId",
                targetConversationId);
            settings.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var members = connection.CreateCommand())
        {
            members.Transaction = transaction;
            members.CommandText = """
                INSERT INTO group_chat_members(
                    conversation_id, character_id, sort_index, is_enabled)
                SELECT
                    $targetConversationId, character_id, sort_index, is_enabled
                FROM group_chat_members
                WHERE conversation_id = $sourceConversationId;
                """;
            members.Parameters.AddWithValue(
                "$sourceConversationId",
                sourceConversationId);
            members.Parameters.AddWithValue(
                "$targetConversationId",
                targetConversationId);
            await members.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandText = """
            INSERT INTO group_chat_state(
                conversation_id, current_speaker_id, next_speaker_id,
                automatic_turns, is_paused, pause_reason, updated_at)
            VALUES($conversationId, '', '', 0, 0, '', $updatedAt);
            """;
        state.Parameters.AddWithValue("$conversationId", targetConversationId);
        state.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await state.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<MessageCandidate>> ReadCandidatesForForkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        long cutoffSequence,
        CancellationToken cancellationToken)
    {
        var result = new List<MessageCandidate>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT mc.id, mc.message_id, mc.candidate_index, mc.content, mc.created_at
            FROM message_candidates mc
            INNER JOIN messages m ON m.id = mc.message_id
            WHERE m.conversation_id = $conversationId
              AND m.is_deleted = 0
              AND m.sequence_no <= $cutoffSequence
            ORDER BY m.sequence_no, mc.candidate_index;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$cutoffSequence", cutoffSequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new MessageCandidate
            {
                Id = reader.GetString(0),
                MessageId = reader.GetString(1),
                CandidateIndex = reader.GetInt32(2),
                Content = reader.GetString(3),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(4))
            });
        }

        return result;
    }

    private static async Task InsertForkMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO messages(
                id, conversation_id, sequence_no, sender_kind, sender_id, content,
                active_candidate_index, created_at, updated_at, is_deleted)
            VALUES(
                $id, $conversationId, $sequenceNo, $senderKind, $senderId, $content,
                $activeCandidateIndex, $createdAt, $updatedAt, 0);
            """;
        command.Parameters.AddWithValue("$id", message.Id);
        command.Parameters.AddWithValue("$conversationId", message.ConversationId);
        command.Parameters.AddWithValue("$sequenceNo", message.SequenceNo);
        command.Parameters.AddWithValue("$senderKind", (int)message.SenderKind);
        command.Parameters.AddWithValue("$senderId", message.SenderId);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$activeCandidateIndex", message.ActiveCandidateIndex);
        command.Parameters.AddWithValue("$createdAt", message.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", message.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await InsertSearchRowAsync(connection, transaction, message, cancellationToken);
    }

    private static async Task InsertCandidateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_candidates(id, message_id, candidate_index, content, created_at)
            VALUES($id, $messageId, $candidateIndex, $content, $createdAt);
            """;
        command.Parameters.AddWithValue("$id", candidate.Id);
        command.Parameters.AddWithValue("$messageId", candidate.MessageId);
        command.Parameters.AddWithValue("$candidateIndex", candidate.CandidateIndex);
        command.Parameters.AddWithValue("$content", candidate.Content);
        command.Parameters.AddWithValue("$createdAt", candidate.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ForkMessageCopy(string SourceMessageId, ChatMessage Target);
}
