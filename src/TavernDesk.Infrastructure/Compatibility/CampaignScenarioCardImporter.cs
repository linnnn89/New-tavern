using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure.Storage;

namespace TavernDesk.Infrastructure.Compatibility;

public sealed class CampaignScenarioCardImporter : ICampaignScenarioCardImporter
{
    private const int MaximumPngBytes = 256 * 1024 * 1024;
    private readonly AppDataPaths _paths;
    private readonly ICampaignScenarioRepository _repository;

    public CampaignScenarioCardImporter(
        AppDataPaths paths,
        ICampaignScenarioRepository repository)
    {
        _paths = paths;
        _repository = repository;
    }

    public async Task<CampaignScenarioImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var sourceFullPath = Path.GetFullPath(sourcePath);
        var file = new FileInfo(sourceFullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("剧本卡文件不存在。", sourceFullPath);
        }

        if (!string.Equals(file.Extension, ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("第一版剧本卡导入仅支持 PNG 角色卡容器。");
        }

        if (file.Length > MaximumPngBytes)
        {
            throw new InvalidDataException("PNG 剧本卡超过 256 MiB 安全上限。");
        }

        var bytes = await File.ReadAllBytesAsync(sourceFullPath, cancellationToken);
        var container = PngCardContainer.Parse(bytes);
        var entries = container.ReadTextEntries();
        var selected = entries.LastOrDefault(entry => entry.Keyword == "ccv3")
                       ?? entries.LastOrDefault(entry => entry.Keyword == "chara")
                       ?? throw new InvalidDataException(
                           "PNG 中没有 ccv3 或 chara 剧本卡文本块。");
        string json;
        try
        {
            json = Encoding.UTF8.GetString(
                Convert.FromBase64String(selected.Text.Trim()));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("剧本卡文本块不是有效 Base64。", exception);
        }

        var root = JsonNode.Parse(
                       json,
                       documentOptions: new JsonDocumentOptions
                       {
                           AllowTrailingCommas = true,
                           CommentHandling = JsonCommentHandling.Skip,
                           MaxDepth = 256
                       }) as JsonObject
                   ?? throw new InvalidDataException("剧本卡 JSON 根节点必须是对象。");
        var data = root["data"] as JsonObject ?? root;
        var name = ReadString(data, "name");
        var description = ReadString(data, "description");
        var scenarioText = ReadString(data, "scenario");
        var firstMessage = ReadString(data, "first_mes");
        var examples = ReadString(data, "mes_example");
        var systemPrompt = ReadString(data, "system_prompt");
        var postHistory = ReadString(data, "post_history_instructions");
        var sourceName = Path.GetFileName(sourceFullPath);
        var isNaruto = name.Contains("RPG", StringComparison.OrdinalIgnoreCase)
                       && (sourceName.Contains(
                               "naruto",
                               StringComparison.OrdinalIgnoreCase)
                           || scenarioText.Contains(
                               "Hidden Leaf",
                               StringComparison.OrdinalIgnoreCase));
        var scenario = isNaruto
            ? CreateNarutoScenario(
                description,
                scenarioText,
                firstMessage,
                examples,
                json,
                sourceName)
            : CreateGenericScenario(
                name,
                description,
                scenarioText,
                firstMessage,
                examples,
                systemPrompt,
                postHistory,
                json,
                sourceName);
        var targetDirectory = ResolveScenarioDirectory(scenario.Id);
        Directory.CreateDirectory(targetDirectory);
        try
        {
            var targetPath = Path.Combine(targetDirectory, "source.png");
            await using (var source = new FileStream(
                             sourceFullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             81920,
                             useAsync: true))
            await using (var destination = new FileStream(
                             targetPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             useAsync: true))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            scenario.CoverPath = targetPath;
            await _repository.UpsertAsync(scenario, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            throw;
        }

        var warnings = new List<string>
        {
            "源 PNG 与完整原始 JSON 已原样保留。",
            "first_mes 已改存为仅供起始大厅显示的说明，不会注入 GM 或 AI 玩家请求。",
            "mes_example 已改存为历史样例档案，不会伪装成当前跑团历史。"
        };
        if (isNaruto)
        {
            warnings.Add("已应用“火影忍者：禁术卷轴”专用 GM 模板。");
        }

        return new CampaignScenarioImportResult(scenario, warnings);
    }

    private string ResolveScenarioDirectory(string scenarioId)
    {
        var root = Path.GetFullPath(_paths.CampaignScenarioCardsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(Path.Combine(root, scenarioId));
        if (!candidate.StartsWith(
                root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("剧本资源目录越出了数据根。");
        }

        return candidate;
    }

    private static CampaignScenario CreateNarutoScenario(
        string description,
        string sourceScenario,
        string firstMessage,
        string examples,
        string rawJson,
        string sourceName) =>
        new()
        {
            Title = "火影忍者：禁术卷轴",
            Summary = string.IsNullOrWhiteSpace(description)
                ? "围绕一份可能打破忍界平衡的古老禁术卷轴展开的跨村任务。"
                : description.Trim(),
            WorldSetting =
                "故事发生在火影忍者的忍界。木叶、其他忍村、查克拉、忍术、"
                + "血继限界、任务等级和村落政治遵循同一套世界因果；"
                + "外来角色保留自身人格与既有能力，但其力量必须由 GM "
                + "根据忍界规则解释、限制并逐步验证。",
            PublicRules =
                "玩家只声明角色的行动、对白和意图，不自行宣布成功、伤害或世界变化。\n"
                + "每条玩家行动由系统自动附带一枚 1d20；GM 负责结合角色、方法与局势解释点数，并裁定 NPC、环境、情报与后果。\n"
                + "未公开情报只能发送给对应玩家；秘密同投时不得互相读取本轮草稿。\n"
                + "所有能力必须服从已建立的查克拉、距离、时间与代价约束。",
            GmInstructions =
                "你是本剧本唯一的 GM 和世界事实写入者。围绕古老卷轴、禁术预言、"
                + "幕后势力与跨村追查推进主线，但允许玩家选择改变路线。\n"
                + "保持火影忍界的组织关系、战斗因果和信息边界；不要替玩家决定主观行动，"
                + "也不要让任何玩家自行裁定成功。\n"
                + "使用每条行动末尾由 TavernDesk 自动生成的真实 1d20；高低点只提供倾向，"
                + "需结合能力、方法、风险与既有事实灵活裁定，不使用固定成功档位。\n"
                + "裁决应同时说明可观察结果、必要的私密情报和简短世界状态增量。",
            OpeningSetup = sourceScenario.Trim(),
            OpeningNarration =
                "反常的乌云压在木叶上空。你们被紧急召往火影办公室；"
                + "一份来历不明、封印古老的卷轴正安静地躺在桌上，"
                + "而它所记载的禁术预言可能打破整个忍界的平衡。",
            LobbyInstructions = firstMessage.Trim(),
            LegacyExamplesArchive = examples,
            SourceCardJson = rawJson,
            SourceFileName = sourceName
        };

    private static CampaignScenario CreateGenericScenario(
        string name,
        string description,
        string sourceScenario,
        string firstMessage,
        string examples,
        string systemPrompt,
        string postHistory,
        string rawJson,
        string sourceName) =>
        new()
        {
            Title = string.IsNullOrWhiteSpace(name)
                ? Path.GetFileNameWithoutExtension(sourceName)
                : name.Trim(),
            Summary = description.Trim(),
            WorldSetting = sourceScenario.Trim(),
            PublicRules =
                "玩家只声明行动、对白和意图；GM 负责裁定结果与写入世界事实。",
            GmInstructions = string.Join(
                "\n\n",
                new[] { systemPrompt.Trim(), postHistory.Trim() }
                    .Where(value => value.Length > 0)),
            OpeningSetup = sourceScenario.Trim(),
            OpeningNarration = string.Empty,
            LobbyInstructions = firstMessage.Trim(),
            LegacyExamplesArchive = examples,
            SourceCardJson = rawJson,
            SourceFileName = sourceName
        };

    private static string ReadString(JsonObject source, string propertyName) =>
        source[propertyName] is JsonValue value
        && value.TryGetValue<string>(out var text)
            ? text
            : string.Empty;
}
