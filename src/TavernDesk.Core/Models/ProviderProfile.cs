namespace TavernDesk.Core.Models;

public enum ProviderAdapterKind
{
    OpenAiCompatible,
    OpenAi,
    Anthropic,
    Google,
    Ollama,
    Custom,
    GrokCli
}

public sealed class ProviderProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public ProviderAdapterKind AdapterKind { get; set; } = ProviderAdapterKind.OpenAiCompatible;
    public string BaseUrl { get; set; } = string.Empty;
    public string SecretReference { get; set; } = string.Empty;
    public double RequestTimeoutSeconds { get; set; } = 300;
    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}
