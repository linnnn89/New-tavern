using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

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
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, owner_id, body, target_tokens, updated_at
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
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4))
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
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO memory_banks(id, owner_id, body, target_tokens, updated_at)
            VALUES($id, $ownerId, $body, $targetTokens, $updatedAt)
            ON CONFLICT(owner_id) DO UPDATE SET
                body = excluded.body,
                target_tokens = excluded.target_tokens,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$ownerId", ownerId);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$targetTokens", Math.Clamp(targetTokens, 1000, 20000));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
