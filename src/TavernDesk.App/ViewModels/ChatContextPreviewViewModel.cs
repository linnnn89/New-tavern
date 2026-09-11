using System.Collections.ObjectModel;
using TavernDesk.App.Localization;
using TavernDesk.App.Presentation;
using TavernDesk.App.Services;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.App.ViewModels;

/// <summary>
/// Window-owned preview state. Calls and notifications use the owning view's
/// context; inputs are captured requests, never a reference to the chat view model.
/// </summary>
public sealed class ChatContextPreviewViewModel : ViewModelBase, IDisposable, IAsyncDisposable
{
    private readonly IContextAssembler _assembler;
    private readonly List<Task> _pendingRefreshes = [];
    private CancellationTokenSource? _cancellation;
    private long _version;
    private string? _conversationId;
    private string? _actualBudgetConversationId;
    private ContextBudget _budget;
    private TokenEstimate _estimate;
    private GroupContextBudgetResult? _groupBudget;
    private string _apiRequestPreview = LanguageRuntime.GetString("Chat.ApiPreview.Select");
    private string _errorStatus = string.Empty;
    private bool _disposed;

    public ChatContextPreviewViewModel(IContextAssembler assembler, ContextBudget budget)
    {
        _assembler = assembler;
        _budget = budget;
        _estimate = new TokenEstimate(0, budget.ReservedOutputTokens, budget.ContextLimit, false);
    }

    public ObservableCollection<ContextSegment> ContextSegments { get; } = [];
    public event EventHandler<ContextAssemblyResult>? PreviewApplied;
    public string ApiRequestPreview
    {
        get => _apiRequestPreview;
        private set => SetProperty(ref _apiRequestPreview, value);
    }
    public string ErrorStatus
    {
        get => _errorStatus;
        private set => SetProperty(ref _errorStatus, value);
    }
    public string EstimatedTokenText
    {
        get
        {
            var source = LanguageRuntime.BackendMessage(_budget.SourceLabel, "Chat.Model.DefaultBudgetSource");
            var accuracy = LanguageRuntime.GetString(_estimate.IsExact ? "Chat.Token.Exact" : "Chat.Token.Estimated");
            return _estimate.ExceedsLimit
                ? LanguageRuntime.Format("Chat.Token.OverLimitFormat", accuracy, _estimate.InputTokens,
                    _estimate.ReservedOutputTokens, _estimate.ContextLimit, source)
                : LanguageRuntime.Format("Chat.Token.EstimateFormat", accuracy, _estimate.InputTokens,
                    _estimate.ReservedOutputTokens, source);
        }
    }
    public int EstimatedInputTokens => _estimate.InputTokens;
    public string EstimatedTokenHeadline => $"{_estimate.TotalTokens:N0} / {_estimate.ContextLimit:N0}";
    public double EstimatedTokenUsagePercent => _estimate.ContextLimit <= 0
        ? 0 : Math.Clamp(100d * _estimate.TotalTokens / _estimate.ContextLimit, 0, 100);
    public string EstimatedTokenUsageLevel => EstimatedTokenUsagePercent >= 90 ? "Danger"
        : EstimatedTokenUsagePercent >= 70 ? "Warning" : "Normal";
    public bool IsEstimatedOverLimit => _estimate.ExceedsLimit || _groupBudget is { CanSend: false };
    public GroupContextBudgetResult? ContextBudgetResult => _groupBudget;

    public void UpdateBudgetSource(ContextBudget budget)
    {
        if (_disposed) return;
        _budget = budget;
        OnPropertyChanged(nameof(EstimatedTokenText));
    }

    public void BeginSelection(string? conversationId, ContextBudget budget)
    {
        if (_disposed) return;
        InvalidateRefresh();
        _conversationId = conversationId;
        UpdateBudgetSource(budget);
        ContextSegments.Clear();
        ApiRequestPreview = LanguageRuntime.GetString("Chat.ApiPreview.SafeSelect");
        ErrorStatus = string.Empty;
        // Reloading the same conversation after a reply preserves its actual budget.
        if (conversationId is null || _actualBudgetConversationId != conversationId)
        {
            _actualBudgetConversationId = null;
            _groupBudget = null;
            OnPropertyChanged(nameof(ContextBudgetResult));
        }
        if (conversationId is null)
            ApplyBudget(new ContextAssemblyResult([], new TokenEstimate(0,
                budget.ReservedOutputTokens, budget.ContextLimit, false)));
    }

    public void ReleaseActualBudget()
    {
        if (!_disposed) _actualBudgetConversationId = null;
    }

    public Task RefreshAsync(ContextAssemblyRequest request, bool immediate = false,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || _conversationId != request.ConversationId) return Task.CompletedTask;
        InvalidateRefresh();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ErrorStatus = string.Empty;
        // Preview must never trigger remote semantic retrieval, even if a caller
        // passes the defaults used by an actual send request.
        var task = RefreshCoreAsync(request with { AllowRemoteSemanticRetrieval = false },
            immediate, _version, _cancellation.Token);
        _pendingRefreshes.RemoveAll(pending => pending.IsCompleted);
        _pendingRefreshes.Add(task);
        return task;
    }

    private async Task RefreshCoreAsync(ContextAssemblyRequest request, bool immediate,
        long version, CancellationToken cancellationToken)
    {
        try
        {
            if (!immediate) await Task.Delay(150, cancellationToken);
            if (!IsCurrent(request.ConversationId, version, cancellationToken)) return;
            var result = await _assembler.AssembleAsync(request, cancellationToken);
            if (!IsCurrent(request.ConversationId, version, cancellationToken)) return;
            ContextSegments.Clear();
            foreach (var segment in result.Segments) ContextSegments.Add(segment);
            if (_actualBudgetConversationId != request.ConversationId) ApplyBudget(result);
            ApiRequestPreview = ChatRequestFactory.RenderApiRequestPreview(result);
            PreviewApplied?.Invoke(this, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(request.ConversationId, version, cancellationToken))
                ErrorStatus = LanguageRuntime.Format("Chat.ContextEstimate.FailedFormat",
                    LanguageRuntime.ErrorMessage(exception));
        }
    }

    public void PublishActualBudget(string conversationId, ContextAssemblyResult result)
    {
        if (_disposed || _conversationId != conversationId) return;
        _actualBudgetConversationId = conversationId;
        InvalidateRefresh();
        ApplyBudget(result);
    }

    private bool IsCurrent(string conversationId, long version, CancellationToken cancellationToken) =>
        !_disposed && !cancellationToken.IsCancellationRequested
        && version == _version && conversationId == _conversationId;

    private void ApplyBudget(ContextAssemblyResult result)
    {
        _groupBudget = result.GroupBudget;
        _estimate = result.Estimate;
        OnPropertyChanged(nameof(ContextBudgetResult));
        OnPropertyChanged(nameof(EstimatedInputTokens));
        OnPropertyChanged(nameof(EstimatedTokenText));
        OnPropertyChanged(nameof(EstimatedTokenHeadline));
        OnPropertyChanged(nameof(EstimatedTokenUsagePercent));
        OnPropertyChanged(nameof(EstimatedTokenUsageLevel));
        OnPropertyChanged(nameof(IsEstimatedOverLimit));
    }

    private void InvalidateRefresh()
    {
        _version++;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InvalidateRefresh();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        // Await superseded work as well: some assemblers complete after cancellation.
        await Task.WhenAll(_pendingRefreshes);
        _pendingRefreshes.Clear();
    }
}
