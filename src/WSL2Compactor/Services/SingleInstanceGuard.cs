namespace WSL2Compactor.Services;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex? _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex? mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public bool IsAcquired => _ownsMutex;

    public static SingleInstanceGuard TryAcquire(string name)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var ownsMutex);
        return new SingleInstanceGuard(mutex, ownsMutex);
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch
            {
                // The mutex may already be abandoned during process teardown.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
