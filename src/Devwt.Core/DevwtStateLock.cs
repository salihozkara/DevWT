namespace Devwt.Core;

public static class DevwtStateLock
{
    private const string MutexName = @"Local\DevWT.State.v1";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName);
        var acquired = false;
        try
        {
            acquired = mutex.WaitOne(Timeout);
            if (!acquired)
            {
                throw new IOException("Timed out waiting for the DevWT state lock.");
            }

            return action();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
        }
    }
}
