using System.Text;
using System.Text.Json.Nodes;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

namespace TavernDesk.Tests;

public sealed class ChatJsonlCompatibilityTests
{
    [Fact]
    public async Task ImportEditExportAndReimportPreserveCandidatesAndUnknownFields()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "雪乃" };
        await services.Characters.UpsertAsync(character);
        var sourcePath = Path.Combine(workspace.Root, "雪乃旧聊天.jsonl");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {"user_name":"林","character_name":"雪乃","create_date":"2026-07-01T10:00:00+08:00","chat_metadata":{"scenario":"图书馆"},"vendor_header":{"keep":true}}
            {"name":"林","is_user":true,"is_system":false,"send_date":"2026-07-01T10:01:00+08:00","mes":"你还记得约定吗？","extra":{"vendor":"user-extra"}}
            {"name":"雪乃","is_user":false,"is_system":false,"send_date":"2026-07-01T10:02:00+08:00","mes":"当然记得。","swipes":["我忘了。","当然记得。"],"swipe_id":1,"swipe_info":[{"token_count":3},{"token_count":4}],"vendor_message":{"keep":42}}
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var imported = await services.ChatArchives.ImportAsync(sourcePath);

        Assert.False(imported.CreatedPlaceholderCharacter);
        Assert.Equal(2, imported.MessageCount);
        Assert.Equal(2, imported.CandidateCount);
        Assert.Equal(character.Id, imported.Conversation.CharacterId);
        var messages = await services.Conversations.ListMessagesAsync(
            imported.Conversation.Id);
        Assert.Equal([1L, 2L], messages.Select(message => message.SequenceNo));
        var assistant = messages[1];
        Assert.Equal(1, assistant.ActiveCandidateIndex);
        Assert.Equal(
            ["我忘了。", "当然记得。"],
            (await services.Conversations.ListCandidatesAsync(assistant.Id))
            .Select(candidate => candidate.Content));

        await services.Conversations.UpdateMessageContentAsync(
            assistant.Id,
            "编辑后仍然记得。");
        var exportPath = Path.Combine(workspace.Root, "雪乃旧聊天-roundtrip.jsonl");
        var exported = await services.ChatArchives.ExportAsync(
            imported.Conversation.Id,
            exportPath);

        Assert.Equal(2, exported.MessageCount);
        Assert.Equal(2, exported.CandidateCount);
        var lines = await File.ReadAllLinesAsync(exportPath);
        Assert.Equal(3, lines.Length);
        var header = JsonNode.Parse(lines[0])!.AsObject();
        var user = JsonNode.Parse(lines[1])!.AsObject();
        var exportedAssistant = JsonNode.Parse(lines[2])!.AsObject();
        Assert.True(header["vendor_header"]!["keep"]!.GetValue<bool>());
        Assert.Equal(
            "user-extra",
            user["extra"]!["vendor"]!.GetValue<string>());
        Assert.Equal(
            42,
            exportedAssistant["vendor_message"]!["keep"]!.GetValue<int>());
        Assert.Equal(
            2,
            exportedAssistant["swipe_info"]!.AsArray().Count);
        Assert.Equal(
            "编辑后仍然记得。",
            exportedAssistant["mes"]!.GetValue<string>());
        Assert.Equal(
            "编辑后仍然记得。",
            exportedAssistant["swipes"]![1]!.GetValue<string>());
        Assert.Equal(1, exportedAssistant["swipe_id"]!.GetValue<int>());

        var reimported = await services.ChatArchives.ImportAsync(exportPath);
        var reimportedMessages = await services.Conversations.ListMessagesAsync(
            reimported.Conversation.Id);
        Assert.Equal("编辑后仍然记得。", reimportedMessages[1].Content);
        Assert.Equal(
            ["我忘了。", "编辑后仍然记得。"],
            (await services.Conversations.ListCandidatesAsync(
                reimportedMessages[1].Id))
            .Select(candidate => candidate.Content));
    }

    [Fact]
    public async Task UnknownCharacterCreatesEditablePlaceholder()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var sourcePath = Path.Combine(workspace.Root, "未知角色.jsonl");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {"user_name":"USER","character_name":"未知角色","chat_metadata":{}}
            {"name":"未知角色","is_user":false,"is_system":false,"mes":"你好。"}
            """);

        var imported = await services.ChatArchives.ImportAsync(sourcePath);

        Assert.True(imported.CreatedPlaceholderCharacter);
        var placeholder = Assert.Single(await services.Characters.ListAsync());
        Assert.Equal("未知角色", placeholder.Name);
        Assert.Equal(placeholder.Id, imported.Conversation.CharacterId);
        Assert.Contains("chara_card_v2", placeholder.RawCardJson);
    }

    [Fact]
    public async Task MalformedArchiveIsRejectedBeforeDatabaseMutation()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var sourcePath = Path.Combine(workspace.Root, "损坏聊天.jsonl");
        await File.WriteAllTextAsync(
            sourcePath,
            """
            {"user_name":"USER","character_name":"不应创建","chat_metadata":{}}
            {"name":"USER","is_user":true,"mes":"第一条"}
            {"name":
            """);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => services.ChatArchives.ImportAsync(sourcePath));

        Assert.Contains("第 3 行", exception.Message);
        Assert.Equal(0, await services.Characters.CountAsync());
        Assert.Equal(0, await services.Conversations.CountAsync());
    }
}
