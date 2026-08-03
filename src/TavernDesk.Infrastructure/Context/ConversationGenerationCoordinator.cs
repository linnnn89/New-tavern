using System.Collections.Concurrent;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class ConversationGenerationCoordinator : IConversationGenerationCoordinator
{
    private readonly ConcurrentDictionary<string, GenerationRun> _runs = new();
    private readonly ConcurrentDictionary<string, ConversationGenerationState> _states = new();
    private readonly object _registrationGate = new();
    private readonly SemaphoreSlim _cancelAllGate = new(1, 1);
    private bool _acceptingRuns = true;

    public event EventHandler<ConversationGenerationState>? StateChanged;

    public ConversationGenerationState GetState(string conversationId) =>
        _states.GetOrAdd(
            conversationId,
            id => new ConversationGenerationState(
                id,
                GenerationId: null,
                ConversationGenerationStatus.Idle,
                ErrorMessage: null,
                DateTimeOffset.Now));

    public Task RunAsync(
        string conversationId,
        Func<CancellationToken, IAsyncEnumerable<string>> streamFactory,
        Func<string, CancellationToken, ValueTask> receiveChunk,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(
            conversationId,
            streamFactory,
            receiveChunk,
            cancellationToken);

    public Task RunProviderAsync(
        string operationId,
        Func<CancellationToken, IAsyncEnumerable<ProviderStreamEvent>> streamFactory,
        Func<ProviderStreamEvent, CancellationToken, ValueTask> receiveEvent,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(
            operationId,
            streamFactory,
            receiveEvent,
            cancellationToken);

    public bool Cancel(string operationId)
    {
        if (!_runs.TryGetValue(operationId, out var run))
        {
            return false;
        }

        Publish(
            operationId,
            run.GenerationId,
            ConversationGenerationStatus.Stopping);
        run.Cancellation.Cancel();
        return true;
    }

    public async Task<int> CancelAllAsync()
    {
        await _cancelAllGate.WaitAsync();
        try
        {
            GenerationRun[] runs;
            lock (_registrationGate)
            {
                _acceptingRuns = false;
                runs = _runs.Values.ToArray();
            }

            foreach (var run in runs)
            {
                Publish(
                    run.OperationId,
                    run.GenerationId,
                    ConversationGenerationStatus.Stopping);
                run.Cancellation.Cancel();
            }

            await Task.WhenAll(runs.Select(run => run.Completion.Task));
            return runs.Length;
        }
        finally
        {
            lock (_registrationGate)
            {
                _acceptingRuns = true;
            }

            _cancelAllGate.Release();
        }
    }

    private async Task RunCoreAsync<T>(
        string operationId,
        Func<CancellationToken, IAsyncEnumerable<T>> streamFactory,
        Func<T, CancellationToken, ValueTask> receiveItem,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        ArgumentNullException.ThrowIfNull(streamFactory);
        ArgumentNullException.ThrowIfNull(receiveItem);

        var generationId = Guid.NewGuid().ToString("N");
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var run = new GenerationRun(operationId, generationId, linkedCancellation);
        lock (_registrationGate)
        {
            if (!_acceptingRuns)
            {
                throw new InvalidOperationException(
                    "正在停止全部生成请求，请稍后再开始新的生成。");
            }

            if (!_runs.TryAdd(operationId, run))
            {
                throw new InvalidOperationException(
                    "同一操作已经存在进行中的生成任务。");
            }
        }

        Publish(operationId, generationId, ConversationGenerationStatus.Queued);
        try
        {
            Publish(operationId, generationId, ConversationGenerationStatus.Streaming);
            await foreach (var item in streamFactory(linkedCancellation.Token)
                               .WithCancellation(linkedCancellation.Token))
            {
                await receiveItem(item, linkedCancellation.Token);
            }

            Publish(
                operationId,
                generationId,
                linkedCancellation.IsCancellationRequested
                    ? ConversationGenerationStatus.Interrupted
                    : ConversationGenerationStatus.Completed);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            Publish(operationId, generationId, ConversationGenerationStatus.Interrupted);
        }
        catch (Exception) when (linkedCancellation.IsCancellationRequested)
        {
            Publish(operationId, generationId, ConversationGenerationStatus.Interrupted);
        }
        catch (Exception exception)
        {
            Publish(
                operationId,
                generationId,
                ConversationGenerationStatus.Failed,
                exception.Message);
            throw;
        }
        finally
        {
            _runs.TryRemove(operationId, out _);
            run.Completion.TrySetResult();
        }
    }

    private void Publish(
        string operationId,
        string generationId,
        ConversationGenerationStatus status,
        string? errorMessage = null)
    {
        var state = new ConversationGenerationState(
            operationId,
            generationId,
            status,
            errorMessage,
            DateTimeOffset.Now);
        _states[operationId] = state;
        StateChanged?.Invoke(this, state);
    }

    private sealed record GenerationRun(
        string OperationId,
        string GenerationId,
        CancellationTokenSource Cancellation)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
