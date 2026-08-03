using System.Windows.Threading;
using TavernDesk.App.Presentation;
using TavernDesk.Core.Models;

namespace TavernDesk.App.ViewModels;

public sealed class ChatMessageItemViewModel : ViewModelBase
{
    private readonly Func<ChatMessageItemViewModel, Task> _edit;
    private readonly Func<ChatMessageItemViewModel, Task> _delete;
    private readonly Func<ChatMessageItemViewModel, Task> _fork;
    private readonly Func<ChatMessageItemViewModel, Task> _regenerate;
    private readonly Action<ChatMessageItemViewModel> _copy;
    private readonly Action<ChatMessageItemViewModel> _openingTools;
    private readonly DispatcherTimer _autoCloseTimer;
    private readonly string? _senderLabel;
    private bool _isToolbarOpen;

    public ChatMessageItemViewModel(
        ChatMessage message,
        Func<ChatMessageItemViewModel, Task> edit,
        Func<ChatMessageItemViewModel, Task> delete,
        Func<ChatMessageItemViewModel, Task> fork,
        Func<ChatMessageItemViewModel, Task> regenerate,
        Action<ChatMessageItemViewModel> copy,
        Action<ChatMessageItemViewModel> openingTools,
        string? senderLabel = null)
    {
        Message = message;
        _edit = edit;
        _delete = delete;
        _fork = fork;
        _regenerate = regenerate;
        _copy = copy;
        _openingTools = openingTools;
        _senderLabel = senderLabel;
        OpenToolsCommand = new RelayCommand(OpenTools);
        EditCommand = new AsyncRelayCommand(() => _edit(this));
        DeleteCommand = new AsyncRelayCommand(() => _delete(this));
        ForkCommand = new AsyncRelayCommand(() => _fork(this));
        RegenerateCommand = new AsyncRelayCommand(
            () => _regenerate(this),
            () => SenderKind == MessageSenderKind.Character);
        CopyCommand = new RelayCommand(() => _copy(this));
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _autoCloseTimer.Tick += (_, _) => CloseTools();
    }

    public ChatMessage Message { get; }
    public string Id => Message.Id;
    public string Content => Message.Content;
    public MessageSenderKind SenderKind => Message.SenderKind;
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
    public RelayCommand CopyCommand { get; }

    public void RefreshContent()
    {
        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(CandidateLabel));
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
}
