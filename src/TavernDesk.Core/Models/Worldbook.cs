namespace TavernDesk.Core.Models;

public enum WorldbookSourceKind
{
    StandaloneJson,
    CharacterCardEmbedded
}

public enum WorldbookScopeKind
{
    Global,
    Character,
    Conversation,
    Campaign,
    CampaignRun
}

public enum WorldbookContentType
{
    Instruction,
    Lore
}

public enum WorldbookVisibility
{
    Public,
    Private,
    GmOnly
}

public sealed class Worldbook
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WorldbookSourceKind SourceKind { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceSha256 { get; set; } = string.Empty;
    public string RawJson { get; set; } = "{}";
    public bool IsEnabled { get; set; } = true;
    public int ScanDepth { get; set; } = 5;
    public int TokenBudget { get; set; } = 1200;
    public bool RecursiveScanning { get; set; } = true;
    public int Revision { get; set; } = 1;
    public int EntryCount { get; set; }
    public int IndexedChunkCount { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string SourceKindText => SourceKind switch
    {
        WorldbookSourceKind.CharacterCardEmbedded => "角色卡内置",
        _ => "独立 JSON"
    };

}

public sealed class WorldbookMount
{
    public string WorldbookId { get; init; } = string.Empty;
    public WorldbookScopeKind ScopeKind { get; init; }
    public string ScopeId { get; init; } = string.Empty;
    public int SortIndex { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int MountedRevision { get; set; } = 1;

    public string ScopeText => ScopeKind switch
    {
        WorldbookScopeKind.Character => "角色绑定",
        WorldbookScopeKind.Conversation => "当前对话",
        WorldbookScopeKind.Campaign => "跑团剧本",
        WorldbookScopeKind.CampaignRun => "跑团局",
        _ => "全局"
    };
}

public sealed class WorldbookEntry
{
    public string WorldbookId { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public IReadOnlyList<string> Keys { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> SecondaryKeys { get; set; } = Array.Empty<string>();
    public WorldbookContentType ContentType { get; set; } = WorldbookContentType.Lore;
    public WorldbookVisibility Visibility { get; set; } = WorldbookVisibility.Public;
    public bool SemanticEnabled { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public bool Constant { get; set; }
    public bool CaseSensitive { get; set; }
    public bool MatchWholeWords { get; set; }
    public WorldbookSelectiveLogic SelectiveLogic { get; set; }
    public int InsertionOrder { get; set; } = 100;
    public WorldbookInsertionPosition Position { get; set; }
    public int Depth { get; set; } = 4;
    public string ProviderRole { get; set; } = "system";
    public int Probability { get; set; } = 100;
    public bool UseProbability { get; set; } = true;
    public string InclusionGroup { get; set; } = string.Empty;
    public int GroupWeight { get; set; } = 100;
    public bool ExcludeRecursion { get; set; }
    public int OriginalIndex { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string ExtensionsJson { get; set; } = "{}";
}

public sealed class WorldbookChunk
{
    public string Id { get; init; } = string.Empty;
    public string WorldbookId { get; init; } = string.Empty;
    public string EntryId { get; init; } = string.Empty;
    public int ChunkIndex { get; init; }
    public string Content { get; init; } = string.Empty;
    public string NormalizedContent { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public int TokenCount { get; init; }
    public string SourceLocator { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed class EmbeddingProfile
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public int? Dimension { get; set; }
    public bool Normalize { get; set; } = true;
    public int BatchSize { get; set; } = 32;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WorldbookEmbedding
{
    public string ChunkId { get; init; } = string.Empty;
    public string ProfileId { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public int Dimension { get; init; }
    public byte[] VectorBlob { get; init; } = Array.Empty<byte>();
    public string ContentHash { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}
