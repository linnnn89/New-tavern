using System.Net;
using System.Net.Http;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;
using TavernDesk.Infrastructure.Providers;

namespace TavernDesk.Tests;

public sealed class ProviderAndStreamingTests
{
    [Fact]
    public async Task DpapiSecretNeverEntersSqliteOrProtectedFileAsPlaintext()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        const string plaintext = "sk-fixture-plain-secret-7YQ2";

        var reference = await services.Secrets.SaveAsync(
            "provider-secret-fixture",
            plaintext);
        var profile = new ProviderProfile
        {
            Name = "DPAPI 测试",
            BaseUrl = "https://mock.invalid/v1",
            SecretReference = reference
        };
        await services.Providers.UpsertAsync(profile);

        Assert.Equal(plaintext, await services.Secrets.ReadAsync(reference));
        Assert.True(await services.Secrets.ExistsAsync(reference));
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        Assert.Equal(
            -1,
            (await ReadSharedBytesAsync(services.Paths.DatabasePath))
            .AsSpan()
            .IndexOf(plainBytes));
        var secretFile = Assert.Single(
            Directory.GetFiles(services.Paths.SecretsDirectory, "*.secret"));
        Assert.Equal(
            -1,
            (await File.ReadAllBytesAsync(secretFile))
            .AsSpan()
            .IndexOf(plainBytes));

        await services.Secrets.DeleteAsync(reference);
        Assert.False(await services.Secrets.ExistsAsync(reference));
    }

    [Fact]
    public async Task ModelRefreshPreservesManualLimitsAndAssignmentRejectsImpossibleOutput()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        const string providerId = "builtin-openrouter";
        await services.Models.ReplaceAsync(
            providerId,
            [new ProviderModelDescriptor("fixture-model", "Fixture")]);
        var model = Assert.Single(await services.Models.ListAsync(providerId));
        model.ContextLimit = 131072;
        model.MaxOutputTokens = 8192;
        await services.Models.UpsertAsync(model);

        await services.Models.ReplaceAsync(
            providerId,
            [new ProviderModelDescriptor("fixture-model", "Fixture renamed")]);

        var refreshed = Assert.Single(await services.Models.ListAsync(providerId));
        Assert.Equal("Fixture renamed", refreshed.DisplayName);
        Assert.Equal(131072, refreshed.ContextLimit);
        Assert.Equal(8192, refreshed.MaxOutputTokens);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
            {
                FunctionKind = ModelFunctionKind.Chat,
                ProviderId = providerId,
                ModelId = refreshed.ModelId,
                ContextLimit = 4096,
                MaxOutputTokens = 8192
            }));
    }

    [Fact]
    public async Task OpenAiCompatibleGatewayParsesModelsAndSseWithoutExternalNetwork()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var profile = new ProviderProfile
        {
            Id = "mock-openai",
            Name = "Mock OpenAI",
            BaseUrl = "https://mock.local",
            SecretReference = "fixture-secret"
        };
        await services.Providers.UpsertAsync(profile);
        var handler = new FixtureHttpHandler();
        var gateway = new OpenAiCompatibleProviderGateway(
            services.Providers,
            new FixedSecretStore("fixture-key"),
            new HttpClient(handler));

        var models = await gateway.RefreshModelsAsync(profile.Id);
        var events = new List<ProviderStreamEvent>();
        await foreach (var streamEvent in gateway.StreamChatAsync(
                           new ModelExecutionRequest(
                               profile.Id,
                               "model-a",
                               [
                                   new ProviderChatMessage("system", "system fixture"),
                                   new ProviderChatMessage("user", "hello")
                               ],
                               512,
                               0.7,
                               1)))
        {
            events.Add(streamEvent);
        }

        Assert.Equal(
            ["model-a", "model-b", "model-null-limits"],
            models.Select(model => model.ModelId));
        var modelA = Assert.Single(models, model => model.ModelId == "model-a");
        Assert.Equal(131072, modelA.ContextLimit);
        Assert.Equal(16384, modelA.MaxOutputTokens);
        var modelB = Assert.Single(models, model => model.ModelId == "model-b");
        Assert.Equal(256000, modelB.ContextLimit);
        Assert.Null(modelB.MaxOutputTokens);
        var modelWithNullLimits = Assert.Single(
            models,
            model => model.ModelId == "model-null-limits");
        Assert.Null(modelWithNullLimits.ContextLimit);
        Assert.Null(modelWithNullLimits.MaxOutputTokens);
        Assert.Equal(
            [
                ProviderStreamEventKind.Reasoning,
                ProviderStreamEventKind.Content,
                ProviderStreamEventKind.Content,
                ProviderStreamEventKind.Completed
            ],
            events.Select(streamEvent => streamEvent.Kind));
        Assert.Equal(
            ["你", "好"],
            events
                .Where(streamEvent =>
                    streamEvent.Kind == ProviderStreamEventKind.Content)
                .Select(streamEvent => streamEvent.Content));
        Assert.DoesNotContain(
            events,
            streamEvent => streamEvent.Content.Contains(
                "internal fixture",
                StringComparison.Ordinal));
        var completion = Assert.Single(
            events,
            streamEvent => streamEvent.Kind == ProviderStreamEventKind.Completed);
        Assert.Equal("stop", completion.FinishReason);
        Assert.Equal(
            new ProviderTokenUsage(7, 5, 12, 3, 4, 3),
            completion.Usage);
        Assert.Equal("Bearer fixture-key", handler.Authorization);
        Assert.Contains("\"stream\":true", handler.ChatRequestJson);
        Assert.Contains(
            "\"stream_options\":{\"include_usage\":true}",
            handler.ChatRequestJson);
        Assert.Contains("\"role\":\"user\"", handler.ChatRequestJson);
        Assert.Equal(1, handler.ModelsRequestCount);
        Assert.Equal(1, handler.ChatRequestCount);
    }

    [Fact]
    public async Task OpenRouterDeepSeekReasoningIsSerializedWithoutFalsePositiveRouting()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var otherProvider = new ProviderProfile
        {
            Id = "mock-other-provider",
            Name = "Other Provider",
            BaseUrl = "https://mock.local/v1"
        };
        await services.Providers.UpsertAsync(otherProvider);
        var handler = new FixtureHttpHandler();
        var gateway = new OpenAiCompatibleProviderGateway(
            services.Providers,
            new FixedSecretStore(string.Empty),
            new HttpClient(handler));

        async Task<JsonElement> SendAsync(
            string providerId,
            string modelId,
            bool reasoningEnabled)
        {
            await foreach (var _ in gateway.StreamChatAsync(
                               new ModelExecutionRequest(
                                   providerId,
                                   modelId,
                                   [new ProviderChatMessage("user", "fixture")],
                                    64,
                                    0.2,
                                    1,
                                    reasoningEnabled,
                                    SessionId: "chat:fixture")))
            {
            }

            using var document = JsonDocument.Parse(handler.ChatRequestJson);
            return document.RootElement.Clone();
        }

        var enabled = await SendAsync(
            "builtin-openrouter",
            "deepseek/deepseek-v4-flash-0731",
            true);
        Assert.True(
            enabled.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.Equal(
            "chat:fixture",
            enabled.GetProperty("session_id").GetString());

        var disabled = await SendAsync(
            "builtin-openrouter",
            "DeepSeek/deepseek-v4-flash",
            false);
        Assert.Equal(
            "none",
            disabled.GetProperty("reasoning").GetProperty("effort").GetString());

        var falsePositive = await SendAsync(
            "builtin-openrouter",
            "vendor/notdeepseeker-v4",
            true);
        Assert.False(falsePositive.TryGetProperty("reasoning", out _));

        var wrongProvider = await SendAsync(
            otherProvider.Id,
            "deepseek/deepseek-v4-flash-0731",
            true);
        Assert.False(wrongProvider.TryGetProperty("reasoning", out _));
        Assert.False(wrongProvider.TryGetProperty("session_id", out _));
    }

    [Fact]
    public async Task PromptSettingsSavesRestoresAndExportsOneCompleteProfile()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var exportPath = Path.Combine(workspace.Root, "prompt-profile.json");
        var viewModel = new PromptSettingsViewModel(
            services.GlobalPrompts,
            new NoOpFileDialogService(exportPath));
        await viewModel.LoadAsync();
        var memoryPrompts = viewModel.Categories
            .Single(category => category.Name == "记忆银行")
            .Prompts;
        Assert.Equal(2, memoryPrompts.Count);
        Assert.DoesNotContain(
            memoryPrompts,
            prompt => prompt.Label.Contains("User 模板", StringComparison.Ordinal));
        var groupPrompts = viewModel.Categories
            .Single(category => category.Name == "群聊")
            .Prompts;
        Assert.Equal(2, groupPrompts.Count);
        Assert.DoesNotContain(
            groupPrompts,
            prompt => prompt.Label.Contains("User 模板", StringComparison.Ordinal));
        viewModel.Open(GlobalPromptKey.CampaignGmSystem);
        Assert.NotNull(viewModel.SelectedPrompt);
        viewModel.SelectedPrompt!.Text = "CUSTOM_GM_PROMPT";

        viewModel.SaveCommand.Execute(null);
        await WaitUntilAsync(() =>
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignGmSystem)
            == "CUSTOM_GM_PROMPT");
        viewModel.ExportCommand.Execute(null);
        await WaitUntilAsync(() => File.Exists(exportPath));

        using var document = JsonDocument.Parse(
            await File.ReadAllTextAsync(exportPath));
        Assert.Equal(
            GlobalPromptProfile.SchemaName,
            document.RootElement.GetProperty("Schema").GetString());
        var prompts = document.RootElement.GetProperty("Prompts");
        Assert.Equal(
            Enum.GetValues<GlobalPromptKey>().Length,
            prompts.EnumerateObject().Count());
        Assert.Equal(
            "CUSTOM_GM_PROMPT",
            prompts.GetProperty(nameof(GlobalPromptKey.CampaignGmSystem))
                .GetString());
        Assert.False(prompts.TryGetProperty("MemoryUpdateUserTemplate", out _));
        Assert.False(prompts.TryGetProperty("MemoryCompressionUserTemplate", out _));
        Assert.False(prompts.TryGetProperty("GroupMemoryMergeUserTemplate", out _));

        viewModel.SelectedPrompt.Text = "TRANSIENT";
        viewModel.RestoreDefaultCommand.Execute(null);
        Assert.Equal(
            GlobalPromptDefaults.CampaignGmSystem,
            viewModel.SelectedPrompt.Text);
    }

    [Fact]
    public async Task LegacyMemoryUserTemplatesAreRemovedFromStoredGlobalProfile()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.MemoryUpdateSystem)] =
                        "CUSTOM_UPDATE_PROMPT",
                    ["MemoryUpdateUserTemplate"] = "OLD_UPDATE_USER_TEMPLATE",
                    [nameof(GlobalPromptKey.MemoryCompressionSystem)] =
                        "CUSTOM_COMPRESSION_PROMPT",
                    ["MemoryCompressionUserTemplate"] =
                        "OLD_COMPRESSION_USER_TEMPLATE",
                    [nameof(GlobalPromptKey.GroupMemoryMergeSystem)] =
                        "CUSTOM_GROUP_MERGE_PROMPT",
                    ["GroupMemoryMergeUserTemplate"] =
                        "OLD_GROUP_MERGE_USER_TEMPLATE"
                }
            }));

        await services.GlobalPrompts.InitializeAsync();

        var stored = await services.Settings.GetAsync("prompts.global.v1");
        Assert.NotNull(stored);
        using var document = JsonDocument.Parse(stored);
        var prompts = document.RootElement.GetProperty("Prompts");
        Assert.False(prompts.TryGetProperty("MemoryUpdateUserTemplate", out _));
        Assert.False(prompts.TryGetProperty("MemoryCompressionUserTemplate", out _));
        Assert.False(prompts.TryGetProperty("GroupMemoryMergeUserTemplate", out _));
        Assert.Equal(
            "CUSTOM_UPDATE_PROMPT",
            prompts.GetProperty(nameof(GlobalPromptKey.MemoryUpdateSystem))
                .GetString());
        Assert.Equal(
            "CUSTOM_COMPRESSION_PROMPT",
            prompts.GetProperty(nameof(GlobalPromptKey.MemoryCompressionSystem))
                .GetString());
        Assert.Equal(
            "CUSTOM_GROUP_MERGE_PROMPT",
            prompts.GetProperty(nameof(GlobalPromptKey.GroupMemoryMergeSystem))
                .GetString());
    }

    [Fact]
    public async Task DefaultProvidersExposeOnlyTheFiveSupportedBackends()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();

        var profiles = await services.Providers.ListAsync();
        var profilesById = profiles.ToDictionary(profile => profile.Id);
        Assert.Equal(5, profilesById.Count);
        Assert.Equal(
            ("OpenRouter", ProviderAdapterKind.OpenAiCompatible, "https://openrouter.ai/api/v1"),
            (
                profilesById["builtin-openrouter"].Name,
                profilesById["builtin-openrouter"].AdapterKind,
                profilesById["builtin-openrouter"].BaseUrl));
        Assert.Equal(
            ("硅基流动", ProviderAdapterKind.OpenAiCompatible, "https://api.siliconflow.cn/v1"),
            (
                profilesById["builtin-siliconflow"].Name,
                profilesById["builtin-siliconflow"].AdapterKind,
                profilesById["builtin-siliconflow"].BaseUrl));
        Assert.Equal(
            ("DeepSeek 官方 API", ProviderAdapterKind.OpenAiCompatible, "https://api.deepseek.com"),
            (
                profilesById["builtin-deepseek"].Name,
                profilesById["builtin-deepseek"].AdapterKind,
                profilesById["builtin-deepseek"].BaseUrl));
        Assert.Equal(
            ("LM Studio（本地）", ProviderAdapterKind.OpenAiCompatible, "http://127.0.0.1:6543"),
            (
                profilesById["builtin-lm-studio"].Name,
                profilesById["builtin-lm-studio"].AdapterKind,
                profilesById["builtin-lm-studio"].BaseUrl));
        Assert.Equal(
            ("Grok CLI（订阅登录）", ProviderAdapterKind.GrokCli, "grok://local"),
            (
                profilesById["builtin-grok-cli"].Name,
                profilesById["builtin-grok-cli"].AdapterKind,
                profilesById["builtin-grok-cli"].BaseUrl));
        Assert.True(Directory.Exists(services.Paths.SecretsDirectory));
        Assert.True(Directory.Exists(services.Paths.GrokCliRuntimeDirectory));
    }

    [Fact]
    public async Task ProviderSettingsOnlyOffersSupportedProvidersAndAdapterKinds()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Providers.UpsertAsync(new ProviderProfile
        {
            Id = "legacy-provider",
            Name = "旧接入商",
            AdapterKind = ProviderAdapterKind.Anthropic,
            BaseUrl = "https://legacy.invalid"
        });
        await services.InitializeAsync();
        var viewModel = new ProviderSettingsViewModel(
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Secrets,
            services.ProviderGateway,
            services.ContextBudget,
            new NoOpInteractionService(),
            services.GlobalPrompts,
            new NoOpFileDialogService());
        await viewModel.LoadAsync();

        Assert.False(Assert.IsType<ProviderProfile>(
            await services.Providers.GetAsync("legacy-provider")).IsEnabled);
        Assert.Equal(
            [ProviderAdapterKind.OpenAiCompatible, ProviderAdapterKind.GrokCli],
            viewModel.AdapterKinds);
        Assert.Equal(
            [
                "builtin-deepseek",
                "builtin-grok-cli",
                "builtin-lm-studio",
                "builtin-openrouter",
                "builtin-siliconflow"
            ],
            viewModel.Profiles
                .Select(profile => profile.Id)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ProviderDeletionPurgesOwnedDataAndDoesNotRestoreDeletedDefaults()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var openRouter = Assert.IsType<ProviderProfile>(
            await services.Providers.GetAsync("builtin-openrouter"));
        var secretReference = await services.Secrets.SaveAsync(
            openRouter.Id,
            "fixture-delete-secret");
        openRouter.SecretReference = secretReference;
        await services.Providers.UpsertAsync(openRouter);
        await services.Models.ReplaceAsync(
            openRouter.Id,
            [new ProviderModelDescriptor("fixture-model", "Fixture Model")]);
        await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
        {
            FunctionKind = ModelFunctionKind.Chat,
            ProviderId = openRouter.Id,
            ModelId = "fixture-model",
            ContextLimit = 32768,
            MaxOutputTokens = 4096
        });

        var viewModel = new ProviderSettingsViewModel(
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Secrets,
            services.ProviderGateway,
            services.ContextBudget,
            new NoOpInteractionService(confirmProviderDeletion: true),
            services.GlobalPrompts,
            new NoOpFileDialogService());
        await viewModel.LoadAsync();
        var profileToDelete = Assert.Single(
            viewModel.Profiles,
            profile => profile.Id == openRouter.Id);

        viewModel.DeleteProviderCommand.Execute(profileToDelete);
        await WaitUntilAsync(
            () => viewModel.Profiles.All(profile => profile.Id != openRouter.Id));

        Assert.Null(await services.Providers.GetAsync(openRouter.Id));
        Assert.False(await services.Secrets.ExistsAsync(secretReference));
        Assert.Empty(await services.Models.ListAsync(openRouter.Id));
        Assert.Null(await services.ModelAssignments.GetAsync(ModelFunctionKind.Chat));
        var chatOverview = Assert.Single(
            viewModel.AssignmentOverview,
            item => item.Value == ModelFunctionKind.Chat);
        Assert.Equal("未分配", chatOverview.ProviderName);

        foreach (var remaining in await services.Providers.ListAsync())
        {
            await services.Providers.DeleteAsync(remaining.Id);
        }

        await services.Providers.EnsureDefaultsAsync();
        Assert.Empty(await services.Providers.ListAsync());
    }

    [Fact]
    public async Task GrokCliGatewayUsesSubscriptionRunnerWithoutApiKey()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var runner = new RecordingGrokCliRunner();
        var gateway = new GrokCliProviderGateway(
            services.Providers,
            services.Paths,
            runner);

        var models = await gateway.RefreshModelsAsync("builtin-grok-cli");
        var events = new List<ProviderStreamEvent>();
        await foreach (var item in gateway.StreamChatAsync(
                           new ModelExecutionRequest(
                               "builtin-grok-cli",
                               GrokCliProviderGateway.DefaultModelId,
                               [
                                   new ProviderChatMessage(
                                       "system",
                                       "你是测试角色。"),
                                   new ProviderChatMessage("user", "你好")
                               ],
                               1024,
                               0.8,
                               1)))
        {
            events.Add(item);
        }

        var model = Assert.Single(models);
        Assert.Equal(GrokCliProviderGateway.DefaultModelId, model.ModelId);
        Assert.Null(runner.ModelId);
        Assert.Equal(
            services.Paths.GrokCliRuntimeDirectory,
            runner.WorkingDirectory);
        using var promptDocument = JsonDocument.Parse(runner.Prompt);
        var promptMessages = promptDocument.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .ToArray();
        Assert.Equal("system", promptMessages[0].GetProperty("role").GetString());
        Assert.Equal(
            "你是测试角色。",
            promptMessages[0].GetProperty("content").GetString());
        Assert.Equal("user", promptMessages[1].GetProperty("role").GetString());
        Assert.Equal(
            [
                ProviderStreamEventKind.Content,
                ProviderStreamEventKind.Content,
                ProviderStreamEventKind.Completed
            ],
            events.Select(item => item.Kind));
        Assert.Equal(
            "订阅回复",
            string.Concat(events
                .Where(item => item.Kind == ProviderStreamEventKind.Content)
                .Select(item => item.Content)));
    }

    [Fact]
    public async Task ProviderSelectionLoadsTheCompleteMatchingEditBuffer()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var viewModel = new ProviderSettingsViewModel(
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Secrets,
            services.ProviderGateway,
            services.ContextBudget,
            new NoOpInteractionService(),
            services.GlobalPrompts,
            new NoOpFileDialogService());
        await viewModel.LoadAsync();
        var openRouter = Assert.Single(
            viewModel.Profiles,
            profile => profile.Id == "builtin-openrouter");
        var previousEditor = viewModel.Editor;

        Assert.True(await viewModel.SelectProfileAsync(openRouter));

        Assert.Same(openRouter, viewModel.SelectedProfile);
        Assert.NotSame(previousEditor, viewModel.Editor);
        Assert.Equal("OpenRouter", viewModel.Editor.Name);
        Assert.Equal(
            ProviderAdapterKind.OpenAiCompatible,
            viewModel.Editor.AdapterKind);
        Assert.Equal(
            "https://openrouter.ai/api/v1",
            viewModel.Editor.BaseUrl);
        Assert.Equal("300", viewModel.Editor.RequestTimeoutSeconds);
        Assert.False(viewModel.Editor.IsDirty);
    }

    [Fact]
    public async Task FunctionAssignmentOverviewShowsAllCurrentAssignmentsAtOnce()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
        {
            FunctionKind = ModelFunctionKind.Chat,
            ProviderId = "builtin-openrouter",
            ModelId = "deepseek/deepseek-v4-flash-0731",
            ContextLimit = 1_048_576,
            MaxOutputTokens = 65_536,
            Temperature = 0.7,
            TopP = 1,
            UpdatedAt = DateTimeOffset.Now
        });
        var viewModel = new ProviderSettingsViewModel(
            services.Providers,
            services.Models,
            services.ModelAssignments,
            services.Secrets,
            services.ProviderGateway,
            services.ContextBudget,
            new NoOpInteractionService(),
            services.GlobalPrompts,
            new NoOpFileDialogService());

        await viewModel.LoadAsync();

        Assert.Equal(viewModel.FunctionOptions.Count, viewModel.AssignmentOverview.Count);
        var chat = Assert.Single(
            viewModel.AssignmentOverview,
            item => item.Value == ModelFunctionKind.Chat);
        Assert.Equal("OpenRouter", chat.ProviderName);
        Assert.Equal("deepseek/deepseek-v4-flash-0731", chat.ModelId);
        Assert.True(chat.IsReasoningAvailable);
        Assert.False(chat.IsReasoningEnabled);
        viewModel.ToggleReasoningCommand.Execute(chat);
        ModelFunctionAssignment? updated = null;
        for (var attempt = 0;
             attempt < 25 && updated?.ReasoningEnabled != true;
             attempt++)
        {
            await Task.Delay(20);
            updated = await services.ModelAssignments.GetAsync(
                ModelFunctionKind.Chat);
        }

        Assert.True(updated?.ReasoningEnabled);
        var memoryUpdate = Assert.Single(
            viewModel.AssignmentOverview,
            item => item.Value == ModelFunctionKind.MemoryUpdate);
        Assert.Equal("未分配", memoryUpdate.ProviderName);
        Assert.Equal("—", memoryUpdate.ModelId);
    }

    [Fact]
    public async Task OpenAiCompatibleGatewayExplainsCloudBillingErrors()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var profile = new ProviderProfile
        {
            Id = "openrouter-error",
            Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1"
        };
        await services.Providers.UpsertAsync(profile);
        var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Content = new StringContent(
                """
                {"error":{"code":402,"message":"Insufficient credits","metadata":{"error_type":"payment_required"}}}
                """,
                Encoding.UTF8,
                "application/json")
        };
        var gateway = new OpenAiCompatibleProviderGateway(
            services.Providers,
            new FixedSecretStore("fixture-key"),
            new HttpClient(new FixedResponseHandler(response)));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => gateway.RefreshModelsAsync(profile.Id));

        Assert.Equal(HttpStatusCode.PaymentRequired, exception.StatusCode);
        Assert.Contains("账户余额或额度不足", exception.Message);
        Assert.Contains("payment_required", exception.Message);
        Assert.DoesNotContain("fixture-key", exception.Message);
    }

    [Fact]
    public async Task OpenAiCompatibleGatewayReadsOpenRouterCachedTokenUsage()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var profile = new ProviderProfile
        {
            Id = "openrouter-cache-usage",
            Name = "OpenRouter",
            BaseUrl = "https://openrouter.ai/api/v1"
        };
        await services.Providers.UpsertAsync(profile);

        var events = await ReadGatewayEventsAsync(
            services,
            profile.Id,
            """
            {"choices":[{"message":{"content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12,"prompt_tokens_details":{"cached_tokens":8}}}
            """,
            "application/json");

        var completed = Assert.Single(
            events,
            item => item.Kind == ProviderStreamEventKind.Completed);
        Assert.Equal(8, completed.Usage?.CachedPromptTokens);
        Assert.Equal(2, completed.Usage?.UncachedPromptTokens);
    }

    [Fact]
    public async Task ReasoningNormalizationUsesSemanticAliasesAndSplitTagFallback()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var profile = new ProviderProfile
        {
            Id = "reasoning-shapes",
            Name = "Reasoning Shapes",
            BaseUrl = "https://reasoning.local"
        };
        await services.Providers.UpsertAsync(profile);

        var structured = await ReadGatewayEventsAsync(
            services,
            profile.Id,
            """
            data: {"choices":[{"delta":{"reasoning":"one"}}]}

            data: {"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","text":"two"}]}}]}

            data: {"choices":[{"delta":{"thinking":"three"}}]}

            data: {"choices":[{"delta":{"reasoning_text":"four"}}]}

            data: {"choices":[{"delta":{"content":"STRUCTURED_OK"},"finish_reason":"stop"}]}

            data: [DONE]

            """,
            "text/event-stream");
        Assert.Single(
            structured,
            item => item.Kind == ProviderStreamEventKind.Reasoning);
        Assert.Equal(
            "STRUCTURED_OK",
            string.Concat(structured
                .Where(item => item.Kind == ProviderStreamEventKind.Content)
                .Select(item => item.Content)));

        var tagged = await ReadGatewayEventsAsync(
            services,
            profile.Id,
            """
            data: {"choices":[{"delta":{"content":"<thi"}}]}

            data: {"choices":[{"delta":{"content":"nk>hidden reasoning"}}]}

            data: {"choices":[{"delta":{"content":" across chunks</th"}}]}

            data: {"choices":[{"delta":{"content":"ink>TAG_OK"},"finish_reason":"stop"}]}

            data: [DONE]

            """,
            "text/event-stream");
        Assert.Single(
            tagged,
            item => item.Kind == ProviderStreamEventKind.Reasoning);
        Assert.Equal(
            "TAG_OK",
            string.Concat(tagged
                .Where(item => item.Kind == ProviderStreamEventKind.Content)
                .Select(item => item.Content)));
        Assert.DoesNotContain(
            tagged,
            item => item.Content.Contains(
                "hidden reasoning",
                StringComparison.Ordinal));

        var literalTag = await ReadGatewayEventsAsync(
            services,
            profile.Id,
            """
            data: {"choices":[{"delta":{"content":"示例："}}]}

            data: {"choices":[{"delta":{"content":"<think>这是正文标签</think>"},"finish_reason":"stop"}]}

            data: [DONE]

            """,
            "text/event-stream");
        Assert.DoesNotContain(
            literalTag,
            item => item.Kind == ProviderStreamEventKind.Reasoning);
        Assert.Equal(
            "示例：<think>这是正文标签</think>",
            string.Concat(literalTag
                .Where(item => item.Kind == ProviderStreamEventKind.Content)
                .Select(item => item.Content)));

        var nonStreaming = await ReadGatewayEventsAsync(
            services,
            profile.Id,
            """
            {"choices":[{"message":{"reasoning_content":"hidden","content":"JSON_OK"},"finish_reason":"stop"}]}
            """,
            "application/json");
        Assert.Equal(
            [
                ProviderStreamEventKind.Reasoning,
                ProviderStreamEventKind.Content,
                ProviderStreamEventKind.Completed
            ],
            nonStreaming.Select(item => item.Kind));
        Assert.Equal("JSON_OK", nonStreaming[1].Content);
    }

    [Fact]
    public async Task TwoChatViewsCanStreamDifferentConversationsWithoutCrossingReplies()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var firstCharacter = new Character { Name = "角色 A" };
        var secondCharacter = new Character { Name = "角色 B" };
        await services.Characters.UpsertAsync(firstCharacter);
        await services.Characters.UpsertAsync(secondCharacter);
        var firstConversation = new Conversation
        {
            CharacterId = firstCharacter.Id,
            Title = "会话 A"
        };
        var secondConversation = new Conversation
        {
            CharacterId = secondCharacter.Id,
            Title = "会话 B"
        };
        await services.Conversations.UpsertAsync(firstConversation);
        await services.Conversations.UpsertAsync(secondConversation);
        await ConfigureFixtureChatModelAsync(services);
        var gateway = new ConversationEchoGateway();
        var firstView = CreateChatViewModel(services, gateway);
        var secondView = CreateChatViewModel(services, gateway);
        await Task.WhenAll(firstView.LoadAsync(), secondView.LoadAsync());
        await Task.WhenAll(
            firstView.OpenConversationAsync(firstConversation.Id),
            secondView.OpenConversationAsync(secondConversation.Id));
        await WaitUntilAsync(() =>
            firstView.SelectedConversation?.Id == firstConversation.Id
            && secondView.SelectedConversation?.Id == secondConversation.Id);

        firstView.ComposerText = "输入 A";
        secondView.ComposerText = "输入 B";
        await WaitUntilAsync(() =>
            firstView.SendLocalCommand.CanExecute(null)
            && secondView.SendLocalCommand.CanExecute(null));
        firstView.SendLocalCommand.Execute(null);
        await WaitUntilAsync(() =>
            services.GenerationCoordinator.GetState(firstConversation.Id).Status
                == ConversationGenerationStatus.Streaming);
        secondView.SendLocalCommand.Execute(null);
        await WaitUntilAsync(() =>
            services.GenerationCoordinator.GetState(firstConversation.Id).Status
                == ConversationGenerationStatus.Completed
            && services.GenerationCoordinator.GetState(secondConversation.Id).Status
                == ConversationGenerationStatus.Completed);
        await WaitUntilAsync(() =>
            !firstView.IsConversationBusy(firstConversation.Id)
            && !secondView.IsConversationBusy(secondConversation.Id));

        var firstMessages = await services.Conversations.ListMessagesAsync(
            firstConversation.Id);
        var secondMessages = await services.Conversations.ListMessagesAsync(
            secondConversation.Id);
        Assert.Equal(["输入 A", "回复：输入 A"], firstMessages.Select(message => message.Content));
        Assert.Equal(["输入 B", "回复：输入 B"], secondMessages.Select(message => message.Content));
        Assert.DoesNotContain(firstMessages, message => message.Content.Contains("输入 B"));
        Assert.DoesNotContain(secondMessages, message => message.Content.Contains("输入 A"));
        Assert.Equal(firstConversation.Id, firstView.SelectedConversation?.Id);
        Assert.Equal(secondConversation.Id, secondView.SelectedConversation?.Id);
        await firstView.DisposeAsync();
        await secondView.DisposeAsync();
    }

    [Fact]
    public async Task BlankLegacyChatPromptGetsRoleplayDefaultOnlyOnce()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.ChatSystem)] = string.Empty
                }
            }));

        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            GlobalPromptDefaults.ChatSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.ChatSystem));

        var explicitlyCleared = services.GlobalPrompts.Snapshot()
            .ToDictionary(item => item.Key, item => item.Value);
        explicitlyCleared[GlobalPromptKey.ChatSystem] = string.Empty;
        await services.GlobalPrompts.SaveAsync(explicitlyCleared);
        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            string.Empty,
            services.GlobalPrompts.Get(GlobalPromptKey.ChatSystem));
    }

    [Fact]
    public async Task ExactLegacyRoleplayDefaultsUpgradeWithoutOverwritingCustomPrompts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.ChatSystem)] =
                        """
                        你正在进行角色扮演对话。请把当前提供的角色卡视为你的身份与行为依据，根据角色名称、描述、性格、场景、对话示例、世界书和已确认记忆，持续一致地扮演该角色。
                        只描写该角色能够感知、思考、说出和实施的内容；不要替 USER 决定言行、心理或行动结果。
                        延续已有剧情、关系与语气，不要机械复述设定，不要声明自己是 AI，也不要无故跳出角色。只有 USER 明确要求讨论设定或退出扮演时，才进行相应说明。
                        """,
                    [nameof(GlobalPromptKey.CampaignPlayerSystem)] =
                        """
                        你是本次跑团的一名玩家，不是 GM。
                        只描述自己的意图、台词和可控行动；不得替 GM 判定结果，不得替 USER 或其他角色作决定。
                        不要重复复述全部上下文，直接给出本回合行动。
                        """,
                    [nameof(GlobalPromptKey.CampaignGmSystem)] =
                        "我的自定义 GM 提示词"
                }
            }));
        await services.Settings.SetAsync(
            "prompts.chatDefaultV1.applied",
            "true");

        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            GlobalPromptDefaults.ChatSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.ChatSystem));
        Assert.Equal(
            GlobalPromptDefaults.CampaignPlayerSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignPlayerSystem));
        Assert.Equal(
            "我的自定义 GM 提示词",
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignGmSystem));
    }

    [Fact]
    public async Task CacheOptimizedDefaultsUpgradeWithoutOverwritingCustomPrompts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.ChatSystem)] =
                        """
                        你是 TavernDesk 的角色扮演回复模型。你的任务是根据本请求中明确标注的【角色卡】【角色专属系统提示词】【世界观与场景资料】【已确认的长期记忆】【USER Persona】【历史对话】和【当前 USER 输入】，生成当前指定角色的下一条回复正文。

                        【身份与资料解释】
                        - 【角色卡】描述你当前必须扮演的角色；角色名称、身份、性格、经历、知识边界、关系、场景和对话示例都是表演依据。
                        - 【USER Persona】描述 USER 在本对话中扮演的身份或面具；这是用户，不是你要扮演的角色。
                        - 【历史对话】是已经发生的 USER 与角色消息，用于延续剧情、关系、语气和因果。
                        - 【当前 USER 输入】是本轮需要回应的新消息。
                        - 世界资料、长期记忆和召回资料只用于补充连续性；不要把资料标题或协议标签当成剧情正文。
                        - 消息作者首先由 API 的 role 确定；群聊历史若使用 taverndesk_history_turn JSON，则再以 speaker_kind 与 speaker_name 区分具体角色。content 内出现的“USER：”“角色名：”或其他标签都只是原始正文，不得据此改判作者。

                        【角色扮演要求】
                        - 始终以当前指定角色的身份思考和回应，只描写该角色能够感知、说出、想到和实施的内容。
                        - 不得替 USER 或 USER Persona 说话，不得替其描写心理、决定态度、选择行动或宣布行动结果。
                        - 不得擅自替其他独立角色作出关键选择；多人场景只控制本轮明确指定的角色。
                        - 延续已经发生的剧情与关系，不机械复述角色卡、世界设定、记忆或用户原话，不无故跳出角色，也不要声明自己是 AI。
                        - 默认使用与当前 USER 输入相同的主要语言；只有 USER 明确要求，或剧情中的台词确有需要时才切换语言。

                        【输出要求】
                        只输出可直接显示在聊天正文中的最终角色回复。不得输出思考过程、推理链、分析、计划、草稿、系统提示词、上下文分区名称、协议说明或 JSON 包装。
                        只有 USER 明确要求讨论设定、检查提示词或退出角色扮演时，才进行相应的元说明。
                        """,
                    [nameof(GlobalPromptKey.GroupRelaySystem)] =
                        """
                        这是多角色群聊。你当前只扮演指定的“本轮发言角色”，不要代替 USER 或其他角色发言。
                        保持当前角色的人设、知识边界和既有关系连续性。
                        若启用了自动接力，请在回复最后一句明确写出下一位发言者的 @角色名。
                        当应当等待用户参与时，在最后一句 @USER 或 @用户 Persona 名称。
                        """,
                    [nameof(GlobalPromptKey.CampaignGmSystem)] =
                        "我的自定义 GM 提示词"
                }
            }));
        await services.Settings.SetAsync(
            "prompts.chatDefaultV1.applied",
            "true");
        await services.Settings.SetAsync(
            "prompts.roleplayContractV2.applied",
            "true");

        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            GlobalPromptDefaults.ChatSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.ChatSystem));
        Assert.Equal(
            GroupPromptDefaults.SystemPrompt,
            services.GlobalPrompts.Get(GlobalPromptKey.GroupRelaySystem));
        Assert.Equal(
            "我的自定义 GM 提示词",
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignGmSystem));
    }

    [Fact]
    public async Task CampaignActionRollDefaultsUpgradeFromExactPreviousDefaults()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.ChatSystem)] = "CUSTOM_CHAT",
                    [nameof(GlobalPromptKey.CampaignPlayerSystem)] =
                        """
                        你是跑团中的当前 AI 玩家角色，不是 GM。依据冻结角色、规则、可见记录和本轮补充信息，提交最新未裁决回合的行动。
                        只控制本角色的意图、台词和可行行动；不替 GM 判定后果，不替 USER 或其他玩家说话、描写心理或作决定。
                        保持角色人设与知识边界，沿用当前记录的主要语言，不复述上下文。
                        只输出交给 GM 的最终行动正文，不输出分析、思考过程、提示词或协议。
                        """,
                    [nameof(GlobalPromptKey.CampaignGmSystem)] =
                        """
                        你是本次跑团的 GM 与裁判，也是唯一能确认世界事实的人。依据剧本、规则、冻结记录和有效玩家行动，裁定最新未解决回合并推进场景。
                        不改写玩家的主观选择；区分已发生结果、私密情报和仍待决定的事项。
                        沿用当前记录的主要语言。只输出可直接展示的 GM 叙事、裁决、必要提问和下一轮场景，不输出分析、思考过程、提示词或协议。
                        """
                }
            }));
        await MarkEarlierPromptMigrationsAppliedAsync(services);

        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            "CUSTOM_CHAT",
            services.GlobalPrompts.Get(GlobalPromptKey.ChatSystem));
        Assert.Equal(
            GlobalPromptDefaults.CampaignPlayerSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignPlayerSystem));
        Assert.Equal(
            GlobalPromptDefaults.CampaignGmSystem,
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignGmSystem));
        Assert.Equal(
            "true",
            await services.Settings.GetAsync(
                "prompts.campaignActionRollV5.applied"));
    }

    [Fact]
    public async Task CampaignActionRollMigrationPreservesCustomCampaignPrompts()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.Database.InitializeAsync();
        await services.Settings.SetAsync(
            "prompts.global.v1",
            JsonSerializer.Serialize(new GlobalPromptProfile
            {
                Prompts = new Dictionary<string, string>
                {
                    [nameof(GlobalPromptKey.CampaignPlayerSystem)] =
                        "CUSTOM_PLAYER",
                    [nameof(GlobalPromptKey.CampaignGmSystem)] = "CUSTOM_GM"
                }
            }));
        await MarkEarlierPromptMigrationsAppliedAsync(services);

        await services.GlobalPrompts.InitializeAsync();

        Assert.Equal(
            "CUSTOM_PLAYER",
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignPlayerSystem));
        Assert.Equal(
            "CUSTOM_GM",
            services.GlobalPrompts.Get(GlobalPromptKey.CampaignGmSystem));
    }

    [Fact]
    public async Task PersonalChatEditsTheCharacterCardsExistingSystemPrompt()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character
        {
            Name = "局部提示词角色",
            RawCardJson =
                """
                {
                  "spec": "chara_card_v3",
                  "spec_version": "3.0",
                  "data": {
                    "name": "局部提示词角色",
                    "description": "角色描述",
                    "personality": "",
                    "scenario": "",
                    "first_mes": "",
                    "mes_example": "",
                    "system_prompt": "",
                    "post_history_instructions": ""
                  }
                }
                """
        };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = character.Name,
            Mode = ConversationMode.SingleCharacter
        };
        await services.Conversations.UpsertAsync(conversation);
        var interaction = new NoOpInteractionService(
            editText: "只以当前角色能够感知和决定的内容回应。");
        var viewModel = CreateChatViewModel(
            services,
            services.ProviderGateway,
            interaction);
        await viewModel.LoadAsync();
        await viewModel.OpenConversationAsync(conversation.Id);
        await WaitUntilAsync(() =>
            viewModel.CharacterPromptCharacterName == character.Name
            && viewModel.EditCharacterSystemPromptCommand.CanExecute(null));

        viewModel.EditCharacterSystemPromptCommand.Execute(null);
        await WaitUntilAsync(() =>
            viewModel.CharacterSystemPrompt
            == "只以当前角色能够感知和决定的内容回应。");

        var persisted = Assert.IsType<Character>(
            await services.Characters.GetAsync(character.Id));
        using var document = JsonDocument.Parse(persisted.RawCardJson);
        Assert.Equal(
            viewModel.CharacterSystemPrompt,
            document.RootElement
                .GetProperty("data")
                .GetProperty("system_prompt")
                .GetString());
        var assembled = await services.ContextAssembler.AssembleAsync(
            new ContextAssemblyRequest(
                conversation.Id,
                "继续",
                32768,
                2048));
        Assert.Contains(assembled.Segments, segment =>
            segment.Id == $"character-system:{character.Id}"
            && segment.Content == viewModel.CharacterSystemPrompt);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ChatApiMessagesLabelRoleCardPersonaHistoryAndCurrentInput()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        await services.Settings.SetAsync("persona.name", "旅行者");
        await services.Settings.SetAsync(
            "persona.description",
            "来自现代世界，谨慎但愿意帮助同伴。");
        var character = new Character
        {
            Name = "雪乃",
            Description = "冷静而敏锐。",
            RawCardJson =
                """
                {
                  "spec": "chara_card_v3",
                  "spec_version": "3.0",
                  "data": {
                    "name": "雪乃",
                    "description": "冷静而敏锐。",
                    "personality": "克制",
                    "scenario": "陌生世界",
                    "first_mes": "",
                    "mes_example": "",
                    "system_prompt": "保持雪乃的措辞与判断方式。",
                    "post_history_instructions": "不要替 USER 作决定。"
                  }
                }
                """
        };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "结构化请求",
            Mode = ConversationMode.SingleCharacter
        };
        await services.Conversations.UpsertAsync(conversation);
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.User,
            SenderId = "local-user",
            Content = "我们之前已经抵达木叶。"
        });
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = character.Id,
            Content = "先观察周围，再决定下一步。"
        });
        await services.MemoryBanks.SaveBodyAsync(
            character.Id,
            "已确认：二人从现代世界抵达木叶。",
            5000);
        await ConfigureFixtureChatModelAsync(services);
        var gateway = new ConversationEchoGateway();
        var viewModel = CreateChatViewModel(services, gateway);
        await viewModel.LoadAsync();
        await viewModel.OpenConversationAsync(conversation.Id);
        await WaitUntilAsync(() =>
            viewModel.SelectedConversation?.Id == conversation.Id);
        viewModel.ComposerText = "我们现在去找火影吗？";
        await WaitUntilAsync(() =>
            viewModel.SendLocalCommand.CanExecute(null));
        await WaitUntilAsync(() =>
            viewModel.ApiRequestPreview.Contains(
                "我们现在去找火影吗？",
                StringComparison.Ordinal));

        viewModel.SendLocalCommand.Execute(null);
        await WaitUntilAsync(() => gateway.Requests.Count == 1);
        await WaitUntilAsync(() =>
            services.GenerationCoordinator.GetState(conversation.Id).Status
            == ConversationGenerationStatus.Completed);

        var request = Assert.Single(gateway.Requests);
        Assert.Equal($"chat:{conversation.Id}", request.SessionId);
        var messages = request.Messages.ToArray();
        Assert.Contains(messages, message =>
            message.Role == "system"
            && message.Content.StartsWith(
                "【角色扮演规则：全局预设】",
                StringComparison.Ordinal)
            && message.Content.Contains("不输出分析、思考过程"));
        Assert.Contains(messages, message =>
            message.Role == "system"
            && message.Content.StartsWith(
                "【角色卡：角色卡 · 雪乃】",
                StringComparison.Ordinal)
            && message.Content.Contains("名称：雪乃"));
        Assert.Contains(messages, message =>
            message.Role == "system"
            && message.Content.StartsWith(
                "【USER Persona：用户 Persona · 旅行者】",
                StringComparison.Ordinal)
            && message.Content.Contains("USER 在本对话中扮演：旅行者")
            && message.Content.Contains("来自现代世界"));
        Assert.Contains(messages, message =>
            message.Role == "system"
            && message.Content.StartsWith(
                "【长期记忆：角色记忆银行】",
                StringComparison.Ordinal)
            && message.Content.Contains("抵达木叶"));

        var globalRules = Array.FindIndex(
            messages,
            message => message.Content.StartsWith(
                "【角色扮演规则：全局预设】",
                StringComparison.Ordinal));
        var memory = Array.FindIndex(
            messages,
            message => message.Content.StartsWith(
                "【长期记忆：角色记忆银行】",
                StringComparison.Ordinal));
        var oldUserMessage = Array.FindIndex(
            messages,
            message => message.Role == "user"
                       && message.Content == "我们之前已经抵达木叶。");
        var oldCharacterMessage = Array.FindIndex(
            messages,
            message => message.Role == "assistant"
                       && message.Content == "先观察周围，再决定下一步。");
        var postHistory = Array.FindIndex(
            messages,
            message => message.Content.StartsWith(
                "【本轮附加要求：角色后置历史指令】",
                StringComparison.Ordinal));
        var currentInput = Array.FindLastIndex(
            messages,
            message => message.Role == "user"
                       && message.Content == "我们现在去找火影吗？");
        Assert.True(globalRules < memory);
        Assert.True(memory < oldUserMessage);
        Assert.True(oldUserMessage < oldCharacterMessage);
        Assert.True(oldCharacterMessage < postHistory);
        Assert.True(postHistory < currentInput);
        Assert.DoesNotContain(messages, message =>
            message.Content.StartsWith("【历史对话开始】", StringComparison.Ordinal)
            || message.Content.StartsWith("【历史对话结束】", StringComparison.Ordinal)
            || message.Content.StartsWith("【当前 USER 输入】", StringComparison.Ordinal));
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ReopenedChatViewReattachesToStreamAfterOriginalViewCloses()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "窗口角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "独立窗口会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        await ConfigureFixtureChatModelAsync(services);
        var gateway = new PausedStreamingGateway();
        var original = CreateChatViewModel(services, gateway);
        await original.LoadAsync();
        await original.OpenConversationAsync(conversation.Id);
        await WaitUntilAsync(() =>
            original.SelectedConversation?.Id == conversation.Id);
        original.ComposerText = "窗口关闭后继续";
        await WaitUntilAsync(() => original.SendLocalCommand.CanExecute(null));
        original.SendLocalCommand.Execute(null);
        await WaitUntilAsync(() =>
            services.GenerationSessions.Get(conversation.Id).PartialContent
                == "前半");

        await original.DisposeAsync();
        var reopened = CreateChatViewModel(services, gateway);
        await reopened.LoadAsync();
        await reopened.OpenConversationAsync(conversation.Id);
        await WaitUntilAsync(() =>
            reopened.Messages.Any(message => message.Content == "前半"));
        reopened.ComposerText = "不应重复发送";
        Assert.False(reopened.SendLocalCommand.CanExecute(null));

        gateway.Release();
        await WaitUntilAsync(() =>
            !services.GenerationSessions.Get(conversation.Id).IsBusy);
        await WaitUntilAsync(() =>
            reopened.Messages.Any(message => message.Content == "前半后半"));
        await WaitUntilAsync(() =>
            !reopened.IsConversationBusy(conversation.Id));

        var messages = await services.Conversations.ListMessagesAsync(
            conversation.Id);
        Assert.Equal(
            ["窗口关闭后继续", "前半后半"],
            messages.Select(message => message.Content));
        Assert.Equal(1, gateway.RequestCount);
        await reopened.DisposeAsync();
    }

    [Fact]
    public async Task ApplicationNavigationDoesNotCancelSharedGeneration()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "生命周期角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "生命周期会话"
        };
        await services.Conversations.UpsertAsync(conversation);
        var viewModel = new MainWindowViewModel(
            services,
            new NoOpFileDialogService(),
            new NoOpInteractionService());
        await viewModel.InitializeAsync();
        Assert.Equal(
            "本地数据已就绪 · 当前无生成请求",
            viewModel.RuntimeStatusText);
        await viewModel.Chat.OpenConversationAsync(conversation.Id);
        viewModel.ShowChatCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(
            viewModel.CurrentPage,
            viewModel.Chat));

        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<string>();
        var run = services.GenerationCoordinator.RunAsync(
            conversation.Id,
            token => HoldOpenStreamAsync(release.Task, token),
            (chunk, _) =>
            {
                received.Add(chunk);
                return ValueTask.CompletedTask;
            });
        await WaitUntilAsync(() =>
            services.GenerationCoordinator
                .GetState(conversation.Id)
                .Status == ConversationGenerationStatus.Streaming);
        await WaitUntilAsync(() =>
            viewModel.RuntimeStatusText == "本地数据已就绪 · 正在接收模型响应");

        viewModel.ShowSettingsCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(
            viewModel.CurrentPage,
            viewModel.Settings));
        Assert.Equal(
            ConversationGenerationStatus.Streaming,
            services.GenerationCoordinator
                .GetState(conversation.Id)
                .Status);

        viewModel.ShowDashboardCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(
            viewModel.CurrentPage,
            viewModel.Dashboard));
        viewModel.ShowCharactersCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(
            viewModel.CurrentPage,
            viewModel.Characters));
        viewModel.ShowChatCommand.Execute(null);
        await WaitUntilAsync(() => ReferenceEquals(
            viewModel.CurrentPage,
            viewModel.Chat));
        Assert.Equal(
            ConversationGenerationStatus.Streaming,
            services.GenerationCoordinator
                .GetState(conversation.Id)
                .Status);

        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["前半", "后半"], received);
        Assert.Equal(
            ConversationGenerationStatus.Completed,
            services.GenerationCoordinator
                .GetState(conversation.Id)
                .Status);
        Assert.Equal(
            "本地数据已就绪 · 当前无生成请求",
            viewModel.RuntimeStatusText);

        viewModel.Campaigns.OpenGlobalPromptCommand.Execute(
            nameof(GlobalPromptKey.CampaignGmSystem));
        await WaitUntilAsync(() =>
            ReferenceEquals(viewModel.CurrentPage, viewModel.Settings)
            && viewModel.Settings.Prompts.SelectedPrompt?.Key
            == GlobalPromptKey.CampaignGmSystem);
        Assert.Equal(3, viewModel.Settings.SelectedSettingsTabIndex);
        Assert.Equal("设置 · 提示词管理", viewModel.CurrentSection);
    }

    private static void SelectConversation(
        ChatViewModel viewModel,
        string conversationId)
    {
        var item = viewModel.ConversationGroups
            .SelectMany(group => group.AllConversations)
            .Single(conversation => conversation.Id == conversationId);
        viewModel.SelectConversationCommand.Execute(item);
    }

    private static async Task MarkEarlierPromptMigrationsAppliedAsync(
        InfrastructureServices services)
    {
        await services.Settings.SetAsync(
            "prompts.chatDefaultV1.applied",
            "true");
        await services.Settings.SetAsync(
            "prompts.roleplayContractV2.applied",
            "true");
        await services.Settings.SetAsync(
            "prompts.cacheOptimizedV3.applied",
            "true");
        await services.Settings.SetAsync(
            "prompts.memorySingleTemplateV4.applied",
            "true");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private static async Task ConfigureFixtureChatModelAsync(
        InfrastructureServices services)
    {
        await services.Providers.UpsertAsync(new ProviderProfile
        {
            Id = "fixture-provider",
            Name = "Fixture Provider",
            BaseUrl = "https://fixture.invalid/v1"
        });
        await services.Models.ReplaceAsync(
            "fixture-provider",
            [new ProviderModelDescriptor("fixture-model", "Fixture Model")]);
        await services.ModelAssignments.UpsertAsync(new ModelFunctionAssignment
        {
            FunctionKind = ModelFunctionKind.Chat,
            ProviderId = "fixture-provider",
            ModelId = "fixture-model",
            ContextLimit = 32768,
            MaxOutputTokens = 1024
        });
    }

    private static ChatViewModel CreateChatViewModel(
        InfrastructureServices services,
        IProviderGateway gateway,
        IUserInteractionService? interaction = null) =>
        new(
            services.Conversations,
            services.Characters,
            services.MemoryBanks,
            services.MemoryWorkflow,
            services.MemoryPrompts,
            services.GroupChats,
            services.GroupRelay,
            services.Retrieval,
            services.Presets,
            services.PresetResolver,
            services.ContextAssembler,
            services.ContextBudget,
            services.GenerationCoordinator,
            services.GenerationSessions,
            services.ModelAssignments,
            gateway,
            services.Settings,
            services.GlobalPrompts,
            interaction ?? new NoOpInteractionService(),
            services.ChatArchives,
            new NoOpFileDialogService());

    private static async IAsyncEnumerable<string> HoldOpenStreamAsync(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return "前半";
        await release.WaitAsync(cancellationToken);
        yield return "后半";
    }

    private static async Task<IReadOnlyList<ProviderStreamEvent>>
        ReadGatewayEventsAsync(
            InfrastructureServices services,
            string providerId,
            string responseBody,
            string mediaType)
    {
        var gateway = new OpenAiCompatibleProviderGateway(
            services.Providers,
            new FixedSecretStore(string.Empty),
            new HttpClient(new StaticChatResponseHandler(
                responseBody,
                mediaType)));
        var events = new List<ProviderStreamEvent>();
        await foreach (var streamEvent in gateway.StreamChatAsync(
                           new ModelExecutionRequest(
                               providerId,
                               "fixture-model",
                               [new ProviderChatMessage("user", "fixture")],
                               64,
                               0.2,
                               1)))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private static async Task<byte[]> ReadSharedBytesAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public int ModelsRequestCount { get; private set; }
        public int ChatRequestCount { get; private set; }
        public string Authorization { get; private set; } = string.Empty;
        public string ChatRequestJson { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString() ?? string.Empty;
            if (request.Method == HttpMethod.Get
                && request.RequestUri?.AbsolutePath == "/v1/models")
            {
                ModelsRequestCount++;
                return JsonResponse("""
                    {"data":[{"id":"model-b","context_length":256000},{"id":"model-a","name":"Model A","context_length":131072,"top_provider":{"max_completion_tokens":16384}},{"id":"model-null-limits","context_length":null,"max_completion_tokens":null,"top_provider":{"max_completion_tokens":null}}]}
                    """);
            }

            if (request.Method == HttpMethod.Post
                && request.RequestUri?.AbsolutePath.EndsWith(
                    "/chat/completions",
                    StringComparison.Ordinal) == true)
            {
                ChatRequestCount++;
                ChatRequestJson = await request.Content!.ReadAsStringAsync(
                    cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        data: {"choices":[{"delta":{"reasoning_content":"internal fixture"}}]}

                        data: {"choices":[{"delta":{"content":"你"}}]}

                        data: {"choices":[{"delta":{"content":"好"},"finish_reason":"stop"}]}

                        data: {"choices":[],"usage":{"prompt_tokens":7,"completion_tokens":5,"total_tokens":12,"prompt_cache_hit_tokens":4,"prompt_cache_miss_tokens":3,"completion_tokens_details":{"reasoning_tokens":3}}}

                        data: [DONE]

                        """,
                        Encoding.UTF8,
                        "text/event-stream")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json")
            };
    }

    private sealed class FixedSecretStore : ISecretStore
    {
        private readonly string _secret;

        public FixedSecretStore(string secret)
        {
            _secret = secret;
        }

        public Task<string> SaveAsync(
            string ownerId,
            string secret,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("fixture-secret");

        public Task<string?> ReadAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(_secret);

        public Task<bool> ExistsAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task DeleteAsync(
            string reference,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class StaticChatResponseHandler(
        string responseBody,
        string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseBody,
                    Encoding.UTF8,
                    mediaType)
            });
    }

    private sealed class FixedResponseHandler(
        HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class RecordingGrokCliRunner : IGrokCliRunner
    {
        public string Prompt { get; private set; } = string.Empty;
        public string? ModelId { get; private set; }
        public string WorkingDirectory { get; private set; } = string.Empty;

        public async IAsyncEnumerable<string> StreamReplyAsync(
            string prompt,
            string? modelId,
            string workingDirectory,
            TimeSpan requestTimeout,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Prompt = prompt;
            ModelId = modelId;
            WorkingDirectory = workingDirectory;
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return "订阅";
            yield return "回复";
        }
    }

    private sealed class ConversationEchoGateway : IProviderGateway
    {
        public ConcurrentQueue<ModelExecutionRequest> Requests { get; } = [];

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Enqueue(request);
            var input = request.Messages
                .Last(message => message.Role == "user")
                .Content;
            await Task.Delay(120, cancellationToken);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                "回复：");
            await Task.Delay(120, cancellationToken);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                input);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                Usage: new ProviderTokenUsage(8, 4, 12),
                FinishReason: "stop");
        }
    }

    private sealed class PausedStreamingGateway : IProviderGateway
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<IReadOnlyList<ProviderModelDescriptor>> RefreshModelsAsync(
            string providerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderModelDescriptor>>([]);

        public async IAsyncEnumerable<ProviderStreamEvent> StreamChatAsync(
            ModelExecutionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Reasoning);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                "前半");
            await _release.Task.WaitAsync(cancellationToken);
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Content,
                "后半");
            yield return new ProviderStreamEvent(
                ProviderStreamEventKind.Completed,
                Usage: new ProviderTokenUsage(12, 6, 18, 2),
                FinishReason: "stop");
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class NoOpInteractionService(
        bool confirmProviderDeletion = false,
        string? editText = null) : IUserInteractionService
    {
        public Task<string?> EditTextAsync(
            string title,
            string prompt,
            string initialText) =>
            Task.FromResult(editText);

        public DeleteMessageDecision ConfirmMessageDeletion() =>
            DeleteMessageDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName) =>
            UnsavedChangesDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName) =>
            UnsavedChangesDecision.Cancel;

        public bool ConfirmCharacterDeletion(string characterName, int conversationCount) => false;
        public bool ConfirmShelfDeletion(string shelfName) => false;
        public bool ConfirmPresetDeletion(string presetName) => false;
        public bool ConfirmProviderDeletion(string providerName) =>
            confirmProviderDeletion;
        public bool ConfirmSecretClear(string providerName) => false;
        public Task<GroupChatDraft?> CreateGroupChatAsync(
            IReadOnlyList<Character> characters) =>
            Task.FromResult<GroupChatDraft?>(null);
        public void CopyText(string text)
        {
        }
    }
}
