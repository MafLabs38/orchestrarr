namespace Orchestrarr.Host.Storage;

public enum StorageProvider
{
    /// <summary>Everything is kept in memory and a restart forgets it: the sandbox's choice.</summary>
    Memory,

    /// <summary>The approvals, the audit trail and the global stop are kept in a SQLite file that survives a restart.</summary>
    Sqlite,
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Memory;

    /// <summary>The SQLite file. A relative path is read from the content root of the host; the image gives an absolute one, in its volume.</summary>
    public string Path { get; set; } = "data/orchestrarr.db";

    /// <summary>
    /// Checked when the host starts: a persistent store with nowhere to persist is an error, never a quiet fallback to memory. Outside
    /// Development a database that does not exist yet starts with the global stop engaged: it may be a volume that was lost.
    /// </summary>
    public IStorageSettings Validate(string contentRoot, bool development)
    {
        if (!Enum.IsDefined(Provider))
        {
            throw new InvalidOperationException("Storage:Provider must be Memory or Sqlite.");
        }
        if (Provider == StorageProvider.Memory)
        {
            return new MemoryStorage();
        }
        if (string.IsNullOrWhiteSpace(Path) || Path.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage:Path is required when Storage:Provider is Sqlite, and must be a path.");
        }

        return new SqliteStorage(System.IO.Path.GetFullPath(Path, contentRoot), StartEngagedWhenNew: !development);
    }
}

/// <summary>What the host uses once the storage options are checked: nothing can be invalid, a file has a path.</summary>
public interface IStorageSettings
{
    bool Persistent { get; }

    /// <summary>What the status says: <c>memory</c> or <c>sqlite</c>.</summary>
    string Kind { get; }
}

/// <summary>Everything in memory, the sandbox's choice.</summary>
public sealed record MemoryStorage : IStorageSettings
{
    public bool Persistent => false;

    public string Kind => "memory";
}

/// <summary>A SQLite file, and whether a database created now starts with the global stop engaged.</summary>
public sealed record SqliteStorage(string Path, bool StartEngagedWhenNew) : IStorageSettings
{
    public bool Persistent => true;

    public string Kind => "sqlite";
}
