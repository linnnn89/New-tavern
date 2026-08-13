using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class GroupChatViewModel : ViewModelBase
{
    private readonly IGroupChatRepository _groups;
    private readonly IGroupRelayPlanner _relayPlanner;
    private readonly ICharacterRepository _characters;
    private readonly IUserInteractionService _interaction;
    private readonly Func<string, Task> _openGroup;
    private readonly Func<string?, Task> _continueRelay;
    private readonly Func<Character, GroupChatSettings, Task> _mergeMemory;
    private readonly Func<string, bool, Task> _updateMemory;
    private string? _conversationId;
    private GroupRelayMode _relayMode = GroupRelayMode.MentionDirected;
    private bool _autoContinueEnabled;
    private string _maximumAutomaticTurns = "8";
    private bool _pauseOnUserMention = true;
    private bool _memberMemoryEnabled;
    private string _memoryPendingTokenThreshold = "4000";
    private string _groupSystemPrompt = GroupPromptDefaults.SystemPrompt;
    private GroupMemberItemViewModel? _selectedNextSpeaker;
    private GroupMemberItemViewModel? _selectedMergeMember;
    private string _status = LanguageRuntime.GetString("GroupChat.ConfigureHint");
    private string _memoryStatus = LanguageRuntime.GetString("GroupChat.MemoryStatusIdle");
    private GroupChatState? _state;
    private long _loadVersion;

    public GroupChatViewModel(
        IGroupChatRepository groups,
        IGroupRelayPlanner relayPlanner,
        ICharacterRepository characters,
        IUserInteractionService interaction,
        Func<string, Task> openGroup,
        Func<string?, Task> continueRelay,
        Func<Character, GroupChatSettings, Task> mergeMemory,
        Func<string, bool, Task> updateMemory)
    {
        _groups = groups;
        _relayPlanner = relayPlanner;
        _characters = characters;
        _interaction = interaction;
        _openGroup = openGroup;
        _continueRelay = continueRelay;
        _mergeMemory = mergeMemory;
        _updateMemory = updateMemory;
        RelayModes =
        [
            new(GroupRelayMode.Manual, LanguageRuntime.GetString("GroupChat.Relay.Manual")),
            new(GroupRelayMode.FixedOrder, LanguageRuntime.GetString("GroupChat.Relay.FixedOrder")),
            new(GroupRelayMode.MentionDirected, LanguageRuntime.GetString("GroupChat.Relay.MentionDirected")),
            new(GroupRelayMode.Random, LanguageRuntime.GetString("GroupChat.Relay.Random"))
        ];

        CreateGroupCommand = new AsyncRelayCommand(CreateGroupAsync);
        SaveSettingsCommand = new AsyncRelayCommand(
            SaveSettingsAsync,
            () => IsGroupConversation);
        ContinueRelayCommand = new AsyncRelayCommand(
            ContinueRelayAsync,
            () => IsGroupConversation);
        PauseOrResumeCommand = new AsyncRelayCommand(
            PauseOrResumeAsync,
            () => IsGroupConversation);
        MergeMemoryCommand = new AsyncRelayCommand(
            MergeMemoryAsync,
            () => IsGroupConversation && SelectedMergeMember is not null);
        UpdateMemoryCommand = new AsyncRelayCommand(
            UpdateMemoryAsync,
            () => IsGroupConversation);
    }

    public ObservableCollection<GroupMemberItemViewModel> Members { get; } = [];
    public IReadOnlyList<GroupRelayModeOption> RelayModes { get; }
    public AsyncRelayCommand CreateGroupCommand { get; }
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public AsyncRelayCommand ContinueRelayCommand { get; }
    public AsyncRelayCommand PauseOrResumeCommand { get; }
    public AsyncRelayCommand MergeMemoryCommand { get; }
    public AsyncRelayCommand UpdateMemoryCommand { get; }

    public bool IsGroupConversation => _conversationId is not null;
    public string? ConversationId => _conversationId;
    public GroupRelayMode RelayMode
    {
        get => _relayMode;
        set => SetProperty(ref _relayMode, value);
    }

    public bool AutoContinueEnabled
    {
        get => _autoContinueEnabled;
        set => SetProperty(ref _autoContinueEnabled, value);
    }

    public string MaximumAutomaticTurns
    {
        get => _maximumAutomaticTurns;
        set => SetProperty(ref _maximumAutomaticTurns, value);
    }

    public bool PauseOnUserMention
    {
        get => _pauseOnUserMention;
        set => SetProperty(ref _pauseOnUserMention, value);
    }

    public bool MemberMemoryEnabled
    {
        get => _memberMemoryEnabled;
        set
        {
            SetProperty(ref _memberMemoryEnabled, value);
        }
    }

    public string MemoryPendingTokenThreshold
    {
        get => _memoryPendingTokenThreshold;
        set => SetProperty(ref _memoryPendingTokenThreshold, value);
    }

    public string GroupSystemPrompt
    {
        get => _groupSystemPrompt;
        set => SetProperty(ref _groupSystemPrompt, value);
    }

    public GroupMemberItemViewModel? SelectedNextSpeaker
    {
        get => _selectedNextSpeaker;
        set => SetProperty(ref _selectedNextSpeaker, value);
    }

    public GroupMemberItemViewModel? SelectedMergeMember
    {
        get => _selectedMergeMember;
        set
        {
            if (SetProperty(ref _selectedMergeMember, value))
            {
                MergeMemoryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string MemoryStatus
    {
        get => _memoryStatus;
        private set => SetProperty(ref _memoryStatus, value);
    }

    public string PauseButtonText => _state?.IsPaused == true
        ? LanguageRuntime.GetString("GroupChat.Resume")
        : LanguageRuntime.GetString("GroupChat.Pause");
    public string RelayStateText => _state is null
        ? LanguageRuntime.GetString("GroupChat.StateNotLoaded")
        : _state.IsPaused
            ? LanguageRuntime.Format(
                "GroupChat.PausedFormat",
                LanguageRuntime.GroupRelayReason(_state.PauseReason))
            : _state.NextSpeakerId.Length > 0
                ? LanguageRuntime.Format(
                    "GroupChat.NextSpeakerFormat",
                    MemberNames.GetValueOrDefault(
                        _state.NextSpeakerId,
                        LanguageRuntime.GetString("GroupChat.UnknownCharacter")))
                : LanguageRuntime.GetString("GroupChat.Ready");

    public IReadOnlyDictionary<string, string> MemberNames =>
        Members.ToDictionary(
            member => member.Character.Id,
            member => member.Character.Name,
            StringComparer.Ordinal);

    public IReadOnlyList<GroupChatMember> SnapshotMembers() =>
        Members.Select((member, index) => new GroupChatMember
        {
            ConversationId = _conversationId ?? string.Empty,
            CharacterId = member.Character.Id,
            SortIndex = index,
            IsEnabled = member.IsEnabled
        }).ToArray();

    public GroupChatSettings SettingsSnapshot()
    {
        if (_conversationId is null)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("GroupChat.NotGroup"));
        }

        return new GroupChatSettings
        {
            ConversationId = _conversationId,
            RelayMode = RelayMode,
            AutoContinueEnabled = AutoContinueEnabled,
            MaximumAutomaticTurns = ParseMaximumTurns(),
            PauseOnUserMention = PauseOnUserMention,
            MemberMemoryEnabled = MemberMemoryEnabled,
            MemoryPendingTokenThreshold = ParseMemoryTokenThreshold(),
            GroupSystemPrompt = GroupSystemPrompt,
            MergeSystemPrompt = MemoryPromptDefaults.GroupMergeSystem,
            MergeUserTemplate = MemoryPromptDefaults.GroupMergeInput
        };
    }

    public GroupRelayDecision DecideNext(
        IReadOnlyList<ChatMessage> messages,
        string personaName,
        string? manuallySelectedSpeakerId = null) =>
        _relayPlanner.DecideNext(
            SettingsSnapshot(),
            SnapshotMembers(),
            MemberNames,
            messages,
            personaName,
            manuallySelectedSpeakerId ?? SelectedNextSpeaker?.Character.Id);

    public async Task SaveRelayStateAsync(
        string currentSpeakerId,
        string nextSpeakerId,
        int automaticTurns,
        bool isPaused,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (_conversationId is null)
        {
            return;
        }

        _state = new GroupChatState
        {
            ConversationId = _conversationId,
            CurrentSpeakerId = currentSpeakerId,
            NextSpeakerId = nextSpeakerId,
            AutomaticTurns = automaticTurns,
            IsPaused = isPaused,
            PauseReason = reason
        };
        await _groups.SaveStateAsync(_state, cancellationToken);
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(RelayStateText));
        Status = LanguageRuntime.GroupRelayReason(reason);
    }

    public void ApplyState(GroupChatState state)
    {
        if (_conversationId != state.ConversationId)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(RelayStateText));
        Status = LanguageRuntime.GroupRelayReason(state.PauseReason);
    }

    public async Task LoadAsync(
        Conversation? conversation,
        CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        if (conversation?.Mode != ConversationMode.Group)
        {
            Clear();
            return;
        }

        var settingsTask = _groups.GetSettingsAsync(conversation.Id, cancellationToken);
        var membersTask = _groups.ListMembersAsync(conversation.Id, cancellationToken);
        var stateTask = _groups.GetStateAsync(conversation.Id, cancellationToken);
        var charactersTask = _characters.ListAsync(cancellationToken);
        await Task.WhenAll(settingsTask, membersTask, stateTask, charactersTask);
        if (version != _loadVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _conversationId = conversation.Id;
        var settings = settingsTask.Result
                       ?? new GroupChatSettings { ConversationId = conversation.Id };
        RelayMode = settings.RelayMode;
        AutoContinueEnabled = settings.AutoContinueEnabled;
        MaximumAutomaticTurns = settings.MaximumAutomaticTurns.ToString();
        PauseOnUserMention = settings.PauseOnUserMention;
        if (_memberMemoryEnabled != settings.MemberMemoryEnabled)
        {
            _memberMemoryEnabled = settings.MemberMemoryEnabled;
            OnPropertyChanged(nameof(MemberMemoryEnabled));
        }
        MemoryPendingTokenThreshold = settings.MemoryPendingTokenThreshold.ToString();
        GroupSystemPrompt = settings.GroupSystemPrompt;
        _state = stateTask.Result;

        var lookup = charactersTask.Result.ToDictionary(
            character => character.Id,
            StringComparer.Ordinal);
        Members.Clear();
        foreach (var member in membersTask.Result)
        {
            if (lookup.TryGetValue(member.CharacterId, out var character))
            {
                Members.Add(new GroupMemberItemViewModel(character, member.IsEnabled));
            }
        }

        SelectedNextSpeaker = Members.FirstOrDefault(member =>
                                  member.Character.Id == _state.NextSpeakerId)
                              ?? Members.FirstOrDefault(member => member.IsEnabled);
        SelectedMergeMember = Members.FirstOrDefault(member => member.IsEnabled);
        Status = LanguageRuntime.Format("GroupChat.LoadedFormat", Members.Count);
        MemoryStatus = LanguageRuntime.GetString("GroupChat.MemoryStatusIdle");
        RaiseStates();
    }

    private async Task CreateGroupAsync()
    {
        var characters = await _characters.ListAsync();
        if (characters.Count < 2)
        {
            Status = LanguageRuntime.GetString("GroupChat.NeedTwoCharacters");
            return;
        }

        var draft = await _interaction.CreateGroupChatAsync(characters);
        if (draft is null)
        {
            return;
        }

        var conversation = new Conversation
        {
            Title = draft.Title.Trim(),
            Mode = ConversationMode.Group
        };
        var settings = new GroupChatSettings
        {
            ConversationId = conversation.Id
        };
        var members = draft.CharacterIds
            .Select((characterId, index) => new GroupChatMember
            {
                ConversationId = conversation.Id,
                CharacterId = characterId,
                SortIndex = index
            })
            .ToArray();
        await _groups.CreateAsync(conversation, settings, members);
        Status = LanguageRuntime.Format("GroupChat.CreatedFormat", conversation.Title);
        await _openGroup(conversation.Id);
    }

    private async Task SaveSettingsAsync()
    {
        var conversationId = _conversationId;
        if (conversationId is null)
        {
            return;
        }

        try
        {
            var settings = SettingsSnapshot();
            var members = SnapshotMembers();
            await _groups.SaveSettingsAsync(settings);
            await _groups.ReplaceMembersAsync(conversationId, members);
            if (_conversationId == conversationId)
            {
                Status = LanguageRuntime.GetString("GroupChat.Saved");
            }
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("GroupChat.SaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task ContinueRelayAsync()
    {
        if (_state?.IsPaused == true)
        {
            Status = LanguageRuntime.GetString("GroupChat.PausedNotice");
            return;
        }

        await _continueRelay(SelectedNextSpeaker?.Character.Id);
    }

    private async Task PauseOrResumeAsync()
    {
        if (_conversationId is null)
        {
            return;
        }

        var isPaused = _state?.IsPaused != true;
        await SaveRelayStateAsync(
            _state?.CurrentSpeakerId ?? string.Empty,
            _state?.NextSpeakerId ?? string.Empty,
            _state?.AutomaticTurns ?? 0,
            isPaused,
            isPaused
                ? LanguageRuntime.GetString("GroupChat.PauseReasonManual")
                : LanguageRuntime.GetString("GroupChat.ResumeReason"));
    }

    private async Task MergeMemoryAsync()
    {
        if (SelectedMergeMember is null)
        {
            return;
        }

        try
        {
            await _mergeMemory(SelectedMergeMember.Character, SettingsSnapshot());
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("GroupChat.MergeDraftFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private async Task UpdateMemoryAsync()
    {
        var conversationId = _conversationId;
        if (conversationId is null)
        {
            return;
        }

        try
        {
            var settings = SettingsSnapshot();
            var members = SnapshotMembers();
            MemoryStatus = LanguageRuntime.GetString("GroupChat.MemoryUpdating");
            await _groups.SaveSettingsAsync(settings);
            await _groups.ReplaceMembersAsync(conversationId, members);
            if (_conversationId != conversationId)
            {
                return;
            }

            await _updateMemory(conversationId, true);
        }
        catch (Exception exception)
        {
            ApplyMemoryUpdateFailure(LanguageRuntime.ErrorMessage(exception));
        }
    }

    public void ApplyMemoryUpdateResult(GroupMemoryUpdateResult result)
    {
        if (_conversationId != result.ConversationId)
        {
            return;
        }

        MemoryStatus = result.Status switch
        {
            GroupMemoryUpdateStatus.Updated => result.Rebuilt
                ? LanguageRuntime.GetString("GroupChat.MemoryRebuilt")
                : LanguageRuntime.GetString("GroupChat.MemoryUpdated"),
            GroupMemoryUpdateStatus.PartiallyUpdated =>
                LanguageRuntime.GetString("GroupChat.MemoryPartiallyUpdated"),
            GroupMemoryUpdateStatus.NoChanges =>
                LanguageRuntime.GetString("GroupChat.MemoryNoChanges"),
            GroupMemoryUpdateStatus.SkippedDisabled =>
                LanguageRuntime.GetString("GroupChat.MemoryDisabled"),
            GroupMemoryUpdateStatus.SkippedNoAssignment =>
                LanguageRuntime.GetString("GroupChat.MemoryNoAssignment"),
            _ => LanguageRuntime.Format(
                "GroupChat.MemoryFailedFormat",
                MemoryErrorMessage(result.ErrorCode))
        };
    }

    private static string MemoryErrorMessage(GroupMemoryErrorCode errorCode) =>
        LanguageRuntime.GetString(errorCode switch
        {
            GroupMemoryErrorCode.Cancelled => "GroupChat.MemoryError.Cancelled",
            GroupMemoryErrorCode.ConcurrentChange =>
                "GroupChat.MemoryError.ConcurrentChange",
            GroupMemoryErrorCode.ContextLimit => "GroupChat.MemoryError.ContextLimit",
            GroupMemoryErrorCode.InvalidResponse =>
                "GroupChat.MemoryError.InvalidResponse",
            GroupMemoryErrorCode.ProviderFailure =>
                "GroupChat.MemoryError.ProviderFailure",
            _ => "GroupChat.MemoryUnknownError"
        });

    public void ApplyMemoryUpdateFailure(string errorMessage)
    {
        MemoryStatus = LanguageRuntime.Format(
            "GroupChat.MemoryFailedFormat",
            errorMessage);
    }

    private int ParseMaximumTurns()
    {
        if (!int.TryParse(MaximumAutomaticTurns, out var maximum)
            || maximum is < 1 or > 100)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("GroupChat.AutoRelayLimitRange"));
        }

        return maximum;
    }

    private int ParseMemoryTokenThreshold()
    {
        if (!int.TryParse(MemoryPendingTokenThreshold, out var threshold)
            || threshold is < 256 or > 100000)
        {
            throw new InvalidOperationException(
                LanguageRuntime.GetString("GroupChat.MemoryThresholdRange"));
        }

        return threshold;
    }

    public void Clear()
    {
        _conversationId = null;
        Members.Clear();
        SelectedNextSpeaker = null;
        SelectedMergeMember = null;
        _state = null;
        Status = LanguageRuntime.GetString("GroupChat.SingleConversation");
        MemoryStatus = LanguageRuntime.GetString("GroupChat.MemoryStatusIdle");
        RaiseStates();
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(IsGroupConversation));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(RelayStateText));
        SaveSettingsCommand.RaiseCanExecuteChanged();
        ContinueRelayCommand.RaiseCanExecuteChanged();
        PauseOrResumeCommand.RaiseCanExecuteChanged();
        MergeMemoryCommand.RaiseCanExecuteChanged();
        UpdateMemoryCommand.RaiseCanExecuteChanged();
    }
}

public sealed class GroupMemberItemViewModel : ViewModelBase
{
    private bool _isEnabled;

    public GroupMemberItemViewModel(Character character, bool isEnabled)
    {
        Character = character;
        _isEnabled = isEnabled;
    }

    public Character Character { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }
}

public sealed record GroupRelayModeOption(
    GroupRelayMode Value,
    string Label);
