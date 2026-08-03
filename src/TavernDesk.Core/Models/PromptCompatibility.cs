namespace TavernDesk.Core.Models;

public enum WorldbookInsertionPosition
{
    BeforeCharacter,
    AfterCharacter,
    HistoryDepth
}

public enum WorldbookSelectiveLogic
{
    AndAny,
    AndAll,
    NotAny,
    NotAll
}

public sealed record WorldbookScanRequest(
    string ConversationId,
    string RawCardJson,
    IReadOnlyList<ChatMessage> Messages,
    string UserInput,
    IReadOnlyDictionary<string, string> MacroVariables,
    int DefaultScanDepth = 5,
    int MaximumRecursionSteps = 4,
    int MaximumContentCharacters = 12000);

public sealed record WorldbookMatch(
    string Id,
    string Title,
    string Content,
    WorldbookInsertionPosition Position,
    int Depth,
    string ProviderRole,
    int InsertionOrder,
    int RecursionLevel);

public sealed record WorldbookScanResult(
    IReadOnlyList<WorldbookMatch> Matches,
    IReadOnlyList<string> Diagnostics);
