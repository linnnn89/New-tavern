using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Context;

public sealed class ConversationGenerationCoordinator : IConversationGenerationCoordinator
{
    private const int MaximumRetainedTerminalStates = 256;
    private readonly ConcurrentDictionary<string, GenerationRun> _runs = new();
    private readonly ConcurrentDictionary<string, ConversationGenerationState> _states = new();
    private readonly ConcurrentQueue<CompletedStateKey> _terminalStateOrder = new();
    // This gate couples registration with the global stop switch. Concurrent
    // dictionaries alone cannot prevent a new run from slipping into Stop All.
    private readonly object _registrationGate = new();
    private readonly SemaphoreSlim _cancelAllGate = new(1, 1);
    private bool _acceptingRuns = true;
    private int _retainedTerminalStates;

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

    public void ReportReceivedText(string operationId, string content)
    {
        if (string.IsNullOrEmpty(content)
            || !_runs.TryGetValue(operationId, out var run))
        {
            return;
        }

        UpdateReceivedProgress(
            run,
            operationId,
            run.GenerationId,
            Encoding.UTF8.GetByteCount(content));
    }

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
            ConversationGenerationStatus.Stopping,
            receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
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
                // Close registration before taking the snapshot, then wait for
                // every run's local finally block before reopening the gate.
                _acceptingRuns = false;
                runs = _runs.Values.ToArray();
            }

            foreach (var run in runs)
            {
                Publish(
                    run.OperationId,
                    run.GenerationId,
                    ConversationGenerationStatus.Stopping,
                    receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
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
                var itemBytes = GetStreamItemByteCount(item);
                if (itemBytes > 0)
                {
                    UpdateReceivedProgress(
                        run,
                        operationId,
                        generationId,
                        itemBytes);
                }

                await receiveItem(item, linkedCancellation.Token);
            }

            Publish(
                operationId,
                generationId,
                linkedCancellation.IsCancellationRequested
                    ? ConversationGenerationStatus.Interrupted
                    : ConversationGenerationStatus.Completed,
                receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            Publish(
                operationId,
                generationId,
                ConversationGenerationStatus.Interrupted,
                receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
        }
        catch (Exception) when (linkedCancellation.IsCancellationRequested)
        {
            // Providers may surface transport/disposal errors after cancellation
            // instead of OperationCanceledException; user intent still defines
            // this terminal state as interrupted rather than failed.
            Publish(
                operationId,
                generationId,
                ConversationGenerationStatus.Interrupted,
                receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
        }
        catch (Exception exception)
        {
            Publish(
                operationId,
                generationId,
                ConversationGenerationStatus.Failed,
                exception.Message,
                ApproximateTokens(run.ReceivedUtf8Bytes));
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
        string? errorMessage = null,
        int receivedTokens = 0)
    {
        ConversationGenerationState state;
        lock (_registrationGate)
        {
            // Status callbacks, normal completion and cancellation can race.
            // ShouldReplace makes terminal states immutable and keeps Stopping
            // from regressing to Streaming.
            if (_states.TryGetValue(operationId, out var current)
                && !ShouldReplace(current.Status, status))
            {
                return;
            }

            state = new ConversationGenerationState(
                operationId,
                generationId,
                status,
                errorMessage,
                DateTimeOffset.Now,
                receivedTokens);
            _states[operationId] = state;
            if (status is ConversationGenerationStatus.Completed
                or ConversationGenerationStatus.Interrupted
                or ConversationGenerationStatus.Failed)
            {
                _terminalStateOrder.Enqueue(new CompletedStateKey(
                    operationId,
                    generationId));
                Interlocked.Increment(ref _retainedTerminalStates);
            }
        }

        if (status is ConversationGenerationStatus.Completed
            or ConversationGenerationStatus.Interrupted
            or ConversationGenerationStatus.Failed)
        {
            TrimTerminalStates();
        }

        StateChanged?.Invoke(this, state);
    }

    private static bool ShouldReplace(
        ConversationGenerationStatus current,
        ConversationGenerationStatus next)
    {
        if (current is ConversationGenerationStatus.Completed
            or ConversationGenerationStatus.Interrupted
            or ConversationGenerationStatus.Failed)
        {
            return false;
        }

        return current != ConversationGenerationStatus.Stopping
               || next != ConversationGenerationStatus.Streaming;
    }

    private void TrimTerminalStates()
    {
        while (Volatile.Read(ref _retainedTerminalStates)
               > MaximumRetainedTerminalStates
               && _terminalStateOrder.TryDequeue(out var oldest))
        {
            Interlocked.Decrement(ref _retainedTerminalStates);
            if (!_states.TryGetValue(oldest.OperationId, out var state)
                || !string.Equals(
                    state.GenerationId,
                    oldest.GenerationId,
                    StringComparison.Ordinal))
            {
                // The same operation id may already belong to a newer run; an
                // old retention entry must not evict that newer state.
                continue;
            }

            ((ICollection<KeyValuePair<string, ConversationGenerationState>>)
                _states).Remove(new KeyValuePair<string, ConversationGenerationState>(
                oldest.OperationId,
                state));
        }
    }

    private static long GetStreamItemByteCount<T>(T item) =>
        item switch
        {
            string text => Encoding.UTF8.GetByteCount(text),
            ProviderStreamEvent streamEvent
                when streamEvent.Kind is ProviderStreamEventKind.Reasoning
                    or ProviderStreamEventKind.Content
                => Encoding.UTF8.GetByteCount(streamEvent.Content),
            _ => 0
        };

    private void UpdateReceivedProgress(
        GenerationRun run,
        string operationId,
        string generationId,
        long itemBytes)
    {
        run.ReceivedUtf8Bytes += itemBytes;
        // Progress is UI telemetry, not accounting. Throttling avoids a
        // dispatcher notification for every tiny streaming chunk.
        if (run.LastProgressTimestamp != 0
            && Stopwatch.GetElapsedTime(run.LastProgressTimestamp)
                < TimeSpan.FromMilliseconds(120))
        {
            return;
        }

        run.LastProgressTimestamp = Stopwatch.GetTimestamp();
        Publish(
            operationId,
            generationId,
            ConversationGenerationStatus.Streaming,
            receivedTokens: ApproximateTokens(run.ReceivedUtf8Bytes));
    }

    private static int ApproximateTokens(long utf8ByteCount)
    {
        if (utf8ByteCount <= 0)
        {
            return 0;
        }

        return (int)Math.Min(
            int.MaxValue,
            Math.Ceiling(utf8ByteCount / 3.2d) + 4);
    }

    private sealed record GenerationRun(
        string OperationId,
        string GenerationId,
        CancellationTokenSource Cancellation)
    {
        public long ReceivedUtf8Bytes { get; set; }
        public long LastProgressTimestamp { get; set; }

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record CompletedStateKey(
        string OperationId,
        string GenerationId);
}
