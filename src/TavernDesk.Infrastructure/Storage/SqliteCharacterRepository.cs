using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCharacterRepository : ICharacterRepository
{
    private readonly SqliteDatabase _database;

    public SqliteCharacterRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<Character>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<Character>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, personality, scenario, first_message,
                   avatar_path, raw_card_json, source_card_format, source_card_path,
                   import_report_json, created_at, updated_at
            FROM characters
            ORDER BY name COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadCharacter(reader));
        }

        return result;
    }

    public async Task<Character?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, personality, scenario, first_message,
                   avatar_path, raw_card_json, source_card_format, source_card_path,
                   import_report_json, created_at, updated_at
            FROM characters
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCharacter(reader) : null;
    }

    public async Task UpsertAsync(Character character, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO characters(
                id, name, description, personality, scenario, first_message,
                avatar_path, raw_card_json, source_card_format, source_card_path,
                import_report_json, created_at, updated_at)
            VALUES(
                $id, $name, $description, $personality, $scenario, $firstMessage,
                $avatarPath, $rawCardJson, $sourceCardFormat, $sourceCardPath,
                $importReportJson, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                personality = excluded.personality,
                scenario = excluded.scenario,
                first_message = excluded.first_message,
                avatar_path = excluded.avatar_path,
                raw_card_json = excluded.raw_card_json,
                source_card_format = excluded.source_card_format,
                source_card_path = excluded.source_card_path,
                import_report_json = excluded.import_report_json,
                updated_at = excluded.updated_at;
            """;

        command.Parameters.AddWithValue("$id", character.Id);
        command.Parameters.AddWithValue("$name", character.Name);
        command.Parameters.AddWithValue("$description", character.Description);
        command.Parameters.AddWithValue("$personality", character.Personality);
        command.Parameters.AddWithValue("$scenario", character.Scenario);
        command.Parameters.AddWithValue("$firstMessage", character.FirstMessage);
        command.Parameters.AddWithValue("$avatarPath", character.AvatarPath);
        command.Parameters.AddWithValue("$rawCardJson", character.RawCardJson);
        command.Parameters.AddWithValue("$sourceCardFormat", (int)character.SourceCardFormat);
        command.Parameters.AddWithValue("$sourceCardPath", character.SourceCardPath);
        command.Parameters.AddWithValue("$importReportJson", character.ImportReportJson);
        command.Parameters.AddWithValue("$createdAt", character.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", character.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM characters WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM characters;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static Character ReadCharacter(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            Personality = reader.GetString(3),
            Scenario = reader.GetString(4),
            FirstMessage = reader.GetString(5),
            AvatarPath = reader.GetString(6),
            RawCardJson = reader.GetString(7),
            SourceCardFormat = (CharacterCardFormat)reader.GetInt32(8),
            SourceCardPath = reader.GetString(9),
            ImportReportJson = reader.GetString(10),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(11)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(12))
        };
}
