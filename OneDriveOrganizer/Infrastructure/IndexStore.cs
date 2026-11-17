using System.Text.Json;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Models;

namespace OneDriveOrganizer.Infrastructure;

public sealed class IndexStore : IDisposable
{
    private readonly SqliteConnection connection;
    public IndexStore(string root)
    {
        var folder = Path.Combine(root, ".odo");
        PathPolicy.AssertNoLinks(folder);
        Directory.CreateDirectory(folder);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(folder, "index.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS files(path TEXT PRIMARY KEY, size INTEGER NOT NULL, modified TEXT NOT NULL, data TEXT NOT NULL); CREATE TABLE IF NOT EXISTS artifacts(path TEXT PRIMARY KEY);";
        command.ExecuteNonQuery();
    }

    public Dictionary<string, FileRecord> Load()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM files";
        using var reader = command.ExecuteReader();
        var records = new Dictionary<string, FileRecord>(PathPolicy.Comparer);
        while (reader.Read())
        {
            var record = JsonSerializer.Deserialize<FileRecord>(reader.GetString(0)) ?? throw new InvalidDataException("Invalid index record");
            records.Add(record.Path, record);
        }
        return records;
    }

    public void Save(IEnumerable<FileRecord> files)
    {
        using var transaction = connection.BeginTransaction();
        using var clear = connection.CreateCommand();
        clear.Transaction = transaction;
        clear.CommandText = "DELETE FROM files";
        clear.ExecuteNonQuery();
        foreach (var file in files)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO files(path,size,modified,data) VALUES($path,$size,$modified,$data)";
            command.Parameters.AddWithValue("$path", file.Path);
            command.Parameters.AddWithValue("$size", file.Size);
            command.Parameters.AddWithValue("$modified", file.LastWriteUtc.ToString("O"));
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(file));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public HashSet<string> Artifacts()
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM artifacts";
        using var reader = command.ExecuteReader();
        var paths = new HashSet<string>(PathPolicy.Comparer);
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    public void Register(string path)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO artifacts(path) VALUES($path)";
        command.Parameters.AddWithValue("$path", PathPolicy.Normalize(path));
        command.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}
