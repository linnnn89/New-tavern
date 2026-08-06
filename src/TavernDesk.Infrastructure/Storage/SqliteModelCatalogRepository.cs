using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteModelCatalogRepository : IModelCatalogRepository
{
    private readonly SqliteDatabase _database;

    public SqliteModelCatalogRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ProviderModel>> ListAsync(
        string providerId,
        ModelCatalogKind? modelKind = null,
        CancellationToken cancellationToken = default)
    {
        var result = new List<ProviderModel>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider_id, model_id, display_name, context_limit,
                   max_output_tokens, supports_streaming, model_kind, updated_at
            FROM provider_models
            WHERE provider_id = $providerId
              AND ($modelKind IS NULL OR model_kind = $modelKind)
            ORDER BY display_name COLLATE NOCASE, model_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$providerId", providerId);
        command.Parameters.AddWithValue(
            "$modelKind",
            modelKind is null ? DBNull.Value : (object)(int)modelKind.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadModel(reader));
        }

        return result;
    }

    public async Task ReplaceAsync(
        string providerId,
        IReadOnlyList<ProviderModelDescriptor> models,
        ModelCatalogKind modelKind = ModelCatalogKind.Chat,
        CancellationToken cancellationToken = default)
    {
        var normalized = models
            .Where(model => !string.IsNullOrWhiteSpace(model.ModelId))
            .GroupBy(model => model.ModelId.Trim(), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = new Dictionary<string, ProviderModel>(StringComparer.Ordinal);
            await using (var query = connection.CreateCommand())
            {
                query.Transaction = (SqliteTransaction)transaction;
                query.CommandText = """
                    SELECT provider_id, model_id, display_name, context_limit,
                           max_output_tokens, supports_streaming, model_kind, updated_at
                    FROM provider_models
                    WHERE provider_id = $providerId
                      AND model_kind = $modelKind;
                    """;
                query.Parameters.AddWithValue("$providerId", providerId);
                query.Parameters.AddWithValue("$modelKind", (int)modelKind);
                await using var reader = await query.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var model = ReadModel(reader);
                    existing[model.ModelId] = model;
                }
            }

            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = (SqliteTransaction)transaction;
                delete.CommandText =
                    "DELETE FROM provider_models "
                    + "WHERE provider_id = $providerId AND model_kind = $modelKind;";
                delete.Parameters.AddWithValue("$providerId", providerId);
                delete.Parameters.AddWithValue("$modelKind", (int)modelKind);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var descriptor in normalized)
            {
                var modelId = descriptor.ModelId.Trim();
                existing.TryGetValue(modelId, out var previous);
                var model = new ProviderModel
                {
                    ProviderId = providerId,
                    ModelId = modelId,
                    DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName)
                        ? modelId
                        : descriptor.DisplayName.Trim(),
                    ContextLimit = ClampContextLimit(
                        descriptor.ContextLimit ?? previous?.ContextLimit ?? 32768),
                    MaxOutputTokens = ClampOutputLimit(
                        descriptor.MaxOutputTokens ?? previous?.MaxOutputTokens ?? 4096),
                    SupportsStreaming = descriptor.SupportsStreaming,
                    ModelKind = modelKind,
                    UpdatedAt = DateTimeOffset.Now
                };
                if (model.MaxOutputTokens > model.ContextLimit)
                {
                    model.MaxOutputTokens = model.ContextLimit;
                }

                await UpsertCoreAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    model,
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

    public async Task UpsertAsync(
        ProviderModel model,
        CancellationToken cancellationToken = default)
    {
        model.ContextLimit = ClampContextLimit(model.ContextLimit);
        model.MaxOutputTokens = ClampOutputLimit(model.MaxOutputTokens);
        if (model.MaxOutputTokens > model.ContextLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(model),
                "模型最大输出不能超过上下文上限。");
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await UpsertCoreAsync(connection, null, model, cancellationToken);
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ProviderModel model,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO provider_models(
                provider_id, model_id, display_name, context_limit,
                max_output_tokens, supports_streaming, model_kind, updated_at)
            VALUES(
                $providerId, $modelId, $displayName, $contextLimit,
                $maxOutputTokens, $supportsStreaming, $modelKind, $updatedAt)
            ON CONFLICT(provider_id, model_id) DO UPDATE SET
                display_name = excluded.display_name,
                context_limit = excluded.context_limit,
                max_output_tokens = excluded.max_output_tokens,
                supports_streaming = excluded.supports_streaming,
                model_kind = excluded.model_kind,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$providerId", model.ProviderId);
        command.Parameters.AddWithValue("$modelId", model.ModelId);
        command.Parameters.AddWithValue("$displayName", model.DisplayName);
        command.Parameters.AddWithValue("$contextLimit", model.ContextLimit);
        command.Parameters.AddWithValue("$maxOutputTokens", model.MaxOutputTokens);
        command.Parameters.AddWithValue("$supportsStreaming", model.SupportsStreaming);
        command.Parameters.AddWithValue("$modelKind", (int)model.ModelKind);
        command.Parameters.AddWithValue("$updatedAt", model.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderModel ReadModel(SqliteDataReader reader) =>
        new()
        {
            ProviderId = reader.GetString(0),
            ModelId = reader.GetString(1),
            DisplayName = reader.GetString(2),
            ContextLimit = reader.GetInt32(3),
            MaxOutputTokens = reader.GetInt32(4),
            SupportsStreaming = reader.GetBoolean(5),
            ModelKind = (ModelCatalogKind)reader.GetInt32(6),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(7))
        };

    private static int ClampContextLimit(int value) =>
        Math.Clamp(value, 1024, 4_194_304);

    private static int ClampOutputLimit(int value) =>
        Math.Clamp(value, 1, 1_048_576);
}
