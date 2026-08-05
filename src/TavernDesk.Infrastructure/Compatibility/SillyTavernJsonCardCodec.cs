using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class SillyTavernJsonCardCodec : ICharacterCardCodec
{
    private const int MaximumJsonBytes = 16 * 1024 * 1024;
    private const int MaximumPreviewBytes = 32 * 1024 * 1024;

    public CharacterCardFormat Format => CharacterCardFormat.Json;
    public string FormatName => "SillyTavern JSON";

    public bool CanRead(string path) =>
        string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    public async Task<CharacterCardImportResult> ImportAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("角色卡文件不存在。", path);
        }

        if (file.Length > MaximumJsonBytes)
        {
            throw new InvalidDataException("角色卡 JSON 超过 16 MiB 安全上限。");
        }

        var json = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var parsed = CharacterCardJsonMapper.Parse(
            json,
            Path.GetFileNameWithoutExtension(path));
        var warnings = parsed.Warnings.ToList();
        var resources = ReadDataUriResources(
            parsed.Data,
            warnings,
            out var preview,
            out var previewExtension);

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
        var root = CharacterCardJsonMapper.UpdatePreservingShape(character);
        var json = CharacterCardJsonMapper.Serialize(root, indented: true);
        await File.WriteAllTextAsync(
            destinationPath,
            json,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var resourceCount = CharacterCardReportSerializer
            .TryRead(character.ImportReportJson)?.Resources.Count ?? 0;
        IReadOnlyList<string> warnings = resourceCount > 0
            ? new[]
            {
                "JSON 容器不会携带 CHARX/PNG 二进制资源；资源仍保存在本地原始源文件中。"
            }
            : Array.Empty<string>();
        return new CharacterCardExportResult(
            Format,
            destinationPath,
            PreservedResourceCount: 0,
            warnings);
    }

    private static IReadOnlyList<CharacterCardResourceInfo> ReadDataUriResources(
        JsonObject data,
        ICollection<string> warnings,
        out byte[]? preview,
        out string? previewExtension)
    {
        preview = null;
        previewExtension = null;
        var result = new List<CharacterCardResourceInfo>();
        if (data["assets"] is not JsonArray assets)
        {
            return result;
        }

        foreach (var asset in assets.OfType<JsonObject>())
        {
            var uri = ReadString(asset, "uri");
            if (uri is null || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (uri?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                    || uri?.StartsWith("https://", StringComparison.OrdinalIgnoreCase) == true)
                {
                    warnings.Add("远程角色资源仅保留 URI，本次导入不会联网下载。");
                }

                continue;
            }

            if (!TryDecodeDataUri(uri, out var mediaType, out var bytes))
            {
                warnings.Add("检测到无法解码的数据 URI 资源；原始 URI 仍保留在角色卡 JSON。");
                continue;
            }

            if (bytes.Length > MaximumPreviewBytes)
            {
                warnings.Add("数据 URI 资源超过 32 MiB，不生成本地预览；原始数据仍保留。");
                continue;
            }

            var type = ReadString(asset, "type") ?? "other";
            var name = ReadString(asset, "name") ?? "asset";
            var extension = NormalizeExtension(
                ReadString(asset, "ext"),
                mediaType);
            result.Add(new CharacterCardResourceInfo(
                $"data-uri/{type}/{name}.{extension}",
                bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                mediaType));

            if (preview is null
                && string.Equals(type, "icon", StringComparison.OrdinalIgnoreCase))
            {
                preview = bytes;
                previewExtension = extension;
            }
        }

        return result;
    }

    private static bool TryDecodeDataUri(
        string uri,
        out string mediaType,
        out byte[] bytes)
    {
        mediaType = "application/octet-stream";
        bytes = [];
        var comma = uri.IndexOf(',');
        if (comma <= 5)
        {
            return false;
        }

        var metadata = uri[5..comma];
        var parts = metadata.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Contains('/'))
        {
            mediaType = parts[0].ToLowerInvariant();
        }

        if (!parts.Any(part => string.Equals(
                part,
                "base64",
                StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(uri[(comma + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeExtension(string? extension, string mediaType)
    {
        var normalized = extension?.Trim().TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized)
            && normalized.All(character => char.IsAsciiLetterOrDigit(character)))
        {
            return normalized;
        }

        return mediaType switch
        {
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/bmp" => "bmp",
            "image/webp" => "webp",
            _ => "png"
        };
    }

    private static string? ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var result)
            ? result
            : null;
}
