using Microsoft.Data.Sqlite;

namespace Orchestrarr.Storage.Test;

/// <summary>A database file in its own folder, removed at the end. It can also be damaged, to see what the stores do when it cannot be read.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-storage-").FullName;

    public string Path => System.IO.Path.Combine(_folder, "orchestrarr.db");

    public SqliteDatabase Open() => SqliteDatabase.Open(Path);

    /// <summary>Replaces the file by something that is not a database.</summary>
    public void Damage()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }
        File.WriteAllText(Path, "this is not a database");
    }

    /// <summary>Removes the damaged file, so that a new database can be made in its place.</summary>
    public void Repair()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }
}
