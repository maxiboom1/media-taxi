using Microsoft.Data.Sqlite;
using MediaTaxi.Domain;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MediaTaxi.Storage;

public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    private readonly FileStream instanceLock;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public JobStore(string stateFolder)
    {
        Directory.CreateDirectory(stateFolder);
        instanceLock = new FileStream(Path.Combine(stateFolder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(stateFolder, "media-taxi.db"), Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS artifacts(path TEXT PRIMARY KEY COLLATE NOCASE,job_id TEXT NOT NULL,kind TEXT NOT NULL); CREATE TABLE IF NOT EXISTS statistics(id TEXT PRIMARY KEY,at INTEGER NOT NULL,kind TEXT NOT NULL,mode TEXT NOT NULL); CREATE INDEX IF NOT EXISTS stats_at ON statistics(at);");
    }
    public List<JobRecord> Load()
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM jobs";
            using var reader = command.ExecuteReader(); var jobs = new List<JobRecord>();
            while (reader.Read()) jobs.Add(JsonSerializer.Deserialize<JobRecord>(reader.GetString(0), JsonOptions) ?? throw new InvalidDataException("Invalid persisted job."));
            return jobs;
        }
    }
    public void Save(JobRecord job)
    {
        lock (gate) Execute("INSERT INTO jobs(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload", ("$id", job.Id), ("$payload", JsonSerializer.Serialize(job, JsonOptions)));
    }
    public void SaveWithEvent(JobRecord job, string kind, DateTimeOffset? at = null)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO jobs(id,payload) VALUES($job,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload; INSERT OR IGNORE INTO statistics(id,at,kind,mode) VALUES($event,$at,$kind,$mode)";
            command.Parameters.AddWithValue("$job", job.Id);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(job, JsonOptions));
            command.Parameters.AddWithValue("$event", job.Id + ":" + kind);
            command.Parameters.AddWithValue("$at", (at ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$mode", job.Mode);
            command.ExecuteNonQuery(); transaction.Commit();
        }
    }
    public void Delete(string jobId)
    {
        lock (gate) Execute("DELETE FROM jobs WHERE id=$id; DELETE FROM artifacts WHERE job_id=$id", ("$id", jobId));
    }
    public bool Claim(string path, string jobId, string kind)
    {
        lock (gate)
        {
            Execute("INSERT OR IGNORE INTO artifacts(path,job_id,kind) VALUES($path,$job,$kind)", ("$path", path), ("$job", jobId), ("$kind", kind));
            return Owner(path) == jobId;
        }
    }
    public string? Owner(string path)
    {
        lock (gate)
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT job_id FROM artifacts WHERE path=$path"; command.Parameters.AddWithValue("$path", path);
            return command.ExecuteScalar() as string;
        }
    }
    public void PruneStatistics(DateTimeOffset? now = null)
    {
        lock (gate) Execute("DELETE FROM statistics WHERE at<$cutoff", ("$cutoff", (now ?? DateTimeOffset.UtcNow).AddHours(-24).ToUnixTimeMilliseconds()));
    }
    public void WriteBatch(IReadOnlyList<JobCheckpoint> batch)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var item in batch)
            {
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.Parameters.AddWithValue("$id", item.Id);
                if (item.Payload is null)
                    command.CommandText = "DELETE FROM jobs WHERE id=$id; DELETE FROM artifacts WHERE job_id=$id";
                else
                {
                    command.CommandText = "INSERT INTO jobs(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=excluded.payload";
                    command.Parameters.AddWithValue("$payload", item.Payload);
                    if (item.EventKind is not null)
                    {
                        command.CommandText += "; INSERT OR IGNORE INTO statistics(id,at,kind,mode) VALUES($event,$at,$kind,$mode)";
                        command.Parameters.AddWithValue("$event", item.Id + ":" + item.EventKind);
                        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        command.Parameters.AddWithValue("$kind", item.EventKind);
                        command.Parameters.AddWithValue("$mode", item.Mode);
                    }
                }
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }
    public StatisticsSnapshot Statistics(DateTimeOffset? now = null)
    {
        lock (gate)
        {
            var cutoff = (now ?? DateTimeOffset.UtcNow).AddHours(-24).ToUnixTimeMilliseconds();
            using var command = connection.CreateCommand(); command.CommandText = "SELECT kind,mode,count(*) FROM statistics WHERE at >= $cutoff GROUP BY kind,mode"; command.Parameters.AddWithValue("$cutoff", cutoff);
            using var reader = command.ExecuteReader(); var counts = new Dictionary<(string, string), int>();
            while (reader.Read()) counts[(reader.GetString(0), reader.GetString(1))] = reader.GetInt32(2);
            int Sum(string kind) => counts.Where(x => x.Key.Item1 == kind).Sum(x => x.Value);
            int Mode(string kind, string mode) => counts.GetValueOrDefault((kind, mode));
            return new(Sum("processed"), Mode("processed", "Native"), Mode("processed", "Transcoded"), Sum("imported"), Mode("imported", "Native"), Mode("imported", "Transcoded"), Sum("processingFailure"), Sum("deliveryFailure"), Sum("proxy"));
        }
    }
    private void Execute(string sql, params (string Name, object Value)[] args)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
    public void Dispose() { connection.Dispose(); instanceLock.Dispose(); }
}
