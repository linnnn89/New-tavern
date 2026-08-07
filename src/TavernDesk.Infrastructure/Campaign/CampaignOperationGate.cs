using System.Collections.Concurrent;
using TavernDesk.Core.Abstractions;

namespace TavernDesk.Infrastructure.Campaigns;

public sealed class CampaignOperationGate : ICampaignOperationGate
{
    private readonly ConcurrentDictionary<string, CampaignLock> _locks = new(
        StringComparer.Ordinal);

    public Task<IAsyncDisposable> EnterGenerationAsync(
        string campaignId,
        CancellationToken cancellationToken = default) =>
        GetLock(campaignId).EnterGenerationAsync(cancellationToken);

    public Task<IAsyncDisposable> EnterMemoryAsync(
        string campaignId,
        CancellationToken cancellationToken = default) =>
        GetLock(campaignId).EnterMemoryAsync(cancellationToken);

    private CampaignLock GetLock(string campaignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(campaignId);
        return _locks.GetOrAdd(
            campaignId,
            static _ => new CampaignLock());
    }

    private sealed class CampaignLock
    {
        private readonly SemaphoreSlim _turnstile = new(1, 1);
        private readonly SemaphoreSlim _resource = new(1, 1);
        private readonly SemaphoreSlim _readerMutex = new(1, 1);
        private int _activeGenerations;

        public async Task<IAsyncDisposable> EnterGenerationAsync(
            CancellationToken cancellationToken)
        {
            // A waiting memory update closes the turnstile so new generation
            // requests wait, while already active player requests may finish.
            await _turnstile.WaitAsync(cancellationToken);
            _turnstile.Release();

            await _readerMutex.WaitAsync(cancellationToken);
            try
            {
                _activeGenerations++;
                if (_activeGenerations == 1)
                {
                    try
                    {
                        await _resource.WaitAsync(cancellationToken);
                    }
                    catch
                    {
                        _activeGenerations--;
                        throw;
                    }
                }
            }
            finally
            {
                _readerMutex.Release();
            }

            return new Lease(ExitGenerationAsync);
        }

        public async Task<IAsyncDisposable> EnterMemoryAsync(
            CancellationToken cancellationToken)
        {
            // Hold the turnstile while waiting for existing generations to
            // drain. This blocks new generation requests from overtaking the
            // waiting memory update.
            await _turnstile.WaitAsync(cancellationToken);
            try
            {
                await _resource.WaitAsync(cancellationToken);
            }
            catch
            {
                _turnstile.Release();
                throw;
            }

            return new Lease(() =>
            {
                _resource.Release();
                _turnstile.Release();
                return ValueTask.CompletedTask;
            });
        }

        private async ValueTask ExitGenerationAsync()
        {
            await _readerMutex.WaitAsync();
            try
            {
                _activeGenerations--;
                if (_activeGenerations == 0)
                {
                    _resource.Release();
                }
            }
            finally
            {
                _readerMutex.Release();
            }
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly Func<ValueTask> _release;
        private int _disposed;

        public Lease(Func<ValueTask> release)
        {
            _release = release;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            return _release();
        }
    }
}
