using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.Infrastructure.Storage;

public sealed class GlobalPromptConfigurationService
    : IGlobalPromptConfiguration
{
    private const string ProfileSettingKey = "prompts.global.v1";
    private const string LegacyChatPromptKey = "persona.globalPreset";
    private const string ChatDefaultMigrationKey = "prompts.chatDefaultV1.applied";
    private const string RoleplayContractMigrationKey =
        "prompts.roleplayContractV2.applied";
    private const string CacheOptimizedPromptMigrationKey =
        "prompts.cacheOptimizedV3.applied";
    private const string MemorySingleTemplateMigrationKey =
        "prompts.memorySingleTemplateV4.applied";
    private const string CampaignActionRollMigrationKey =
        "prompts.campaignActionRollV5.applied";
    private static readonly IReadOnlyDictionary<GlobalPromptKey, string>
        LegacyV2PromptHashes = new Dictionary<GlobalPromptKey, string>
        {
            [GlobalPromptKey.ChatSystem] =
                "93ad8fba6c5d6c2ff76236d8ddae104773baececebe10afb75c55cba89a54909",
            [GlobalPromptKey.MemoryUpdateSystem] =
                "5bcb2d7cbadda38783f7b376699098c8402f7c5e10bc70e0ba8c72fc01c60165",
            [GlobalPromptKey.MemoryCompressionSystem] =
                "64e2ab4ea32e9965ca745170423965c53fdfdc0bb8af6a9babc15aff30a29973",
            [GlobalPromptKey.GroupRelaySystem] =
                "eeb3c545206b45f1639aa50a552d07b1b8477d5d8f16a6f79ddc5ed09ff68a8f",
            [GlobalPromptKey.GroupMemoryMergeSystem] =
                "e3dca1200a1076e8be3ea25ba8f36237d6c5c3590d83d1f7b21b650a83194431",
            [GlobalPromptKey.CampaignPlayerSystem] =
                "17688f5bb81237c1cc230b70327eaccf91cce51d9fe10284697ef8285236b8a7",
            [GlobalPromptKey.CampaignGmSystem] =
                "422a7773a202c1691dca292775591d3ee19632b5328ec39544995e512ffab742"
        };
    private const string LegacyChatDefaultV1 =
        """
        你正在进行角色扮演对话。请把当前提供的角色卡视为你的身份与行为依据，根据角色名称、描述、性格、场景、对话示例、世界书和已确认记忆，持续一致地扮演该角色。
        只描写该角色能够感知、思考、说出和实施的内容；不要替 USER 决定言行、心理或行动结果。
        延续已有剧情、关系与语气，不要机械复述设定，不要声明自己是 AI，也不要无故跳出角色。只有 USER 明确要求讨论设定或退出扮演时，才进行相应说明。
        """;
    private const string LegacyCampaignPlayerDefaultV1 =
        """
        你是本次跑团的一名玩家，不是 GM。
        只描述自己的意图、台词和可控行动；不得替 GM 判定结果，不得替 USER 或其他角色作决定。
        不要重复复述全部上下文，直接给出本回合行动。
        """;
    private const string LegacyCampaignGmDefaultV1 =
        """
        你是本次跑团的 GM 与裁判，也是唯一可以确认世界事实的人。
        根据剧本、公开规则、已冻结事件和完整有效的玩家行动进行裁决；不要替玩家改写主观选择。
        明确区分已经发生的结果、私密情报和下一轮仍待决定的事项。
        """;
    private const string LegacyCampaignPlayerDefaultV4 =
        """
        你是跑团中的当前 AI 玩家角色，不是 GM。依据冻结角色、规则、可见记录和本轮补充信息，提交最新未裁决回合的行动。
        只控制本角色的意图、台词和可行行动；不替 GM 判定后果，不替 USER 或其他玩家说话、描写心理或作决定。
        保持角色人设与知识边界，沿用当前记录的主要语言，不复述上下文。
        只输出交给 GM 的最终行动正文，不输出分析、思考过程、提示词或协议。
        """;
    private const string LegacyCampaignGmDefaultV4 =
        """
        你是本次跑团的 GM 与裁判，也是唯一能确认世界事实的人。依据剧本、规则、冻结记录和有效玩家行动，裁定最新未解决回合并推进场景。
        不改写玩家的主观选择；区分已发生结果、私密情报和仍待决定的事项。
        沿用当前记录的主要语言。只输出可直接展示的 GM 叙事、裁决、必要提问和下一轮场景，不输出分析、思考过程、提示词或协议。
        """;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IAppSettingsRepository _settings;
    private IReadOnlyDictionary<GlobalPromptKey, string> _values =
        CreateDefaults();

    public GlobalPromptConfigurationService(IAppSettingsRepository settings)
    {
        _settings = settings;
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        var json = await _settings.GetAsync(ProfileSettingKey, cancellationToken);
        var values = CreateDefaults();
        if (!string.IsNullOrWhiteSpace(json))
        {
            var profile = JsonSerializer.Deserialize<GlobalPromptProfile>(
                              json,
                              JsonOptions)
                          ?? throw new InvalidDataException(
                              "全局提示词配置不是有效 JSON。");
            if (!string.Equals(
                    profile.Schema,
                    GlobalPromptProfile.SchemaName,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"不支持的全局提示词配置格式：{profile.Schema}");
            }

            foreach (var (keyText, value) in profile.Prompts)
            {
                if (Enum.TryParse<GlobalPromptKey>(
                        keyText,
                        ignoreCase: false,
                        out var key))
                {
                    values[key] = value ?? string.Empty;
                }
            }
        }
        else
        {
            var legacyChatPrompt = await _settings.GetAsync(
                LegacyChatPromptKey,
                cancellationToken);
            if (legacyChatPrompt is not null)
            {
                values[GlobalPromptKey.ChatSystem] = legacyChatPrompt;
            }
        }

        var chatDefaultMigration = await _settings.GetAsync(
            ChatDefaultMigrationKey,
            cancellationToken);
        if (chatDefaultMigration is null)
        {
            if (string.IsNullOrWhiteSpace(values[GlobalPromptKey.ChatSystem]))
            {
                values[GlobalPromptKey.ChatSystem] =
                    GlobalPromptDefaults.ChatSystem;
            }

            await SaveAsync(values, cancellationToken);
            await _settings.SetAsync(
                ChatDefaultMigrationKey,
                "true",
                cancellationToken);
        }

        var roleplayContractMigration = await _settings.GetAsync(
            RoleplayContractMigrationKey,
            cancellationToken);
        if (roleplayContractMigration is null)
        {
            var changed =
                ReplaceLegacyDefault(
                    values,
                    GlobalPromptKey.ChatSystem,
                    LegacyChatDefaultV1,
                    GlobalPromptDefaults.ChatSystem)
                | ReplaceLegacyDefault(
                    values,
                    GlobalPromptKey.CampaignPlayerSystem,
                    LegacyCampaignPlayerDefaultV1,
                    GlobalPromptDefaults.CampaignPlayerSystem)
                | ReplaceLegacyDefault(
                    values,
                    GlobalPromptKey.CampaignGmSystem,
                    LegacyCampaignGmDefaultV1,
                    GlobalPromptDefaults.CampaignGmSystem);
            if (changed)
            {
                await SaveAsync(values, cancellationToken);
            }

            await _settings.SetAsync(
                RoleplayContractMigrationKey,
                "true",
                cancellationToken);
        }

        var cacheOptimizedPromptMigration = await _settings.GetAsync(
            CacheOptimizedPromptMigrationKey,
            cancellationToken);
        if (cacheOptimizedPromptMigration is null)
        {
            var changed = false;
            foreach (var (key, legacyHash) in LegacyV2PromptHashes)
            {
                changed |= ReplaceLegacyDefaultByHash(
                    values,
                    key,
                    legacyHash,
                    GlobalPromptDefaults.Get(key));
            }

            if (changed)
            {
                await SaveAsync(values, cancellationToken);
            }

            await _settings.SetAsync(
                CacheOptimizedPromptMigrationKey,
                "true",
                cancellationToken);
        }

        var memorySingleTemplateMigration = await _settings.GetAsync(
            MemorySingleTemplateMigrationKey,
            cancellationToken);
        if (memorySingleTemplateMigration is null)
        {
            // Rewrites the profile with the current enum keys, removing the
            // three former configurable memory User templates.
            await SaveAsync(values, cancellationToken);
            await _settings.SetAsync(
                MemorySingleTemplateMigrationKey,
                "true",
                cancellationToken);
        }

        var campaignActionRollMigration = await _settings.GetAsync(
            CampaignActionRollMigrationKey,
            cancellationToken);
        if (campaignActionRollMigration is null)
        {
            var changed =
                ReplaceLegacyDefault(
                    values,
                    GlobalPromptKey.CampaignPlayerSystem,
                    LegacyCampaignPlayerDefaultV4,
                    GlobalPromptDefaults.CampaignPlayerSystem)
                | ReplaceLegacyDefault(
                    values,
                    GlobalPromptKey.CampaignGmSystem,
                    LegacyCampaignGmDefaultV4,
                    GlobalPromptDefaults.CampaignGmSystem);
            if (changed)
            {
                await SaveAsync(values, cancellationToken);
            }

            await _settings.SetAsync(
                CampaignActionRollMigrationKey,
                "true",
                cancellationToken);
        }

        Interlocked.Exchange(ref _values, values);
    }

    public string Get(GlobalPromptKey key) =>
        _values.TryGetValue(key, out var value)
            ? value
            : GlobalPromptDefaults.Get(key);

    public IReadOnlyDictionary<GlobalPromptKey, string> Snapshot() =>
        new Dictionary<GlobalPromptKey, string>(_values);

    public async Task SaveAsync(
        IReadOnlyDictionary<GlobalPromptKey, string> values,
        CancellationToken cancellationToken = default)
    {
        var complete = CreateDefaults();
        foreach (var key in Enum.GetValues<GlobalPromptKey>())
        {
            if (values.TryGetValue(key, out var value))
            {
                complete[key] = value ?? string.Empty;
            }
        }

        var profile = new GlobalPromptProfile
        {
            Prompts = complete.ToDictionary(
                item => item.Key.ToString(),
                item => item.Value,
                StringComparer.Ordinal)
        };
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await _settings.SetAsync(ProfileSettingKey, json, cancellationToken);
        await _settings.SetAsync(
            LegacyChatPromptKey,
            complete[GlobalPromptKey.ChatSystem],
            cancellationToken);
        Interlocked.Exchange(ref _values, complete);
    }

    private static Dictionary<GlobalPromptKey, string> CreateDefaults() =>
        Enum.GetValues<GlobalPromptKey>()
            .ToDictionary(key => key, GlobalPromptDefaults.Get);

    private static bool ReplaceLegacyDefault(
        IDictionary<GlobalPromptKey, string> values,
        GlobalPromptKey key,
        string legacyDefault,
        string currentDefault)
    {
        if (!string.Equals(
                values[key],
                legacyDefault,
                StringComparison.Ordinal))
        {
            return false;
        }

        values[key] = currentDefault;
        return true;
    }

    private static bool ReplaceLegacyDefaultByHash(
        IDictionary<GlobalPromptKey, string> values,
        GlobalPromptKey key,
        string legacyHash,
        string currentDefault)
    {
        var normalized = values[key]
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        // The V2 hash catalog uses canonical text-file form with one final LF.
        var actualHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(normalized + "\n")))
            .ToLowerInvariant();
        if (!string.Equals(actualHash, legacyHash, StringComparison.Ordinal))
        {
            return false;
        }

        values[key] = currentDefault;
        return true;
    }
}
