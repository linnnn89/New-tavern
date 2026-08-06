using TavernDesk.Core.Abstractions;
using TavernDesk.Infrastructure.Campaigns;
using TavernDesk.Infrastructure.Compatibility;
using TavernDesk.Infrastructure.Context;
using TavernDesk.Infrastructure.Group;
using TavernDesk.Infrastructure.Memory;
using TavernDesk.Infrastructure.Providers;
using TavernDesk.Infrastructure.Security;
using TavernDesk.Infrastructure.Storage;
using TavernDesk.Infrastructure.Worldbooks;

namespace TavernDesk.Infrastructure;

public sealed class InfrastructureServices
{
    public InfrastructureServices(string? dataRoot = null)
    {
        DataConfiguration = new AppDataConfiguration();
        Paths = new AppDataPaths(dataRoot, DataConfiguration);
        Database = new SqliteDatabase(Paths);
        DataLocation = new AppDataLocationService(
            DataConfiguration,
            Paths,
            Database);
        Characters = new SqliteCharacterRepository(Database, Paths);
        CharacterShelves = new SqliteCharacterShelfRepository(Database);
        CampaignScenarios = new SqliteCampaignScenarioRepository(Database, Paths);
        Campaigns = new SqliteCampaignRepository(Database);
        CampaignMemoryRepository = new SqliteCampaignMemoryRepository(Database);
        Conversations = new SqliteConversationRepository(Database);
        Providers = new SqliteProviderProfileRepository(Database);
        Models = new SqliteModelCatalogRepository(Database);
        ModelAssignments = new SqliteModelAssignmentRepository(Database);
        Settings = new SqliteAppSettingsRepository(Database);
        GlobalPrompts = new GlobalPromptConfigurationService(Settings);
        MemoryBanks = new SqliteMemoryBankService(Database);
        MemoryWorkflow = new SqliteMemoryWorkflowRepository(Database);
        MemoryPrompts = new MemoryPromptComposer();
        GroupChats = new SqliteGroupChatRepository(Database);
        GroupRelay = new GroupRelayPlanner();
        Retrieval = new SqliteMessageRetrievalRepository(Database);
        Presets = new SqlitePresetRepository(Database);
        PresetResolver = new PresetResolver(Presets);
        TokenEstimator = new ModelAwareTokenEstimator();
        CampaignContextPlanner = new CampaignContextPlanner(TokenEstimator, GlobalPrompts);
        ContextBudget = new DefaultContextBudgetProvider();
        MacroEngine = new SafeMacroEngine();
        WorldbookEngine = new CharacterWorldbookEngine(MacroEngine);
        Worldbooks = new SqliteWorldbookRepository(Database, Paths);
        // This coordinator is application-scoped. Every current or future chat
        // window must share it so closing a view cannot cancel an in-flight stream.
        GenerationCoordinator = new ConversationGenerationCoordinator();
        GenerationSessions = new ConversationGenerationSessionStore();
        Secrets = new WindowsDpapiSecretStore(Paths);
        var openAiCompatibleGateway =
            new OpenAiCompatibleProviderGateway(Providers, Secrets);
        var grokCliGateway = new GrokCliProviderGateway(Providers, Paths);
        ProviderGateway = new ProviderGatewayRouter(
            Providers,
            openAiCompatibleGateway,
            grokCliGateway);
        EmbeddingProviderGateway = (IEmbeddingProviderGateway)ProviderGateway;
        CampaignMemory = new CampaignMemoryUpdateService(
            Campaigns,
            CampaignMemoryRepository,
            ModelAssignments,
            ProviderGateway,
            GenerationCoordinator);
        CampaignRunner = new CampaignRunner(
            Campaigns,
            CampaignScenarios,
            ProviderGateway,
            GenerationCoordinator,
            GlobalPrompts,
            CampaignMemory,
            CampaignMemoryRepository,
            CampaignContextPlanner);
        CharacterCardCodecs =
        [
            new SillyTavernPngCardCodec(),
            new SillyTavernJsonCardCodec(),
            new SillyTavernCharxCardCodec()
        ];
        WorldbookService = new WorldbookService(
            Worldbooks,
            ModelAssignments,
            EmbeddingProviderGateway,
            CharacterCardCodecs,
            MacroEngine,
            Providers);
        CharacterCards = new CharacterCardLibrary(
            Paths,
            Characters,
            CharacterCardCodecs,
            WorldbookService);
        ContextAssembler = new BasicContextAssembler(
            Conversations,
            Characters,
            MemoryBanks,
            TokenEstimator,
            WorldbookEngine,
            WorldbookService,
            MacroEngine,
            Retrieval);
        CampaignScenarioCards = new CampaignScenarioCardImporter(
            Paths,
            CampaignScenarios);
        CampaignCharacterSnapshots = new CampaignCharacterSnapshotAdapter();
        ChatArchives = new SillyTavernChatJsonlService(
            Database,
            Characters,
            Conversations,
            Paths);
    }

    public AppDataConfiguration DataConfiguration { get; }
    public AppDataPaths Paths { get; }
    public AppDataLocationService DataLocation { get; }
    public SqliteDatabase Database { get; }
    public ICharacterRepository Characters { get; }
    public ICharacterShelfRepository CharacterShelves { get; }
    public ICampaignScenarioRepository CampaignScenarios { get; }
    public ICampaignRepository Campaigns { get; }
    public ICampaignMemoryRepository CampaignMemoryRepository { get; }
    public ICampaignMemoryUpdateService CampaignMemory { get; }
    public IConversationRepository Conversations { get; }
    public IProviderProfileRepository Providers { get; }
    public IModelCatalogRepository Models { get; }
    public IModelAssignmentRepository ModelAssignments { get; }
    public IAppSettingsRepository Settings { get; }
    public IGlobalPromptConfiguration GlobalPrompts { get; }
    public IMemoryBankService MemoryBanks { get; }
    public IMemoryWorkflowRepository MemoryWorkflow { get; }
    public IMemoryPromptComposer MemoryPrompts { get; }
    public IGroupChatRepository GroupChats { get; }
    public IGroupRelayPlanner GroupRelay { get; }
    public IMessageRetrievalRepository Retrieval { get; }
    public IWorldbookRepository Worldbooks { get; }
    public IWorldbookService WorldbookService { get; }
    public IPresetRepository Presets { get; }
    public IPresetResolver PresetResolver { get; }
    public ITokenEstimator TokenEstimator { get; }
    public ICampaignContextPlanner CampaignContextPlanner { get; }
    public IContextBudgetProvider ContextBudget { get; }
    public IMacroEngine MacroEngine { get; }
    public IWorldbookEngine WorldbookEngine { get; }
    public IContextAssembler ContextAssembler { get; }
    public IConversationGenerationCoordinator GenerationCoordinator { get; }
    public IConversationGenerationSessionStore GenerationSessions { get; }
    public ISecretStore Secrets { get; }
    public IProviderGateway ProviderGateway { get; }
    public IEmbeddingProviderGateway EmbeddingProviderGateway { get; }
    public ICampaignRunner CampaignRunner { get; }
    public IReadOnlyList<ICharacterCardCodec> CharacterCardCodecs { get; }
    public ICharacterCardLibrary CharacterCards { get; }
    public ICampaignScenarioCardImporter CampaignScenarioCards { get; }
    public ICampaignCharacterSnapshotAdapter CampaignCharacterSnapshots { get; }
    public IChatArchiveService ChatArchives { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (!Paths.IsExternalOverride)
        {
            await DataConfiguration.EnsureStartupConfigurationAsync(
                cancellationToken);
        }

        await Database.InitializeAsync(cancellationToken);
        await DataLocation.RepairDatabasePathsAsync(cancellationToken);
        await Campaigns.RecoverInterruptedGenerationsAsync(cancellationToken);
        await Providers.EnsureDefaultsAsync(cancellationToken);
        await GlobalPrompts.InitializeAsync(cancellationToken);
    }
}
