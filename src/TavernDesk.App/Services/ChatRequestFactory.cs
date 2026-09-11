using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.App.Localization;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.Services;

/// <summary>Maps captured chat state to context/provider requests without reading mutable view state.</summary>
internal static class ChatRequestFactory
{
    public static ContextAssemblyRequest CreateContextRequest(
        string conversationId, string userInput, long? historyBeforeSequenceNo,
        ContextInputSnapshot snapshot, GroupMemoryScopeMask invalidScopes,
        bool hasUnsavedGroupMemory, string? continuationInstruction,
        bool allowRemoteSemanticRetrieval)
    {
        var group = string.Equals(
            snapshot.Group?.Settings.ConversationId,
            conversationId,
            StringComparison.Ordinal)
            ? snapshot.Group
            : null;
        var historicalRegeneration = historyBeforeSequenceNo.HasValue;
        return new ContextAssemblyRequest(
            conversationId,
            userInput,
            snapshot.ContextLimit,
            snapshot.ReservedOutputTokens,
            MemoryOverride: historicalRegeneration
                ? string.Empty
                : snapshot.MemoryBody,
            PersonaName: snapshot.PersonaName,
            PersonaDescription: snapshot.PersonaDescription,
            GlobalPreset: snapshot.GlobalPreset,
            HistoryBeforeSequenceNo: historyBeforeSequenceNo,
            SpeakerCharacterId: snapshot.SpeakerCharacterId,
            GroupMemberIds: group?.Members
                .Where(member => member.IsEnabled
                                 || member.CharacterId == snapshot.SpeakerCharacterId)
                .Select(member => member.CharacterId)
                .ToArray(),
            GroupMemoryOverride: group is null
                ? null
                : historicalRegeneration
                  || invalidScopes.HasFlag(GroupMemoryScopeMask.Shared)
                  || hasUnsavedGroupMemory
                    ? string.Empty
                    : snapshot.MemoryBody,
            GroupMemberMemoryEnabled:
                !historicalRegeneration
                && !invalidScopes.HasFlag(GroupMemoryScopeMask.Members)
                && (group?.Settings.MemberMemoryEnabled ?? false),
            GroupSystemPrompt: group?.Settings.GroupSystemPrompt,
            GroupBatonInstruction: BuildGroupBatonInstruction(snapshot),
            Retrieval: snapshot.Retrieval,
            ModelId: snapshot.ModelId,
            ContinuationInstruction: continuationInstruction,
            AllowRemoteSemanticRetrieval: allowRemoteSemanticRetrieval);
    }

    private static string? BuildGroupBatonInstruction(ContextInputSnapshot snapshot)
    {
        if (snapshot.Group is null || snapshot.SpeakerCharacterId is null)
        {
            return null;
        }

        var speaker = snapshot.Group.MemberNames.GetValueOrDefault(
            snapshot.SpeakerCharacterId,
            snapshot.SpeakerCharacterId);
        var enabledNames = string.Join(
            "、",
            snapshot.Group.Members
                .Where(member => member.IsEnabled)
                .Select(member => snapshot.Group.MemberNames.GetValueOrDefault(
                    member.CharacterId,
                    member.CharacterId)));
        return LanguageRuntime.Format(
            "Chat.Group.BatonInstructionFormat",
            speaker,
            enabledNames);
    }

    public static ModelExecutionRequest CreateExecutionRequest(
        ModelFunctionAssignment assignment,
        ContextAssemblyResult context,
        string conversationId) =>
        new(
            assignment.ProviderId,
            assignment.ModelId,
            context.Segments
                .Select(segment => new ProviderChatMessage(
                    segment.ProviderRole,
                    segment.ProviderContent ?? segment.Content))
                .ToArray(),
            assignment.MaxOutputTokens,
            assignment.Temperature,
            assignment.TopP,
            assignment.ReasoningEnabled,
            SessionId: $"chat:{conversationId}");

    public static string RenderApiRequestPreview(ContextAssemblyResult context)
    {
        var payload = new
        {
            messages = context.Segments.Select(segment => new
            {
                role = segment.ProviderRole,
                source = segment.Title,
                content = segment.ProviderContent ?? segment.Content
            }),
            token_estimate = new
            {
                input = context.Estimate.InputTokens,
                reserved_output = context.Estimate.ReservedOutputTokens,
                context_limit = context.Estimate.ContextLimit,
                is_exact = context.Estimate.IsExact
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
    }

}

internal sealed record ContextInputSnapshot(
    int ContextLimit,
    int ReservedOutputTokens,
    string? ModelId,
    string MemoryBody,
    string PersonaName,
    string PersonaDescription,
    string GlobalPreset,
    string? SpeakerCharacterId,
    GroupContextSnapshot? Group,
    RetrievalContextOptions? Retrieval);

internal sealed record GroupContextSnapshot(
    GroupChatSettings Settings,
    IReadOnlyList<GroupChatMember> Members,
    IReadOnlyDictionary<string, string> MemberNames,
    string? ManualSpeakerId);
