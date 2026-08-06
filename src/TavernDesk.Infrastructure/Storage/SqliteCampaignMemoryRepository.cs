using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteCampaignMemoryRepository : ICampaignMemoryRepository
{
    private readonly SqliteDatabase _database;

    public SqliteCampaignMemoryRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<CampaignMemoryBank?> GetBankAsync(
        string campaignId,
        CampaignMemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, campaign_id, scope, body, target_tokens,
                   source_through_event_sequence, prompt_version, updated_at
            FROM campaign_memory_banks
            WHERE campaign_id = $campaignId AND scope = $scope;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadBank(reader)
            : null;
    }

    public async Task<CampaignMemoryCheckpoint?> GetCheckpointAsync(
        string campaignId,
        CampaignMemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT campaign_id, scope, last_event_sequence,
                   processed_round, updated_at
            FROM campaign_memory_checkpoints
            WHERE campaign_id = $campaignId AND scope = $scope;
            """;
        command.Parameters.AddWithValue("$campaignId", campaignId);
        command.Parameters.AddWithValue("$scope", (int)scope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadCheckpoint(reader)
            : null;
    }

    public async Task SaveBatchAsync(
        IReadOnlyList<CampaignMemoryBank> banks,
        IReadOnlyList<CampaignMemoryCheckpoint> checkpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(banks);
        ArgumentNullException.ThrowIfNull(checkpoints);
        if (banks.Count == 0 && checkpoints.Count == 0)
        {
            return;
        }

        var campaignIds = banks.Select(item => item.CampaignId)
            .Concat(checkpoints.Select(item => item.CampaignId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (campaignIds.Length != 1
            || campaignIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "一次跑团记忆保存只能包含同一个 campaignId。",
                nameof(banks));
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        try
        {
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
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task UpsertBankAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignMemoryBank bank,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO campaign_memory_banks(
                id, campaign_id, scope, body, target_tokens,
                source_through_event_sequence, prompt_version, updated_at)
            VALUES(
                $id, $campaignId, $scope, $body, $targetTokens,
                $sourceThroughEventSequence, $promptVersion, $updatedAt)
            ON CONFLICT(campaign_id, scope) DO UPDATE SET
                body = excluded.body,
                target_tokens = excluded.target_tokens,
                source_through_event_sequence = excluded.source_through_event_sequence,
                prompt_version = excluded.prompt_version,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", bank.Id);
        command.Parameters.AddWithValue("$campaignId", bank.CampaignId);
        command.Parameters.AddWithValue("$scope", (int)bank.Scope);
        command.Parameters.AddWithValue("$body", bank.Body);
        command.Parameters.AddWithValue("$targetTokens", Math.Clamp(bank.TargetTokens, 1000, 20000));
        command.Parameters.AddWithValue(
            "$sourceThroughEventSequence",
            Math.Max(0, bank.SourceThroughEventSequence));
        command.Parameters.AddWithValue("$promptVersion", bank.PromptVersion);
        command.Parameters.AddWithValue("$updatedAt", bank.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCheckpointAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CampaignMemoryCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO campaign_memory_checkpoints(
                campaign_id, scope, last_event_sequence,
                processed_round, updated_at)
            VALUES(
                $campaignId, $scope, $lastEventSequence,
                $processedRound, $updatedAt)
            ON CONFLICT(campaign_id, scope) DO UPDATE SET
                last_event_sequence = excluded.last_event_sequence,
                processed_round = excluded.processed_round,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$campaignId", checkpoint.CampaignId);
        command.Parameters.AddWithValue("$scope", (int)checkpoint.Scope);
        command.Parameters.AddWithValue(
            "$lastEventSequence",
            Math.Max(0, checkpoint.LastEventSequence));
        command.Parameters.AddWithValue(
            "$processedRound",
            Math.Max(0, checkpoint.ProcessedRound));
        command.Parameters.AddWithValue("$updatedAt", checkpoint.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static CampaignMemoryBank ReadBank(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            CampaignId = reader.GetString(1),
            Scope = (CampaignMemoryScope)reader.GetInt32(2),
            Body = reader.GetString(3),
            TargetTokens = reader.GetInt32(4),
            SourceThroughEventSequence = reader.GetInt64(5),
            PromptVersion = reader.GetString(6),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
        };

    private static CampaignMemoryCheckpoint ReadCheckpoint(
        SqliteDataReader reader) =>
        new()
        {
            CampaignId = reader.GetString(0),
            Scope = (CampaignMemoryScope)reader.GetInt32(1),
            LastEventSequence = reader.GetInt64(2),
            ProcessedRound = reader.GetInt32(3),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(4))
        };
}
