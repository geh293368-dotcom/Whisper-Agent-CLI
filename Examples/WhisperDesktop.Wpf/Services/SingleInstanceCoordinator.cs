namespace WhisperDesktop.Modern.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    const string MutexName = "Local\\WhisperDesktop.Modern.SingleInstance.v1";

    readonly Mutex mutex;
    bool ownsMutex;

    public bool IsPrimaryInstance => ownsMutex;

    public SingleInstanceCoordinator()
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        ownsMutex = createdNew;
    }

    public void Dispose()
    {
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
    }
}
