using System.Text.Json;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Tests;

public sealed class AppDataConfigurationTests
{
    [Fact]
    public async Task MissingConfigurationCreatesDefaultRootAndConfig()
    {
        using var workspace = new TestWorkspace();
        var configurationDirectory = Path.Combine(
            workspace.Root,
            "local-app-data",
            "TavernDesk");
        var defaultRoot = Path.Combine(
            workspace.Root,
            "Documents",
            "TavernDesk");
        var configuration = new AppDataConfiguration(
            configurationDirectory,
            defaultRoot);

        await configuration.EnsureStartupConfigurationAsync();

        Assert.True(Directory.Exists(defaultRoot));
        Assert.True(File.Exists(configuration.ConfigurationPath));
        Assert.Equal(defaultRoot, configuration.ConfiguredDataRoot);

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(configuration.ConfigurationPath));
        Assert.Equal(
            defaultRoot,
            document.RootElement.GetProperty(
                AppDataConfiguration.DataRootPropertyName).GetString());
    }

    [Fact]
    public async Task ExistingConfigurationIsReadWithoutReplacingConfiguredRoot()
    {
        using var workspace = new TestWorkspace();
        var configurationDirectory = Path.Combine(
            workspace.Root,
            "local-app-data",
            "TavernDesk");
        var configuredRoot = Path.Combine(
            workspace.Root,
            "MigratedDocuments",
            "TavernDesk");
        var defaultRoot = Path.Combine(
            workspace.Root,
            "Documents",
            "TavernDesk");
        Directory.CreateDirectory(configurationDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(
                configurationDirectory,
                AppDataConfiguration.ConfigFileName),
            JsonSerializer.Serialize(new
            {
                dataRoot = configuredRoot
            }));

        var configuration = new AppDataConfiguration(
            configurationDirectory,
            defaultRoot);
        await configuration.EnsureStartupConfigurationAsync();

        Assert.Equal(configuredRoot, configuration.ConfiguredDataRoot);
        Assert.False(Directory.Exists(defaultRoot));
        Assert.False(Directory.Exists(configuredRoot));
    }
}
