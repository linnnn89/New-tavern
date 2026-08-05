using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCharacterShelfRepository : ICharacterShelfRepository
{
    private readonly SqliteDatabase _database;

    public SqliteCharacterShelfRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<CharacterShelf>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<CharacterShelf>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, sort_index, created_at, updated_at
            FROM character_shelves
            ORDER BY sort_index, name COLLATE NOCASE, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CharacterShelf
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                SortIndex = reader.GetInt32(2),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(3)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(4))
            });
        }

        return result;
    }

    public async Task UpsertAsync(
        CharacterShelf shelf,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shelf.Name);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO character_shelves(
                id, name, sort_index, created_at, updated_at)
            VALUES(
                $id, $name, $sortIndex, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                sort_index = excluded.sort_index,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", shelf.Id);
        command.Parameters.AddWithValue("$name", shelf.Name.Trim());
        command.Parameters.AddWithValue("$sortIndex", shelf.SortIndex);
        command.Parameters.AddWithValue("$createdAt", shelf.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", shelf.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        string shelfId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM character_shelves WHERE id = $shelfId;";
        command.Parameters.AddWithValue("$shelfId", shelfId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> ListCharacterIdsAsync(
        string shelfId,
        CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT character_id
            FROM character_shelf_items
            WHERE shelf_id = $shelfId
            ORDER BY sort_index, added_at, character_id;
            """;
        command.Parameters.AddWithValue("$shelfId", shelfId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task AddCharacterAsync(
        string shelfId,
        string characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO character_shelf_items(
                shelf_id, character_id, sort_index, added_at)
            VALUES(
                $shelfId, $characterId,
                COALESCE(
                    (SELECT MAX(sort_index) + 1
                     FROM character_shelf_items
                     WHERE shelf_id = $shelfId),
                    0),
                $addedAt)
            ON CONFLICT(shelf_id, character_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$shelfId", shelfId);
        command.Parameters.AddWithValue("$characterId", characterId);
        command.Parameters.AddWithValue("$addedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveCharacterAsync(
        string shelfId,
        string characterId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM character_shelf_items
            WHERE shelf_id = $shelfId AND character_id = $characterId;
            """;
        command.Parameters.AddWithValue("$shelfId", shelfId);
        command.Parameters.AddWithValue("$characterId", characterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
