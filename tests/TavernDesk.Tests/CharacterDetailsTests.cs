using TavernDesk.App.Services;
using TavernDesk.App.ViewModels;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;
using TavernDesk.Infrastructure;

namespace TavernDesk.Tests;

public sealed class CharacterDetailsTests
{
    [Fact]
    public async Task CardCommandsOpenTheExplicitCharacterAndKeepFullResponsivePreview()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var megumi = new Character { Name = "加藤惠" };
        var yukino = new Character { Name = "雪之下雪乃" };
        await services.Characters.UpsertAsync(megumi);
        await services.Characters.UpsertAsync(yukino);

        var megumiConversation = new Conversation
        {
            CharacterId = megumi.Id,
            Title = "加藤惠的聊天"
        };
        var yukinoConversation = new Conversation
        {
            CharacterId = yukino.Id,
            Title = "雪乃的聊天"
        };
        await services.Conversations.UpsertAsync(megumiConversation);
        await services.Conversations.UpsertAsync(yukinoConversation);
        const string longPreview = "这是一段明显超过二十个字符并会交给界面按宽度裁切的末次发言";
        await services.Conversations.AddMessageAsync(new ChatMessage
        {
            ConversationId = yukinoConversation.Id,
            SenderKind = MessageSenderKind.Character,
            SenderId = yukino.Id,
            Content = longPreview
        });

        ConversationSummary? openedConversation = null;
        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            summary =>
            {
                openedConversation = summary;
                return Task.CompletedTask;
            });
        await viewModel.LoadAsync();
        Assert.Null(viewModel.ActiveCharacter);
        Assert.Empty(viewModel.Editor.CharacterId);

        viewModel.OpenCharacterToolsCommand.Execute(yukino);
        await WaitUntilAsync(() =>
            viewModel.IsCharacterToolsOpen
            && viewModel.SelectedCharacter?.Id == yukino.Id
            && viewModel.CharacterConversationCount == 1);

        Assert.False(viewModel.IsCharacterEditing);
        var listedConversation = Assert.Single(viewModel.CharacterConversations);
        Assert.Equal(yukinoConversation.Id, listedConversation.Id);
        Assert.Equal(longPreview, listedConversation.PreviewText);

        viewModel.OpenCharacterConversationCommand.Execute(listedConversation);
        await WaitUntilAsync(() => openedConversation is not null);
        Assert.Equal(yukinoConversation.Id, openedConversation!.Id);

        viewModel.CloseCharacterToolsCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCharacterToolsOpen);
        Assert.Null(viewModel.ActiveCharacter);
        Assert.Empty(viewModel.Editor.CharacterId);
        Assert.Empty(viewModel.CharacterConversations);

        viewModel.EditCharacterCommand.Execute(megumi);
        await WaitUntilAsync(() =>
            viewModel.ActiveCharacter?.Id == megumi.Id
            && viewModel.IsCharacterEditing);
        Assert.Equal("加藤惠", viewModel.Editor.Name);
        Assert.Equal(megumi.Id, viewModel.Editor.CharacterId);
    }

    [Fact]
    public async Task EachCharacterDetailSessionOwnsItsEditorBuffer()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var saber = new Character
        {
            Name = "Saber",
            Description = "骑士王",
            Personality = "高傲而负责",
            Scenario = "冬木市"
        };
        var orihime = new Character
        {
            Name = "Orihime Inoue",
            Description = "井上织姬",
            Personality = "温柔",
            Scenario = "空座町"
        };
        await services.Characters.UpsertAsync(saber);
        await services.Characters.UpsertAsync(orihime);

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();

        viewModel.EditCharacterCommand.Execute(saber);
        await WaitUntilAsync(() =>
            viewModel.IsCharacterEditing
            && viewModel.Editor.CharacterId == saber.Id);
        var saberEditor = viewModel.Editor;
        Assert.Equal("Saber", saberEditor.Name);
        Assert.Equal("骑士王", saberEditor.Description);

        viewModel.CloseCharacterToolsCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCharacterToolsOpen);
        viewModel.EditCharacterCommand.Execute(orihime);
        await WaitUntilAsync(() =>
            viewModel.IsCharacterEditing
            && viewModel.Editor.CharacterId == orihime.Id);
        var orihimeEditor = viewModel.Editor;

        Assert.NotSame(saberEditor, orihimeEditor);
        Assert.Equal("Orihime Inoue", orihimeEditor.Name);
        Assert.Equal("井上织姬", orihimeEditor.Description);

        // A late operation holding the old session buffer cannot blank the new page.
        saberEditor.Clear();
        Assert.Equal(orihime.Id, viewModel.Editor.CharacterId);
        Assert.Equal("Orihime Inoue", viewModel.Editor.Name);
        Assert.Equal("井上织姬", viewModel.Editor.Description);

        viewModel.CloseCharacterToolsCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCharacterToolsOpen);
        viewModel.EditCharacterCommand.Execute(orihime);
        await WaitUntilAsync(() =>
            viewModel.IsCharacterEditing
            && viewModel.Editor.CharacterId == orihime.Id);
        Assert.NotSame(orihimeEditor, viewModel.Editor);
        Assert.Equal("Orihime Inoue", viewModel.Editor.Name);
    }

    [Fact]
    public async Task InvalidEditorBindingCannotTrapTheCharacterSessionOpen()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "Saber" };
        await services.Characters.UpsertAsync(character);

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.OpenCharacterToolsCommand.Execute(character);
        await WaitUntilAsync(() => viewModel.IsCharacterToolsOpen);

        viewModel.Editor.Clear();
        viewModel.ShowCharacterEditorCommand.Execute(null);
        Assert.False(viewModel.IsCharacterEditing);

        viewModel.CloseCharacterToolsCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCharacterToolsOpen);
        Assert.Null(viewModel.ActiveCharacter);
    }

    [Fact]
    public async Task RefreshingShelfWhileEditingKeepsTheCurrentDraft()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character
        {
            Name = "Saber",
            Description = "正式描述"
        };
        await services.Characters.UpsertAsync(character);

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.EditCharacterCommand.Execute(character);
        await WaitUntilAsync(() => viewModel.Editor.CharacterId == character.Id);
        var editor = viewModel.Editor;
        editor.Description = "尚未保存的草稿";

        await viewModel.LoadAsync();

        Assert.Same(editor, viewModel.Editor);
        Assert.Equal(character.Id, viewModel.ActiveCharacter?.Id);
        Assert.Equal("尚未保存的草稿", viewModel.Editor.Description);
        Assert.True(viewModel.Editor.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavingBeforeOpeningChatUsesSavedCharacterSnapshot(
        bool createNewChat)
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character
        {
            Name = "保存前名称",
            FirstMessage = "保存前开场白"
        };
        await services.Characters.UpsertAsync(character);

        Character? openedCharacter = null;
        Task CaptureOpenedCharacter(Character current)
        {
            openedCharacter = current;
            return Task.CompletedTask;
        }

        var viewModel = new CharactersViewModel(
            services.Characters,
            services.CharacterShelves,
            services.Conversations,
            services.CharacterCards,
            services.Settings,
            new NoOpFileDialogService(),
            new TestInteractionService(
                unsavedChangesDecision: UnsavedChangesDecision.Save),
            CaptureOpenedCharacter,
            CaptureOpenedCharacter,
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.EditCharacterCommand.Execute(character);
        await WaitUntilAsync(() => viewModel.Editor.CharacterId == character.Id);
        viewModel.Editor.Name = "保存后名称";
        viewModel.Editor.FirstMessage = "保存后开场白";
        var clickParameter = Assert.IsType<Character>(viewModel.ActiveCharacter);

        if (createNewChat)
        {
            viewModel.CreateNewChatCommand.Execute(clickParameter);
        }
        else
        {
            viewModel.StartChatCommand.Execute(clickParameter);
        }

        await WaitUntilAsync(() => openedCharacter is not null);
        Assert.Equal("保存后名称", openedCharacter!.Name);
        Assert.Equal("保存后开场白", openedCharacter.FirstMessage);
        var stored = await services.Characters.GetAsync(character.Id);
        Assert.Equal("保存后名称", stored?.Name);
        Assert.Equal("保存后开场白", stored?.FirstMessage);
    }

    [Fact]
    public async Task CharacterDetailModesAreMutuallyExclusive()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "Saber" };
        await services.Characters.UpsertAsync(character);

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.OpenCharacterToolsCommand.Execute(character);
        await WaitUntilAsync(() => viewModel.IsCharacterToolsOpen);
        Assert.False(viewModel.IsCharacterEditing);
        Assert.False(viewModel.IsClassificationOpen);

        viewModel.ToggleClassificationCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsClassificationOpen);
        Assert.False(viewModel.IsCharacterEditing);

        viewModel.ShowCharacterEditorCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.IsCharacterEditing);
        Assert.False(viewModel.IsClassificationOpen);

        viewModel.ShowCharacterOverviewCommand.Execute(null);
        await WaitUntilAsync(() =>
            !viewModel.IsCharacterEditing
            && !viewModel.IsClassificationOpen);
    }

    [Fact]
    public async Task SavingOneCharacterCannotPublishItsLateResultIntoAnotherSession()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var saber = new Character
        {
            Name = "Saber",
            Description = "旧描述"
        };
        var orihime = new Character
        {
            Name = "Orihime Inoue",
            Description = "织姬描述"
        };
        await services.Characters.UpsertAsync(saber);
        await services.Characters.UpsertAsync(orihime);

        var delayedRepository = new DelayedCharacterSaveRepository(
            services.Characters,
            saber.Id);
        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(
                unsavedChangesDecision: UnsavedChangesDecision.Discard),
            _ => Task.CompletedTask,
            characters: delayedRepository);
        await viewModel.LoadAsync();
        viewModel.EditCharacterCommand.Execute(saber);
        await WaitUntilAsync(() => viewModel.Editor.CharacterId == saber.Id);
        viewModel.Editor.Description = "已保存的新描述";

        viewModel.SaveCharacterCommand.Execute(null);
        await delayedRepository.UpsertStarted;
        viewModel.OpenCharacterToolsCommand.Execute(orihime);
        await WaitUntilAsync(() => viewModel.ActiveCharacter?.Id == orihime.Id);
        viewModel.Editor.Description = "B 会话中的新草稿";
        delayedRepository.ReleaseUpsert();
        await WaitUntilAsync(async () =>
        {
            var stored = await services.Characters.GetAsync(saber.Id);
            return stored?.Description == "已保存的新描述";
        });
        await WaitUntilAsync(() => viewModel.SaveCharacterCommand.CanExecute(null));

        Assert.Equal(orihime.Id, viewModel.ActiveCharacter?.Id);
        Assert.Equal(orihime.Id, viewModel.Editor.CharacterId);
        Assert.Equal("Orihime Inoue", viewModel.Editor.Name);
        Assert.Equal("B 会话中的新草稿", viewModel.Editor.Description);
        Assert.True(viewModel.Editor.IsDirty);
        Assert.DoesNotContain("Saber", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkRemovalFromCustomShelfKeepsCharactersAndChats()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var saber = new Character { Name = "Saber" };
        var tamamo = new Character { Name = "Tamamo" };
        var yukino = new Character { Name = "Yukino" };
        foreach (var character in new[] { saber, tamamo, yukino })
        {
            await services.Characters.UpsertAsync(character);
        }

        var shelf = new CharacterShelf { Name = "FGO" };
        await services.CharacterShelves.UpsertAsync(shelf);
        foreach (var character in new[] { saber, tamamo, yukino })
        {
            await services.CharacterShelves.AddCharacterAsync(shelf.Id, character.Id);
        }

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.SelectedShelf = viewModel.CustomShelfItems.Single(item => item.Id == shelf.Id);
        await WaitUntilAsync(() => viewModel.VisibleCharacters.Count == 3);

        Assert.True(viewModel.IsCustomShelfSelected);
        Assert.False(viewModel.IsShelfBatchMode);
        Assert.False(viewModel.RemoveSelectedFromShelfCommand.CanExecute(
            new[] { saber, tamamo }));
        var hiddenSelection = new List<Character> { saber };
        viewModel.ToggleShelfBatchModeCommand.Execute(hiddenSelection);
        Assert.True(viewModel.IsShelfBatchMode);
        Assert.Empty(hiddenSelection);
        Assert.True(viewModel.RemoveSelectedFromShelfCommand.CanExecute(
            new[] { saber, tamamo }));
        viewModel.RemoveSelectedFromShelfCommand.Execute(new[] { saber, tamamo });
        await WaitUntilAsync(async () =>
            (await services.CharacterShelves.ListCharacterIdsAsync(shelf.Id)).Count == 1);

        Assert.Equal(
            yukino.Id,
            Assert.Single(await services.CharacterShelves.ListCharacterIdsAsync(shelf.Id)));
        Assert.Equal(3, (await services.Characters.ListAsync()).Count);
        Assert.Equal(yukino.Id, Assert.Single(viewModel.VisibleCharacters).Id);
        Assert.Contains("角色卡和聊天记录未删除", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingCharacterKeepsItsChatAsDeletedSingleCharacterConversation()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "待删除角色" };
        await services.Characters.UpsertAsync(character);
        var conversation = new Conversation
        {
            CharacterId = character.Id,
            Title = "需要保留的聊天"
        };
        await services.Conversations.UpsertAsync(conversation);

        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(confirmCharacterDeletion: true),
            _ => Task.CompletedTask);
        await viewModel.LoadAsync();
        viewModel.OpenCharacterToolsCommand.Execute(character);
        await WaitUntilAsync(() => viewModel.SelectedCharacter?.Id == character.Id);

        viewModel.DeleteCharacterCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCharacterToolsOpen);

        Assert.Null(await services.Characters.GetAsync(character.Id));
        var retained = Assert.Single(await services.Conversations.ListAllAsync());
        Assert.Equal(conversation.Id, retained.Id);
        Assert.Null(retained.CharacterId);
        Assert.Equal(ConversationMode.SingleCharacter, retained.Mode);
    }

    [Fact]
    public async Task ReplacingAvatarUsesManagedCopyAndKeepsChosenSource()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var character = new Character { Name = "图片测试" };
        await services.Characters.UpsertAsync(character);
        var sourcePath = Path.Combine(workspace.Root, "chosen-avatar.png");
        await File.WriteAllBytesAsync(sourcePath, [137, 80, 78, 71]);

        var storedPath = await services.CharacterCards.ReplaceAvatarAsync(character, sourcePath);

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(storedPath));
        Assert.StartsWith(
            Path.Combine(services.Paths.CharacterCardsDirectory, character.Id),
            storedPath,
            StringComparison.OrdinalIgnoreCase);
        var storedCharacter = await services.Characters.GetAsync(character.Id);
        Assert.Equal(storedPath, storedCharacter?.AvatarPath);
    }

    [Fact]
    public async Task LateInitialCharacterLoadCannotOverwriteReopenedCharacterSession()
    {
        using var workspace = new TestWorkspace();
        var services = new InfrastructureServices(workspace.Root);
        await services.InitializeAsync();
        var saber = new Character { Name = "Saber" };
        var orihime = new Character { Name = "Orihime Inoue" };
        await services.Characters.UpsertAsync(saber);
        await services.Characters.UpsertAsync(orihime);
        var orihimeConversation = new Conversation
        {
            CharacterId = orihime.Id,
            Title = "Orihime 会话"
        };
        await services.Conversations.UpsertAsync(orihimeConversation);

        var delayedRepository = new DelayedCharacterListRepository(
            services.Conversations,
            saber.Id,
            TimeSpan.FromMilliseconds(180));
        var viewModel = CreateViewModel(
            services,
            new TestInteractionService(),
            _ => Task.CompletedTask,
            delayedRepository);
        await viewModel.LoadAsync();

        viewModel.EditCharacterCommand.Execute(saber);
        await WaitUntilAsync(() => viewModel.ActiveCharacter?.Id == saber.Id);
        viewModel.CloseCharacterToolsCommand.Execute(null);
        await WaitUntilAsync(() => viewModel.ActiveCharacter is null);
        viewModel.OpenCharacterToolsCommand.Execute(orihime);
        await WaitUntilAsync(() =>
            viewModel.ActiveCharacter?.Id == orihime.Id
            && viewModel.CharacterConversationCount == 1);
        await Task.Delay(240);

        Assert.Equal(orihime.Id, viewModel.ActiveCharacter?.Id);
        Assert.Equal(orihime.Id, viewModel.Editor.CharacterId);
        Assert.Equal("Orihime Inoue", viewModel.Editor.Name);
        Assert.DoesNotContain("Saber", viewModel.Status, StringComparison.Ordinal);
        Assert.Equal(
            orihimeConversation.Id,
            Assert.Single(viewModel.CharacterConversations).Id);
    }

    private static CharactersViewModel CreateViewModel(
        InfrastructureServices services,
        IUserInteractionService interaction,
        Func<ConversationSummary, Task> openConversation,
        IConversationRepository? conversations = null,
        ICharacterRepository? characters = null) =>
        new(
            characters ?? services.Characters,
            services.CharacterShelves,
            conversations ?? services.Conversations,
            services.CharacterCards,
            services.Settings,
            new NoOpFileDialogService(),
            interaction,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            openConversation);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(await condition());
    }

    private sealed class TestInteractionService(
        bool confirmCharacterDeletion = false,
        UnsavedChangesDecision unsavedChangesDecision =
            UnsavedChangesDecision.Cancel) : IUserInteractionService
    {
        public Task<string?> EditTextAsync(string title, string prompt, string initialText) =>
            Task.FromResult<string?>(null);

        public DeleteMessageDecision ConfirmMessageDeletion() =>
            DeleteMessageDecision.Cancel;

        public UnsavedChangesDecision ConfirmUnsavedCharacterChanges(string characterName) =>
            unsavedChangesDecision;

        public UnsavedChangesDecision ConfirmUnsavedProviderChanges(string providerName) =>
            UnsavedChangesDecision.Cancel;

        public bool ConfirmCharacterDeletion(string characterName, int conversationCount) =>
            confirmCharacterDeletion;

        public bool ConfirmShelfDeletion(string shelfName) => false;
        public bool ConfirmPresetDeletion(string presetName) => false;
        public bool ConfirmProviderDeletion(string providerName) => false;
        public bool ConfirmSecretClear(string providerName) => false;

        public Task<GroupChatDraft?> CreateGroupChatAsync(IReadOnlyList<Character> characters) =>
            Task.FromResult<GroupChatDraft?>(null);

        public void CopyText(string text)
        {
        }
    }

    private sealed class DelayedCharacterSaveRepository(
        ICharacterRepository inner,
        string delayedCharacterId) : ICharacterRepository
    {
        private readonly TaskCompletionSource<bool> _upsertStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseUpsert =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task UpsertStarted => _upsertStarted.Task;

        public void ReleaseUpsert() => _releaseUpsert.TrySetResult(true);

        public Task<IReadOnlyList<Character>> ListAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);

        public Task<Character?> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public async Task UpsertAsync(
            Character character,
            CancellationToken cancellationToken = default)
        {
            if (string.Equals(
                    character.Id,
                    delayedCharacterId,
                    StringComparison.Ordinal))
            {
                _upsertStarted.TrySetResult(true);
                await _releaseUpsert.Task.WaitAsync(cancellationToken);
            }

            await inner.UpsertAsync(character, cancellationToken);
        }

        public Task DeleteAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public Task<int> CountAsync(
            CancellationToken cancellationToken = default) =>
            inner.CountAsync(cancellationToken);
    }

    private sealed class DelayedCharacterListRepository(
        IConversationRepository inner,
        string delayedCharacterId,
        TimeSpan delay) : IConversationRepository
    {
        public Task<IReadOnlyList<ConversationSummary>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            inner.ListRecentAsync(limit, cancellationToken);

        public Task<IReadOnlyList<ConversationSummary>> ListAllAsync(
            CancellationToken cancellationToken = default) =>
            inner.ListAllAsync(cancellationToken);

        public async Task<IReadOnlyList<ConversationSummary>> ListByCharacterAsync(
            string characterId,
            CancellationToken cancellationToken = default)
        {
            if (characterId == delayedCharacterId)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return await inner.ListByCharacterAsync(characterId, cancellationToken);
        }

        public Task<ConversationSummary?> GetLatestForCharacterAsync(
            string characterId,
            CancellationToken cancellationToken = default) =>
            inner.GetLatestForCharacterAsync(characterId, cancellationToken);

        public Task<Conversation?> GetAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(
            string conversationId,
            CancellationToken cancellationToken = default) =>
            inner.ListMessagesAsync(conversationId, cancellationToken);

        public Task UpsertAsync(
            Conversation conversation,
            CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(conversation, cancellationToken);

        public Task AddMessageAsync(
            ChatMessage message,
            CancellationToken cancellationToken = default) =>
            inner.AddMessageAsync(message, cancellationToken);

        public Task AddCandidateAsync(
            MessageCandidate candidate,
            CancellationToken cancellationToken = default) =>
            inner.AddCandidateAsync(candidate, cancellationToken);

        public Task<IReadOnlyList<MessageCandidate>> ListCandidatesAsync(
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ListCandidatesAsync(messageId, cancellationToken);

        public Task AddAndActivateCandidateAsync(
            MessageCandidate candidate,
            CancellationToken cancellationToken = default) =>
            inner.AddAndActivateCandidateAsync(candidate, cancellationToken);

        public Task UpdateMessageContentAsync(
            string messageId,
            string content,
            CancellationToken cancellationToken = default) =>
            inner.UpdateMessageContentAsync(messageId, content, cancellationToken);

        public Task DeleteMessageAsync(
            string messageId,
            bool includeSubsequent,
            CancellationToken cancellationToken = default) =>
            inner.DeleteMessageAsync(messageId, includeSubsequent, cancellationToken);

        public Task<Conversation> ForkThroughMessageAsync(
            string conversationId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            inner.ForkThroughMessageAsync(conversationId, messageId, cancellationToken);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            inner.CountAsync(cancellationToken);
    }
}
