namespace TavernDesk.Core.Models;

public sealed record ChatJsonlImportResult(
    Conversation Conversation,
    string CharacterName,
    bool CreatedPlaceholderCharacter,
    int MessageCount,
    int CandidateCount,
    IReadOnlyList<string> Warnings);

public sealed record ChatJsonlExportResult(
    string ConversationId,
    string DestinationPath,
    int MessageCount,
    int CandidateCount,
    IReadOnlyList<string> Warnings);
