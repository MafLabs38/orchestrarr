namespace Orchestrarr.Storage;

/// <summary>
/// The one writer of a database file, enforced: the host takes an exclusive lock on a file beside the database and holds it as long as it lives, so
/// that a second host on the same file (a rolling update that starts the new pod before it stops the old one, a second replica) stops at its start
/// instead of writing next to the first with a stop it cannot see (ADR 0013). The lock goes when the process does, whatever way it ends.
/// </summary>
public sealed class SqliteWriterLock : IDisposable
{
    private readonly FileStream _lock;

    private SqliteWriterLock(FileStream held) => _lock = held;

    /// <summary>The name of the file that carries the lock.</summary>
    public static string LockPathOf(string databasePath) => databasePath + ".lock";

    /// <summary>Takes the lock, or says that another process holds it. The folder of the database must exist.</summary>
    public static SqliteWriterLock Acquire(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            return new SqliteWriterLock(new FileStream(Path.GetFullPath(LockPathOf(databasePath)), options));
        }
        catch (IOException failure)
        {
            throw new InvalidOperationException($"Another process holds the database ({LockPathOf(databasePath)}): only one host may write it. Run a single replica, and stop the old one before the new one starts (a Recreate strategy).", failure);
        }
    }

    public void Dispose() => _lock.Dispose();
}
