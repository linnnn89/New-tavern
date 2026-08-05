using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqlitePresetRepository : IPresetRepository
{
    private readonly SqliteDatabase _database;

    public SqlitePresetRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<PromptPreset>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<PromptPreset>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, overlay_json, created_at, updated_at
            FROM presets
            ORDER BY name COLLATE NOCASE, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadPreset(reader));
        }

        return result;
    }

    public async Task<PromptPreset?> GetAsync(
        string presetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, description, overlay_json, created_at, updated_at
            FROM presets
            WHERE id = $presetId;
            """;
        command.Parameters.AddWithValue("$presetId", presetId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPreset(reader)
            : null;
    }

    public async Task UpsertAsync(
        PromptPreset preset,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            throw new ArgumentException("预设名称不能为空。", nameof(preset));
        }

        preset.UpdatedAt = DateTimeOffset.Now;
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO presets(
                id, name, description, overlay_json, created_at, updated_at)
            VALUES(
                $id, $name, $description, $overlayJson, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                description = excluded.description,
                overlay_json = excluded.overlay_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", preset.Id);
        command.Parameters.AddWithValue("$name", preset.Name.Trim());
        command.Parameters.AddWithValue("$description", preset.Description);
        command.Parameters.AddWithValue("$overlayJson", preset.OverlayJson);
        command.Parameters.AddWithValue("$createdAt", preset.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", preset.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        string presetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM presets WHERE id = $presetId;";
        command.Parameters.AddWithValue("$presetId", presetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PresetMount>> ListMountsAsync(
        PresetScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<PresetMount>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preset_id, sort_index, is_enabled
            FROM preset_mounts
            WHERE scope_kind = $scopeKind AND scope_id = $scopeId
            ORDER BY sort_index, preset_id;
            """;
        command.Parameters.AddWithValue("$scopeKind", (int)scopeKind);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PresetMount(
                scopeKind,
                scopeId,
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetBoolean(2)));
        }

        return result;
    }

    public async Task SetMountAsync(
        PresetMount mount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mount.ScopeId)
            || string.IsNullOrWhiteSpace(mount.PresetId))
        {
            throw new ArgumentException("预设挂载缺少作用域或预设 ID。", nameof(mount));
        }

        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO preset_mounts(
                scope_kind, scope_id, preset_id, sort_index, is_enabled)
            VALUES($scopeKind, $scopeId, $presetId, $sortIndex, $isEnabled)
            ON CONFLICT(scope_kind, scope_id, preset_id) DO UPDATE SET
                sort_index = excluded.sort_index,
                is_enabled = excluded.is_enabled;
            """;
        command.Parameters.AddWithValue("$scopeKind", (int)mount.ScopeKind);
        command.Parameters.AddWithValue("$scopeId", mount.ScopeId);
        command.Parameters.AddWithValue("$presetId", mount.PresetId);
        command.Parameters.AddWithValue("$sortIndex", mount.SortIndex);
        command.Parameters.AddWithValue("$isEnabled", mount.IsEnabled);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RemoveMountAsync(
        PresetScopeKind scopeKind,
        string scopeId,
        string presetId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM preset_mounts
            WHERE scope_kind = $scopeKind
              AND scope_id = $scopeId
              AND preset_id = $presetId;
            """;
        command.Parameters.AddWithValue("$scopeKind", (int)scopeKind);
        command.Parameters.AddWithValue("$scopeId", scopeId);
        command.Parameters.AddWithValue("$presetId", presetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PromptPreset ReadPreset(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            Description = reader.GetString(2),
            OverlayJson = reader.GetString(3),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(4)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
}
