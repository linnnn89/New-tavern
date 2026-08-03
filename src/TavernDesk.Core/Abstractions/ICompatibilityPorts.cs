using TavernDesk.Core.Models;

namespace TavernDesk.Core.Abstractions;

public sealed record CharacterCardResourceInfo(
    string RelativePath,
    long Size,
    string Sha256,
    string MediaType);

public sealed record CharacterCardImportReport(
    CharacterCardFormat Format,
    string FormatName,
    string Spec,
    string SpecVersion,
    string SourceFileName,
    bool SourcePreserved,
    IReadOnlyList<string> UnknownFieldPaths,
    IReadOnlyList<CharacterCardResourceInfo> Resources,
    IReadOnlyList<string> Warnings,
    DateTimeOffset ImportedAt);

public sealed record CharacterCardImportResult(
    Character Character,
    CharacterCardImportReport Report,
    byte[]? PreviewImage,
    string? PreviewExtension);

public sealed record CharacterCardExportResult(
    CharacterCardFormat Format,
    string DestinationPath,
    int PreservedResourceCount,
    IReadOnlyList<string> Warnings);

public interface ICharacterCardCodec
{
    CharacterCardFormat Format { get; }
    string FormatName { get; }
    bool CanRead(string path);
    Task<CharacterCardImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<CharacterCardExportResult> ExportAsync(
        Character character,
        string destinationPath,
        CancellationToken cancellationToken = default);
}

public interface ICharacterCardLibrary
{
    IReadOnlyList<ICharacterCardCodec> Codecs { get; }

    Task<CharacterCardImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<CharacterCardExportResult> ExportAsync(
        Character character,
        string destinationPath,
        CancellationToken cancellationToken = default);

    Task<string> ReplaceAvatarAsync(
        Character character,
        string sourcePath,
        CancellationToken cancellationToken = default);
}

public interface IWorldbookEngine
{
    Task<WorldbookScanResult> ScanAsync(
        WorldbookScanRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPresetResolver
{
    Task<ResolvedPreset> ResolveAsync(
        string? characterId,
        string? conversationId,
        CancellationToken cancellationToken = default);
}

public interface IPresetRepository
{
    Task<IReadOnlyList<PromptPreset>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<PromptPreset?> GetAsync(
        string presetId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        PromptPreset preset,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string presetId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PresetMount>> ListMountsAsync(
        PresetScopeKind scopeKind,
        string scopeId,
        CancellationToken cancellationToken = default);

    Task SetMountAsync(
        PresetMount mount,
        CancellationToken cancellationToken = default);

    Task RemoveMountAsync(
        PresetScopeKind scopeKind,
        string scopeId,
        string presetId,
        CancellationToken cancellationToken = default);
}

public interface IMacroEngine
{
    string Expand(string template, IReadOnlyDictionary<string, string> variables);
}

public interface IChatArchiveService
{
    Task<ChatJsonlImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default);

    Task<ChatJsonlExportResult> ExportAsync(
        string conversationId,
        string destinationPath,
        CancellationToken cancellationToken = default);
}
