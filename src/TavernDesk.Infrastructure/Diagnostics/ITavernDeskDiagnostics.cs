using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Diagnostics;

public sealed record ApiTestTraceMetadata(
    string Operation,
    string ProviderId,
    string ProviderName,
    string? ModelId,
    string Adapter,
    string Endpoint);

public sealed record ApiTestOutputSummary(
    int FileCount,
    long TotalBytes);

public sealed class ApiTestOutputBusyException : InvalidOperationException;

public interface ITavernDeskDiagnostics
{
    string ErrorLogDirectory { get; }

    string ApiTestOutputDirectory { get; }

    bool IsApiTestModeEnabled { get; }

    bool HasActiveApiTestTraces { get; }

    void LogError(
        string category,
        Exception exception,
        IReadOnlyDictionary<string, object?>? context = null,
        bool includeExceptionMessage = false);

    Task SetApiTestModeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<ApiTestTraceSession> BeginApiTraceAsync(
        ApiTestTraceMetadata metadata,
        object? requestBody,
        CancellationToken cancellationToken = default);

    Task<ApiTestOutputSummary> GetApiTestOutputSummaryAsync(
        CancellationToken cancellationToken = default);

    Task<int> ClearApiTestOutputAsync(
        CancellationToken cancellationToken = default);
}

public abstract class ApiTestTraceSession : IAsyncDisposable
{
    public abstract void Observe(ProviderStreamEvent streamEvent);

    public abstract Task CompleteAsync(
        object? responseBody = null,
        CancellationToken cancellationToken = default);

    public abstract Task FailAsync(
        Exception exception,
        CancellationToken cancellationToken = default);

    public abstract ValueTask DisposeAsync();
}

public sealed class NullTavernDeskDiagnostics : ITavernDeskDiagnostics
{
    public static NullTavernDeskDiagnostics Instance { get; } = new();

    private NullTavernDeskDiagnostics()
    {
    }

    public string ErrorLogDirectory => string.Empty;

    public string ApiTestOutputDirectory => string.Empty;

    public bool IsApiTestModeEnabled => false;

    public bool HasActiveApiTestTraces => false;

    public void LogError(
        string category,
        Exception exception,
        IReadOnlyDictionary<string, object?>? context = null,
        bool includeExceptionMessage = false)
    {
    }

    public Task SetApiTestModeEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<ApiTestTraceSession> BeginApiTraceAsync(
        ApiTestTraceMetadata metadata,
        object? requestBody,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ApiTestTraceSession>(DisabledApiTestTraceSession.Instance);

    public Task<ApiTestOutputSummary> GetApiTestOutputSummaryAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ApiTestOutputSummary(0, 0));

    public Task<int> ClearApiTestOutputAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);

    private sealed class DisabledApiTestTraceSession : ApiTestTraceSession
    {
        public static DisabledApiTestTraceSession Instance { get; } = new();

        public override void Observe(ProviderStreamEvent streamEvent)
        {
        }

        public override Task CompleteAsync(
            object? responseBody = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override Task FailAsync(
            Exception exception,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
