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
        var examples = ReadString(data, "mes_example");
        var systemPrompt = ReadString(data, "system_prompt");
        var postHistory = ReadString(data, "post_history_instructions");
        var sourceName = Path.GetFileName(sourceFullPath);
        var scenario = CreateGenericScenario(
            name,
            description,
            scenarioText,
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
            "first_mes 不再作为独立剧本字段保存或显示。",
            "mes_example 已改存为历史样例档案，不会伪装成当前跑团历史。"
        };

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

    private static CampaignScenario CreateGenericScenario(
        string name,
        string description,
        string sourceScenario,
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
