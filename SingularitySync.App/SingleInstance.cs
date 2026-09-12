namespace SingularitySync.App;

internal sealed class SingleInstance : IDisposable
{
    // Global scope also prevents another Windows session from starting a second copy.
    private const string MutexName = @"Global\SingularitySync.App.SingleInstance.v1";
    private readonly Mutex mutex;
    private SingleInstance(Mutex mutex) => this.mutex = mutex;

    public static SingleInstance? TryAcquire(string name = MutexName)
    {
        Mutex mutex;
        try { mutex = new Mutex(false, name); }
        catch (UnauthorizedAccessException) { return null; } // Owned by another Windows user.
        bool acquired;
        try { acquired = mutex.WaitOne(250); }
        catch (AbandonedMutexException) { acquired = true; }
        if (acquired) return new(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}
