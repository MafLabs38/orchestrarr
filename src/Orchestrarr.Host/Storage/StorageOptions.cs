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

    /// <summary>Checked when the host starts: a persistent store with nowhere to persist is an error, never a quiet fallback to memory.</summary>
    public StorageSettings Validate(string contentRoot)
    {
        if (!Enum.IsDefined(Provider))
        {
            throw new InvalidOperationException("Storage:Provider must be Memory or Sqlite.");
        }
        if (Provider == StorageProvider.Memory)
        {
            return new StorageSettings(StorageProvider.Memory, null);
        }
        if (string.IsNullOrWhiteSpace(Path) || Path.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Storage:Path is required when Storage:Provider is Sqlite, and must be a path.");
        }

        return new StorageSettings(StorageProvider.Sqlite, System.IO.Path.GetFullPath(Path, contentRoot));
    }
}

/// <summary>What the host uses once the storage options are checked.</summary>
public sealed record StorageSettings(StorageProvider Provider, string? Path)
{
    public bool Persistent => Provider == StorageProvider.Sqlite;
}
