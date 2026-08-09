using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCampaignScenarioRepository : ICampaignScenarioRepository
{
    private readonly SqliteDatabase _database;
    private readonly AppDataPaths _paths;

    public SqliteCampaignScenarioRepository(
        SqliteDatabase database,
        AppDataPaths paths)
    {
        _database = database;
        _paths = paths;
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
                   new_npc_permission, relationship_change_permission,
                   independent_plot_permission,
                   legacy_examples_archive,
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
                   new_npc_permission, relationship_change_permission,
                   independent_plot_permission,
                   legacy_examples_archive,
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
                new_npc_permission, relationship_change_permission,
                independent_plot_permission,
                lobby_instructions, legacy_examples_archive,
                source_card_json, source_file_name, cover_path,
                created_at, updated_at)
            VALUES(
                $id, $title, $summary, $worldSetting, $publicRules,
                $gmInstructions, $openingSetup, $openingNarration,
                $newNpcPermission, $relationshipChangePermission,
                $independentPlotPermission,
                '', $legacyExamplesArchive,
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
                new_npc_permission = excluded.new_npc_permission,
                relationship_change_permission = excluded.relationship_change_permission,
                independent_plot_permission = excluded.independent_plot_permission,
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
            "$newNpcPermission",
            (int)scenario.NewNpcPermission);
        command.Parameters.AddWithValue(
            "$relationshipChangePermission",
            (int)scenario.RelationshipChangePermission);
        command.Parameters.AddWithValue(
            "$independentPlotPermission",
            (int)scenario.IndependentPlotPermission);
        command.Parameters.AddWithValue(
            "$legacyExamplesArchive",
            scenario.LegacyExamplesArchive);
        command.Parameters.AddWithValue("$sourceCardJson", scenario.SourceCardJson);
        command.Parameters.AddWithValue("$sourceFileName", scenario.SourceFileName);
        command.Parameters.AddWithValue(
            "$coverPath",
            _paths.ToManagedStoredPath(
                scenario.CoverPath,
                AppDataPaths.CampaignScenarioCardsDirectoryName,
                scenario.Id));
        command.Parameters.AddWithValue("$createdAt", scenario.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", scenario.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private CampaignScenario Read(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        return new()
        {
            Id = id,
            Title = reader.GetString(1),
            Summary = reader.GetString(2),
            WorldSetting = reader.GetString(3),
            PublicRules = reader.GetString(4),
            GmInstructions = reader.GetString(5),
            OpeningSetup = reader.GetString(6),
            OpeningNarration = reader.GetString(7),
            NewNpcPermission = (CampaignNarrativePermission)reader.GetInt32(8),
            RelationshipChangePermission =
                (CampaignNarrativePermission)reader.GetInt32(9),
            IndependentPlotPermission =
                (CampaignNarrativePermission)reader.GetInt32(10),
            LegacyExamplesArchive = reader.GetString(11),
            SourceCardJson = reader.GetString(12),
            SourceFileName = reader.GetString(13),
            CoverPath = _paths.ResolveManagedPath(
                reader.GetString(14),
                AppDataPaths.CampaignScenarioCardsDirectoryName,
                id),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(15)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(16))
        };
    }
}
