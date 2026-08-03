using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCampaignScenarioRepository : ICampaignScenarioRepository
{
    private readonly SqliteDatabase _database;

    public SqliteCampaignScenarioRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<CampaignScenario>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<CampaignScenario>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, summary, world_setting, public_rules,
                   gm_instructions, opening_setup, opening_narration,
                   lobby_instructions, legacy_examples_archive,
                   source_card_json, source_file_name, cover_path,
                   created_at, updated_at
            FROM campaign_scenarios
            ORDER BY updated_at DESC, title COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(Read(reader));
        }

        return result;
    }

    public async Task<CampaignScenario?> GetAsync(
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, title, summary, world_setting, public_rules,
                   gm_instructions, opening_setup, opening_narration,
                   lobby_instructions, legacy_examples_archive,
                   source_card_json, source_file_name, cover_path,
                   created_at, updated_at
            FROM campaign_scenarios
            WHERE id = $scenarioId;
            """;
        command.Parameters.AddWithValue("$scenarioId", scenarioId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(
        CampaignScenario scenario,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario.Title);
        scenario.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO campaign_scenarios(
                id, title, summary, world_setting, public_rules,
                gm_instructions, opening_setup, opening_narration,
                lobby_instructions, legacy_examples_archive,
                source_card_json, source_file_name, cover_path,
                created_at, updated_at)
            VALUES(
                $id, $title, $summary, $worldSetting, $publicRules,
                $gmInstructions, $openingSetup, $openingNarration,
                $lobbyInstructions, $legacyExamplesArchive,
                $sourceCardJson, $sourceFileName, $coverPath,
                $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                summary = excluded.summary,
                world_setting = excluded.world_setting,
                public_rules = excluded.public_rules,
                gm_instructions = excluded.gm_instructions,
                opening_setup = excluded.opening_setup,
                opening_narration = excluded.opening_narration,
                lobby_instructions = excluded.lobby_instructions,
                legacy_examples_archive = excluded.legacy_examples_archive,
                source_card_json = excluded.source_card_json,
                source_file_name = excluded.source_file_name,
                cover_path = excluded.cover_path,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", scenario.Id);
        command.Parameters.AddWithValue("$title", scenario.Title.Trim());
        command.Parameters.AddWithValue("$summary", scenario.Summary);
        command.Parameters.AddWithValue("$worldSetting", scenario.WorldSetting);
        command.Parameters.AddWithValue("$publicRules", scenario.PublicRules);
        command.Parameters.AddWithValue("$gmInstructions", scenario.GmInstructions);
        command.Parameters.AddWithValue("$openingSetup", scenario.OpeningSetup);
        command.Parameters.AddWithValue(
            "$openingNarration",
            scenario.OpeningNarration);
        command.Parameters.AddWithValue(
            "$lobbyInstructions",
            scenario.LobbyInstructions);
        command.Parameters.AddWithValue(
            "$legacyExamplesArchive",
            scenario.LegacyExamplesArchive);
        command.Parameters.AddWithValue("$sourceCardJson", scenario.SourceCardJson);
        command.Parameters.AddWithValue("$sourceFileName", scenario.SourceFileName);
        command.Parameters.AddWithValue("$coverPath", scenario.CoverPath);
        command.Parameters.AddWithValue("$createdAt", scenario.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", scenario.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CampaignScenario Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Title = reader.GetString(1),
            Summary = reader.GetString(2),
            WorldSetting = reader.GetString(3),
            PublicRules = reader.GetString(4),
            GmInstructions = reader.GetString(5),
            OpeningSetup = reader.GetString(6),
            OpeningNarration = reader.GetString(7),
            LobbyInstructions = reader.GetString(8),
            LegacyExamplesArchive = reader.GetString(9),
            SourceCardJson = reader.GetString(10),
            SourceFileName = reader.GetString(11),
            CoverPath = reader.GetString(12),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(13)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(14))
        };
}
