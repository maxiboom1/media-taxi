using MediaTaxi.Domain;
using MediaTaxi.Storage;
using Microsoft.Data.Sqlite;

namespace MediaTaxi.Tests;

public sealed class StoreTransactionTests
{
    [Fact]
    public void DeliveryCheckpointAndSingleImportSurviveReopen()
    {
        using var temporary = new TemporaryFolder();
        var job = new JobRecord { OriginalName = "news.mxf", Mode = "Native", Stage = JobStage.Delivering, Wrapped = true,
            Destinations = [new() { Id = "TLV1", CopyStatus = CopyState.Succeeded }] };
        using (var store = new JobStore(temporary.Path))
        {
            store.SaveWithEvent(job, "imported");
            job.Stage = JobStage.Archiving;
            store.SaveWithEvent(job, "imported");
        }
        using var reopened = new JobStore(temporary.Path);
        var persisted = Assert.Single(reopened.Load());
        Assert.Equal(job.Id, persisted.Id);
        Assert.Equal(JobStage.Archiving, persisted.Stage);
        Assert.True(persisted.Wrapped);
        Assert.Equal(CopyState.Succeeded, Assert.Single(persisted.Destinations).CopyStatus);
        Assert.Equal(1, reopened.Statistics().Imported);
        Assert.Equal(1, reopened.Statistics().NativeImported);
    }

    [Fact]
    public void CommittedEventsRemainAfterSourceJobRetirementAndReopen()
    {
        using var temporary = new TemporaryFolder();
        var job = new JobRecord { OriginalName = "retired.mxf", Mode = "Transcoded", Stage = JobStage.Done, Wrapped = true, Archived = true, ProxyStatus = "Ready" };
        using (var store = new JobStore(temporary.Path))
        {
            foreach (var kind in new[] { "processed", "imported", "proxy" })
            {
                store.SaveWithEvent(job, kind);
                store.SaveWithEvent(job, kind);
            }
            store.Delete(job.Id);
        }
        using var reopened = new JobStore(temporary.Path);
        Assert.Empty(reopened.Load());
        var statistics = reopened.Statistics();
        Assert.Equal(1, statistics.Processed);
        Assert.Equal(1, statistics.TranscodedProcessed);
        Assert.Equal(1, statistics.Imported);
        Assert.Equal(1, statistics.TranscodedImported);
        Assert.Equal(1, statistics.ProxyCompletions);
    }

    [Fact]
    public void RepeatingSuccessfulEventDoesNotExtendItsOriginalRollingWindow()
    {
        using var temporary = new TemporaryFolder();
        using var store = new JobStore(temporary.Path);
        var now = DateTimeOffset.UtcNow;
        var job = new JobRecord { OriginalName = "old.mxf", Mode = "Native", Stage = JobStage.Delivering };
        store.SaveWithEvent(job, "imported", now.AddHours(-23));
        job.Stage = JobStage.Done;
        store.SaveWithEvent(job, "imported", now);
        Assert.Equal(1, store.Statistics(now).Imported);
        Assert.Equal(0, store.Statistics(now.AddHours(2)).Imported);
        Assert.Equal(JobStage.Done, Assert.Single(store.Load()).Stage);
    }

    [Fact]
    public void EventWriteFailureRollsBackItsStageCheckpoint()
    {
        using var temporary = new TemporaryFolder();
        using var store = new JobStore(temporary.Path);
        var job = new JobRecord { OriginalName = "atomic.mxf", Mode = "Native", Stage = JobStage.Wrapping };
        store.Save(job);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(temporary.Path, "media-taxi.db"), Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_statistic BEFORE INSERT ON statistics BEGIN SELECT RAISE(ABORT, 'simulated event persistence failure'); END;";
            command.ExecuteNonQuery();
        }
        job.Stage = JobStage.Delivering; job.Wrapped = true;
        Assert.Throws<SqliteException>(() => store.SaveWithEvent(job, "processed"));
        var persisted = Assert.Single(store.Load());
        Assert.Equal(JobStage.Wrapping, persisted.Stage);
        Assert.False(persisted.Wrapped);
        Assert.Equal(0, store.Statistics().Processed);
    }
}
