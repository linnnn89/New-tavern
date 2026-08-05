using TavernDesk.App.ViewModels;
using TavernDesk.Core.Models;
using System.Text.Json.Nodes;

namespace TavernDesk.Tests;

public sealed class CharacterEditBufferTests
{
    [Fact]
    public void EditingBufferDoesNotMutateCharacterUntilApplied()
    {
        var character = new Character
        {
            Name = "原名称",
            Description = "原描述"
        };
        var buffer = new CharacterEditBuffer();
        buffer.Load(character);

        buffer.Name = "新名称";
        buffer.Description = "新描述";

        Assert.True(buffer.IsDirty);
        Assert.Equal("原名称", character.Name);
        Assert.Equal("原描述", character.Description);
        buffer.ApplyTo(character);
        Assert.Equal("新名称", character.Name);
        Assert.Equal("新描述", character.Description);
    }

    [Fact]
    public void AdvancedFieldsPreserveUnknownCardDataWhenApplied()
    {
        var character = new Character
        {
            Name = "雪乃",
            RawCardJson = """
                {
                  "spec": "chara_card_v2",
                  "spec_version": "2.0",
                  "vendor_root": {"keep": true},
                  "data": {
                    "name": "雪乃",
                    "description": "原描述",
                    "personality": "",
                    "scenario": "",
                    "first_mes": "",
                    "mes_example": "原示例",
                    "alternate_greetings": ["原开场"],
                    "tags": ["原标签"],
                    "extensions": {
                      "vendor_extension": {"answer": 42},
                      "depth_prompt": {
                        "prompt": "原深度提示",
                        "depth": 3,
                        "role": "system"
                      }
                    }
                  }
                }
                """
        };
        var buffer = new CharacterEditBuffer();
        buffer.Load(character);

        buffer.MessageExample = "新示例";
        Assert.Single(buffer.AlternateGreetings);
        buffer.AlternateGreetings[0].Text = "开场一";
        buffer.AddAlternateGreetingCommand.Execute(null);
        buffer.AlternateGreetings[1].Text = "开场二";
        buffer.TagsText = "冰雪, 学生";
        buffer.DepthPrompt = "在第六层维持克制语气";
        buffer.DepthPromptDepth = 6;
        buffer.DepthPromptRole = "assistant";
        buffer.ApplyTo(character);

        var root = JsonNode.Parse(character.RawCardJson)!.AsObject();
        var data = root["data"]!.AsObject();
        Assert.True(root["vendor_root"]!["keep"]!.GetValue<bool>());
        Assert.Equal(
            42,
            data["extensions"]!["vendor_extension"]!["answer"]!.GetValue<int>());
        Assert.Equal("新示例", data["mes_example"]!.GetValue<string>());
        Assert.Equal(
            ["开场一", "开场二"],
            data["alternate_greetings"]!.AsArray()
                .Select(node => node!.GetValue<string>()));
        Assert.Equal(
            ["冰雪", "学生"],
            data["tags"]!.AsArray()
                .Select(node => node!.GetValue<string>()));
        Assert.Equal(
            6,
            data["extensions"]!["depth_prompt"]!["depth"]!.GetValue<int>());
        Assert.Equal(
            "assistant",
            data["extensions"]!["depth_prompt"]!["role"]!.GetValue<string>());
    }

    [Fact]
    public void InvalidAdvancedJsonDoesNotPartiallyMutateCharacter()
    {
        var character = new Character
        {
            Name = "原名称",
            Description = "原描述",
            RawCardJson = """{"name":"原名称","description":"原描述"}"""
        };
        var originalRawJson = character.RawCardJson;
        var buffer = new CharacterEditBuffer();
        buffer.Load(character);
        buffer.Name = "不应写入";
        buffer.CharacterBookJson = "[]";

        Assert.Throws<InvalidDataException>(() => buffer.ApplyTo(character));

        Assert.Equal("原名称", character.Name);
        Assert.Equal("原描述", character.Description);
        Assert.Equal(originalRawJson, character.RawCardJson);
    }
}
