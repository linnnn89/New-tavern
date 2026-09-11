using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Storage;
using TavernDesk.Infrastructure.Worldbooks;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class CharacterCardLibrary : ICharacterCardLibrary
{
    private readonly AppDataPaths _paths;
    private readonly ICharacterRepository _characters;
    private readonly WorldbookService _worldbooks;
    private readonly SqliteCharacterImportStore _imports;

    public CharacterCardLibrary(
        AppDataPaths paths,
        ICharacterRepository characters,
        IReadOnlyList<ICharacterCardCodec> codecs,
        WorldbookService worldbooks,
        SqliteCharacterImportStore imports)
    {
        _paths = paths;
        _characters = characters;
        _worldbooks = worldbooks;
        _imports = imports;
        Codecs = codecs;
    }

    public IReadOnlyList<ICharacterCardCodec> Codecs { get; }

    public async Task<CharacterCardImportResult> ImportAsync(
        string sourcePath, CancellationToken cancellationToken = default)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var codec = Codecs.FirstOrDefault(candidate => candidate.CanRead(sourceFullPath))
            ?? throw new NotSupportedException($"不支持此角色卡格式：{Path.GetExtension(sourceFullPath)}");
        var extension = Path.GetExtension(sourceFullPath).ToLowerInvariant();
        var stagingDirectory = GetCharacterDirectory(".import-" + Guid.NewGuid().ToString("N"));
        string? committedDirectory = null;
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            // Decode and preserve the same bounded snapshot, even if the source
            // is replaced while the import is running.
            var snapshotPath = Path.Combine(stagingDirectory, Path.GetFileName(sourceFullPath));
            await using (var input = ImportFileReader.Open(sourceFullPath, ImportFileReader.MaximumBytesFor(extension)))
            await using (var output = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 64 * 1024, useAsync: true))
                await ImportFileReader.CopyAsync(input, output, ImportFileReader.MaximumBytesFor(extension), cancellationToken);
            var decoded = await codec.ImportAsync(snapshotPath, cancellationToken);
            var character = decoded.Character;
            var targetDirectory = GetCharacterDirectory(character.Id);
            if (Directory.Exists(targetDirectory))
                throw new IOException($"角色资源目录已存在：{character.Id}");
            var sourceCopyPath = Path.Combine(stagingDirectory, $"source{extension}");
            if (!string.Equals(snapshotPath, sourceCopyPath, StringComparison.OrdinalIgnoreCase))
                File.Move(snapshotPath, sourceCopyPath);
            character.SourceCardFormat = codec.Format;
            character.SourceCardPath = Path.Combine(targetDirectory, $"source{extension}");
            if (codec.Format == CharacterCardFormat.Png)
                character.AvatarPath = character.SourceCardPath;
            else if (decoded.PreviewImage is { Length: > 0 }
                     && TryNormalizePreviewExtension(decoded.PreviewExtension, out var previewExtension))
            {
                var coverName = $"cover.{previewExtension}";
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, coverName), decoded.PreviewImage, cancellationToken);
                character.AvatarPath = Path.Combine(targetDirectory, coverName);
            }

            var report = decoded.Report with { SourcePreserved = true };
            var embedded = WorldbookJsonParser.Parse(character.RawCardJson, character.Name);
            WorldbookImportResult? prepared = null;
            if (embedded.FoundBook)
            {
                prepared = await _worldbooks.PrepareImportAsync(sourceCopyPath, cancellationToken);
                prepared.Worldbook.SourcePath = character.SourceCardPath;
            }
            else if (character.RawCardJson.Contains("\"character_book\"", StringComparison.Ordinal))
                report = report with { Warnings = report.Warnings.Concat(embedded.Diagnostics).ToArray() };

            cancellationToken.ThrowIfCancellationRequested();
            // A crash here may leave an unreferenced asset folder, but committed
            // rows can never reference a file that has not been published yet.
            Directory.Move(stagingDirectory, targetDirectory);
            committedDirectory = targetDirectory;
            character.UpdatedAt = DateTimeOffset.Now;
            report = await _imports.SaveAsync(character, report, prepared, cancellationToken);
            return decoded with { Character = character, Report = report };
        }
        catch
        {
            DeleteNewCharacterDirectory(stagingDirectory);
            if (committedDirectory is not null) DeleteNewCharacterDirectory(committedDirectory);
            throw;
        }
    }

    public Task<CharacterCardExportResult> ExportAsync(
        Character character,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destinationFullPath = Path.GetFullPath(destinationPath);
        var codec = Codecs.FirstOrDefault(candidate => candidate.CanRead(destinationFullPath))
                    ?? throw new NotSupportedException(
                        $"不支持此导出格式：{Path.GetExtension(destinationFullPath)}");
        return codec.ExportAsync(character, destinationFullPath, cancellationToken);
    }

    public async Task<string> ReplaceAvatarAsync(
        Character character,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var extension = Path.GetExtension(sourceFullPath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif"))
        {
            throw new NotSupportedException($"不支持此图片格式：{extension}");
        }

        var targetDirectory = GetCharacterDirectory(character.Id);
        Directory.CreateDirectory(targetDirectory);
        var destinationPath = Path.Combine(
            targetDirectory,
            $"avatar-custom-{Guid.NewGuid():N}{extension}");
        var previousAvatarPath = character.AvatarPath;
        var previousUpdatedAt = character.UpdatedAt;

        try
        {
            await CopyFileAsync(sourceFullPath, destinationPath, cancellationToken);
            character.AvatarPath = destinationPath;
            character.UpdatedAt = DateTimeOffset.Now;
            await _characters.UpsertAsync(character, cancellationToken);
        }
        catch
        {
            character.AvatarPath = previousAvatarPath;
            character.UpdatedAt = previousUpdatedAt;
            if (File.Exists(destinationPath))
            {
                File.Delete(destinationPath);
            }

            throw;
        }

        TryDeletePreviousCustomAvatar(previousAvatarPath, targetDirectory);
        return destinationPath;
    }

    private string GetCharacterDirectory(string characterId)
    {
        var root = Path.GetFullPath(_paths.CharacterCardsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, characterId));
        if (!candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("角色资源目录越过了角色卡库根目录。");
        }

        return candidate;
    }

    private void DeleteNewCharacterDirectory(string targetDirectory)
    {
        var verified = GetCharacterDirectory(Path.GetFileName(targetDirectory));
        if (!string.Equals(
                verified,
                Path.GetFullPath(targetDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("拒绝清理未经验证的角色资源目录。");
        }

        if (Directory.Exists(verified))
        {
            Directory.Delete(verified, recursive: true);
        }
    }

    private static void TryDeletePreviousCustomAvatar(
        string previousAvatarPath,
        string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(previousAvatarPath))
        {
            return;
        }

        try
        {
            var previousFullPath = Path.GetFullPath(previousAvatarPath);
            var verifiedDirectory = Path.GetFullPath(targetDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(
                    Path.GetDirectoryName(previousFullPath),
                    verifiedDirectory,
                    StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(previousFullPath).StartsWith(
                    "avatar-custom-",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(previousFullPath))
            {
                File.Delete(previousFullPath);
            }
        }
        catch (IOException)
        {
            // The new avatar is already durable. A locked previous image can remain
            // as a harmless local work-copy instead of rolling back the user change.
        }
        catch (UnauthorizedAccessException)
        {
            // Same recovery rule as above: keep the selected image active.
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            useAsync: true);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static bool TryNormalizePreviewExtension(
        string? extension,
        out string normalized)
    {
        normalized = extension?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        return normalized is "png" or "jpg" or "jpeg" or "bmp" or "gif";
    }
}
