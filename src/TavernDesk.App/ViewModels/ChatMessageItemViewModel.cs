using System.Windows.Threading;
using System.Text.RegularExpressions;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed partial class ChatMessageItemViewModel : ViewModelBase
{
    private readonly Func<ChatMessageItemViewModel, Task> _edit;
    private readonly Func<ChatMessageItemViewModel, Task> _delete;
    private readonly Func<ChatMessageItemViewModel, Task> _fork;
    private readonly Func<ChatMessageItemViewModel, Task> _regenerate;
    private readonly Func<ChatMessageItemViewModel, Task> _continueGeneration;
    private readonly Func<ChatMessageItemViewModel, bool> _canContinueGeneration;
    private readonly Func<ChatMessageItemViewModel, MessageCandidate, Task>
        _activateCandidate;
    private readonly Action<ChatMessageItemViewModel> _copy;
    private readonly Action<ChatMessageItemViewModel> _openingTools;
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly IReadOnlyList<MessageCandidate> _candidates;
    private string? _senderLabel;
    private string _personaMacroValue;
    private string _characterMacroValue;
    private bool _isToolbarOpen;

    public ChatMessageItemViewModel(
        ChatMessage message,
        Func<ChatMessageItemViewModel, Task> edit,
        Func<ChatMessageItemViewModel, Task> delete,
        Func<ChatMessageItemViewModel, Task> fork,
        Func<ChatMessageItemViewModel, Task> regenerate,
        Func<ChatMessageItemViewModel, Task> continueGeneration,
        Func<ChatMessageItemViewModel, bool> canContinueGeneration,
        Func<ChatMessageItemViewModel, MessageCandidate, Task> activateCandidate,
        IReadOnlyList<MessageCandidate> candidates,
        Action<ChatMessageItemViewModel> copy,
        Action<ChatMessageItemViewModel> openingTools,
        string? senderLabel = null,
        string? personaName = null,
        string? characterName = null,
        string? avatarPath = null)
    {
        Message = message;
        _edit = edit;
        _delete = delete;
        _fork = fork;
        _regenerate = regenerate;
        _continueGeneration = continueGeneration;
        _canContinueGeneration = canContinueGeneration;
        _activateCandidate = activateCandidate;
        _candidates = candidates
            .OrderBy(candidate => candidate.CandidateIndex)
            .ToArray();
        _copy = copy;
        _openingTools = openingTools;
        _senderLabel = senderLabel;
        _personaMacroValue = NormalizeMacroValue(personaName, "USER");
        _characterMacroValue = NormalizeMacroValue(characterName, "角色");
        AvatarPath = avatarPath ?? string.Empty;
        OpenToolsCommand = new RelayCommand(OpenTools);
        EditCommand = new AsyncRelayCommand(() => _edit(this));
        DeleteCommand = new AsyncRelayCommand(() => _delete(this));
        ForkCommand = new AsyncRelayCommand(() => _fork(this));
        RegenerateCommand = new AsyncRelayCommand(
            () => _regenerate(this),
            () => SenderKind == MessageSenderKind.Character);
        ContinueCommand = new AsyncRelayCommand(
            () => _continueGeneration(this),
            () => _canContinueGeneration(this));
        PreviousCandidateCommand = new AsyncRelayCommand(
            () => MoveCandidateAsync(-1),
            () => CanMoveCandidate(-1));
        NextCandidateCommand = new AsyncRelayCommand(
            () => MoveCandidateAsync(1),
            () => CanMoveCandidate(1));
        CopyCommand = new RelayCommand(() => _copy(this));
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autoCloseTimer.Tick += (_, _) => CloseTools();
    }

    public ChatMessage Message { get; }
    public string Id => Message.Id;
    public string Content => Message.Content;
    public string DisplayContent => TavernNameMacroPattern().Replace(
        Message.Content,
        match => string.Equals(
            match.Groups["name"].Value,
            "user",
            StringComparison.OrdinalIgnoreCase)
                ? _personaMacroValue
                : _characterMacroValue);
    public MessageSenderKind SenderKind => Message.SenderKind;
    public string AvatarPath { get; }
    public string TimestampText => Message.CreatedAt.ToLocalTime().ToString("HH:mm");
    public string SenderLabel => _senderLabel ?? SenderKind switch
    {
        MessageSenderKind.User => "USER",
        MessageSenderKind.Character => "角色",
        MessageSenderKind.System => "SYSTEM",
        _ => "工具"
    };
    public string CandidateLabel => SenderKind == MessageSenderKind.Character
        ? $"候选 {Message.ActiveCandidateIndex + 1}"
        : string.Empty;
    public bool HasMultipleCandidates =>
        SenderKind == MessageSenderKind.Character && _candidates.Count > 1;
    public string CandidateNavigationLabel =>
        HasMultipleCandidates
            ? $"{CurrentCandidatePosition + 1}/{_candidates.Count}"
            : string.Empty;

    public bool IsToolbarOpen
    {
        get => _isToolbarOpen;
        set
        {
            if (SetProperty(ref _isToolbarOpen, value) && !value)
            {
                _autoCloseTimer.Stop();
            }
        }
    }

    public RelayCommand OpenToolsCommand { get; }
    public AsyncRelayCommand EditCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand ForkCommand { get; }
    public AsyncRelayCommand RegenerateCommand { get; }
    public AsyncRelayCommand ContinueCommand { get; }
    public AsyncRelayCommand PreviousCandidateCommand { get; }
    public AsyncRelayCommand NextCandidateCommand { get; }
    public RelayCommand CopyCommand { get; }

    public void RefreshContent()
    {
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(DisplayContent));
        OnPropertyChanged(nameof(CandidateLabel));
        OnPropertyChanged(nameof(CandidateNavigationLabel));
    }

    public void ApplyCandidate(MessageCandidate candidate)
    {
        Message.Content = candidate.Content;
        Message.ActiveCandidateIndex = candidate.CandidateIndex;
        RefreshContent();
        PreviousCandidateCommand.RaiseCanExecuteChanged();
        NextCandidateCommand.RaiseCanExecuteChanged();
    }

    public void UpdateSenderLabel(string? senderLabel)
    {
        var normalized = string.IsNullOrWhiteSpace(senderLabel)
            ? null
            : senderLabel.Trim();
        if (string.Equals(_senderLabel, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _senderLabel = normalized;
        OnPropertyChanged(nameof(SenderLabel));
    }

    public void UpdateTavernNames(string? personaName, string? characterName)
    {
        var normalizedPersona = NormalizeMacroValue(personaName, "USER");
        var normalizedCharacter = NormalizeMacroValue(characterName, "角色");
        if (string.Equals(
                _personaMacroValue,
                normalizedPersona,
                StringComparison.Ordinal)
            && string.Equals(
                _characterMacroValue,
                normalizedCharacter,
                StringComparison.Ordinal))
        {
            return;
        }

        _personaMacroValue = normalizedPersona;
        _characterMacroValue = normalizedCharacter;
        OnPropertyChanged(nameof(DisplayContent));
    }

    public void CloseTools()
    {
        _autoCloseTimer.Stop();
        IsToolbarOpen = false;
    }

    private void OpenTools()
    {
        _openingTools(this);
        IsToolbarOpen = true;
        _autoCloseTimer.Stop();
        _autoCloseTimer.Start();
    }

    private int CurrentCandidatePosition
    {
        get
        {
            for (var index = 0; index < _candidates.Count; index++)
            {
                if (_candidates[index].CandidateIndex == Message.ActiveCandidateIndex)
                {
                    return index;
                }
            }

            return 0;
        }
    }

    private bool CanMoveCandidate(int offset)
    {
        if (!HasMultipleCandidates)
        {
            return false;
        }

        var target = CurrentCandidatePosition + offset;
        return target >= 0 && target < _candidates.Count;
    }

    private Task MoveCandidateAsync(int offset)
    {
        if (!CanMoveCandidate(offset))
        {
            return Task.CompletedTask;
        }

        return _activateCandidate(
            this,
            _candidates[CurrentCandidatePosition + offset]);
    }

    private static string NormalizeMacroValue(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value)
            ? fallback
            : value.Trim();

    [GeneratedRegex(
        @"\{\{\s*(?<name>user|char)\s*\}\}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TavernNameMacroPattern();
}
