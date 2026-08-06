namespace TavernDesk.App.Services;

public sealed class SingleInstanceGate : IDisposable
{
    private Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGate(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public static SingleInstanceGate TryAcquire(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        return new SingleInstanceGate(mutex, createdNew);
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        mutex.Dispose();
    }
}
