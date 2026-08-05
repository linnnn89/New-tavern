using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteModelAssignmentRepository : IModelAssignmentRepository
{
    private readonly SqliteDatabase _database;

    public SqliteModelAssignmentRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ModelFunctionAssignment>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<ModelFunctionAssignment>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT function_kind, provider_id, model_id, context_limit,
                   max_output_tokens, temperature, top_p, reasoning_enabled,
                   updated_at
            FROM model_function_assignments
            ORDER BY function_kind;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadAssignment(reader));
        }

        return result;
    }

    public async Task<ModelFunctionAssignment?> GetAsync(
        ModelFunctionKind functionKind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT function_kind, provider_id, model_id, context_limit,
                   max_output_tokens, temperature, top_p, reasoning_enabled,
                   updated_at
            FROM model_function_assignments
            WHERE function_kind = $functionKind;
            """;
        command.Parameters.AddWithValue("$functionKind", (int)functionKind);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadAssignment(reader)
            : null;
    }

    public async Task UpsertAsync(
        ModelFunctionAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assignment.ProviderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignment.ModelId);
        Validate(assignment);

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO model_function_assignments(
                function_kind, provider_id, model_id, context_limit,
                max_output_tokens, temperature, top_p, reasoning_enabled,
                updated_at)
            VALUES(
                $functionKind, $providerId, $modelId, $contextLimit,
                $maxOutputTokens, $temperature, $topP, $reasoningEnabled,
                $updatedAt)
            ON CONFLICT(function_kind) DO UPDATE SET
                provider_id = excluded.provider_id,
                model_id = excluded.model_id,
                context_limit = excluded.context_limit,
                max_output_tokens = excluded.max_output_tokens,
                temperature = excluded.temperature,
                top_p = excluded.top_p,
                reasoning_enabled = excluded.reasoning_enabled,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$functionKind", (int)assignment.FunctionKind);
        command.Parameters.AddWithValue("$providerId", assignment.ProviderId);
        command.Parameters.AddWithValue("$modelId", assignment.ModelId);
        command.Parameters.AddWithValue("$contextLimit", assignment.ContextLimit);
        command.Parameters.AddWithValue("$maxOutputTokens", assignment.MaxOutputTokens);
        command.Parameters.AddWithValue("$temperature", assignment.Temperature);
        command.Parameters.AddWithValue("$topP", assignment.TopP);
        command.Parameters.AddWithValue(
            "$reasoningEnabled",
            assignment.ReasoningEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", assignment.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Validate(ModelFunctionAssignment assignment)
    {
        if (assignment.ContextLimit is < 1024 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignment.ContextLimit),
                "上下文上限必须在 1024–4194304 tokens 之间。");
        }

        if (assignment.MaxOutputTokens < 1
            || assignment.MaxOutputTokens > assignment.ContextLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignment.MaxOutputTokens),
                "输出上限必须大于 0 且不超过上下文上限。");
        }

        if (assignment.Temperature is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignment.Temperature),
                "temperature 必须在 0–2 之间。");
        }

        if (assignment.TopP is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(assignment.TopP),
                "top_p 必须在 0（不含）–1 之间。");
        }
    }

    private static ModelFunctionAssignment ReadAssignment(SqliteDataReader reader) =>
        new()
        {
            FunctionKind = (ModelFunctionKind)reader.GetInt32(0),
            ProviderId = reader.GetString(1),
            ModelId = reader.GetString(2),
            ContextLimit = reader.GetInt32(3),
            MaxOutputTokens = reader.GetInt32(4),
            Temperature = reader.GetDouble(5),
            TopP = reader.GetDouble(6),
            ReasoningEnabled = reader.GetInt32(7) != 0,
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8))
        };
}
