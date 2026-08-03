using Microsoft.Data.Sqlite;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class SqliteProviderProfileRepository : IProviderProfileRepository
{
    private const string DefaultsInitializedKey =
        "providers.catalog_v2_initialized";
    private readonly SqliteDatabase _database;

    public SqliteProviderProfileRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<ProviderProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ProviderProfile>();
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, adapter_kind, base_url, secret_reference,
                   request_timeout_seconds, is_enabled, created_at, updated_at
            FROM provider_profiles
            ORDER BY name COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadProfile(reader));
        }

        return result;
    }

    public async Task<ProviderProfile?> GetAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, adapter_kind, base_url, secret_reference,
                   request_timeout_seconds, is_enabled, created_at, updated_at
            FROM provider_profiles
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadProfile(reader)
            : null;
    }

    public async Task UpsertAsync(ProviderProfile profile, CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO provider_profiles(
                id, name, adapter_kind, base_url, secret_reference,
                request_timeout_seconds, is_enabled, created_at, updated_at)
            VALUES(
                $id, $name, $adapterKind, $baseUrl, $secretReference,
                $requestTimeoutSeconds, $isEnabled, $createdAt, $updatedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                adapter_kind = excluded.adapter_kind,
                base_url = excluded.base_url,
                secret_reference = excluded.secret_reference,
                request_timeout_seconds = excluded.request_timeout_seconds,
                is_enabled = excluded.is_enabled,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", profile.Id);
        command.Parameters.AddWithValue("$name", profile.Name);
        command.Parameters.AddWithValue("$adapterKind", (int)profile.AdapterKind);
        command.Parameters.AddWithValue("$baseUrl", profile.BaseUrl);
        command.Parameters.AddWithValue("$secretReference", profile.SecretReference);
        command.Parameters.AddWithValue("$requestTimeoutSeconds", profile.RequestTimeoutSeconds);
        command.Parameters.AddWithValue("$isEnabled", profile.IsEnabled);
        command.Parameters.AddWithValue("$createdAt", profile.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", profile.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM provider_profiles WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task EnsureDefaultsAsync(CancellationToken cancellationToken = default)
    {
        if (await DefaultsWereInitializedAsync(cancellationToken))
        {
            return;
        }

        var existingProfiles = await ListAsync(cancellationToken);
        var existingIds = existingProfiles
            .Select(profile => profile.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var profile in existingProfiles.Where(profile =>
                     !ProviderProfileIds.IsSupported(profile.Id)
                     && profile.IsEnabled))
        {
            profile.IsEnabled = false;
            profile.UpdatedAt = DateTimeOffset.Now;
            await UpsertAsync(profile, cancellationToken);
        }

        var defaults = new[]
        {
            new ProviderProfile
            {
                Id = ProviderProfileIds.GrokCli,
                Name = "Grok CLI（订阅登录）",
                AdapterKind = ProviderAdapterKind.GrokCli,
                BaseUrl = "grok://local",
                RequestTimeoutSeconds = 600
            },
            new ProviderProfile
            {
                Id = ProviderProfileIds.OpenRouter,
                Name = "OpenRouter",
                AdapterKind = ProviderAdapterKind.OpenAiCompatible,
                BaseUrl = "https://openrouter.ai/api/v1"
            },
            new ProviderProfile
            {
                Id = ProviderProfileIds.SiliconFlow,
                Name = "硅基流动",
                AdapterKind = ProviderAdapterKind.OpenAiCompatible,
                BaseUrl = "https://api.siliconflow.cn/v1"
            },
            new ProviderProfile
            {
                Id = ProviderProfileIds.DeepSeek,
                Name = "DeepSeek 官方 API",
                AdapterKind = ProviderAdapterKind.OpenAiCompatible,
                BaseUrl = "https://api.deepseek.com"
            },
            new ProviderProfile
            {
                Id = ProviderProfileIds.LmStudio,
                Name = "LM Studio（本地）",
                AdapterKind = ProviderAdapterKind.OpenAiCompatible,
                BaseUrl = "http://127.0.0.1:6543"
            }
        };

        foreach (var profile in defaults.Where(profile => !existingIds.Contains(profile.Id)))
        {
            await UpsertAsync(profile, cancellationToken);
        }

        await MarkDefaultsInitializedAsync(cancellationToken);
    }

    public async Task<int> CountEnabledAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM provider_profiles WHERE is_enabled = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<bool> DefaultsWereInitializedAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM app_settings WHERE key = $key LIMIT 1;";
        command.Parameters.AddWithValue("$key", DefaultsInitializedKey);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task MarkDefaultsInitializedAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = _database.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value, updated_at)
            VALUES($key, '1', $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$key", DefaultsInitializedKey);
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.Now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ProviderProfile ReadProfile(SqliteDataReader reader) =>
        new()
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            AdapterKind = (ProviderAdapterKind)reader.GetInt32(2),
            BaseUrl = reader.GetString(3),
            SecretReference = reader.GetString(4),
            RequestTimeoutSeconds = reader.GetDouble(5),
            IsEnabled = reader.GetBoolean(6),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(7)),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(8))
        };
}
