using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class SourceGenerationTests
{
    [Fact]
    public async Task ProbedCacheWaitingSourceReplacementGetsNewGenerationAndFreshInspection()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StabilitySeconds = 3600;
        var old = fixture.Seed("cache-wait.mxf", SourceLocation.Watch, JobStage.Queued);
        old.Inspection = new(true, "old inspection"); old.CacheWaitReason = "Waiting for cache"; fixture.Store.Save(old);
        using var engine = fixture.CreateEngine();
        await File.AppendAllTextAsync(old.SourcePath, " changed after probing");
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().All(j => j.Id != old.Id));
        var replacement = Assert.Single(fixture.Store.Load());
        Assert.NotEqual(old.Id, replacement.Id); Assert.Null(replacement.Inspection); Assert.True(replacement.Managed);
    }
    [Theory]
    [InlineData(SourceLocation.Finished)]
    [InlineData(SourceLocation.Failed)]
    public async Task SamePathReplacementRetiresOldGenerationAndObservesNewSource(SourceLocation location)
    {
        using var fixture = new RecoveryFixture();
        var yesterday = fixture.Seed("daily.mxf", location, location == SourceLocation.Finished ? JobStage.Done : JobStage.Failed);
        yesterday.TerminalFailure = location == SourceLocation.Failed;
        fixture.ReserveOutputs(yesterday); await RecoveryMedia.WriteClipAsync(yesterday.ArtifactPath!, yesterday.Id);
        await File.WriteAllTextAsync(yesterday.ProxyPath!, "proxy:" + yesterday.Id);
        yesterday.ProxySha256 = await FileOperations.HashAsync(yesterday.ProxyPath!, default); fixture.Store.Save(yesterday);
        await File.WriteAllTextAsync(yesterday.SourcePath, "today's different source generation");
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().All(j => j.Id != yesterday.Id));
        var today = Assert.Single(fixture.Store.Load());
        Assert.NotEqual(yesterday.Id, today.Id); Assert.False(today.Managed); Assert.Equal(JobStage.Existing, today.Stage);
        Assert.Equal("today's different source generation", await File.ReadAllTextAsync(today.SourcePath));
        Assert.False(File.Exists(yesterday.ArtifactPath)); Assert.False(File.Exists(yesterday.ProxyPath));
        Assert.Equal(0, fixture.Media.PrepareCalls);
    }

    [Fact]
    public async Task GrowingUnclaimedSourceKeepsArrivalAndResetsItsStability()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StabilitySeconds = 3600;
        var waiting = fixture.Seed("growing.mxf", SourceLocation.Watch, JobStage.Waiting);
        var arrival = waiting.FirstSeenUtc;
        using var engine = fixture.CreateEngine();
        await File.AppendAllTextAsync(waiting.SourcePath, " another producer chunk");
        await engine.ReconcileAsync(default);
        var updated = Assert.Single(fixture.Store.Load());
        Assert.Equal(waiting.Id, updated.Id); Assert.Equal(arrival, updated.FirstSeenUtc);
        Assert.True(updated.SourceSize > waiting.SourceSize); Assert.True(updated.StableSinceUtc > waiting.StableSinceUtc);
        Assert.Equal(0, updated.AbsentScans); Assert.Equal(0, fixture.Media.PrepareCalls);
    }

    [Fact]
    public async Task FailedOldWatchGenerationCannotQuarantineReplacementProducerFile()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StabilitySeconds = 3600;
        var old = fixture.Seed("reused.mxf", SourceLocation.Watch, JobStage.MoveFailed);
        old.TerminalFailure = true; fixture.Store.Save(old);
        await File.WriteAllTextAsync(old.SourcePath, "new producer input with a different length");
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().All(j => j.Id != old.Id));
        var current = Assert.Single(fixture.Store.Load());
        Assert.NotEqual(old.Id, current.Id); Assert.Equal(SourceLocation.Watch, current.Location);
        Assert.False(current.TerminalFailure); Assert.Equal(JobStage.Waiting, current.Stage);
        Assert.Equal("new producer input with a different length", await File.ReadAllTextAsync(current.SourcePath));
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.Folders.Failed));
    }

    [Fact]
    public async Task CommittedMoveTargetRefreshesFingerprintWithoutCreatingDuplicate()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StabilitySeconds = 3600;
        var job = fixture.Seed("moved.mxf", SourceLocation.Watch, JobStage.Archiving);
        job.Wrapped = true; job.StableSinceUtc = DateTimeOffset.UtcNow;
        job.MoveTarget = Path.Combine(fixture.Options.Folders.Finished, job.OriginalName); job.MoveLocation = SourceLocation.Finished;
        File.Move(job.SourcePath, job.MoveTarget);
        File.SetLastWriteTimeUtc(job.MoveTarget, DateTime.UtcNow.AddMinutes(-2)); fixture.Store.Save(job);
        using var engine = fixture.CreateEngine(); await engine.ReconcileAsync(default);
        var restored = Assert.Single(fixture.Store.Load()); var target = new FileInfo(restored.SourcePath);
        Assert.Equal(job.Id, restored.Id); Assert.Equal(SourceLocation.Finished, restored.Location); Assert.True(restored.Archived);
        Assert.Null(restored.MoveTarget); Assert.Equal(target.LastWriteTimeUtc, restored.SourceWriteUtc.UtcDateTime);
        Assert.Equal(target.CreationTimeUtc, restored.SourceCreatedUtc.UtcDateTime); Assert.Equal(0, restored.AbsentScans);
    }

    [Fact]
    public async Task RemovedUnmanagedBootstrapEntryRetiresWhenOutputRootsDoNotExist()
    {
        using var fixture = new RecoveryFixture();
        var existing = fixture.Seed("bootstrap.mxf", SourceLocation.Finished, JobStage.Existing);
        existing.Managed = false; fixture.Store.Save(existing); File.Delete(existing.SourcePath);
        Directory.Delete(fixture.Options.Folders.Proxy); Directory.Delete(fixture.Options.Folders.Work);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Count == 0);
        Assert.Empty(engine.GetSystem().CleanupPending);
        Assert.False(Directory.Exists(fixture.Options.Folders.Proxy));
    }
}
