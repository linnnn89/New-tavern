using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed partial class SqliteMessageRetrievalRepository
    : IMessageRetrievalRepository
{
    private readonly SqliteDatabase _database;

    public SqliteMessageRetrievalRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<RetrievalSettings> GetSettingsAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_enabled, scope, recent_message_count,
                   maximum_results, token_budget, updated_at
            FROM retrieval_settings
            WHERE conversation_id = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new RetrievalSettings
            {
                ConversationId = conversationId
            };
        }

        return new RetrievalSettings
        {
            ConversationId = conversationId,
            IsEnabled = reader.GetBoolean(0),
            Scope = (RetrievalScope)reader.GetInt32(1),
            RecentMessageCount = reader.GetInt32(2),
            MaximumResults = reader.GetInt32(3),
            TokenBudget = reader.GetInt32(4),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    public async Task SaveSettingsAsync(
        RetrievalSettings settings,
        CancellationToken cancellationToken = default)
    {
        Validate(settings);
        settings.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retrieval_settings(
                conversation_id, is_enabled, scope, recent_message_count,
                maximum_results, token_budget, updated_at)
            VALUES(
                $conversationId, $isEnabled, $scope, $recentMessageCount,
                $maximumResults, $tokenBudget, $updatedAt)
            ON CONFLICT(conversation_id) DO UPDATE SET
                is_enabled = excluded.is_enabled,
                scope = excluded.scope,
                recent_message_count = excluded.recent_message_count,
                maximum_results = excluded.maximum_results,
                token_budget = excluded.token_budget,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", settings.ConversationId);
        command.Parameters.AddWithValue("$isEnabled", settings.IsEnabled);
        command.Parameters.AddWithValue("$scope", (int)settings.Scope);
        command.Parameters.AddWithValue(
            "$recentMessageCount",
            settings.RecentMessageCount);
        command.Parameters.AddWithValue("$maximumResults", settings.MaximumResults);
        command.Parameters.AddWithValue("$tokenBudget", settings.TokenBudget);
        command.Parameters.AddWithValue("$updatedAt", settings.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RetrievedMessage>> SearchAsync(
        MessageRetrievalQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.QueryText)
            || query.MaximumResults <= 0)
        {
            return [];
        }

        // Trigram FTS cannot form a useful MATCH term below three characters;
        // fall back to escaped LIKE so short Chinese names and abbreviations still
        // remain retrievable instead of failing open to an empty result.
        var matchQuery = BuildMatchQuery(query.QueryText);
        return matchQuery.Length == 0
            ? await SearchShortTextAsync(query, cancellationToken)
            : await SearchFtsAsync(query, matchQuery, cancellationToken);
    }

    public async Task RebuildIndexAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            cancellationToken);
        await using var clear = connection.CreateCommand();
        clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "DELETE FROM message_search_trigram;";
        await clear.ExecuteNonQueryAsync(cancellationToken);
        await using var rebuild = connection.CreateCommand();
        rebuild.Transaction = (SqliteTransaction)transaction;
        rebuild.CommandText = """
            INSERT INTO message_search_trigram(message_id, conversation_id, content)
            SELECT id, conversation_id, content
            FROM messages
            WHERE is_deleted = 0;
            """;
        await rebuild.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<RetrievedMessage>> SearchFtsAsync(
        MessageRetrievalQuery query,
        string matchQuery,
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var exclusions = AddExclusions(command, query.ExcludedMessageIds);
        command.CommandText = $"""
            SELECT m.id, m.conversation_id, c.title, m.sequence_no,
                   m.sender_kind, m.sender_id, m.content,
                   bm25(message_search_trigram) AS rank, m.created_at
            FROM message_search_trigram
            JOIN messages m ON m.id = message_search_trigram.message_id
            JOIN conversations c ON c.id = m.conversation_id
            WHERE message_search_trigram MATCH $matchQuery
              AND m.is_deleted = 0
              AND {BuildScopePredicate(query)}
              AND ($beforeSequenceNo IS NULL
                   OR m.conversation_id <> $conversationId
                   OR m.sequence_no < $beforeSequenceNo)
              {exclusions}
            ORDER BY rank, m.created_at DESC
            LIMIT $maximumResults;
            """;
        AddQueryParameters(command, query);
        command.Parameters.AddWithValue("$matchQuery", matchQuery);
        return await ReadResultsAsync(command, cancellationToken);
    }

    private async Task<IReadOnlyList<RetrievedMessage>> SearchShortTextAsync(
        MessageRetrievalQuery query,
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        var exclusions = AddExclusions(command, query.ExcludedMessageIds);
        command.CommandText = $"""
            SELECT m.id, m.conversation_id, c.title, m.sequence_no,
                   m.sender_kind, m.sender_id, m.content,
                   0.0 AS rank, m.created_at
            FROM messages m
            JOIN conversations c ON c.id = m.conversation_id
            WHERE m.is_deleted = 0
              AND m.content LIKE $like ESCAPE '\'
              AND {BuildScopePredicate(query)}
              AND ($beforeSequenceNo IS NULL
                   OR m.conversation_id <> $conversationId
                   OR m.sequence_no < $beforeSequenceNo)
              {exclusions}
            ORDER BY m.created_at DESC
            LIMIT $maximumResults;
            """;
        AddQueryParameters(command, query);
        command.Parameters.AddWithValue(
            "$like",
            $"%{EscapeLike(query.QueryText.Trim())}%");
        return await ReadResultsAsync(command, cancellationToken);
    }

    private static string BuildScopePredicate(MessageRetrievalQuery query) =>
        query.Scope == RetrievalScope.SameCharacter
        && !string.IsNullOrWhiteSpace(query.CharacterId)
            ? "c.character_id = $characterId"
            : "m.conversation_id = $conversationId";

    private static void AddQueryParameters(
        SqliteCommand command,
        MessageRetrievalQuery query)
    {
        command.Parameters.AddWithValue("$conversationId", query.ConversationId);
        command.Parameters.AddWithValue(
            "$characterId",
            (object?)query.CharacterId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$beforeSequenceNo",
            (object?)query.BeforeSequenceNo ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$maximumResults",
            Math.Clamp(query.MaximumResults, 1, 50));
    }

    private static string AddExclusions(
        SqliteCommand command,
        IReadOnlySet<string> excludedIds)
    {
        if (excludedIds.Count == 0)
        {
            return string.Empty;
        }

        var names = new List<string>();
        var index = 0;
        foreach (var id in excludedIds)
        {
            var name = $"$excluded{index++}";
            names.Add(name);
            command.Parameters.AddWithValue(name, id);
        }

        return $"AND m.id NOT IN ({string.Join(", ", names)})";
    }

    private static async Task<IReadOnlyList<RetrievedMessage>> ReadResultsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<RetrievedMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new RetrievedMessage(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                (MessageSenderKind)reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDouble(7),
                DateTimeOffset.Parse(reader.GetString(8))));
        }

        return result;
    }

    private static string BuildMatchQuery(string queryText)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SearchTermPattern().Matches(queryText))
        {
            var token = match.Value.Trim();
            if (token.Length < 3)
            {
                continue;
            }

            // Generate bounded overlapping CJK trigrams. Unlike word-oriented
            // tokenization this supports searches inside unsegmented Chinese text,
            // while the cap keeps user input from producing an excessive query.
            if (ContainsCjk(token) && token.Length > 3)
            {
                for (var index = 0; index <= token.Length - 3; index++)
                {
                    terms.Add(token.Substring(index, 3));
                    if (terms.Count >= 12)
                    {
                        break;
                    }
                }
            }
            else
            {
                terms.Add(token);
            }

            if (terms.Count >= 12)
            {
                break;
            }
        }

        return string.Join(
            " OR ",
            terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\""));
    }

    private static bool ContainsCjk(string value) =>
        value.Any(character =>
            character is >= '\u3400' and <= '\u9fff');

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    private static void Validate(RetrievalSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.ConversationId))
        {
            throw new ArgumentException("召回设置缺少会话 ID。", nameof(settings));
        }

        if (settings.RecentMessageCount is < 2 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "近期消息数量必须在 2–500 之间。");
        }

        if (settings.MaximumResults is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "最大召回条数必须在 1–50 之间。");
        }

        if (settings.TokenBudget is < 100 or > 20000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                "召回 Token 预算必须在 100–20000 之间。");
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SearchTermPattern();
}
