using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TavernDesk.Infrastructure.Storage;

/// <summary>
/// Stores the location of the user's personal data outside that data root.
/// The file deliberately lives in the Windows per-user application area so a
/// data-root change cannot strand the setting that tells TavernDesk where to
/// find the data.
/// </summary>
public sealed class AppDataConfiguration
{
    public const string DataRootPropertyName = "dataRoot";
    public const string ConfigFileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AppDataConfiguration(
        string? configurationDirectory = null,
        string? defaultDataRoot = null)
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var baseDirectory = string.IsNullOrWhiteSpace(configurationDirectory)
            ? (string.IsNullOrWhiteSpace(localAppData)
                ? AppContext.BaseDirectory
                : Path.Combine(localAppData, "TavernDesk"))
            : configurationDirectory;

        ConfigurationDirectory = Path.GetFullPath(baseDirectory);
        ConfigurationPath = Path.Combine(ConfigurationDirectory, ConfigFileName);

        var documents = Environment.GetFolderPath(
            Environment.SpecialFolder.MyDocuments);
        DefaultDataRoot = Path.GetFullPath(
            string.IsNullOrWhiteSpace(defaultDataRoot)
                ? Path.Combine(
                    string.IsNullOrWhiteSpace(documents)
                        ? AppContext.BaseDirectory
                        : documents,
                    "TavernDesk")
                : defaultDataRoot);
        ConfiguredDataRoot = ReadConfiguredDataRoot();
    }

    public string ConfigurationDirectory { get; }

    public string ConfigurationPath { get; }

    public string DefaultDataRoot { get; }

    public string? ConfiguredDataRoot { get; private set; }

    /// <summary>
    /// Creates the first-run personal-data root and its external configuration
    /// file. An explicit command-line or environment override is handled by
    /// the caller and must not be persisted here.
    /// </summary>
    public async Task EnsureStartupConfigurationAsync(
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(ConfigurationPath))
        {
            return;
        }

        Directory.CreateDirectory(DefaultDataRoot);
        await SaveDataRootAsync(DefaultDataRoot, cancellationToken);
    }

    public string ResolveRoot(
        string? explicitRoot,
        out bool isExternalOverride)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            isExternalOverride = true;
            return Path.GetFullPath(explicitRoot);
        }

        var environmentRoot = Environment.GetEnvironmentVariable(
            AppDataPaths.DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            isExternalOverride = true;
            return Path.GetFullPath(environmentRoot);
        }

        isExternalOverride = false;
        return ConfiguredDataRoot ?? DefaultDataRoot;
    }

    public async Task SaveDataRootAsync(
        string dataRoot,
        CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(dataRoot);
        Directory.CreateDirectory(ConfigurationDirectory);

        JsonObject document;
        if (File.Exists(ConfigurationPath))
        {
            try
            {
                document = JsonNode.Parse(
                               await File.ReadAllTextAsync(
                                   ConfigurationPath,
                                   cancellationToken)) as JsonObject
                           ?? new JsonObject();
            }
            catch (JsonException)
            {
                document = new JsonObject();
            }
        }
        else
        {
            document = new JsonObject();
        }

        document[DataRootPropertyName] = normalized;
        var temporaryPath = Path.Combine(
            ConfigurationDirectory,
            $".{ConfigFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                document.ToJsonString(JsonOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);
            File.Move(temporaryPath, ConfigurationPath, overwrite: true);
            ConfiguredDataRoot = normalized;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string? ReadConfiguredDataRoot()
    {
        if (!File.Exists(ConfigurationPath))
        {
            return null;
        }

        try
        {
            var document = JsonNode.Parse(File.ReadAllText(ConfigurationPath))
                           as JsonObject;
            var value = document?[DataRootPropertyName];
            return value is JsonValue jsonValue
                   && jsonValue.TryGetValue<string>(out var configured)
                   && !string.IsNullOrWhiteSpace(configured)
                ? Path.GetFullPath(configured)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentException)
        {
            // A corrupt or inaccessible optional config must not prevent the
            // application from starting with the documented default root.
            return null;
        }
    }
}
