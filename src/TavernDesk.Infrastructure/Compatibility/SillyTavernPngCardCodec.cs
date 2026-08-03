using System.Security.Cryptography;
using System.Text;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class SillyTavernPngCardCodec : ICharacterCardCodec
{
    private const int MaximumPngBytes = 256 * 1024 * 1024;
    private static readonly byte[] PlaceholderPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    public CharacterCardFormat Format => CharacterCardFormat.Png;
    public string FormatName => "PNG/APNG Character Card";

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase);

    public async Task<CharacterCardImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("角色卡文件不存在。", path);
        }

        if (file.Length > MaximumPngBytes)
        {
            throw new InvalidDataException("PNG 角色卡超过 256 MiB 安全上限。");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var container = PngCardContainer.Parse(bytes);
        var entries = container.ReadTextEntries();
        var warnings = new List<string>();
        var ccv3Entries = entries
            .Where(entry => entry.Keyword == "ccv3")
            .ToArray();
        var legacyEntries = entries
            .Where(entry => entry.Keyword == "chara")
            .ToArray();
        if (ccv3Entries.Length + legacyEntries.Length == 0)
        {
            throw new InvalidDataException("PNG 中没有 ccv3 或 chara 角色卡文本块。");
        }

        if (ccv3Entries.Length > 1 || legacyEntries.Length > 1)
        {
            warnings.Add("PNG 中存在重复角色卡文本块；已使用同类最后一个块，导出时会整理为单一 ccv3/chara。");
        }

        var selected = ccv3Entries.LastOrDefault();
        if (selected is null)
        {
            selected = legacyEntries.Last();
            warnings.Add("PNG 缺少 ccv3，已回退读取 legacy chara。");
        }

        var json = DecodeBase64Json(selected.Text);
        var parsed = CharacterCardJsonMapper.Parse(
            json,
            Path.GetFileNameWithoutExtension(path));
        warnings.AddRange(parsed.Warnings);
        if (selected.Keyword == "ccv3" && parsed.Spec != "chara_card_v3")
        {
            warnings.Add("ccv3 文本块中的 JSON 未声明 chara_card_v3；原值仍已导入和保留。");
        }

        var resources = new List<CharacterCardResourceInfo>();
        foreach (var entry in entries.Where(item =>
                     item.Keyword.StartsWith(
                         "chara-ext-asset_:",
                         StringComparison.Ordinal)))
        {
            try
            {
                var resource = Convert.FromBase64String(entry.Text.Trim());
                resources.Add(new CharacterCardResourceInfo(
                    entry.Keyword["chara-ext-asset_:".Length..],
                    resource.LongLength,
                    Convert.ToHexString(SHA256.HashData(resource)).ToLowerInvariant(),
                    MediaTypeForPath(entry.Keyword)));
            }
            catch (FormatException)
            {
                warnings.Add($"PNG 扩展资源 {entry.Keyword} 无法解码；原 chunk 仍保留在源文件中。");
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
            PreviewImage: null,
            PreviewExtension: "png");
    }

    public async Task<CharacterCardExportResult> ExportAsync(
        Character character,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var basePngPath = FindBasePng(character);
        var baseBytes = basePngPath is null
            ? PlaceholderPng
            : await File.ReadAllBytesAsync(basePngPath, cancellationToken);
        var container = PngCardContainer.Parse(baseBytes);
        var v3 = CharacterCardJsonMapper.Serialize(
            CharacterCardJsonMapper.ToV3(character));
        var v2 = CharacterCardJsonMapper.Serialize(
            CharacterCardJsonMapper.ToV2Backfill(character));
        var output = container.RewriteCharacterCard(
            Convert.ToBase64String(Encoding.UTF8.GetBytes(v3)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(v2)));
        await File.WriteAllBytesAsync(destinationPath, output, cancellationToken);

        var sourceReport = CharacterCardReportSerializer.TryRead(
            character.ImportReportJson);
        var existingResources = container.ReadTextEntries().Count(entry =>
            entry.Keyword.StartsWith("chara-ext-asset_:", StringComparison.Ordinal));
        var warnings = new List<string>();
        if (basePngPath is null)
        {
            warnings.Add("角色没有可用 PNG 封面，已使用 1×1 占位图；角色卡 JSON 已正常嵌入。");
        }

        if (character.SourceCardFormat == CharacterCardFormat.Charx
            && sourceReport is { Resources.Count: > 0 })
        {
            warnings.Add("PNG 导出不会复制 CHARX 的全部二进制资源；原 CHARX 仍保存在本地角色源文件中。");
        }

        return new CharacterCardExportResult(
            Format,
            destinationPath,
            existingResources,
            warnings);
    }

    private static string DecodeBase64Json(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("PNG 角色卡文本块不是有效 Base64。", exception);
        }
    }

    private static string? FindBasePng(Character character)
    {
        foreach (var path in new[] { character.SourceCardPath, character.AvatarPath })
        {
            if (!string.IsNullOrWhiteSpace(path)
                && string.Equals(
                    Path.GetExtension(path),
                    ".png",
                    StringComparison.OrdinalIgnoreCase)
                && File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

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
            _ => "application/octet-stream"
        };
}
