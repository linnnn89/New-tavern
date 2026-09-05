using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class SillyTavernCharxCardCodec : ICharacterCardCodec
{
    private const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumEntryBytes = 128L * 1024 * 1024;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumEntryCount = 2048;
    private const int MaximumCardJsonBytes = 16 * 1024 * 1024;
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;

    public CharacterCardFormat Format => CharacterCardFormat.Charx;
    public string FormatName => "CHARX Character Card";

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".charx", StringComparison.OrdinalIgnoreCase);

    public async Task<CharacterCardImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("角色卡文件不存在。", path);
        }

        if (file.Length > MaximumArchiveBytes)
        {
            throw new InvalidDataException("CHARX 超过 256 MiB 安全上限。");
        }

        await using var fileStream = File.OpenRead(path);
        using var archive = new ZipArchive(
            fileStream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);
        // Validate the whole central directory before reading even card.json.
        // This makes path, expansion, duplicate, and entry-count limits apply to
        // every archive we accept or may later preserve during re-export.
        var entries = ValidateEntries(archive);
        var cardEntry = entries.SingleOrDefault(entry => entry.Path == "card.json")
                        ?? throw new InvalidDataException("CHARX 根目录缺少 card.json。");
        if (cardEntry.Length > MaximumCardJsonBytes)
        {
            throw new InvalidDataException("CHARX card.json 超过 16 MiB 安全上限。");
        }

        string json;
        await using (var cardStream = cardEntry.Entry.Open())
        using (var reader = new StreamReader(
                   cardStream,
                   new UTF8Encoding(
                       encoderShouldEmitUTF8Identifier: false,
                       throwOnInvalidBytes: true),
                   detectEncodingFromByteOrderMarks: true,
                   leaveOpen: false))
        {
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        var parsed = CharacterCardJsonMapper.Parse(
            json,
            Path.GetFileNameWithoutExtension(path));
        var warnings = parsed.Warnings.ToList();
        if (parsed.Spec != "chara_card_v3")
        {
            warnings.Add("CHARX card.json 未声明 chara_card_v3；已兼容导入，导出 CHARX 时会规范为 V3。");
        }

        var resources = new List<CharacterCardResourceInfo>();
        foreach (var entry in entries.Where(item => item.Path != "card.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = entry.Entry.Open();
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            resources.Add(new CharacterCardResourceInfo(
                entry.Path,
                entry.Length,
                Convert.ToHexString(hash).ToLowerInvariant(),
                MediaTypeForPath(entry.Path)));
        }

        byte[]? preview = null;
        string? previewExtension = null;
        var iconPath = FindMainEmbeddedIconPath(parsed.Data);
        if (iconPath is not null)
        {
            var icon = entries.SingleOrDefault(entry => entry.Path == iconPath);
            if (icon is null)
            {
                warnings.Add($"角色主封面资源不存在：{iconPath}");
            }
            else if (icon.Length > MaximumPreviewBytes)
            {
                warnings.Add("角色主封面超过 32 MiB，资源仍保留但不生成书架预览。");
            }
            else if (IsWpfPreviewExtension(Path.GetExtension(icon.Path)))
            {
                await using var iconStream = icon.Entry.Open();
                using var buffer = new MemoryStream((int)icon.Length);
                await iconStream.CopyToAsync(buffer, cancellationToken);
                preview = buffer.ToArray();
                previewExtension = Path.GetExtension(icon.Path)
                    .TrimStart('.')
                    .ToLowerInvariant();
            }
            else
            {
                warnings.Add($"角色主封面格式 {Path.GetExtension(icon.Path)} 暂不能由 WPF 直接预览；资源仍完整保留。");
            }
        }

        parsed.Character.RawCardJson = parsed.Root.ToJsonString();
        var report = new CharacterCardImportReport(
            Format,
            FormatName,
            parsed.Spec,
            parsed.SpecVersion,
            Path.GetFileName(path),
            SourcePreserved: false,
            parsed.UnknownFieldPaths,
            resources,
            warnings,
            DateTimeOffset.Now);
        return new CharacterCardImportResult(
            parsed.Character,
            report,
            preview,
            previewExtension);
    }

    public async Task<CharacterCardExportResult> ExportAsync(
        Character character,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destinationFullPath = Path.GetFullPath(destinationPath);
        var destinationDirectory = Path.GetDirectoryName(destinationFullPath)
                                   ?? throw new InvalidOperationException("导出路径没有父目录。");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryPath = Path.Combine(
            destinationDirectory,
            $".{Path.GetFileName(destinationFullPath)}.{Guid.NewGuid():N}.tmp");
        var copiedResources = 0;
        var warnings = new List<string>();
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             81920,
                             useAsync: true))
            using (var destinationArchive = new ZipArchive(
                       output,
                       ZipArchiveMode.Create,
                       leaveOpen: true,
                       entryNameEncoding: Encoding.UTF8))
            {
                JsonArray? assetsOverride = null;
                if (character.SourceCardFormat == CharacterCardFormat.Charx
                    && File.Exists(character.SourceCardPath))
                {
                    // Preserve extension resources from the original archive, but
                    // regenerate card.json below from the current working copy so
                    // stale metadata cannot override the user's edits.
                    copiedResources = await CopyPreservedEntriesAsync(
                        character.SourceCardPath,
                        destinationArchive,
                        cancellationToken);
                }
                else
                {
                    var cover = FindLocalCover(character);
                    if (cover is not null)
                    {
                        var extension = Path.GetExtension(cover)
                            .TrimStart('.')
                            .ToLowerInvariant();
                        var entryPath = $"assets/icon/images/main.{extension}";
                        await CopyFileToEntryAsync(
                            cover,
                            destinationArchive,
                            entryPath,
                            cancellationToken);
                        copiedResources = 1;
                        assetsOverride = BuildAssetsWithMainIcon(
                            character,
                            entryPath,
                            extension);
                    }
                    else
                    {
                        warnings.Add("角色没有本地封面；CHARX 已导出 card.json，但没有内嵌 icon 资源。");
                    }
                }

                var cardJson = CharacterCardJsonMapper.Serialize(
                    CharacterCardJsonMapper.ToV3(character, assetsOverride),
                    indented: true);
                var cardEntry = destinationArchive.CreateEntry(
                    "card.json",
                    CompressionLevel.Optimal);
                await using var cardStream = cardEntry.Open();
                await using var writer = new StreamWriter(
                    cardStream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: false);
                await writer.WriteAsync(cardJson.AsMemory(), cancellationToken);
            }

            // Replace the destination only after the ZIP has closed successfully;
            // an interrupted export therefore cannot truncate an existing card.
            File.Move(temporaryPath, destinationFullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }

        return new CharacterCardExportResult(
            Format,
            destinationFullPath,
            copiedResources,
            warnings);
    }

    private static IReadOnlyList<ValidatedZipEntry> ValidateEntries(ZipArchive archive)
    {
        // Strict paths are required even though imports currently stream entries:
        // accepted paths are also copied into future exports and may eventually be
        // consumed by extractors with different traversal behavior.
        if (archive.Entries.Count > MaximumEntryCount)
        {
            throw new InvalidDataException($"CHARX 文件条目超过 {MaximumEntryCount} 个安全上限。");
        }

        var result = new List<ValidatedZipEntry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalLength = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var path = ValidateEntryPath(entry.FullName);
            if (!paths.Add(path))
            {
                throw new InvalidDataException($"CHARX 存在重复路径：{path}");
            }

            if (entry.Length > MaximumEntryBytes)
            {
                throw new InvalidDataException($"CHARX 条目超过 128 MiB：{path}");
            }

            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaximumExpandedBytes)
            {
                throw new InvalidDataException("CHARX 解压后总大小超过 512 MiB 安全上限。");
            }

            if (entry.Length > 16 * 1024 * 1024
                && entry.CompressedLength > 0
                && entry.Length / Math.Max(1, entry.CompressedLength) > 1000)
            {
                throw new InvalidDataException($"CHARX 条目压缩比异常：{path}");
            }

            result.Add(new ValidatedZipEntry(entry, path, entry.Length));
        }

        if (result.Count(entry => entry.Path == "card.json") > 1)
        {
            throw new InvalidDataException("CHARX 根目录存在多个 card.json。");
        }

        return result;
    }

    private static string ValidateEntryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith('/')
            || path.StartsWith('\\')
            || path.Contains('\\')
            || path.Contains(':'))
        {
            throw new InvalidDataException($"CHARX 条目路径无效：{path}");
        }

        var parts = path.Split('/');
        if (parts.Any(part => part is "" or "." or ".."))
        {
            throw new InvalidDataException($"CHARX 条目路径可能越界：{path}");
        }

        return path;
    }

    private static string? FindMainEmbeddedIconPath(JsonObject data)
    {
        if (data["assets"] is not JsonArray assets)
        {
            return null;
        }

        var icons = assets
            .OfType<JsonObject>()
            .Where(asset => string.Equals(
                ReadString(asset, "type"),
                "icon",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var selected = icons.FirstOrDefault(asset => string.Equals(
                           ReadString(asset, "name"),
                           "main",
                           StringComparison.OrdinalIgnoreCase))
                       ?? icons.FirstOrDefault();
        var uri = selected is null ? null : ReadString(selected, "uri");
        // "embeded" is the spelling defined by the character-card ecosystem;
        // correcting it here would break compatibility with existing CHARX files.
        if (uri?.StartsWith("embeded://", StringComparison.Ordinal) != true)
        {
            return null;
        }

        return ValidateEntryPath(uri["embeded://".Length..]);
    }

    private static async Task<int> CopyPreservedEntriesAsync(
        string sourcePath,
        ZipArchive destination,
        CancellationToken cancellationToken)
    {
        await using var sourceStream = File.OpenRead(sourcePath);
        using var source = new ZipArchive(
            sourceStream,
            ZipArchiveMode.Read,
            leaveOpen: false,
            entryNameEncoding: Encoding.UTF8);
        var entries = ValidateEntries(source);
        var copied = 0;
        foreach (var item in entries.Where(entry => entry.Path != "card.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = destination.CreateEntry(item.Path, CompressionLevel.Optimal);
            target.LastWriteTime = item.Entry.LastWriteTime;
            await using var input = item.Entry.Open();
            await using var output = target.Open();
            await input.CopyToAsync(output, cancellationToken);
            copied++;
        }

        return copied;
    }

    private static async Task CopyFileToEntryAsync(
        string sourcePath,
        ZipArchive destination,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var target = destination.CreateEntry(entryPath, CompressionLevel.Optimal);
        await using var input = File.OpenRead(sourcePath);
        await using var output = target.Open();
        await input.CopyToAsync(output, cancellationToken);
    }

    private static JsonArray BuildAssetsWithMainIcon(
        Character character,
        string entryPath,
        string extension)
    {
        var root = CharacterCardJsonMapper.ToV3(character);
        var data = (JsonObject)root["data"]!;
        var assets = data["assets"] is JsonArray existing
            ? existing.DeepClone() as JsonArray ?? new JsonArray()
            : new JsonArray();
        for (var index = assets.Count - 1; index >= 0; index--)
        {
            if (assets[index] is JsonObject asset
                && string.Equals(
                    ReadString(asset, "type"),
                    "icon",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    ReadString(asset, "name"),
                    "main",
                    StringComparison.OrdinalIgnoreCase))
            {
                assets.RemoveAt(index);
            }
        }

        assets.Insert(0, new JsonObject
        {
            ["type"] = "icon",
            ["uri"] = $"embeded://{entryPath}",
            ["name"] = "main",
            ["ext"] = extension
        });
        return assets;
    }

    private static string? FindLocalCover(Character character)
    {
        foreach (var path in new[] { character.AvatarPath, character.SourceCardPath })
        {
            if (!string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && IsWpfPreviewExtension(Path.GetExtension(path)))
            {
                return path;
            }
        }

        return null;
    }

    private static bool IsWpfPreviewExtension(string extension) =>
        extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";

    private static string? ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : null;

    private static string MediaTypeForPath(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".json" => "application/json",
            _ => "application/octet-stream"
        };

    private sealed record ValidatedZipEntry(
        ZipArchiveEntry Entry,
        string Path,
        long Length);
}
