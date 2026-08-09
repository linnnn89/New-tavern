using System.Text.Json.Serialization;

namespace TavernDesk.Core.Models;

public static class CampaignNarrativeProtocol
{
    public const string DeclarationHeader = "【TavernDesk叙事权限声明】";
    public const string EvaluationHeader = "【下一轮评定参考】";
}

public enum CampaignNarrativePermission
{
    Forbidden,
    PlayerIntentOnly,
    GmDiscretion
}

public sealed class CampaignNarrativeState
{
    public int SchemaVersion { get; set; } = 1;
    public int RoundNo { get; set; } = 1;
    public int TurnIndex { get; set; }
    public long LastCommittedResolutionSequence { get; set; }
    public List<string> ActiveParticipantIds { get; set; } = [];
    public List<string> KnownNpcNames { get; set; } = [];
    public List<string> RelationshipNotes { get; set; } = [];
    public List<string> ActivePlotThreads { get; set; } = [];
}

public sealed record CampaignNarrativeAuthority(
    CampaignFlowPreset Preset,
    string ModeContract,
    string DirectorInstructions,
    CampaignNarrativePermission NewNpcPermission,
    CampaignNarrativePermission RelationshipChangePermission,
    CampaignNarrativePermission IndependentPlotPermission,
    IReadOnlyList<string> ActiveIntentIds,
    IReadOnlyList<string> ActiveParticipantIds,
    IReadOnlyList<string> InactiveParticipantIds,
    CampaignNarrativeState State);

public sealed class CampaignNarrativeChange
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("source_intent_id")]
    public string? SourceIntentId { get; set; }
}

public sealed class CampaignGmNarrativeDelta
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("resolved_player_ids")]
    public List<string> ResolvedPlayerIds { get; set; } = [];

    [JsonPropertyName("introduced_npcs")]
    public List<CampaignNarrativeChange> IntroducedNpcs { get; set; } = [];

    [JsonPropertyName("relationship_changes")]
    public List<CampaignNarrativeChange> RelationshipChanges { get; set; } = [];

    [JsonPropertyName("started_plot_threads")]
    public List<CampaignNarrativeChange> StartedPlotThreads { get; set; } = [];
}

public sealed record CampaignGmValidationResult(
    bool IsValid,
    string DisplayContent,
    string StructuredDataJson,
    string? FailureReason,
    CampaignGmNarrativeDelta? Delta);
