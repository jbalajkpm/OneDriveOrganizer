using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using OneDriveOrganizer.Infrastructure;

namespace OneDriveOrganizer.Services.AI;

public sealed class AiStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public AiStore(string root)
    {
        using (var index = new IndexStore(root)) { }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, ".odo", "index.db") }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS ai_vectors(hash TEXT NOT NULL, kind TEXT NOT NULL, model TEXT NOT NULL, vector BLOB NOT NULL, PRIMARY KEY(hash,kind,model));
            CREATE TABLE IF NOT EXISTS ai_calls(hash TEXT NOT NULL, provider TEXT NOT NULL, state TEXT NOT NULL, json TEXT, PRIMARY KEY(hash,provider));
            """;
        command.ExecuteNonQuery();
    }

    public float[]? Vector(string hash, string kind, string model)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT vector FROM ai_vectors WHERE hash=$hash AND kind=$kind AND model=$model";
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$model", model);
            if (command.ExecuteScalar() is not byte[] data) return null;
            if (data.Length != Vectors.Dimensions * 4) throw new InvalidDataException("Invalid cached embedding BLOB");
            var vector = new float[Vectors.Dimensions];
            for (int i = 0; i < vector.Length; i++) vector[i] = BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4, 4));
            return Vectors.Normalize(vector);
        }
    }

    public void SaveVector(string hash, string kind, string model, float[] vector)
    {
        var normalized = Vectors.Normalize(vector);
        var data = new byte[normalized.Length * 4];
        for (int i = 0; i < normalized.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4, 4), normalized[i]);
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_vectors VALUES($hash,$kind,$model,$vector) ON CONFLICT(hash,kind,model) DO UPDATE SET vector=excluded.vector";
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$model", model); command.Parameters.AddWithValue("$vector", data);
            command.ExecuteNonQuery();
        }
    }

    public string? Result(string hash, string provider)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state,json FROM ai_calls WHERE hash=$hash AND provider=$provider";
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$provider", provider);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            if (reader.GetString(0) != "Complete") throw new IOException($"AI call {provider}/{hash} is pending or uncertain. It will not be automatically submitted again; inspect the provider operation and local ledger.");
            return reader.GetString(1);
        }
    }

    public void Begin(string hash, string provider)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO ai_calls(hash,provider,state) VALUES($hash,$provider,'Pending')";
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$provider", provider);
            command.ExecuteNonQuery();
        }
    }

    public void Complete(string hash, string provider, string json)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE ai_calls SET state='Complete',json=$json WHERE hash=$hash AND provider=$provider";
            command.Parameters.AddWithValue("$hash", hash); command.Parameters.AddWithValue("$provider", provider); command.Parameters.AddWithValue("$json", json);
            if (command.ExecuteNonQuery() != 1) throw new InvalidDataException("AI request ledger entry is missing");
        }
    }
    public void Dispose() => connection.Dispose();
}
