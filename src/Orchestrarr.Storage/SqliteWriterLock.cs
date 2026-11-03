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

    /// <summary>
    /// Takes the lock, or says that another process holds it. It comes first, before the database is created, migrated or written to: a host that
    /// is refused has touched nothing. It creates the folder of the database when it is not there.
    /// </summary>
    public static SqliteWriterLock Acquire(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteDatabase.EnsureFolder(Path.GetFullPath(databasePath));
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
            throw new WriterLockException($"Cannot take the lock {LockPathOf(databasePath)}: another process most likely holds the database, and only one host may write it. Run a single replica, and stop the old one before the new one starts (a Recreate strategy).", failure);
        }
    }

    public void Dispose() => _lock.Dispose();
}

/// <summary>The lock of the one writer could not be taken: another host holds the database, or the lock file cannot be used.</summary>
public sealed class WriterLockException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
