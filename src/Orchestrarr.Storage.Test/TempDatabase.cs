using Microsoft.Data.Sqlite;

namespace Orchestrarr.Storage.Test;

/// <summary>A database file in its own folder, removed at the end. It can also be damaged, to see what the stores do when it cannot be read.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("orchestrarr-storage-").FullName;

    public string Path => System.IO.Path.Combine(_folder, "orchestrarr.db");

    public SqliteDatabase Open() => SqliteDatabase.Open(Path);

    /// <summary>Opens it as a restart would: no connection of the previous run is still there.</summary>
    public SqliteDatabase Reopen()
    {
        SqliteConnection.ClearAllPools();
        return Open();
    }

    /// <summary>Replaces the file by something that is not a database.</summary>
    public void Damage()
    {
        SqliteConnection.ClearAllPools();
        DeleteFiles();
        File.WriteAllText(Path, "this is not a database");
    }

    /// <summary>Deletes the files as an operator or a swapped volume would, without telling the host: no connection is closed or cleared first.</summary>
    public void Vanish() => DeleteFiles();

    /// <summary>Removes the damaged file, so that a new database can be made in its place.</summary>
    public void Repair()
    {
        SqliteConnection.ClearAllPools();
        DeleteFiles();
    }

    private void DeleteFiles()
    {
        foreach (var file in Directory.GetFiles(_folder))
        {
            File.Delete(file);
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A folder of a test left behind must not hide what the test found
        }
    }
}
