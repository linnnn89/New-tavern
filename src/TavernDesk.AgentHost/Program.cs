using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Campaigns;

try
{
    if (args.Length == 2
        && string.Equals(args[0], "--storage-smoke", StringComparison.Ordinal))
    {
        await RunStorageSmokeAsync(args[1]);
        return;
    }

    if (args.Length == 3
        && string.Equals(
            args[0],
            "--character-card-smoke",
            StringComparison.Ordinal))
    {
        await RunCharacterCardSmokeAsync(args[1], args[2]);
        return;
    }

    if (args.Length == 2
        && string.Equals(
            args[0],
            "--campaign-live-preflight",
            StringComparison.Ordinal))
    {
        await RunCampaignLivePreflightAsync(args[1]);
        return;
    }

    if (args.Length == 8
        && string.Equals(
            args[0],
            "--campaign-live-smoke",
            StringComparison.Ordinal))
    {
        if (!int.TryParse(args[6], out var roundCount)
            || roundCount is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "真实跑团验证的回合数必须是 1–3。");
        }

        if (!int.TryParse(args[7], out var maxOutputTokens)
            || maxOutputTokens is < 128 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "真实跑团验证的单次输出上限必须是 128–10000 tokens。");
        }

        await RunCampaignLiveSmokeAsync(
            args[1],
            args[2],
            args[3],
            [args[4], args[5]],
            roundCount,
            maxOutputTokens);
        return;
    }

    if (args.Length == 5
        && string.Equals(
            args[0],
            "--provider-live-smoke",
            StringComparison.Ordinal))
    {
        if (!int.TryParse(args[4], out var contextLimit)
            || contextLimit is < 1024 or > 4_194_304)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "上下文上限必须是 1024–4194304 之间的整数。");
        }

        await RunProviderLiveSmokeAsync(
            args[1],
            args[2],
            args[3],
            contextLimit);
        return;
    }

    var response = new
    {
        service = "TavernDesk.AgentHost",
        version = "0.1.0",
        status = "foundation-only",
        note = "聊天辅助工具宿主尚未启用；本进程不执行代码 Agent 工作流。"
    };

    Console.WriteLine(JsonSerializer.Serialize(response));
    return;
}
catch (Exception exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        status = "ERROR",
        errorType = exception.GetType().Name,
        message = exception.Message
    }));
    Environment.ExitCode = 1;
}

static async Task RunCampaignLivePreflightAsync(string sourceDataRoot)
{
    var sourceRoot = Path.GetFullPath(sourceDataRoot);
    var databasePath = Path.Combine(sourceRoot, "taverndesk.db");
    if (!File.Exists(databasePath))
    {
        throw new FileNotFoundException(
            "正式数据根中不存在 TavernDesk 数据库。",
            databasePath);
    }

    var snapshotRoot = Path.Combine(
        Path.GetTempPath(),
        $"taverndesk-campaign-preflight-{Guid.NewGuid():N}");
    Directory.CreateDirectory(snapshotRoot);
    File.Copy(
        databasePath,
        Path.Combine(snapshotRoot, "taverndesk.db"),
        overwrite: false);
    try
    {
        var services = new InfrastructureServices(snapshotRoot);
        var charactersTask = services.Characters.ListAsync();
        var scenariosTask = services.CampaignScenarios.ListAsync();
        var providersTask = services.Providers.ListAsync();
        var assignmentTask = services.ModelAssignments.GetAsync(
            ModelFunctionKind.Chat);
        var promptProfileTask = services.Settings.GetAsync("prompts.global.v1");
        await Task.WhenAll(
            charactersTask,
            scenariosTask,
            providersTask,
            assignmentTask,
            promptProfileTask);

        var providers = providersTask.Result;
        var modelCatalogs = new List<object>();
        foreach (var provider in providers.Where(item => item.IsEnabled))
        {
            var models = (await services.Models.ListAsync(provider.Id))
                .Where(model => model.ModelKind is ModelCatalogKind.Chat
                    or ModelCatalogKind.Custom)
                .ToArray();
            modelCatalogs.Add(new
            {
                providerId = provider.Id,
                providerName = provider.Name,
                adapter = provider.AdapterKind.ToString(),
                provider.BaseUrl,
                secretConfigured = !string.IsNullOrWhiteSpace(
                    provider.SecretReference),
                modelCount = models.Length,
                models = models
                    .Where(model =>
                        string.Equals(
                            model.ProviderId,
                            assignmentTask.Result?.ProviderId,
                            StringComparison.Ordinal)
                        && (string.Equals(
                                model.ModelId,
                                assignmentTask.Result?.ModelId,
                                StringComparison.Ordinal)
                            || model.ModelId.Contains(
                                "deepseek",
                                StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(model => model.DisplayName)
                    .Take(20)
                    .Select(model => new
                    {
                        model.ModelId,
                        model.DisplayName,
                        model.ContextLimit,
                        model.MaxOutputTokens
                    })
                    .ToArray()
            });
        }

        var promptValues = Enum.GetValues<GlobalPromptKey>()
            .ToDictionary(key => key, GlobalPromptDefaults.Get);
        if (!string.IsNullOrWhiteSpace(promptProfileTask.Result))
        {
            var profile = JsonSerializer.Deserialize<GlobalPromptProfile>(
                              promptProfileTask.Result)
                          ?? throw new InvalidDataException(
                              "正式数据根的全局提示词配置不是有效 JSON。");
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
                    promptValues[key] = value ?? string.Empty;
                }
            }
        }

        var report = new
        {
            status = "READY",
            sourceDataRoot = sourceRoot,
            characters = charactersTask.Result
                .OrderBy(character => character.Name)
                .Select(character =>
                {
                    var snapshot = services.CampaignCharacterSnapshots.Create(
                        character,
                        memoryBody: null,
                        includeMemory: false,
                        includeOriginalWorldKnowledge: false);
                    return new
                    {
                        character.Id,
                        character.Name,
                        sourceFormat = character.SourceCardFormat.ToString(),
                        descriptionChars = character.Description.Length,
                        personalityChars = character.Personality.Length,
                        scenarioChars = character.Scenario.Length,
                        rawCardChars = character.RawCardJson.Length,
                        snapshotChars = snapshot.CharacterSnapshotJson.Length,
                        snapshotSha256 = Sha256(
                            Encoding.UTF8.GetBytes(
                                snapshot.CharacterSnapshotJson))
                    };
                })
                .ToArray(),
            scenarios = scenariosTask.Result
                .OrderBy(scenario => scenario.Title)
                .Select(scenario => new
                {
                    scenario.Id,
                    scenario.Title,
                    worldChars = scenario.WorldSetting.Length,
                    rulesChars = scenario.PublicRules.Length,
                    gmInstructionChars = scenario.GmInstructions.Length,
                    openingChars = scenario.OpeningSetup.Length,
                    sourceCardChars = scenario.SourceCardJson.Length
                })
                .ToArray(),
            providers = modelCatalogs,
            chatAssignment = assignmentTask.Result is null
                ? null
                : new
                {
                    assignmentTask.Result.ProviderId,
                    assignmentTask.Result.ModelId,
                    assignmentTask.Result.ContextLimit,
                    assignmentTask.Result.MaxOutputTokens,
                    assignmentTask.Result.Temperature,
                    assignmentTask.Result.TopP,
                    assignmentTask.Result.ReasoningEnabled
                },
            campaignPrompts = new[]
            {
                GlobalPromptKey.CampaignPlayerSystem,
                GlobalPromptKey.CampaignGmSystem
            }.Select(key => new
            {
                key = key.ToString(),
                chars = promptValues[key].Length,
                sha256 = Sha256(Encoding.UTF8.GetBytes(promptValues[key]))
            }).ToArray(),
            apiRequests = 0,
            secretReads = 0,
            databaseWrites = 0
        };
        Console.WriteLine(JsonSerializer.Serialize(
            report,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(snapshotRoot, recursive: true);
    }
}

static async Task RunCampaignLiveSmokeAsync(
    string sourceDataRoot,
    string outputRoot,
    string scenarioId,
    IReadOnlyList<string> characterIds,
    int roundCount,
    int maxOutputTokens)
{
    if (characterIds.Count != 2
        || characterIds.Any(string.IsNullOrWhiteSpace)
        || characterIds.Distinct(StringComparer.Ordinal).Count() != 2)
    {
        throw new ArgumentException(
            "真实跑团验证必须提供两个不同的 AI 角色 ID。",
            nameof(characterIds));
    }

    var sourceRoot = Path.GetFullPath(sourceDataRoot);
    var sourceDatabasePath = Path.Combine(sourceRoot, "taverndesk.db");
    var sourceSecretsPath = Path.Combine(sourceRoot, "secrets");
    if (!File.Exists(sourceDatabasePath))
    {
        throw new FileNotFoundException(
            "正式数据根中不存在 TavernDesk 数据库。",
            sourceDatabasePath);
    }

    var outputFullPath = Path.GetFullPath(outputRoot);
    if (Directory.Exists(outputFullPath)
        && Directory.EnumerateFileSystemEntries(outputFullPath).Any())
    {
        throw new IOException(
            "真实跑团验证输出目录不是空目录；为避免覆盖，请使用新目录。");
    }

    Directory.CreateDirectory(outputFullPath);
    var isolatedDataRoot = Path.Combine(outputFullPath, "data");
    var reportPath = Path.Combine(
        outputFullPath,
        "campaign-live-report.json");
    var transcriptPath = Path.Combine(
        outputFullPath,
        "campaign-transcript.md");
    var sourceSnapshotRoot = Path.Combine(
        Path.GetTempPath(),
        $"taverndesk-campaign-live-source-{Guid.NewGuid():N}");
    var sourceDatabaseHashBefore = Sha256(
        await File.ReadAllBytesAsync(sourceDatabasePath));
    var sourceSecretsFingerprintBefore =
        DirectoryFingerprint(sourceSecretsPath);

    string stage = "prepare-source-snapshot";
    string selectedScenarioTitle = string.Empty;
    string selectedProviderName = string.Empty;
    string selectedModelId = string.Empty;
    string playerPromptSha256 = string.Empty;
    int playerPromptChars = 0;
    string gmPromptSha256 = string.Empty;
    int gmPromptChars = 0;
    string? campaignId = null;
    Exception? failure = null;
    string? failedStage = null;
    CampaignAggregate? finalAggregate = null;
    InfrastructureServices? isolatedServices = null;
    AuditingProviderGateway? auditedGateway = null;
    var characterSelections = new List<LiveCampaignCharacterAudit>();
    var roundAudits = new List<LiveCampaignRoundAudit>();

    try
    {
        Directory.CreateDirectory(sourceSnapshotRoot);
        File.Copy(
            sourceDatabasePath,
            Path.Combine(sourceSnapshotRoot, "taverndesk.db"),
            overwrite: false);
        CopyDirectoryFiles(
            sourceSecretsPath,
            Path.Combine(sourceSnapshotRoot, "secrets"));

        stage = "load-real-source-data";
        var sourceServices = new InfrastructureServices(sourceSnapshotRoot);
        await sourceServices.InitializeAsync();
        var scenario = await sourceServices.CampaignScenarios.GetAsync(
                           scenarioId)
                       ?? throw new InvalidOperationException(
                           $"正式数据中不存在剧本 {scenarioId}。");
        var characters = new List<Character>();
        foreach (var characterId in characterIds)
        {
            characters.Add(
                await sourceServices.Characters.GetAsync(characterId)
                ?? throw new InvalidOperationException(
                    $"正式数据中不存在角色 {characterId}。"));
        }

        var assignment = await sourceServices.ModelAssignments.GetAsync(
                             ModelFunctionKind.Chat)
                         ?? throw new InvalidOperationException(
                             "正式数据没有“角色聊天”模型分配。");
        var provider = await sourceServices.Providers.GetAsync(
                           assignment.ProviderId)
                       ?? throw new InvalidOperationException(
                           "当前模型分配引用的接入商不存在。");
        if (!provider.IsEnabled)
        {
            throw new InvalidOperationException(
                $"接入商“{provider.Name}”当前未启用。");
        }

        if (provider.AdapterKind != ProviderAdapterKind.OpenAiCompatible)
        {
            throw new InvalidOperationException(
                "本次低成本短局只使用已通过初验的 OpenRouter/OpenAI-compatible 路由。");
        }

        if (string.IsNullOrWhiteSpace(provider.SecretReference))
        {
            throw new InvalidOperationException(
                $"接入商“{provider.Name}”没有已保存的 DPAPI Key。");
        }

        var selectedModel = (await sourceServices.Models.ListAsync(provider.Id))
            .Where(model => model.ModelKind is ModelCatalogKind.Chat
                or ModelCatalogKind.Custom)
            .SingleOrDefault(model => string.Equals(
                model.ModelId,
                assignment.ModelId,
                StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"本地模型目录中不存在当前分配 {assignment.ModelId}。");
        if (maxOutputTokens > assignment.MaxOutputTokens
            || maxOutputTokens > selectedModel.MaxOutputTokens)
        {
            throw new InvalidOperationException(
                "验证输出上限超过当前模型分配或模型目录上限。");
        }

        selectedScenarioTitle = scenario.Title;
        selectedProviderName = provider.Name;
        selectedModelId = assignment.ModelId;
        var playerPrompt = sourceServices.GlobalPrompts.Get(
            GlobalPromptKey.CampaignPlayerSystem);
        var gmPrompt = sourceServices.GlobalPrompts.Get(
            GlobalPromptKey.CampaignGmSystem);
        playerPromptChars = playerPrompt.Length;
        playerPromptSha256 = Sha256(Encoding.UTF8.GetBytes(playerPrompt));
        gmPromptChars = gmPrompt.Length;
        gmPromptSha256 = Sha256(Encoding.UTF8.GetBytes(gmPrompt));

        stage = "create-isolated-campaign";
        isolatedServices = new InfrastructureServices(isolatedDataRoot);
        await isolatedServices.InitializeAsync();
        await isolatedServices.CampaignScenarios.UpsertAsync(scenario);
        var snapshots = new Dictionary<
            string,
            CampaignCharacterSnapshotResult>(StringComparer.Ordinal);
        foreach (var character in characters)
        {
            await isolatedServices.Characters.UpsertAsync(character);
            var snapshot = sourceServices.CampaignCharacterSnapshots.Create(
                character,
                memoryBody: null,
                includeMemory: false,
                includeOriginalWorldKnowledge: false);
            snapshots[character.Id] = snapshot;
            characterSelections.Add(new LiveCampaignCharacterAudit(
                character.Id,
                character.Name,
                snapshot.CharacterSnapshotJson.Length,
                Sha256(Encoding.UTF8.GetBytes(
                    snapshot.CharacterSnapshotJson)),
                snapshot.Warnings));
        }

        var campaign = new Campaign
        {
            StoryId = scenario.Id,
            Title =
                $"后台真实短局 · {scenario.Title} · {string.Join(" + ", characters.Select(item => item.Name))}",
            WorldSetting = scenario.WorldSetting,
            Rules = scenario.PublicRules,
            OpeningPrompt = scenario.OpeningSetup,
            GmInstructions = scenario.GmInstructions,
            NewNpcPermission = scenario.NewNpcPermission,
            RelationshipChangePermission =
                scenario.RelationshipChangePermission,
            IndependentPlotPermission = scenario.IndependentPlotPermission,
            GmKind = CampaignGmKind.Ai,
            UserAlsoPlayer = true,
            FlowPreset = CampaignFlowPreset.CollaborativeTable,
            UserPersonaName = "USER",
            UserPersonaDescription = "真实短局中的真人玩家席位。",
            GmProviderId = provider.Id,
            GmModelId = assignment.ModelId,
            GmContextLimit = assignment.ContextLimit,
            GmMaxOutputTokens = maxOutputTokens,
            GmTemperature = assignment.Temperature,
            GmTopP = assignment.TopP,
            PlayerHistoryBudget = Math.Min(12_000, assignment.ContextLimit / 2),
            GmHistoryBudget = Math.Min(16_000, assignment.ContextLimit / 2)
        };
        campaignId = campaign.Id;
        var user = new CampaignParticipant
        {
            CampaignId = campaign.Id,
            Kind = CampaignParticipantKind.User,
            SortIndex = 0,
            DisplayName = "USER",
            PersonaSnapshotJson = JsonSerializer.Serialize(new
            {
                name = "USER",
                description = "真实短局中的真人玩家席位。"
            })
        };
        var participants = new List<CampaignParticipant> { user };
        for (var index = 0; index < characters.Count; index++)
        {
            var character = characters[index];
            var snapshot = snapshots[character.Id];
            participants.Add(new CampaignParticipant
            {
                CampaignId = campaign.Id,
                Kind = CampaignParticipantKind.Ai,
                SortIndex = index + 1,
                SourceCharacterId = character.Id,
                DisplayName = character.Name,
                CharacterSnapshotJson = snapshot.CharacterSnapshotJson,
                MemorySnapshot = string.Empty,
                OriginalWorldKnowledgeSnapshot = string.Empty,
                ProviderId = provider.Id,
                ModelId = assignment.ModelId,
                ContextLimit = assignment.ContextLimit,
                MaxOutputTokens = maxOutputTokens,
                Temperature = assignment.Temperature,
                TopP = assignment.TopP
            });
        }

        await isolatedServices.Campaigns.SaveDraftAsync(
            campaign,
            participants);

        auditedGateway = new AuditingProviderGateway(
            sourceServices.ProviderGateway);
        var runner = new CampaignRunner(
            isolatedServices.Campaigns,
            isolatedServices.CampaignScenarios,
            auditedGateway,
            isolatedServices.GenerationCoordinator,
            sourceServices.GlobalPrompts,
            isolatedServices.CampaignMemory,
            isolatedServices.CampaignMemoryRepository,
            operationGate: isolatedServices.CampaignOperationGate);

        stage = "start-campaign";
        var started = await runner.StartAsync(campaign.Id);
        Assert(
            started.Campaign.Phase == CampaignPhase.AwaitingActions,
            "开局后没有进入等待玩家行动阶段。");
        Assert(
            auditedGateway.Requests.Count == 0,
            "静态剧本开场不应额外调用模型。");

        var userActions = CreateCampaignUserActions(
            characters[0].Name,
            characters[1].Name);
        for (var roundNo = 1; roundNo <= roundCount; roundNo++)
        {
            var beforeRound = await isolatedServices.Campaigns.GetAsync(
                                  campaign.Id)
                              ?? throw new InvalidOperationException(
                                  "执行回合前无法重新读取隔离跑团。");
            Assert(
                beforeRound.Campaign.CurrentRound == roundNo
                && beforeRound.Campaign.Phase
                == CampaignPhase.AwaitingActions,
                $"第 {roundNo} 回合开始状态不正确。");
            var requestStartIndex = auditedGateway.Requests.Count + 1;

            stage = $"round-{roundNo}-submit-user-action";
            var userAction = await runner.SubmitUserActionAsync(
                campaign.Id,
                userActions[roundNo - 1]);
            Assert(
                userAction.GenerationStatus
                == CampaignGenerationStatus.Completed
                && userAction.IsLocked,
                $"第 {roundNo} 回合 USER 行动没有锁定。");

            stage = $"round-{roundNo}-generate-ai-player-actions";
            var aiActions = await runner.GenerateAiActionsAsync(campaign.Id);
            Assert(
                aiActions.Count == characters.Count,
                $"第 {roundNo} 回合没有生成两个 AI 玩家行动。");
            foreach (var aiAction in aiActions)
            {
                Assert(
                    aiAction.GenerationStatus
                    == CampaignGenerationStatus.Completed
                    && aiAction.IsLocked
                    && !string.IsNullOrWhiteSpace(aiAction.Content),
                    $"第 {roundNo} 回合 AI 玩家行动未完成：{aiAction.EndReason}。");
                Assert(
                    !ContainsHiddenReasoningMarkup(aiAction.Content),
                    $"第 {roundNo} 回合 AI 玩家正文包含隐藏思考标签。");
            }

            Assert(
                auditedGateway.Requests.Count
                == requestStartIndex - 1 + characters.Count,
                $"第 {roundNo} 回合 AI 玩家请求数不正确。");

            stage = $"round-{roundNo}-generate-ai-gm-resolution";
            var gmResolution = await runner.GenerateGmResolutionAsync(
                campaign.Id);
            Assert(
                gmResolution.GenerationStatus
                == CampaignGenerationStatus.Completed
                && gmResolution.IsLocked
                && !string.IsNullOrWhiteSpace(gmResolution.Content),
                $"第 {roundNo} 回合 AI GM 裁定未完成：{gmResolution.EndReason}。");
            Assert(
                !ContainsHiddenReasoningMarkup(gmResolution.Content),
                $"第 {roundNo} 回合 AI GM 正文包含隐藏思考标签。");

            stage = $"round-{roundNo}-verify-transition";
            var afterRound = await isolatedServices.Campaigns.GetAsync(
                                 campaign.Id)
                             ?? throw new InvalidOperationException(
                                 "完成回合后无法重新读取隔离跑团。");
            Assert(
                afterRound.Campaign.CurrentRound == roundNo + 1
                && afterRound.Campaign.Phase
                == CampaignPhase.AwaitingActions,
                $"第 {roundNo} 回合 GM 裁定后没有进入下一回合。");
            var requestEndIndex = auditedGateway.Requests.Count;
            Assert(
                requestEndIndex - requestStartIndex + 1
                == characters.Count + 1,
                $"第 {roundNo} 回合没有形成两个玩家请求和一个 GM 请求。");
            roundAudits.Add(new LiveCampaignRoundAudit(
                roundNo,
                userAction.Content,
                aiActions.Select(item => item.Id).ToArray(),
                gmResolution.Id,
                requestStartIndex,
                requestEndIndex));
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                status = "ROUND_COMPLETE",
                roundNo,
                apiRequests = requestEndIndex,
                nextRound = afterRound.Campaign.CurrentRound
            }));
        }

        stage = "verify-complete-campaign";
        finalAggregate = await isolatedServices.Campaigns.GetAsync(campaign.Id)
                         ?? throw new InvalidOperationException(
                             "完成后无法重新读取隔离跑团。");
        Assert(
            finalAggregate.Campaign.CurrentRound == roundCount + 1
            && finalAggregate.Campaign.Phase == CampaignPhase.AwaitingActions,
            "最后一次 GM 裁定后没有进入预期的下一回合等待行动阶段。");
        Assert(
            finalAggregate.Events.Count(item =>
                item.Kind == CampaignEventKind.PlayerIntent
                && item.GenerationStatus == CampaignGenerationStatus.Completed)
            == roundCount * participants.Count,
            "完整短局的有效玩家行动数不正确。");
        Assert(
            finalAggregate.Events.Count(item =>
                item.Kind == CampaignEventKind.GmResolution
                && item.GenerationStatus == CampaignGenerationStatus.Completed)
            == roundCount,
            "完整短局的 GM 裁定数不正确。");
        Assert(
            finalAggregate.Events.All(item =>
                item.GenerationStatus is (
                    CampaignGenerationStatus.None
                    or CampaignGenerationStatus.Completed)),
            "成功路径中出现失败或中断事件。");
        var expectedApiRequests = roundCount * (characters.Count + 1);
        Assert(
            auditedGateway.Requests.Count == expectedApiRequests,
            $"真实短局成功路径应恰好调用 {expectedApiRequests} 次模型。");
        Assert(
            auditedGateway.Requests.All(request => request.Completed),
            "至少一个 Provider 请求没有收到完成事件。");
        Assert(
            auditedGateway.Requests.All(request =>
                request.MaxOutputTokens == maxOutputTokens),
            "至少一个 Provider 请求没有遵守验证输出上限。");
        Assert(
            finalAggregate.Participants
                .Where(item => item.Kind == CampaignParticipantKind.Ai)
                .All(item =>
                    string.IsNullOrEmpty(item.MemorySnapshot)
                    && string.IsNullOrEmpty(
                        item.OriginalWorldKnowledgeSnapshot)),
            "至少一个 AI 玩家意外导入了普通记忆或原世界知识。");
        stage = "complete";
    }
    catch (Exception exception)
    {
        failure = exception;
        failedStage = stage;
    }
    finally
    {
        if (isolatedServices is not null
            && campaignId is not null
            && finalAggregate is null)
        {
            try
            {
                finalAggregate = await isolatedServices.Campaigns.GetAsync(
                    campaignId);
            }
            catch
            {
                // The primary failure remains authoritative.
            }
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(sourceSnapshotRoot))
            {
                Directory.Delete(sourceSnapshotRoot, recursive: true);
            }
        }
        catch (Exception cleanupException)
        {
            if (failure is null)
            {
                failure = new IOException(
                    "包含临时 DPAPI 密钥副本的源快照未能清理。",
                    cleanupException);
                failedStage = "cleanup-source-snapshot";
            }
        }
    }

    var sourceDatabaseHashAfter = Sha256(
        await File.ReadAllBytesAsync(sourceDatabasePath));
    var sourceSecretsFingerprintAfter =
        DirectoryFingerprint(sourceSecretsPath);
    var sourceSnapshotDeleted = !Directory.Exists(sourceSnapshotRoot);
    var sourceDatabaseUnchanged = string.Equals(
        sourceDatabaseHashBefore,
        sourceDatabaseHashAfter,
        StringComparison.Ordinal);
    var sourceSecretsUnchanged = string.Equals(
        sourceSecretsFingerprintBefore,
        sourceSecretsFingerprintAfter,
        StringComparison.Ordinal);
    if (failure is null && !sourceDatabaseUnchanged)
    {
        failure = new InvalidOperationException(
            "正式数据库在后台验证期间发生变化。");
        failedStage = "verify-formal-database-unchanged";
    }

    if (failure is null && !sourceSecretsUnchanged)
    {
        failure = new InvalidOperationException(
            "正式 DPAPI 密钥目录在后台验证期间发生变化。");
        failedStage = "verify-formal-secrets-unchanged";
    }

    var requests = auditedGateway?.Requests ?? [];
    if (finalAggregate is not null)
    {
        try
        {
            stage = "write-transcript";
            await WriteNewTextFileAsync(
                transcriptPath,
                BuildCampaignTranscript(
                    finalAggregate,
                    roundAudits,
                    requests));
        }
        catch (Exception transcriptException)
        {
            if (failure is null)
            {
                failure = new IOException(
                    "完整跑团记录未能写入 Markdown 转录。",
                    transcriptException);
                failedStage = "write-transcript";
            }
        }
    }

    var usageReportedCount = requests.Count(item => item.Usage is not null);
    var promptTokens = requests.Sum(item => item.Usage?.PromptTokens ?? 0);
    var completionTokens =
        requests.Sum(item => item.Usage?.CompletionTokens ?? 0);
    var totalTokens = requests.Sum(item => item.Usage?.TotalTokens ?? 0);
    var reasoningTokens =
        requests.Sum(item => item.Usage?.ReasoningTokens ?? 0);
    var cachedPromptTokens =
        requests.Sum(item => item.Usage?.CachedPromptTokens ?? 0);
    var uncachedPromptTokens =
        requests.Sum(item => item.Usage?.UncachedPromptTokens ?? 0);
    var apiRequestUpperBound = roundCount * (characterIds.Count + 1);
    var report = new
    {
        status = failure is null ? "PASS" : "FAIL",
        failedStage,
        error = failure is null
            ? null
            : new
            {
                type = failure.GetType().Name,
                failure.Message
            },
        source = new
        {
            dataRoot = sourceRoot,
            databaseSha256Before = sourceDatabaseHashBefore,
            databaseSha256After = sourceDatabaseHashAfter,
            databaseUnchanged = sourceDatabaseUnchanged,
            encryptedSecretsFingerprintBefore =
                sourceSecretsFingerprintBefore,
            encryptedSecretsFingerprintAfter =
                sourceSecretsFingerprintAfter,
            encryptedSecretsUnchanged = sourceSecretsUnchanged,
            temporarySourceSnapshotDeleted = sourceSnapshotDeleted
        },
        selection = new
        {
            scenarioId,
            scenarioTitle = selectedScenarioTitle,
            characters = characterSelections,
            providerName = selectedProviderName,
            modelId = selectedModelId,
            flowPreset = CampaignFlowPreset.CollaborativeTable.ToString(),
            gmKind = CampaignGmKind.Ai.ToString(),
            userPlayedBy = "Codex",
            roundCount,
            maxOutputTokens,
            promptProfile = new
            {
                campaignPlayerSystem = new
                {
                    chars = playerPromptChars,
                    sha256 = playerPromptSha256
                },
                campaignGmSystem = new
                {
                    chars = gmPromptChars,
                    sha256 = gmPromptSha256
                }
            },
            memoryImported = false,
            originalWorldKnowledgeImported = false
        },
        isolatedDataRoot,
        transcriptPath = finalAggregate is null ? null : transcriptPath,
        completedRounds = roundAudits,
        campaign = finalAggregate is null
            ? null
            : new
            {
                finalAggregate.Campaign.Id,
                finalAggregate.Campaign.Title,
                status = finalAggregate.Campaign.Status.ToString(),
                phase = finalAggregate.Campaign.Phase.ToString(),
                finalAggregate.Campaign.CurrentRound,
                finalAggregate.Campaign.StateVersion,
                eventCount = finalAggregate.Events.Count,
                events = finalAggregate.Events
                    .OrderBy(item => item.SequenceNo)
                    .Select(item => new
                    {
                        item.SequenceNo,
                        item.RoundNo,
                        kind = item.Kind.ToString(),
                        item.ActorId,
                        visibility = item.Visibility.ToString(),
                        generationStatus =
                            item.GenerationStatus.ToString(),
                        endReason = item.EndReason.ToString(),
                        item.IsLocked,
                        contentChars = item.Content.Length,
                        contentSha256 = Sha256(
                            Encoding.UTF8.GetBytes(item.Content)),
                        preview = ContentPreview(item.Content)
                    })
                    .ToArray()
            },
        providerRequests = requests,
        apiRequests = requests.Count,
        apiRequestUpperBound,
        usage = new
        {
            reportedRequestCount = usageReportedCount,
            promptTokens,
            completionTokens,
            totalTokens,
            reasoningTokens,
            cachedPromptTokens,
            uncachedPromptTokens
        },
        recordsPreserved = new
        {
            isolatedDatabase = Path.Combine(
                isolatedDataRoot,
                "taverndesk.db"),
            transcript = finalAggregate is null ? null : transcriptPath,
            report = reportPath
        },
        plaintextSecretRecorded = false,
        fullPromptContentRecordedOutsideProviderRequests = false
    };
    await using (var reportStream = new FileStream(
                     reportPath,
                     FileMode.CreateNew,
                     FileAccess.Write,
                     FileShare.None))
    {
        await JsonSerializer.SerializeAsync(
            reportStream,
            report,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
    }

    if (failure is not null)
    {
        throw new InvalidOperationException(
            $"真实跑团验证失败；报告已保存到 {reportPath}。{failure.Message}",
            failure);
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "PASS",
        reportPath,
        isolatedDataRoot,
        scenario = selectedScenarioTitle,
        characters = characterSelections
            .Select(item => item.CharacterName)
            .ToArray(),
        provider = selectedProviderName,
        modelId = selectedModelId,
        apiRequests = requests.Count,
        usage = new
        {
            promptTokens,
            completionTokens,
            totalTokens,
            cachedPromptTokens
        },
        finalRound = finalAggregate!.Campaign.CurrentRound,
        finalPhase = finalAggregate.Campaign.Phase.ToString(),
        sourceDatabaseUnchanged,
        sourceSecretsUnchanged
    }));
}

static async Task RunStorageSmokeAsync(string dataRoot)
{
    var services = new InfrastructureServices(dataRoot);
    await services.InitializeAsync();

    var character = new Character
    {
        Name = "持久化自检角色",
        Description = "用于项目内一次性存储验证。",
        Personality = "稳定",
        Scenario = "本地",
        FirstMessage = "第一条消息"
    };
    await services.Characters.UpsertAsync(character);

    var conversation = new Conversation
    {
        CharacterId = character.Id,
        Title = "持久化自检会话"
    };
    await services.Conversations.UpsertAsync(conversation);

    var first = new ChatMessage
    {
        ConversationId = conversation.Id,
        SenderKind = MessageSenderKind.Character,
        SenderId = character.Id,
        Content = "第一条消息"
    };
    var second = new ChatMessage
    {
        ConversationId = conversation.Id,
        SenderKind = MessageSenderKind.User,
        SenderId = "local-user",
        Content = "第二条消息"
    };
    await services.Conversations.AddMessageAsync(first);
    await services.Conversations.AddMessageAsync(second);
    await services.Conversations.UpdateMessageContentAsync(second.Id, "第二条消息（已编辑）");

    var fork = await services.Conversations.ForkThroughMessageAsync(conversation.Id, first.Id);
    var forkMessages = await services.Conversations.ListMessagesAsync(fork.Id);
    Assert(forkMessages.Count == 1, "独立分支必须只复制到指定消息。");
    Assert(forkMessages[0].Id != first.Id, "独立分支不得共享消息 ID。");

    await services.Conversations.DeleteMessageAsync(second.Id, includeSubsequent: false);
    var remaining = await services.Conversations.ListMessagesAsync(conversation.Id);
    Assert(remaining.Count == 1, "永久删除单条消息不得影响前序消息。");

    await services.MemoryBanks.SaveBodyAsync(character.Id, "记忆银行自检正文", 5000);
    var secondCharacter = new Character
    {
        Name = "持久化自检角色乙",
        Description = "用于群聊界面与存储验证。",
        Personality = "直接",
        Scenario = "本地",
        FirstMessage = "群聊第一条消息"
    };
    await services.Characters.UpsertAsync(secondCharacter);
    var group = new Conversation
    {
        Title = "持久化自检群聊",
        Mode = ConversationMode.Group
    };
    await services.GroupChats.CreateAsync(
        group,
        new GroupChatSettings
        {
            ConversationId = group.Id,
            RelayMode = GroupRelayMode.MentionDirected,
            AutoContinueEnabled = false
        },
        [
            new GroupChatMember
            {
                ConversationId = group.Id,
                CharacterId = character.Id,
                SortIndex = 0
            },
            new GroupChatMember
            {
                ConversationId = group.Id,
                CharacterId = secondCharacter.Id,
                SortIndex = 1
            }
        ]);
    await services.Conversations.AddMessageAsync(new ChatMessage
    {
        ConversationId = group.Id,
        SenderKind = MessageSenderKind.Character,
        SenderId = character.Id,
        Content = "群聊界面验证消息。@持久化自检角色乙"
    });
    await services.MemoryBanks.SaveBodyAsync(
        MemoryOwnerIds.ForGroup(group.Id),
        "群聊独立记忆自检正文",
        5000);
    await services.Settings.SetAsync("smoke.marker", "persisted");

    var reopened = new InfrastructureServices(dataRoot);
    await reopened.InitializeAsync();
    var reopenedCharacter = await reopened.Characters.GetAsync(character.Id);
    var reopenedMessages = await reopened.Conversations.ListMessagesAsync(conversation.Id);
    var reopenedMemory = await reopened.MemoryBanks.GetAsync(character.Id);
    var reopenedGroupSettings = await reopened.GroupChats.GetSettingsAsync(group.Id);
    var reopenedGroupMembers = await reopened.GroupChats.ListMembersAsync(group.Id);
    var reopenedGroupMemory = await reopened.MemoryBanks.GetAsync(
        MemoryOwnerIds.ForGroup(group.Id));
    var reopenedMarker = await reopened.Settings.GetAsync("smoke.marker");

    Assert(reopenedCharacter?.Name == character.Name, "角色重启重读失败。");
    Assert(reopenedMessages.Count == 1, "会话消息重启重读失败。");
    Assert(reopenedMemory?.Body == "记忆银行自检正文", "记忆银行重启重读失败。");
    Assert(reopenedMemory?.TargetTokens == 5000, "记忆目标 tokens 重启重读失败。");
    Assert(
        reopenedGroupSettings?.RelayMode == GroupRelayMode.MentionDirected,
        "群聊设置重启重读失败。");
    Assert(reopenedGroupMembers.Count == 2, "群聊成员重启重读失败。");
    Assert(
        reopenedGroupMemory?.Body == "群聊独立记忆自检正文",
        "群聊独立记忆重启重读失败。");
    Assert(reopenedMarker == "persisted", "应用设置重启重读失败。");
    Assert(await reopened.Providers.CountEnabledAsync() >= 3, "默认接入商未初始化。");

    var deleteConversation = new Conversation
    {
        CharacterId = character.Id,
        Title = "会话物理删除自检"
    };
    await reopened.Conversations.UpsertAsync(deleteConversation);
    var deleteMessage = new ChatMessage
    {
        ConversationId = deleteConversation.Id,
        SenderKind = MessageSenderKind.Character,
        SenderId = character.Id,
        Content = "这条自检消息和本地索引都应被删除。"
    };
    await reopened.Conversations.AddMessageAsync(deleteMessage);
    await reopened.Conversations.AddCandidateAsync(new MessageCandidate
    {
        MessageId = deleteMessage.Id,
        CandidateIndex = 0,
        Content = deleteMessage.Content
    });
    await reopened.Conversations.DeleteConversationAsync(deleteConversation.Id);
    Assert(
        await reopened.Conversations.GetAsync(deleteConversation.Id) is null,
        "会话物理删除后仍能读取会话行。");
    Assert(
        (await reopened.Conversations.ListMessagesAsync(deleteConversation.Id)).Count == 0,
        "会话物理删除后仍能读取消息。");
    await using (var cacheConnection = reopened.Database.CreateConnection())
    {
        await cacheConnection.OpenAsync();
        await using var cacheCommand = cacheConnection.CreateCommand();
        cacheCommand.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM message_search WHERE conversation_id = $id),
                (SELECT COUNT(*) FROM message_search_trigram WHERE conversation_id = $id),
                (SELECT COUNT(*) FROM message_candidates WHERE message_id = $messageId);
            """;
        cacheCommand.Parameters.AddWithValue("$id", deleteConversation.Id);
        cacheCommand.Parameters.AddWithValue("$messageId", deleteMessage.Id);
        await using var cacheReader = await cacheCommand.ExecuteReaderAsync();
        Assert(await cacheReader.ReadAsync(), "删除检查缺少缓存统计结果。");
        Assert(cacheReader.GetInt64(0) == 0, "旧 FTS 消息索引未清理。");
        Assert(cacheReader.GetInt64(1) == 0, "trigram 消息索引未清理。");
        Assert(cacheReader.GetInt64(2) == 0, "候选回复缓存未清理。");
    }
    Assert(
        (await reopened.MemoryBanks.GetAsync(character.Id))?.Body == "记忆银行自检正文",
        "删除聊天记录错误影响了角色整体记忆。");

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        status = "PASS",
        dataRoot,
        characterId = character.Id,
        conversationId = conversation.Id,
        forkConversationId = fork.Id,
        groupConversationId = group.Id,
        checks = new[]
        {
            "character-upsert-reopen",
            "conversation-message-reopen",
            "message-edit",
            "independent-fork-through-message",
            "soft-delete-and-single-purge",
            "memory-bank-reopen",
            "group-settings-members-memory-reopen",
            "app-setting-reopen",
            "default-providers",
            "conversation-hard-delete-preserves-character-memory"
        },
        apiRequests = 0
    }));
}

static async Task RunCharacterCardSmokeAsync(
    string sourcePath,
    string outputRoot)
{
    var sourceFullPath = Path.GetFullPath(sourcePath);
    var outputFullPath = Path.GetFullPath(outputRoot);
    if (!File.Exists(sourceFullPath))
    {
        throw new FileNotFoundException("真实角色卡验证源文件不存在。", sourceFullPath);
    }

    Directory.CreateDirectory(outputFullPath);
    var dataRoot = Path.Combine(outputFullPath, "data");
    var exportedPath = Path.Combine(
        outputFullPath,
        "yukino-spec-v2-roundtrip.png");
    var reportPath = Path.Combine(
        outputFullPath,
        "m4-character-card-roundtrip-report.json");
    if (File.Exists(exportedPath) || File.Exists(reportPath))
    {
        throw new IOException(
            "验证输出已经存在；为避免覆盖，请使用新的输出目录。");
    }

    var sourceBytes = await File.ReadAllBytesAsync(sourceFullPath);
    var sourceHashBefore = Sha256(sourceBytes);
    var sourceVisualFingerprint = PngVisualFingerprint(sourceBytes);
    var services = new InfrastructureServices(dataRoot);
    await services.InitializeAsync();
    var imported = await services.CharacterCards.ImportAsync(sourceFullPath);
    var storedSourceHash = Sha256(
        await File.ReadAllBytesAsync(imported.Character.SourceCardPath));
    Assert(
        sourceHashBefore == storedSourceHash,
        "导入后的只读源副本与验证源文件哈希不一致。");

    var originalRoot = JsonNode.Parse(imported.Character.RawCardJson)!.AsObject();
    var unknownValues = imported.Report.UnknownFieldPaths.ToDictionary(
        path => path,
        path => ReadJsonPath(originalRoot, path)?.ToJsonString(),
        StringComparer.Ordinal);
    var editedRoot = (JsonObject)originalRoot.DeepClone();
    var data = editedRoot["data"] as JsonObject ?? editedRoot;
    const string marker = "TavernDesk M4.1 真实 PNG 往返验证";
    imported.Character.Name = $"{imported.Character.Name} · M4.1验证";
    data["name"] = imported.Character.Name;
    data["creator_notes"] = marker;
    data["system_prompt"] = $"保持原角色设定。{marker}";
    data["post_history_instructions"] = $"继续遵守上下文。{marker}";
    var greetings = data["alternate_greetings"] as JsonArray ?? new JsonArray();
    greetings.Add($"备选开场 · {marker}");
    data["alternate_greetings"] = greetings;
    var extensions = data["extensions"] as JsonObject ?? new JsonObject();
    extensions["depth_prompt"] = new JsonObject
    {
        ["prompt"] = $"深度提示 · {marker}",
        ["depth"] = 6,
        ["role"] = "assistant"
    };
    extensions["taverndesk_m4_verification"] = new JsonObject
    {
        ["marker"] = marker,
        ["preserve"] = true
    };
    data["extensions"] = extensions;
    imported.Character.RawCardJson = editedRoot.ToJsonString();
    await services.Characters.UpsertAsync(imported.Character);

    var exportResult = await services.CharacterCards.ExportAsync(
        imported.Character,
        exportedPath);
    var reimported = await services.CharacterCards.ImportAsync(exportedPath);
    var roundTripRoot = JsonNode.Parse(
        reimported.Character.RawCardJson)!.AsObject();
    var roundTripData = roundTripRoot["data"] as JsonObject ?? roundTripRoot;
    Assert(
        reimported.Character.Name == imported.Character.Name,
        "编辑后的角色名未通过真实 PNG 往返。");
    Assert(
        roundTripData["creator_notes"]?.GetValue<string>() == marker,
        "creator_notes 未通过真实 PNG 往返。");
    Assert(
        roundTripData["system_prompt"]?.GetValue<string>()?.Contains(marker)
            == true,
        "system_prompt 未通过真实 PNG 往返。");
    Assert(
        roundTripData["post_history_instructions"]?.GetValue<string>()
            ?.Contains(marker) == true,
        "post_history_instructions 未通过真实 PNG 往返。");
    Assert(
        roundTripData["alternate_greetings"] is JsonArray roundTripGreetings
        && roundTripGreetings.Any(item =>
            item?.GetValue<string>()?.Contains(marker) == true),
        "alternate_greetings 未通过真实 PNG 往返。");
    Assert(
        roundTripData["extensions"]?["depth_prompt"]?["depth"]?.GetValue<int>()
            == 6,
        "depth_prompt 未通过真实 PNG 往返。");
    Assert(
        roundTripData["extensions"]?["taverndesk_m4_verification"]?["preserve"]
            ?.GetValue<bool>() == true,
        "新增未知扩展字段未通过真实 PNG 往返。");
    foreach (var pair in unknownValues)
    {
        Assert(
            ReadJsonPath(roundTripRoot, pair.Key)?.ToJsonString() == pair.Value,
            $"原角色卡未知字段 {pair.Key} 未被保留。");
    }

    var originalResources = imported.Report.Resources
        .ToDictionary(resource => resource.RelativePath, StringComparer.Ordinal);
    var roundTripResources = reimported.Report.Resources
        .ToDictionary(resource => resource.RelativePath, StringComparer.Ordinal);
    foreach (var pair in originalResources)
    {
        Assert(
            roundTripResources.TryGetValue(pair.Key, out var resource)
            && resource.Sha256 == pair.Value.Sha256
            && resource.Size == pair.Value.Size,
            $"角色卡资源 {pair.Key} 未被完整保留。");
    }

    var exportedBytes = await File.ReadAllBytesAsync(exportedPath);
    var exportedVisualFingerprint = PngVisualFingerprint(exportedBytes);
    Assert(
        exportedVisualFingerprint == sourceVisualFingerprint,
        "导出 PNG 的图像像素数据与源文件不一致。");
    var sourceHashAfter = Sha256(await File.ReadAllBytesAsync(sourceFullPath));
    Assert(
        sourceHashAfter == sourceHashBefore,
        "真实验证源 PNG 在验证过程中发生了变化。");

    var report = new
    {
        status = "PASS",
        sourcePath = sourceFullPath,
        sourceSha256 = sourceHashBefore,
        sourceUntouched = sourceHashAfter == sourceHashBefore,
        storedSourceSha256 = storedSourceHash,
        exportedPath,
        exportedSha256 = Sha256(exportedBytes),
        visualFingerprint = sourceVisualFingerprint,
        importedSpec = imported.Report.Spec,
        importedSpecVersion = imported.Report.SpecVersion,
        originalName = originalRoot["data"]?["name"]?.GetValue<string>()
                       ?? originalRoot["name"]?.GetValue<string>(),
        roundTripName = reimported.Character.Name,
        unknownFieldCount = unknownValues.Count,
        unknownFieldsPreserved = unknownValues.Keys.ToArray(),
        originalResourceCount = originalResources.Count,
        roundTripResourceCount = roundTripResources.Count,
        preservedResourceCount = exportResult.PreservedResourceCount,
        advancedFieldsVerified = new[]
        {
            "creator_notes",
            "system_prompt",
            "post_history_instructions",
            "alternate_greetings",
            "extensions.depth_prompt",
            "extensions.taverndesk_m4_verification"
        },
        warnings = imported.Report.Warnings
            .Concat(exportResult.Warnings)
            .Concat(reimported.Report.Warnings)
            .Distinct()
            .ToArray(),
        apiRequests = 0
    };
    await using (var reportStream = new FileStream(
                     reportPath,
                     FileMode.CreateNew,
                     FileAccess.Write,
                     FileShare.None))
    {
        await JsonSerializer.SerializeAsync(
            reportStream,
            report,
            new JsonSerializerOptions { WriteIndented = true });
    }

    Console.WriteLine(JsonSerializer.Serialize(report));
}

static async Task RunProviderLiveSmokeAsync(
    string outputRoot,
    string baseUrl,
    string modelId,
    int contextLimit)
{
    var outputFullPath = Path.GetFullPath(outputRoot);
    if (Directory.Exists(outputFullPath)
        && Directory.EnumerateFileSystemEntries(outputFullPath).Any())
    {
        throw new IOException("验证输出目录不是空目录；为避免覆盖，请使用新目录。");
    }

    Directory.CreateDirectory(outputFullPath);
    var dataRoot = Path.Combine(outputFullPath, "data");
    var reportPath = Path.Combine(
        outputFullPath,
        "m42-lmstudio-live-report.json");
    const string providerId = "m42-live-lmstudio";
    const int validationOutputLimit = 1024;
    var services = new InfrastructureServices(dataRoot);
    await services.InitializeAsync();
    await services.Providers.UpsertAsync(new ProviderProfile
    {
        Id = providerId,
        Name = "LM Studio · M4.2 Live",
        AdapterKind = ProviderAdapterKind.OpenAiCompatible,
        BaseUrl = baseUrl,
        RequestTimeoutSeconds = 300,
        IsEnabled = true
    });

    var models = await services.ProviderGateway.RefreshModelsAsync(providerId);
    Assert(
        models.Any(model => string.Equals(
            model.ModelId,
            modelId,
            StringComparison.Ordinal)),
        $"LM Studio 模型目录没有返回目标模型 {modelId}。");
    await services.Models.ReplaceAsync(providerId, models);
    var selectedModel = (await services.Models.ListAsync(providerId))
        .Where(model => model.ModelKind is ModelCatalogKind.Chat
            or ModelCatalogKind.Custom)
        .Single(model => string.Equals(
            model.ModelId,
            modelId,
            StringComparison.Ordinal));
    selectedModel.ContextLimit = contextLimit;
    selectedModel.MaxOutputTokens = validationOutputLimit;
    await services.Models.UpsertAsync(selectedModel);
    await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
    {
        FunctionKind = ModelFunctionKind.Chat,
        ProviderId = providerId,
        ModelId = modelId,
        ContextLimit = contextLimit,
        MaxOutputTokens = validationOutputLimit,
        Temperature = 0.1,
        TopP = 0.9
    });

    // Keep a synthetic conversation in the isolated data root for optional visual QA.
    var character = new Character
    {
        Name = "M4.2 流式验证角色",
        Description = "仅用于 LM Studio 流式界面验证，不含用户资料。",
        FirstMessage = "这是隔离验证会话。"
    };
    await services.Characters.UpsertAsync(character);
    var conversation = new Conversation
    {
        CharacterId = character.Id,
        Title = "M4.2 thinking 与正文切换"
    };
    await services.Conversations.UpsertAsync(conversation);
    await services.Conversations.AddMessageAsync(new ChatMessage
    {
        ConversationId = conversation.Id,
        SenderKind = MessageSenderKind.Character,
        SenderId = character.Id,
        Content = character.FirstMessage
    });

    var single = await ConsumeLiveStreamAsync(
        services.ProviderGateway,
        CreateLiveRequest(providerId, modelId, "SINGLE_OK", validationOutputLimit));
    Assert(single.Completed, "单流没有收到完成事件。");
    Assert(
        single.Content.Contains("SINGLE_OK", StringComparison.Ordinal),
        "单流最终正文没有返回 SINGLE_OK。");

    var cancellation = await CancelLiveStreamAfterFirstEventAsync(
        services.ProviderGateway,
        CreateLiveRequest(providerId, modelId, "CANCEL_OK", 2048));
    Assert(cancellation.EventSeen, "取消验证未收到任何流事件。");
    Assert(cancellation.CancellationObserved, "取消令牌没有中断当前流。");

    var alphaTask = ConsumeLiveStreamAsync(
        services.ProviderGateway,
        CreateLiveRequest(providerId, modelId, "ALPHA_OK", validationOutputLimit));
    var betaTask = ConsumeLiveStreamAsync(
        services.ProviderGateway,
        CreateLiveRequest(providerId, modelId, "BETA_OK", validationOutputLimit));
    await Task.WhenAll(alphaTask, betaTask);
    var alpha = await alphaTask;
    var beta = await betaTask;
    Assert(
        alpha.Content.Contains("ALPHA_OK", StringComparison.Ordinal)
        && !alpha.Content.Contains("BETA_OK", StringComparison.Ordinal),
        "ALPHA 流正文发生缺失或串线。");
    Assert(
        beta.Content.Contains("BETA_OK", StringComparison.Ordinal)
        && !beta.Content.Contains("ALPHA_OK", StringComparison.Ordinal),
        "BETA 流正文发生缺失或串线。");
    var concurrentOverlap =
        alpha.FirstEventAt <= beta.CompletedAt
        && beta.FirstEventAt <= alpha.CompletedAt;
    Assert(concurrentOverlap, "两条请求没有形成可观测的并发重叠。");

    var report = new
    {
        status = "PASS",
        baseUrl,
        modelId,
        contextLimit,
        configuredMaxOutputTokens = validationOutputLimit,
        discoveredModelCount = models.Count,
        targetModelDiscovered = true,
        single,
        cancellation,
        concurrent = new
        {
            overlap = concurrentOverlap,
            alpha,
            beta
        },
        isolatedUiDataRoot = dataRoot,
        isolatedConversationId = conversation.Id,
        checks = new[]
        {
            "bare-server-root-normalizes-to-v1",
            "target-model-discovery",
            "reasoning-separated-from-final-content",
            "single-stream-final-content",
            "per-request-cancellation",
            "two-stream-concurrency-without-crossing",
            "actual-usage-and-finish-reason-when-reported"
        },
        apiRequests = 5,
        privateContentSent = false
    };
    await using (var reportStream = new FileStream(
                     reportPath,
                     FileMode.CreateNew,
                     FileAccess.Write,
                     FileShare.None))
    {
        await JsonSerializer.SerializeAsync(
            reportStream,
            report,
            new JsonSerializerOptions { WriteIndented = true });
    }

    Console.WriteLine(JsonSerializer.Serialize(report));
}

static ModelExecutionRequest CreateLiveRequest(
    string providerId,
    string modelId,
    string expectedLabel,
    int maxOutputTokens) =>
    new(
        providerId,
        modelId,
        [
            new ProviderChatMessage(
                "system",
                "You are a local protocol test. In the final answer, output only the requested ASCII label."),
            new ProviderChatMessage(
                "user",
                $"Return exactly {expectedLabel}.")
        ],
        maxOutputTokens,
        Temperature: 0.1,
        TopP: 0.9);

static async Task<LiveStreamResult> ConsumeLiveStreamAsync(
    IProviderGateway gateway,
    ModelExecutionRequest request)
{
    var startedAt = DateTimeOffset.UtcNow;
    DateTimeOffset? firstEventAt = null;
    var completedAt = startedAt;
    var content = new StringBuilder();
    var sawReasoning = false;
    var completed = false;
    var eventCount = 0;
    ProviderTokenUsage? usage = null;
    string? finishReason = null;
    await foreach (var streamEvent in gateway.StreamChatAsync(request))
    {
        firstEventAt ??= DateTimeOffset.UtcNow;
        eventCount++;
        switch (streamEvent.Kind)
        {
            case ProviderStreamEventKind.Reasoning:
                sawReasoning = true;
                break;
            case ProviderStreamEventKind.Content:
                content.Append(streamEvent.Content);
                break;
            case ProviderStreamEventKind.Completed:
                completed = true;
                usage = streamEvent.Usage;
                finishReason = streamEvent.FinishReason;
                break;
        }
    }

    completedAt = DateTimeOffset.UtcNow;
    return new LiveStreamResult(
        request.Messages.Last().Content.Split(' ').Last().TrimEnd('.'),
        content.ToString(),
        sawReasoning,
        completed,
        eventCount,
        usage,
        finishReason,
        startedAt,
        firstEventAt ?? completedAt,
        completedAt);
}

static async Task<LiveCancellationResult> CancelLiveStreamAfterFirstEventAsync(
    IProviderGateway gateway,
    ModelExecutionRequest request)
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    var eventSeen = false;
    var cancellationObserved = false;
    try
    {
        await foreach (var _ in gateway.StreamChatAsync(
                           request,
                           cancellation.Token))
        {
            eventSeen = true;
            cancellation.Cancel();
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        cancellationObserved = true;
    }

    return new LiveCancellationResult(
        eventSeen,
        cancellationObserved,
        DateTimeOffset.UtcNow);
}

static IReadOnlyList<string> CreateCampaignUserActions(
    string firstCharacterName,
    string secondCharacterName) =>
[
    $"我停在安全距离外，先观察目标物及周围痕迹；请{firstCharacterName}分析规则与风险，请{secondCharacterName}留意现场容易被忽略的变化。在 GM 裁定前，我不触碰目标物，也不预设观察结果。",
    $"依据上一回合 GM 已公开的局面，我提议做最小风险分工：我警戒入口，请{firstCharacterName}检查现有线索之间是否矛盾，请{secondCharacterName}观察我们可能漏掉的细节。所有实际发现和成败仍交由 GM 裁定。",
    $"面对当前公开局面，我先示意停止冒进，并与{firstCharacterName}、{secondCharacterName}确认共同目标；随后只执行现有证据支持的最保守协同行动。若必须接触目标物，我仅做最低限度辅助，把具体后果留给 GM 裁定。"
];

static void CopyDirectoryFiles(string sourcePath, string destinationPath)
{
    if (!Directory.Exists(sourcePath))
    {
        return;
    }

    foreach (var sourceFile in Directory.EnumerateFiles(
                 sourcePath,
                 "*",
                 SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
        var destinationFile = Path.Combine(destinationPath, relativePath);
        Directory.CreateDirectory(
            Path.GetDirectoryName(destinationFile)
            ?? throw new InvalidOperationException(
                "无法解析临时密钥副本目录。"));
        File.Copy(sourceFile, destinationFile, overwrite: false);
    }
}

static string DirectoryFingerprint(string directoryPath)
{
    if (!Directory.Exists(directoryPath))
    {
        return "missing";
    }

    var entries = Directory.EnumerateFiles(
            directoryPath,
            "*",
            SearchOption.AllDirectories)
        .Select(path => new
        {
            Path = path,
            RelativePath = Path.GetRelativePath(directoryPath, path)
                .Replace(Path.DirectorySeparatorChar, '/')
        })
        .OrderBy(item => item.RelativePath, StringComparer.Ordinal)
        .ToArray();
    var manifest = new StringBuilder();
    foreach (var entry in entries)
    {
        manifest.Append(entry.RelativePath)
            .Append('\0')
            .Append(new FileInfo(entry.Path).Length)
            .Append('\0')
            .Append(Sha256(File.ReadAllBytes(entry.Path)))
            .Append('\n');
    }

    return Sha256(Encoding.UTF8.GetBytes(manifest.ToString()));
}

static bool ContainsHiddenReasoningMarkup(string content) =>
    content.Contains("<think", StringComparison.OrdinalIgnoreCase)
    || content.Contains("</think", StringComparison.OrdinalIgnoreCase)
    || content.Contains("<thinking", StringComparison.OrdinalIgnoreCase)
    || content.Contains("</thinking", StringComparison.OrdinalIgnoreCase)
    || content.Contains("<analysis", StringComparison.OrdinalIgnoreCase)
    || content.Contains("</analysis", StringComparison.OrdinalIgnoreCase);

static string ContentPreview(string content, int maxChars = 240)
{
    var normalized = string.Join(
        " ",
        content.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries));
    return normalized.Length <= maxChars
        ? normalized
        : $"{normalized[..maxChars]}…";
}

static async Task WriteNewTextFileAsync(string path, string content)
{
    await using var stream = new FileStream(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None);
    await using var writer = new StreamWriter(
        stream,
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    await writer.WriteAsync(content);
}

static string BuildCampaignTranscript(
    CampaignAggregate aggregate,
    IReadOnlyList<LiveCampaignRoundAudit> roundAudits,
    IReadOnlyList<LiveCampaignRequestAudit> requests)
{
    var names = aggregate.Participants.ToDictionary(
        item => item.Id,
        item => item.DisplayName,
        StringComparer.Ordinal);
    var transcript = new StringBuilder()
        .AppendLine("# TavernDesk 后台真实跑团转录")
        .AppendLine()
        .AppendLine($"- Campaign ID: `{aggregate.Campaign.Id}`")
        .AppendLine($"- 标题：{aggregate.Campaign.Title}")
        .AppendLine(
            $"- 模式：{aggregate.Campaign.FlowPreset}；GM：{aggregate.Campaign.GmKind}")
        .AppendLine(
            $"- 已完整裁定回合：{roundAudits.Count}；当前回合：{aggregate.Campaign.CurrentRound}")
        .AppendLine($"- Provider 请求：{requests.Count}")
        .AppendLine()
        .AppendLine("## 完整事件")
        .AppendLine();
    foreach (var campaignEvent in aggregate.Events.OrderBy(
                 item => item.SequenceNo))
    {
        var actor = names.TryGetValue(campaignEvent.ActorId, out var name)
            ? name
            : campaignEvent.ActorId switch
            {
                "gm" => "GM（剧本开场）",
                "gm:ai" => "AI GM",
                "gm:user" => "USER GM",
                _ => campaignEvent.ActorId
            };
        transcript
            .AppendLine(
                $"### #{campaignEvent.SequenceNo} · 第 {campaignEvent.RoundNo} 回合 · {campaignEvent.Kind} · {actor}")
            .AppendLine()
            .AppendLine(
                $"- 可见性：{campaignEvent.Visibility}；生成状态：{campaignEvent.GenerationStatus}；结束原因：{campaignEvent.EndReason}；锁定：{campaignEvent.IsLocked}")
            .AppendLine()
            .AppendLine(campaignEvent.Content.TrimEnd())
            .AppendLine();
    }

    transcript
        .AppendLine("## Provider 请求审计")
        .AppendLine()
        .AppendLine(
            "此处保留请求顺序、路由、输入长度/哈希、输出长度/哈希、时序和 usage；不记录明文 API Key 或隐藏推理正文。")
        .AppendLine();
    foreach (var request in requests.OrderBy(item => item.Sequence))
    {
        var usage = request.Usage is null
            ? "未报告"
            : $"prompt={request.Usage.PromptTokens}, completion={request.Usage.CompletionTokens}, total={request.Usage.TotalTokens}, cached={request.Usage.CachedPromptTokens ?? 0}";
        transcript
            .AppendLine(
                $"### 请求 {request.Sequence} · `{request.SessionId}`")
            .AppendLine()
            .AppendLine(
                $"- 路由：`{request.ProviderId}` / `{request.ModelId}`")
            .AppendLine(
                $"- 完成：{request.Completed}；finish reason：`{request.FinishReason ?? string.Empty}`；事件数：{request.EventCount}")
            .AppendLine(
                $"- 输入消息：{request.Messages.Count}；输出：{request.ContentChars} chars；隐藏推理流：{request.ReasoningChars} chars")
            .AppendLine($"- Usage：{usage}")
            .AppendLine();
    }

    return transcript.ToString();
}

static JsonNode? ReadJsonPath(JsonObject root, string path)
{
    if (path == "$")
    {
        return root;
    }

    JsonNode? current = root;
    foreach (var segment in path[2..].Split('.'))
    {
        current = current?[segment];
    }

    return current;
}

static string Sha256(byte[] value) =>
    Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

static string PngVisualFingerprint(byte[] png)
{
    if (png.Length < 8
        || !png.AsSpan(0, 8).SequenceEqual(
            new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
    {
        throw new InvalidDataException("角色卡不是有效 PNG 文件。");
    }

    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    var offset = 8;
    while (offset + 12 <= png.Length)
    {
        var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            png.AsSpan(offset, 4)));
        if (length < 0 || offset + length + 12 > png.Length)
        {
            throw new InvalidDataException("PNG chunk 长度越界。");
        }

        var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
        if (type is "IHDR" or "PLTE" or "tRNS" or "IDAT")
        {
            hash.AppendData(png.AsSpan(offset + 4, length + 4));
        }

        offset += length + 12;
        if (type == "IEND")
        {
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
    }

    throw new InvalidDataException("PNG 缺少 IEND chunk。");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

internal sealed record LiveStreamResult(
    string ExpectedLabel,
    string Content,
    bool SawReasoning,
    bool Completed,
    int EventCount,
    ProviderTokenUsage? Usage,
    string? FinishReason,
    DateTimeOffset StartedAt,
    DateTimeOffset FirstEventAt,
    DateTimeOffset CompletedAt);

internal sealed record LiveCancellationResult(
    bool EventSeen,
    bool CancellationObserved,
    DateTimeOffset CompletedAt);

internal sealed record LiveCampaignCharacterAudit(
    string CharacterId,
    string CharacterName,
    int SnapshotChars,
    string SnapshotSha256,
    IReadOnlyList<string> SnapshotWarnings);

internal sealed record LiveCampaignRoundAudit(
    int RoundNo,
    string UserAction,
    IReadOnlyList<string> AiActionEventIds,
    string GmResolutionEventId,
    int FirstRequestSequence,
    int LastRequestSequence);

internal sealed record LiveCampaignMessageAudit(
    string Role,
    int ContentChars,
    string ContentSha256);

internal sealed record LiveCampaignRequestAudit(
    int Sequence,
    string ProviderId,
    string ModelId,
    string? SessionId,
    int MaxOutputTokens,
    double Temperature,
    double TopP,
    bool? ReasoningEnabled,
    IReadOnlyList<LiveCampaignMessageAudit> Messages,
    bool Completed,
    int EventCount,
    int ContentChars,
    string ContentSha256,
    int ReasoningChars,
    string ReasoningSha256,
    ProviderTokenUsage? Usage,
    string? FinishReason,
    DateTimeOffset StartedAt,
    DateTimeOffset FirstEventAt,
    DateTimeOffset CompletedAt,
    long DurationMilliseconds);

internal sealed class AuditingProviderGateway : IProviderGateway
{
    private readonly IProviderGateway _inner;
    private readonly object _sync = new();
    private readonly List<LiveCampaignRequestAudit> _requests = [];
    private int _sequence;

    public AuditingProviderGateway(IProviderGateway inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<LiveCampaignRequestAudit> Requests
    {
        get
        {
            lock (_sync)
            {
                return _requests
                    .OrderBy(item => item.Sequence)
                    .ToArray();
            }
        }
    }

    public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
        string providerId,
        CancellationToken cancellationToken = default) =>
        _inner.RefreshModelsAsync(providerId, cancellationToken);

    public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
        ModelExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        DateTimeOffset? firstEventAt = null;
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var eventCount = 0;
        var completed = false;
        ProviderTokenUsage? usage = null;
        string? finishReason = null;
        try
        {
            await foreach (var streamEvent in _inner.StreamChatAsync(
                               request,
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                firstEventAt ??= DateTimeOffset.UtcNow;
                eventCount++;
                switch (streamEvent.Kind)
                {
                    case ProviderStreamEventKind.Content:
                        content.Append(streamEvent.Content);
                        break;
                    case ProviderStreamEventKind.Reasoning:
                        reasoning.Append(streamEvent.Content);
                        break;
                    case ProviderStreamEventKind.Completed:
                        completed = true;
                        usage = streamEvent.Usage;
                        finishReason = streamEvent.FinishReason;
                        break;
                }

                yield return streamEvent;
            }
        }
        finally
        {
            var completedAt = DateTimeOffset.UtcNow;
            var audit = new LiveCampaignRequestAudit(
                Interlocked.Increment(ref _sequence),
                request.ProviderId,
                request.ModelId,
                request.SessionId,
                request.MaxOutputTokens,
                request.Temperature,
                request.TopP,
                request.ReasoningEnabled,
                request.Messages.Select(message =>
                    new LiveCampaignMessageAudit(
                        message.Role,
                        message.Content.Length,
                        Hash(message.Content))).ToArray(),
                completed,
                eventCount,
                content.Length,
                Hash(content.ToString()),
                reasoning.Length,
                Hash(reasoning.ToString()),
                usage,
                finishReason,
                startedAt,
                firstEventAt ?? completedAt,
                completedAt,
                (long)(completedAt - startedAt).TotalMilliseconds);
            lock (_sync)
            {
                _requests.Add(audit);
            }
        }
    }

    private static string Hash(string content) =>
        Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
}
