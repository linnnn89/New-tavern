using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Compatibility;

namespace TavernDesk.Tests;

public sealed class CharacterCardCompatibilityTests
{
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Theory]
    [InlineData("card-v1.json", "$.x_legacy_unknown")]
    [InlineData("card-v2.json", "$.x_root_unknown")]
    [InlineData("card-v3.json", "$.x_root_unknown")]
    public async Task JsonRoundTripPreservesUnknownFields(
        string fixtureName,
        string unknownPath)
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var fixture = FixturePath(fixtureName);

        var imported = await services.CharacterCards.ImportAsync(fixture);
        Assert.Contains(unknownPath, imported.Report.UnknownFieldPaths);
        Assert.True(imported.Report.SourcePreserved);
        Assert.Equal(
            Sha256(await File.ReadAllBytesAsync(fixture)),
            Sha256(await File.ReadAllBytesAsync(imported.Character.SourceCardPath)));

        imported.Character.Name = $"编辑后 {fixtureName}";
        await services.Characters.UpsertAsync(imported.Character);
        var exportedPath = Path.Combine(workspace.Root, $"export-{fixtureName}");
        await services.CharacterCards.ExportAsync(imported.Character, exportedPath);

        var original = JsonNode.Parse(await File.ReadAllTextAsync(fixture))!.AsObject();
        var exported = JsonNode.Parse(await File.ReadAllTextAsync(exportedPath))!.AsObject();
        Assert.Equal(
            ReadPath(original, unknownPath)?.ToJsonString(),
            ReadPath(exported, unknownPath)?.ToJsonString());
        var data = exported["data"] as JsonObject ?? exported;
        Assert.Equal($"编辑后 {fixtureName}", data["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task EditingCompleteV3CardPreservesAllUneditedContent()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var sourcePath = Path.Combine(workspace.Root, "complete-card-v3.json");
        var template = JsonNode.Parse(
            await File.ReadAllTextAsync(FixturePath("card-v3.json")))!.AsObject();
        var data = template["data"]!.AsObject();
        data["name"] = "全字段测试角色";
        data["description"] = "这是保存前的完整角色描述。";
        data["personality"] = "沉着、好奇，并重视事实。";
        data["scenario"] = "角色正在测试酒馆中检查自己的角色卡。";
        data["first_mes"] = "欢迎来到全字段角色卡测试。";
        data["mes_example"] = "<START>\n{{char}}：这是一段示例对白。";
        data["creator_notes"] = "此卡仅用于公开导入、编辑和导出回归。";
        data["system_prompt"] = "保持角色身份，并只输出最终正文。";
        data["post_history_instructions"] = "回复前检查场景和既有事实。";
        data["alternate_greetings"] = new JsonArray(
            "这是第二条开场白。",
            "这是第三条开场白。");
        data["tags"] = new JsonArray("完整字段", "兼容测试");
        data["creator"] = "TavernDesk tests";
        data["character_version"] = "3.0-complete";
        data["character_book"] = new JsonObject
        {
            ["name"] = "完整测试世界书",
            ["description"] = "验证世界书 JSON 在无关编辑后仍保留。",
            ["entries"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 1,
                    ["keys"] = new JsonArray("测试酒馆"),
                    ["content"] = "测试酒馆位于本地数据根中。",
                    ["enabled"] = true
                }
            }
        };
        data["nickname"] = "完整测试员";
        data["creator_notes_multilingual"] = new JsonObject
        {
            ["zh-CN"] = "中文作者说明。"
        };
        data["source"] = new JsonArray("local-test-template");
        data["group_only_greetings"] = new JsonArray("这是群聊专用开场白。");
        var extensions = data["extensions"]!.AsObject();
        extensions["depth_prompt"] = new JsonObject
        {
            ["prompt"] = "在第六层提醒角色保持克制。",
            ["depth"] = 6,
            ["role"] = "assistant",
            ["x_nested_unknown"] = new JsonObject
            {
                ["keep"] = "必须保留"
            }
        };
        await File.WriteAllTextAsync(sourcePath, template.ToJsonString());

        var imported = await services.CharacterCards.ImportAsync(sourcePath);
        var editor = new CharacterEditBuffer();
        editor.Load(imported.Character);

        Assert.All(
            new[]
            {
                editor.Name,
                editor.Description,
                editor.Personality,
                editor.Scenario,
                editor.FirstMessage,
                editor.MessageExample,
                editor.CreatorNotes,
                editor.SystemPrompt,
                editor.PostHistoryInstructions,
                editor.TagsText,
                editor.Creator,
                editor.CharacterVersion,
                editor.CharacterBookJson,
                editor.DepthPrompt,
                editor.DepthPromptRole,
                editor.RawCardJson
            },
            value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.Equal(2, editor.AlternateGreetings.Count);
        Assert.Equal(6, editor.DepthPromptDepth);

        editor.Description = "这是保存后的完整角色描述。";
        editor.ApplyTo(imported.Character);
        await services.Characters.UpsertAsync(imported.Character);
        var exportedPath = Path.Combine(workspace.Root, "complete-card-v3-exported.json");
        await services.CharacterCards.ExportAsync(imported.Character, exportedPath);

        var exported = JsonNode.Parse(
            await File.ReadAllTextAsync(exportedPath))!.AsObject();
        var exportedData = exported["data"]!.AsObject();
        Assert.Equal("全字段测试角色", exportedData["name"]!.GetValue<string>());
        Assert.Equal(
            "这是保存后的完整角色描述。",
            exportedData["description"]!.GetValue<string>());
        Assert.Equal(
            "沉着、好奇，并重视事实。",
            exportedData["personality"]!.GetValue<string>());
        Assert.Equal(
            "角色正在测试酒馆中检查自己的角色卡。",
            exportedData["scenario"]!.GetValue<string>());
        Assert.Equal(
            "欢迎来到全字段角色卡测试。",
            exportedData["first_mes"]!.GetValue<string>());
        Assert.Equal(
            "<START>\n{{char}}：这是一段示例对白。",
            exportedData["mes_example"]!.GetValue<string>());
        Assert.Equal(
            "此卡仅用于公开导入、编辑和导出回归。",
            exportedData["creator_notes"]!.GetValue<string>());
        Assert.Equal(
            "保持角色身份，并只输出最终正文。",
            exportedData["system_prompt"]!.GetValue<string>());
        Assert.Equal(
            "回复前检查场景和既有事实。",
            exportedData["post_history_instructions"]!.GetValue<string>());
        Assert.Equal(
            new[] { "这是第二条开场白。", "这是第三条开场白。" },
            exportedData["alternate_greetings"]!.AsArray()
                .Select(item => item!.GetValue<string>()));
        Assert.Equal(
            new[] { "完整字段", "兼容测试" },
            exportedData["tags"]!.AsArray()
                .Select(item => item!.GetValue<string>()));
        Assert.Equal("TavernDesk tests", exportedData["creator"]!.GetValue<string>());
        Assert.Equal("3.0-complete", exportedData["character_version"]!.GetValue<string>());
        Assert.Equal(
            "完整测试世界书",
            exportedData["character_book"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "测试酒馆位于本地数据根中。",
            exportedData["character_book"]!["entries"]![0]!["content"]!
                .GetValue<string>());
        Assert.Equal("完整测试员", exportedData["nickname"]!.GetValue<string>());
        Assert.Equal(
            "中文作者说明。",
            exportedData["creator_notes_multilingual"]!["zh-CN"]!.GetValue<string>());
        Assert.Equal(
            "local-test-template",
            exportedData["source"]![0]!.GetValue<string>());
        Assert.Equal(
            "这是群聊专用开场白。",
            exportedData["group_only_greetings"]![0]!.GetValue<string>());

        var exportedDepthPrompt = exportedData["extensions"]!["depth_prompt"]!.AsObject();
        Assert.Equal(
            "在第六层提醒角色保持克制。",
            exportedDepthPrompt["prompt"]!.GetValue<string>());
        Assert.Equal(6, exportedDepthPrompt["depth"]!.GetValue<int>());
        Assert.Equal("assistant", exportedDepthPrompt["role"]!.GetValue<string>());
        Assert.True(
            exportedDepthPrompt.TryGetPropertyValue(
                "x_nested_unknown",
                out var nestedUnknown),
            "编辑无关字段后，depth_prompt 内的未知字段不应丢失。");
        Assert.NotNull(nestedUnknown);
        Assert.Equal(
            "必须保留",
            nestedUnknown!.AsObject()["keep"]!
                .GetValue<string>());
        Assert.Equal(
            "preserve-me",
            exportedData["extensions"]!["fixture_extension"]!["value"]!
                .GetValue<string>());
        Assert.True(exported["x_root_unknown"]!["stable"]!.GetValue<string>() == "yes");
        Assert.True(exportedData["x_data_unknown"]!["nested"]![0]!.GetValue<bool>());
    }

    [Fact]
    public async Task PngRoundTripPreservesImageAndNonCardChunks()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var sourcePath = Path.Combine(workspace.Root, "fixed-card.png");
        var v3Json = await File.ReadAllTextAsync(FixturePath("card-v3.json"));
        var resourceBytes = Encoding.UTF8.GetBytes("stable embedded resource");
        var chunks = ReadPngChunks(OnePixelPng).ToList();
        InsertBeforeEnd(chunks, "tEXt", BuildText("fixture-note", "do-not-change"));
        InsertBeforeEnd(
            chunks,
            "tEXt",
            BuildText(
                "chara-ext-asset_:assets/lore/data.txt",
                Convert.ToBase64String(resourceBytes)));
        InsertBeforeEnd(
            chunks,
            "tEXt",
            BuildText(
                "ccv3",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(v3Json))));
        await File.WriteAllBytesAsync(sourcePath, WritePngChunks(chunks));

        var sourceHash = Sha256(await File.ReadAllBytesAsync(sourcePath));
        var imported = await services.CharacterCards.ImportAsync(sourcePath);
        Assert.Equal(CharacterCardFormat.Png, imported.Character.SourceCardFormat);
        Assert.Equal(imported.Character.SourceCardPath, imported.Character.AvatarPath);
        Assert.Equal(
            sourceHash,
            Sha256(await File.ReadAllBytesAsync(imported.Character.SourceCardPath)));
        var reportResource = Assert.Single(imported.Report.Resources);
        Assert.Equal(Sha256(resourceBytes), reportResource.Sha256);

        imported.Character.Name = "PNG 编辑后";
        var exportedPath = Path.Combine(workspace.Root, "roundtrip.png");
        var result = await services.CharacterCards.ExportAsync(
            imported.Character,
            exportedPath);
        var exportedChunks = ReadPngChunks(await File.ReadAllBytesAsync(exportedPath));

        Assert.Equal(
            chunks.Single(chunk => chunk.Type == "IDAT").Data,
            exportedChunks.Single(chunk => chunk.Type == "IDAT").Data);
        Assert.Contains(
            exportedChunks,
            chunk => chunk.Type == "tEXt"
                     && ReadTextKeyword(chunk.Data) == "fixture-note"
                     && Encoding.Latin1.GetString(
                         chunk.Data[(Array.IndexOf(chunk.Data, (byte)0) + 1)..])
                     == "do-not-change");
        var resourceChunk = exportedChunks.Single(chunk =>
            chunk.Type == "tEXt"
            && ReadTextKeyword(chunk.Data)
                == "chara-ext-asset_:assets/lore/data.txt");
        Assert.Equal(
            Sha256(resourceBytes),
            Sha256(Convert.FromBase64String(ReadTextValue(resourceChunk.Data))));
        var ccv3 = exportedChunks.Single(chunk =>
            chunk.Type == "tEXt" && ReadTextKeyword(chunk.Data) == "ccv3");
        var exportedCard = JsonNode.Parse(
            Encoding.UTF8.GetString(Convert.FromBase64String(ReadTextValue(ccv3.Data))))!;
        Assert.Equal("PNG 编辑后", exportedCard["data"]!["name"]!.GetValue<string>());
        Assert.Equal(1, result.PreservedResourceCount);
    }

    [Fact]
    public async Task CharxRoundTripPreservesAllNonCardEntriesAndCover()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var sourcePath = Path.Combine(workspace.Root, "fixed-card.charx");
        var card = JsonNode.Parse(
            await File.ReadAllTextAsync(FixturePath("card-v3.json")))!.AsObject();
        var data = card["data"]!.AsObject();
        data["assets"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "icon",
                ["uri"] = "embeded://assets/icon/images/main.png",
                ["name"] = "main",
                ["ext"] = "png"
            }
        };
        var customBytes = Encoding.UTF8.GetBytes("custom resource survives");
        await CreateCharxAsync(
            sourcePath,
            card.ToJsonString(),
            new Dictionary<string, byte[]>
            {
                ["assets/icon/images/main.png"] = OnePixelPng,
                ["notes/custom.bin"] = customBytes
            });

        var sourceHash = Sha256(await File.ReadAllBytesAsync(sourcePath));
        var imported = await services.CharacterCards.ImportAsync(sourcePath);
        Assert.Equal(CharacterCardFormat.Charx, imported.Character.SourceCardFormat);
        Assert.Equal(
            sourceHash,
            Sha256(await File.ReadAllBytesAsync(imported.Character.SourceCardPath)));
        Assert.True(File.Exists(imported.Character.AvatarPath));
        Assert.Equal(
            Sha256(OnePixelPng),
            Sha256(await File.ReadAllBytesAsync(imported.Character.AvatarPath)));
        Assert.Equal(2, imported.Report.Resources.Count);

        imported.Character.Name = "CHARX 编辑后";
        var exportedPath = Path.Combine(workspace.Root, "roundtrip.charx");
        var exported = await services.CharacterCards.ExportAsync(
            imported.Character,
            exportedPath);
        using var archive = ZipFile.OpenRead(exportedPath);
        Assert.Equal(3, archive.Entries.Count(entry => !entry.FullName.EndsWith('/')));
        Assert.Equal(
            Sha256(OnePixelPng),
            Sha256(await ReadZipEntryAsync(
                archive,
                "assets/icon/images/main.png")));
        Assert.Equal(
            Sha256(customBytes),
            Sha256(await ReadZipEntryAsync(archive, "notes/custom.bin")));
        var exportedCard = JsonNode.Parse(
            Encoding.UTF8.GetString(await ReadZipEntryAsync(archive, "card.json")))!;
        Assert.Equal("CHARX 编辑后", exportedCard["data"]!["name"]!.GetValue<string>());
        Assert.Equal(
            "preserve-me",
            exportedCard["data"]!["extensions"]!["fixture_extension"]!["value"]!
                .GetValue<string>());
        Assert.Equal(2, exported.PreservedResourceCount);
    }

    [Fact]
    public async Task CharxRejectsTraversalEntry()
    {
        using var workspace = new TestWorkspace();
        var path = Path.Combine(workspace.Root, "traversal.charx");
        await CreateCharxAsync(
            path,
            await File.ReadAllTextAsync(FixturePath("card-v3.json")),
            new Dictionary<string, byte[]>
            {
                ["../escape.txt"] = Encoding.UTF8.GetBytes("blocked")
            });
        var codec = new SillyTavernCharxCardCodec();

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => codec.ImportAsync(path));

        Assert.Contains("越界", exception.Message);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "escape.txt")));
    }

    [Fact]
    public async Task CustomShelfPersistsMembershipWithoutDuplicatingCharacter()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "书架角色" };
        await services.Characters.UpsertAsync(character);
        var shelf = new CharacterShelf { Name = "常用角色" };
        await services.CharacterShelves.UpsertAsync(shelf);

        await services.CharacterShelves.AddCharacterAsync(shelf.Id, character.Id);
        await services.CharacterShelves.AddCharacterAsync(shelf.Id, character.Id);

        Assert.Equal(
            [character.Id],
            await services.CharacterShelves.ListCharacterIdsAsync(shelf.Id));
        Assert.Equal(1, await services.Characters.CountAsync());
        await services.CharacterShelves.DeleteAsync(shelf.Id);
        Assert.Equal(1, await services.Characters.CountAsync());
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static JsonNode? ReadPath(JsonObject root, string path)
    {
        JsonNode? current = root;
        foreach (var part in path[2..].Split('.'))
        {
            current = current?[part];
        }

        return current;
    }

    private static async Task CreateCharxAsync(
        string path,
        string cardJson,
        IReadOnlyDictionary<string, byte[]> entries)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            81920,
            useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        await WriteZipEntryAsync(
            archive,
            "card.json",
            Encoding.UTF8.GetBytes(cardJson));
        foreach (var entry in entries)
        {
            await WriteZipEntryAsync(archive, entry.Key, entry.Value);
        }
    }

    private static async Task WriteZipEntryAsync(
        ZipArchive archive,
        string path,
        byte[] bytes)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await output.WriteAsync(bytes);
    }

    private static async Task<byte[]> ReadZipEntryAsync(
        ZipArchive archive,
        string path)
    {
        var entry = archive.GetEntry(path)
                    ?? throw new InvalidDataException($"ZIP 缺少 {path}");
        await using var input = entry.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output);
        return output.ToArray();
    }

    private static string Sha256(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void InsertBeforeEnd(
        List<TestPngChunk> chunks,
        string type,
        byte[] data)
    {
        var end = chunks.FindIndex(chunk => chunk.Type == "IEND");
        chunks.Insert(end, new TestPngChunk(type, data));
    }

    private static byte[] BuildText(string keyword, string value)
    {
        var keyBytes = Encoding.Latin1.GetBytes(keyword);
        var valueBytes = Encoding.Latin1.GetBytes(value);
        return [.. keyBytes, 0, .. valueBytes];
    }

    private static string ReadTextKeyword(byte[] data)
    {
        var separator = Array.IndexOf(data, (byte)0);
        return Encoding.Latin1.GetString(data, 0, separator);
    }

    private static string ReadTextValue(byte[] data)
    {
        var separator = Array.IndexOf(data, (byte)0);
        return Encoding.Latin1.GetString(data, separator + 1, data.Length - separator - 1);
    }

    private static IReadOnlyList<TestPngChunk> ReadPngChunks(byte[] png)
    {
        var chunks = new List<TestPngChunk>();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(offset, 4)));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            chunks.Add(new TestPngChunk(
                type,
                png.AsSpan(offset + 8, length).ToArray()));
            offset += length + 12;
            if (type == "IEND")
            {
                break;
            }
        }

        return chunks;
    }

    private static byte[] WritePngChunks(IReadOnlyList<TestPngChunk> chunks)
    {
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var length = new byte[4];
        var crc = new byte[4];
        foreach (var chunk in chunks)
        {
            BinaryPrimitives.WriteUInt32BigEndian(length, (uint)chunk.Data.Length);
            output.Write(length);
            var type = Encoding.ASCII.GetBytes(chunk.Type);
            output.Write(type);
            output.Write(chunk.Data);
            BinaryPrimitives.WriteUInt32BigEndian(
                crc,
                ComputePngCrc(type, chunk.Data));
            output.Write(crc);
        }

        return output.ToArray();
    }

    private static uint ComputePngCrc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data)
    {
        var crc = 0xffffffffu;
        foreach (var value in type)
        {
            crc = UpdateCrc(crc, value);
        }

        foreach (var value in data)
        {
            crc = UpdateCrc(crc, value);
        }

        return crc ^ 0xffffffffu;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? 0xedb88320u ^ (crc >> 1)
                : crc >> 1;
        }

        return crc;
    }

    private sealed record TestPngChunk(string Type, byte[] Data);
}
