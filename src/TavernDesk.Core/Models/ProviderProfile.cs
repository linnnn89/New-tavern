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

public static class ProviderProfileIds
{
    public const string GrokCli = "builtin-grok-cli";
    public const string OpenRouter = "builtin-openrouter";
    public const string SiliconFlow = "builtin-siliconflow";
    public const string DeepSeek = "builtin-deepseek";
    public const string LmStudio = "builtin-lm-studio";

    public static bool IsSupported(string id) =>
        id is GrokCli or OpenRouter or SiliconFlow or DeepSeek or LmStudio;
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
