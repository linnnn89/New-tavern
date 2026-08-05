namespace TavernDesk.Core.Models;

public enum ModelFunctionKind
{
    Chat = 0,
    MemoryUpdate = 1,
    MemoryCompression = 2,
    GroupChat = 3,
    GroupMemoryMerge = 4,
    Embedding = 5
}

public enum ModelCatalogKind
{
    // Catalog/source metadata only. It never selects an API endpoint.
    Chat = 0,
    Embedding = 1,
    Custom = 2
}

public sealed class ProviderModel
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public string DisplayName { get; set; } = string.Empty;
    public int ContextLimit { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 4096;
    public bool SupportsStreaming { get; set; } = true;
    public ModelCatalogKind ModelKind { get; set; } = ModelCatalogKind.Chat;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string ModelKindText => ModelKind switch
    {
        ModelCatalogKind.Embedding => "Embedding 目录",
        ModelCatalogKind.Custom => "自定义",
        _ => "模型目录"
    };
}

public sealed class ModelFunctionAssignment
{
    public ModelFunctionKind FunctionKind { get; init; }
    public string ProviderId { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int ContextLimit { get; set; } = 32768;
    public int MaxOutputTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.8;
    public double TopP { get; set; } = 1;
    public bool ReasoningEnabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public static class ModelFeatureSupport
{
    private static readonly char[] ModelIdSeparators =
        ['/', '\\', ':', '.', '_', '-'];

    public static bool SupportsOpenRouterDeepSeekReasoning(
        ProviderProfile? provider,
        string? modelId)
    {
        if (!IsOpenRouter(provider)
            || string.IsNullOrWhiteSpace(modelId))
        {
            return false;
        }

        return modelId.Split(
                ModelIdSeparators,
                StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
            .Any(part => string.Equals(
                part,
                "deepseek",
                StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsOpenRouter(ProviderProfile? provider) =>
        provider is not null
        && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && string.Equals(
            uri.Host,
            "openrouter.ai",
            StringComparison.OrdinalIgnoreCase);
}

public sealed record PersonaProfile(
    string Name,
    string Description,
    string GlobalPreset);

public enum ChatSendMode
{
    SendAndGenerate,
    SaveOnly
}

public enum ChatDisplayMode
{
    Bubble,
    Novel
}
