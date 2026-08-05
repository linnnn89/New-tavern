namespace TavernDesk.Infrastructure.Storage;

public sealed class AppDataPaths
{
    public const string DataRootEnvironmentVariable = "TAVERNDESK_DATA_ROOT";

    public AppDataPaths(string? explicitRoot = null)
    {
        var configuredRoot = explicitRoot;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            configuredRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        }

        RootDirectory = string.IsNullOrWhiteSpace(configuredRoot)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "TavernDesk")
            : Path.GetFullPath(configuredRoot);

        DatabasePath = Path.Combine(RootDirectory, "taverndesk.db");
        AttachmentsDirectory = Path.Combine(RootDirectory, "attachments");
        CharacterCardsDirectory = Path.Combine(RootDirectory, "character-cards");
        CampaignScenarioCardsDirectory = Path.Combine(
            RootDirectory,
            "campaign-scenario-cards");
        SecretsDirectory = Path.Combine(RootDirectory, "secrets");
        GrokCliRuntimeDirectory = Path.Combine(RootDirectory, "grok-cli-runtime");
        ExportsDirectory = Path.Combine(RootDirectory, "exports");
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string AttachmentsDirectory { get; }
    public string CharacterCardsDirectory { get; }
    public string CampaignScenarioCardsDirectory { get; }
    public string SecretsDirectory { get; }
    public string GrokCliRuntimeDirectory { get; }
    public string ExportsDirectory { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AttachmentsDirectory);
        Directory.CreateDirectory(CharacterCardsDirectory);
        Directory.CreateDirectory(CampaignScenarioCardsDirectory);
        Directory.CreateDirectory(SecretsDirectory);
        Directory.CreateDirectory(GrokCliRuntimeDirectory);
        Directory.CreateDirectory(ExportsDirectory);
    }
}
