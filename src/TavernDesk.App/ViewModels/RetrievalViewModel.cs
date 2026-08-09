using System.Collections.ObjectModel;
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
    private string _status = "选择会话后可配置长期上下文召回。";
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
        new(RetrievalScope.CurrentConversation, "仅当前会话"),
        new(RetrievalScope.SameCharacter, "同一角色的全部会话")
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
                ? "群聊召回限定当前群聊 ID，避免跨群聊记忆污染。"
                : "默认只召回当前会话；可主动切换为同一角色的其他会话。";
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
            Status = "选择会话后可配置长期上下文召回。";
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
        foreach (var diagnostic in result.Diagnostics ?? [])
        {
            Diagnostics.Add(diagnostic);
        }

        Status = IsEnabled
            ? $"本轮已注入 {Matches.Count} 条召回；排除 {_excludedIds.Count} 条。"
            : "本轮已关闭自动召回，历史仍按原始完整列表组装。";
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
            Status = "召回设置已保存到当前会话。";
            _contextChanged();
        }
        catch (Exception exception)
        {
            Status = $"召回设置未保存：{exception.Message}";
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
        Status = $"本轮已排除：{match.Title}";
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
        Status = "已清除本轮排除项。";
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
