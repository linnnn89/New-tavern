using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Abstractions;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class RetrievalViewModel : ViewModelBase
{
    private readonly IMessageRetrievalRepository _repository;
    private readonly Action _contextChanged;
    private readonly HashSet<string> _excludedIds = new(StringComparer.Ordinal);
    private string _conversationId = string.Empty;
    private bool _isEnabled = true;
    private RetrievalScope _scope = RetrievalScope.CurrentConversation;
    private int _recentMessageCount = 20;
    private int _maximumResults = 6;
    private int _tokenBudget = 1200;
    private string _status = LanguageRuntime.GetString("Retrieval.SelectConversation");
    private bool _loading;
    private long _loadVersion;

    public RetrievalViewModel(
        IMessageRetrievalRepository repository,
        Action contextChanged)
    {
        _repository = repository;
        _contextChanged = contextChanged;
        SaveSettingsCommand = new AsyncRelayCommand(
            SaveSettingsAsync,
            () => IsAvailable);
        ExcludeCommand = new RelayCommand(Exclude);
        ClearExclusionsCommand = new RelayCommand(
            ClearExclusions,
            () => _excludedIds.Count > 0);
    }

    public ObservableCollection<RetrievalMatchViewModel> Matches { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public AsyncRelayCommand SaveSettingsCommand { get; }
    public RelayCommand ExcludeCommand { get; }
    public RelayCommand ClearExclusionsCommand { get; }
    public IReadOnlyList<RetrievalScopeOption> ScopeOptions { get; } =
    [
        new(RetrievalScope.CurrentConversation, LanguageRuntime.GetString("Retrieval.Scope.CurrentConversation")),
        new(RetrievalScope.SameCharacter, LanguageRuntime.GetString("Retrieval.Scope.SameCharacter"))
    ];

    public bool IsAvailable => _conversationId.Length > 0;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetOption(ref _isEnabled, value);
    }

    public RetrievalScope Scope
    {
        get => _scope;
        set => SetOption(ref _scope, value);
    }

    public int RecentMessageCount
    {
        get => _recentMessageCount;
        set => SetOption(ref _recentMessageCount, value);
    }

    public int MaximumResults
    {
        get => _maximumResults;
        set => SetOption(ref _maximumResults, value);
    }

    public int TokenBudget
    {
        get => _tokenBudget;
        set => SetOption(ref _tokenBudget, value);
    }

    public int ExcludedCount => _excludedIds.Count;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public async Task LoadAsync(
        Conversation conversation,
        CancellationToken cancellationToken = default)
    {
        var version = Interlocked.Increment(ref _loadVersion);
        var settings = await _repository.GetSettingsAsync(
            conversation.Id,
            cancellationToken);
        if (cancellationToken.IsCancellationRequested
            || version != Volatile.Read(ref _loadVersion))
        {
            return;
        }

        _loading = true;
        try
        {
            _conversationId = conversation.Id;
            IsEnabled = settings.IsEnabled;
            Scope = conversation.Mode == ConversationMode.Group
                ? RetrievalScope.CurrentConversation
                : settings.Scope;
            RecentMessageCount = settings.RecentMessageCount;
            MaximumResults = settings.MaximumResults;
            TokenBudget = settings.TokenBudget;
            _excludedIds.Clear();
            Matches.Clear();
            Diagnostics.Clear();
            Status = conversation.Mode == ConversationMode.Group
                ? LanguageRuntime.GetString("Retrieval.GroupScopeNotice")
                : LanguageRuntime.GetString("Retrieval.DefaultScopeNotice");
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(ExcludedCount));
            SaveSettingsCommand.RaiseCanExecuteChanged();
            ClearExclusionsCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _loading = false;
        }
    }

    public void Clear()
    {
        Interlocked.Increment(ref _loadVersion);
        _loading = true;
        try
        {
            _conversationId = string.Empty;
            _excludedIds.Clear();
            Matches.Clear();
            Diagnostics.Clear();
            Status = LanguageRuntime.GetString("Retrieval.SelectConversation");
            OnPropertyChanged(nameof(IsAvailable));
            OnPropertyChanged(nameof(ExcludedCount));
            SaveSettingsCommand.RaiseCanExecuteChanged();
            ClearExclusionsCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _loading = false;
        }
    }

    public RetrievalContextOptions? Snapshot() =>
        IsAvailable
            ? new RetrievalContextOptions(
                IsEnabled,
                Scope,
                RecentMessageCount,
                MaximumResults,
                TokenBudget,
                new HashSet<string>(_excludedIds, StringComparer.Ordinal))
            : null;

    public void UpdateFromContext(ContextAssemblyResult result)
    {
        Matches.Clear();
        foreach (var segment in result.Segments.Where(segment =>
                     segment.Kind == ContextSegmentKind.Search
                     && segment.Id.StartsWith(
                         "retrieval:",
                         StringComparison.Ordinal)))
        {
            Matches.Add(new RetrievalMatchViewModel(
                segment.Id["retrieval:".Length..],
                segment.Title,
                segment.Content));
        }

        Diagnostics.Clear();
        foreach (var diagnostic in LanguageRuntime.LocalizeDiagnostics(
                     result.Diagnostics,
                     "Retrieval.DiagnosticsSummaryFormat"))
        {
            Diagnostics.Add(diagnostic);
        }

        Status = IsEnabled
            ? LanguageRuntime.Format("Retrieval.InjectedFormat", Matches.Count, _excludedIds.Count)
            : LanguageRuntime.GetString("Retrieval.Disabled");
    }

    private async Task SaveSettingsAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        try
        {
            await _repository.SaveSettingsAsync(new RetrievalSettings
            {
                ConversationId = _conversationId,
                IsEnabled = IsEnabled,
                Scope = Scope,
                RecentMessageCount = RecentMessageCount,
                MaximumResults = MaximumResults,
                TokenBudget = TokenBudget
            });
            Status = LanguageRuntime.GetString("Retrieval.Saved");
            _contextChanged();
        }
        catch (Exception exception)
        {
            Status = LanguageRuntime.Format("Retrieval.SaveFailedFormat", LanguageRuntime.ErrorMessage(exception));
        }
    }

    private void Exclude(object? parameter)
    {
        if (parameter is not RetrievalMatchViewModel match
            || !_excludedIds.Add(match.MessageId))
        {
            return;
        }

        OnPropertyChanged(nameof(ExcludedCount));
        ClearExclusionsCommand.RaiseCanExecuteChanged();
        Status = LanguageRuntime.Format("Retrieval.ExcludedFormat", match.Title);
        _contextChanged();
    }

    private void ClearExclusions()
    {
        if (_excludedIds.Count == 0)
        {
            return;
        }

        _excludedIds.Clear();
        OnPropertyChanged(nameof(ExcludedCount));
        ClearExclusionsCommand.RaiseCanExecuteChanged();
        Status = LanguageRuntime.GetString("Retrieval.ExclusionsCleared");
        _contextChanged();
    }

    private void SetOption<T>(ref T field, T value)
    {
        if (SetProperty(ref field, value) && !_loading)
        {
            _contextChanged();
        }
    }
}

public sealed record RetrievalMatchViewModel(
    string MessageId,
    string Title,
    string Content);

public sealed record RetrievalScopeOption(
    RetrievalScope Value,
    string Label);
