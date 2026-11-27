using System.Diagnostics;
using System.Text;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Engine;
using MediaTaxi.Inventory;
using MediaTaxi.Media;
using MediaTaxi.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaTaxi.Tests;

public sealed class EngineRecoveryTests
{
    [Fact]
    public async Task ReleasedCacheResumesOnlyProxyWithoutReencodingOrRepeatingFailedCopy()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("released.mxf", SourceLocation.Finished, JobStage.Proxy);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Archived = true; job.DeliveryCompleted = true;
        job.ExpectedFrameCount = 50; job.CacheState = CacheLifecycle.Released; job.Destinations[0].CopyStatus = CopyState.Failed;
        fixture.Store.Save(job);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Done));
        var done = Assert.Single(fixture.Store.Load());
        Assert.Equal(ProxyState.Ready, done.ProxyStatus); Assert.Equal(CopyState.Failed, done.Destinations[0].CopyStatus);
        Assert.Equal(0, fixture.Media.PrepareCalls); Assert.Equal(0, fixture.Media.ExtractCalls);
        Assert.False(File.Exists(Path.Combine(fixture.Options.Destinations[0].Path, "released.clip")));
        Assert.Equal(job.FirstSeenUtc, done.FirstSeenUtc);
    }
    [Fact]
    public async Task LostCacheRegeneratesOnlyForPendingInitialDeliveryAndDoesNotRecountProcessed()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("lost.mxf", SourceLocation.Finished, JobStage.Delivering);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Archived = true; job.Mode = "Native";
        fixture.Store.SaveWithEvent(job, "processed", DateTimeOffset.UtcNow.AddHours(-25));
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Done));
        var done = Assert.Single(fixture.Store.Load());
        Assert.Equal(1, fixture.Media.PrepareCalls); Assert.Equal(CopyState.Succeeded, done.Destinations[0].CopyStatus);
        Assert.False(File.Exists(done.ArtifactPath)); Assert.Equal(CacheLifecycle.Released, done.CacheState);
        Assert.Equal(0, engine.GetStatistics().Processed); Assert.Equal(1, engine.GetStatistics().Imported);
    }
    [Fact]
    public async Task MissingSourcePausesAnInterruptedJobBeforeRetirementInsteadOfResumingDelivery()
    {
        using var fixture = new RecoveryFixture();
        fixture.Options.Timing.ReconcileSeconds = 60;
        var job = fixture.Seed("removed.mxf", SourceLocation.Watch, JobStage.Delivering);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Mode = "Native";
        await RecoveryMedia.WriteClipAsync(job.ArtifactPath!, job.Id);
        fixture.Store.Save(job); File.Delete(job.SourcePath);
        using var engine = fixture.CreateEngine();
        await engine.ReconcileAsync(default); await Task.Delay(150); await engine.ReconcileAsync(default);
        var paused = Assert.Single(fixture.Store.Load());
        Assert.Equal(1, paused.AbsentScans);
        Assert.Equal(JobStage.Delivering, paused.Stage);
        Assert.All(paused.Destinations, destination => Assert.Equal(CopyState.NotAttempted, destination.CopyStatus));
        Assert.Equal(0, engine.GetStatistics().ProcessingFailures);
    }

    [Fact]
    public async Task RejectedFormatKeepsActualIncomingInspectionSummary()
    {
        using var fixture = new RecoveryFixture(); fixture.Media.Native = false;
        var job = fixture.Seed("rejected-format.mxf", SourceLocation.Watch, JobStage.Waiting);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Failed));
        var rejected = Assert.Single(fixture.Store.Load());
        Assert.Equal("H.264 / 1280x720 / 25 fps", rejected.MediaSummary);
        Assert.Equal(SourceLocation.Failed, rejected.Location); Assert.Equal(job.Id, rejected.Id);
        Assert.Equal(0, fixture.Media.PrepareCalls);
    }
    [Theory]
    [InlineData(JobStage.Probing)]
    [InlineData(JobStage.Transcoding)]
    [InlineData(JobStage.Wrapping)]
    public void RestartQueuesInterruptedUnwrappedStage(JobStage stage)
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("restart.mxf", SourceLocation.Watch, stage);
        job.Progress = 42; fixture.Store.Save(job);
        using var engine = fixture.CreateEngine();
        var restored = Assert.Single(fixture.Store.Load());
        Assert.Equal(JobStage.Queued, restored.Stage);
        Assert.Null(restored.Progress);
    }

    [Fact]
    public async Task RestartClearsOnlyOwnedPreparationAndPublicationPartials()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("restart.mxf", SourceLocation.Watch, JobStage.Wrapping);
        fixture.ReserveOutputs(job);
        Directory.CreateDirectory(job.WorkPath!);
        File.WriteAllText(Path.Combine(job.WorkPath!, "video.h264"), "interrupted extraction");
        var clipPartial = job.ArtifactPath + "." + job.Id + ".partial";
        var proxyPartial = job.ProxyPath + "." + job.Id + ".partial";
        var foreignPartial = job.ArtifactPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        File.WriteAllText(clipPartial, "interrupted wrap"); File.WriteAllText(proxyPartial, "interrupted proxy");
        File.WriteAllText(foreignPartial, "another owner"); fixture.Store.Save(job);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Id == job.Id && j.Stage == JobStage.Done));
        var done = Assert.Single(fixture.Store.Load());
        Assert.Equal(SourceLocation.Finished, done.Location);
        Assert.Equal(1, fixture.Media.PrepareCalls);
        Assert.False(File.Exists(clipPartial)); Assert.False(File.Exists(proxyPartial));
        Assert.Equal("another owner", File.ReadAllText(foreignPartial));
        var archived = new FileInfo(done.SourcePath);
        Assert.Equal(archived.LastWriteTimeUtc, done.SourceWriteUtc.UtcDateTime);
        Assert.Equal(archived.CreationTimeUtc, done.SourceCreatedUtc.UtcDateTime);
    }

    [Fact]
    public async Task ArchivedTranscodedRecoveryKeepsModeAndClearsExtractionWork()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("normalized.mxf", SourceLocation.Finished, JobStage.Proxy);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Archived = true; job.Mode = "Transcoded";
        await RecoveryMedia.WriteClipAsync(job.ArtifactPath!, job.Id);
        job.ArtifactSha256 = await FileOperations.HashAsync(job.ArtifactPath!, default);
        Directory.CreateDirectory(job.WorkPath!); File.WriteAllText(Path.Combine(job.WorkPath!, "video.h264"), "old extraction");
        File.WriteAllText(job.ProxyPath + "." + job.Id + ".partial", "old proxy"); fixture.Store.Save(job);
        fixture.Store.SaveWithEvent(job, "processed");
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Done));
        var done = Assert.Single(fixture.Store.Load());
        Assert.Equal("Transcoded", done.Mode);
        Assert.Equal(0, fixture.Media.ExtractCalls); Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.Equal(1, engine.GetStatistics().TranscodedProcessed);
        Assert.Equal(0, engine.GetStatistics().NativeProcessed);
    }

    [Fact]
    public async Task CompletedWrappedCheckpointDoesNotRecreateExpiredProcessedCounterOnRecovery()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("old-normalized.mxf", SourceLocation.Finished, JobStage.Proxy);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Archived = true; job.Mode = "Transcoded";
        await RecoveryMedia.WriteClipAsync(job.ArtifactPath!, job.Id);
        fixture.Store.SaveWithEvent(job, "processed", DateTimeOffset.UtcNow.AddHours(-25));
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Done));
        Assert.Equal(0, engine.GetStatistics().Processed);
        Assert.Equal(1, engine.GetStatistics().ProxyCompletions);
    }

    [Fact]
    public async Task InterruptedArchiveWithBothCopiesCompletesWithoutBootstrapDuplicate()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("published.mxf", SourceLocation.Watch, JobStage.Archiving);
        fixture.ReserveOutputs(job); job.Wrapped = true; job.Mode = "Native";
        job.Destinations[0].CopyStatus = CopyState.Succeeded;
        job.MoveTarget = Path.Combine(fixture.Options.Folders.Finished, job.OriginalName); job.MoveLocation = SourceLocation.Finished;
        File.Copy(job.SourcePath, job.MoveTarget);
        await RecoveryMedia.WriteClipAsync(job.ArtifactPath!, job.Id);
        fixture.Store.SaveWithEvent(job, "processed");
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Done));
        var restored = Assert.Single(fixture.Store.Load());
        Assert.Equal(job.Id, restored.Id); Assert.Equal(SourceLocation.Finished, restored.Location);
        Assert.False(File.Exists(Path.Combine(fixture.Options.Folders.Watch, job.OriginalName)));
        Assert.Equal("production source", await File.ReadAllTextAsync(restored.SourcePath));
        Assert.Equal(0, fixture.Media.PrepareCalls);
    }

    [Fact]
    public async Task InterruptedFailedMoveCompletesItsPreviouslyPublishedTarget()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("rejected.mxf", SourceLocation.Watch, JobStage.MoveFailed);
        job.TerminalFailure = true; job.MoveLocation = SourceLocation.Failed;
        job.MoveTarget = Path.Combine(fixture.Options.Folders.Failed, job.OriginalName);
        File.Copy(job.SourcePath, job.MoveTarget); fixture.Store.Save(job);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.Failed));
        var restored = Assert.Single(fixture.Store.Load());
        Assert.Equal(job.Id, restored.Id); Assert.Equal(SourceLocation.Failed, restored.Location);
        Assert.Equal(job.MoveTarget, restored.SourcePath); Assert.Null(restored.MoveTarget);
        Assert.False(File.Exists(job.SourcePath));
    }

    [Fact]
    public async Task ArchiveRecoveryRequiresExplicitIntentAndIdenticalContent()
    {
        using var temp = new TemporaryFolder();
        var source = Path.Combine(temp.Path, "source"); var target = Path.Combine(temp.Path, "target");
        await File.WriteAllTextAsync(source, "original"); await File.WriteAllTextAsync(target, "original");
        await Assert.ThrowsAsync<IOException>(() => FileOperations.MoveVerifiedAsync(source, target, Guid.NewGuid().ToString("N"), default));
        await File.WriteAllTextAsync(target, "different");
        await Assert.ThrowsAsync<IOException>(() => FileOperations.MoveVerifiedAsync(source, target, Guid.NewGuid().ToString("N"), default, true));
        Assert.Equal("original", await File.ReadAllTextAsync(source)); Assert.Equal("different", await File.ReadAllTextAsync(target));
        await File.WriteAllTextAsync(target, "original");
        await FileOperations.MoveVerifiedAsync(source, target, Guid.NewGuid().ToString("N"), default, true);
        Assert.False(File.Exists(source)); Assert.Equal("original", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task RetirementRemovesOnlyExactOwnedDestinationPartialAndKeepsFinal()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("orphan.mxf", SourceLocation.Watch, JobStage.Delivering);
        var final = Path.Combine(fixture.Options.Destinations[0].Path, "orphan.clip");
        var partial = final + "." + job.Id + ".partial";
        var foreign = final + "." + Guid.NewGuid().ToString("N") + ".partial";
        job.Destinations[0].TempPath = partial; job.Destinations[0].CopyStatus = CopyState.InProgress;
        File.WriteAllText(final, "completed protected file"); File.WriteAllText(partial, "interrupted transfer"); File.WriteAllText(foreign, "another job");
        fixture.Store.Save(job); File.Delete(job.SourcePath);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Count == 0);
        Assert.False(File.Exists(partial)); Assert.Equal("another job", File.ReadAllText(foreign));
        Assert.Equal("completed protected file", File.ReadAllText(final));
    }

    [Fact]
    public async Task OfflineDestinationPartialCleanupStaysPendingWithoutCopyAttempt()
    {
        using var fixture = new RecoveryFixture();
        var job = fixture.Seed("orphan.mxf", SourceLocation.Watch, JobStage.Delivering);
        var partial = Path.Combine(fixture.Options.Destinations[0].Path, "orphan.clip") + "." + job.Id + ".partial";
        job.Destinations[0].TempPath = partial; job.Destinations[0].CopyStatus = CopyState.InProgress;
        File.WriteAllText(partial, "interrupted transfer"); fixture.Store.Save(job); File.Delete(job.SourcePath);
        FolderHealth Probe(string id, string path, bool write) => new(id, path, !id.StartsWith("Destination", StringComparison.Ordinal), !id.StartsWith("Destination", StringComparison.Ordinal), null, "Offline test share");
        using var engine = fixture.CreateEngine(folderProbe: Probe);
        await fixture.PumpUntilAsync(engine, () => fixture.Store.Load().Any(j => j.Stage == JobStage.CleanupPending));
        Assert.True(File.Exists(partial)); Assert.Single(engine.GetSystem().CleanupPending);
        Assert.Equal(0, fixture.Media.PrepareCalls);
    }

    [Fact]
    public async Task OfflineDestinationProbeDoesNotBlockDiscoveryOrSpawnDuplicates()
    {
        using var fixture = new RecoveryFixture();
        fixture.Options.Timing.StabilitySeconds = 3600;
        fixture.Options.Timing.HealthCheckTimeoutSeconds = 1;
        using var release = new ManualResetEventSlim();
        var calls = 0;
        FolderHealth Probe(string id, string path, bool write)
        {
            if (id.StartsWith("Destination", StringComparison.Ordinal)) { Interlocked.Increment(ref calls); release.Wait(); }
            return new(id, path, true, true, 100, null);
        }
        using var engine = fixture.CreateEngine(folderProbe: Probe);
        File.WriteAllText(Path.Combine(fixture.Options.Folders.Watch, "arrived.mxf"), "growing producer");
        try
        {
            var elapsed = Stopwatch.StartNew();
            await engine.ReconcileAsync(default);
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1.5));
            Assert.Single(engine.GetJobs("active").Jobs);
            await Task.Delay(2100);
            for (var n = 0; n < 3; n++) await engine.ReconcileAsync(default);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Contains(engine.GetSystem().Folders, f => f.Id.StartsWith("Destination", StringComparison.Ordinal) && !f.Readable);
        }
        finally { release.Set(); await Task.Delay(50); }
    }

    [Fact]
    public async Task TimedOutSourceScanPausesCleanupAndKeepsOnlyOneEnumeration()
    {
        using var fixture = new RecoveryFixture();
        fixture.Options.Timing.SourceScanTimeoutSeconds = 2;
        var tracked = fixture.Seed("old.mxf", SourceLocation.Finished, JobStage.Existing);
        tracked.Managed = false; fixture.Store.Save(tracked); File.Delete(tracked.SourcePath);
        using var release = new ManualResetEventSlim();
        var calls = 0;
        FolderScan Scan() { Interlocked.Increment(ref calls); release.Wait(); return new FolderScanner(fixture.Options).Scan(); }
        using var engine = fixture.CreateEngine(scan: Scan);
        try
        {
            var elapsed = Stopwatch.StartNew(); await engine.ReconcileAsync(default);
            Assert.InRange(elapsed.Elapsed.TotalSeconds, 1.5, 4);
            await engine.ReconcileAsync(default);
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.Single(fixture.Store.Load()); Assert.False(engine.IsReady);
            Assert.Contains(engine.GetSystem().Errors, e => e.Contains("Cleanup is paused", StringComparison.OrdinalIgnoreCase));
        }
        finally { release.Set(); await Task.Delay(50); }
    }
}

internal sealed class RecoveryFixture : IDisposable
{
    private readonly TemporaryFolder temporary = new();
    private readonly InventoryService inventory;
    public TaxiOptions Options { get; }
    public JobStore Store { get; }
    public RecoveryMedia Media { get; } = new();
    public RecoveryFixture()
    {
        string Root(string name) { var path = Path.Combine(temporary.Path, name); Directory.CreateDirectory(path); return path; }
        Options = new() { Folders = new() { Watch = Root("watch"), Finished = Root("finished"), Failed = Root("failed"), Proxy = Root("proxy"), Work = Root("work"), State = Root("state") }, Timing = new() { StabilitySeconds = 0, ReconcileSeconds = .02 } };
        Options.Destinations.Add(new() { Id = "tria", Name = "TLV1", Path = Root("tria"), Host = "127.0.0.1" });
        Store = new(Options.Folders.State); inventory = new(Options, NullLogger<InventoryService>.Instance);
    }
    public JobEngine CreateEngine(Func<FolderScan>? scan = null, Func<string, string, bool, FolderHealth>? folderProbe = null, Action<IReadOnlyList<JobCheckpoint>>? checkpoint = null) =>
        new(Options, Store, Media, inventory, NullLogger<JobEngine>.Instance, scan, folderProbe, checkpoint);
    public JobRecord Seed(string name, SourceLocation location, JobStage stage)
    {
        var root = location == SourceLocation.Watch ? Options.Folders.Watch : location == SourceLocation.Finished ? Options.Folders.Finished : Options.Folders.Failed;
        var path = Path.Combine(root, name); File.WriteAllText(path, "production source"); var f = new FileInfo(path);
        var job = new JobRecord { OriginalName = name, ClipName = Path.GetFileNameWithoutExtension(name), SourcePath = path, Location = location, Stage = stage,
            StableSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-1), SourceSize = f.Length, SourceWriteUtc = f.LastWriteTimeUtc, SourceCreatedUtc = f.CreationTimeUtc,
            Destinations = [new() { Id = "tria" }] };
        Store.Save(job); return job;
    }
    public void ReserveOutputs(JobRecord job)
    {
        job.ProxyPath = Path.Combine(Options.Folders.Proxy, job.ClipName + ".mp4");
        using var cache = new WorkCache(Options);
        cache.Prepare(job);
        job.ArtifactPath = Path.Combine(job.WorkPath!, "delivery.clip");
        Assert.True(Store.Claim(job.ArtifactPath, job.Id, "clip")); Assert.True(Store.Claim(job.ProxyPath, job.Id, "proxy"));
    }
    public async Task PumpUntilAsync(JobEngine engine, Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            await engine.ReconcileAsync(default);
            if (condition()) { await Task.Delay(20); return; }
            await Task.Delay(20);
        }
        Assert.Fail("Recovery condition not reached: " + string.Join(";", Store.Load().Select(j => j.Stage + ":" + j.Error)));
    }
    public void Dispose() { inventory.Dispose(); Store.Dispose(); temporary.Dispose(); }
}

internal sealed class RecoveryMedia : IMediaService
{
    public int PrepareCalls;
    public int ExtractCalls => 0;
    public bool Native { get; set; } = true;
    public async Task<WrappedSource> WrapSourceAsync(string source, MediaInspection inspection, string work, string output, string id, Action<double>? progress, CancellationToken ct)
    {
        var prepared = await PrepareAsync(source, work, progress, ct);
        return new(await WrapAsync(prepared, output, id, ct), prepared.FrameCount);
    }
    public Task<ProxyArtifact> CreateSourceProxyAsync(string source, MediaInspection? inspection, long frames, string output, string id, Action<double>? progress, CancellationToken ct) =>
        CreateProxyAsync(new PreparedMedia(source, source, frames, 8, 0, Native, ""), output, id, progress, ct);
    public Task<MediaInspection> InspectAsync(string source, CancellationToken ct) => Task.FromResult(new MediaInspection(Native, Native ? "Native test media" : "H.264 / 1280x720 / 25 fps"));
    public Task<MediaHealth> CheckHealthAsync(CancellationToken ct) => Task.FromResult(new MediaHealth(true, "fake", "fake", ["libx264", "aac"], null));
    public Task<PreparedMedia> PrepareAsync(string source, string work, Action<double>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCalls); AssertFresh(work);
        return Task.FromResult(new PreparedMedia(source, source, 50, 2, 0, true, "Native test media"));
    }
    private static void AssertFresh(string work)
    {
        Assert.False(Directory.Exists(work) && Directory.EnumerateFiles(work).Any(p => Path.GetFileName(p) is "video.h264" or "audio.wav"), "Interrupted preparation was not cleared.");
        Directory.CreateDirectory(work); File.WriteAllText(Path.Combine(work, "video.h264"), "prepared");
    }
    public async Task<ClipArtifact> WrapAsync(PreparedMedia media, string output, string id, CancellationToken ct)
    {
        Assert.False(File.Exists(output + "." + id + ".partial"));
        await WriteClipAsync(output, id); return new(output, await FileOperations.HashAsync(output, ct), id);
    }
    public static Task WriteClipAsync(string path, string id)
    {
        var header = new byte[32768]; Encoding.UTF8.GetBytes($"<AbekasClipMeta1><UUID>{Guid.Parse(id):D}</UUID></AbekasClipMeta1>").CopyTo(header, 0);
        return File.WriteAllBytesAsync(path, header);
    }
    public async Task<ProxyArtifact> CreateProxyAsync(PreparedMedia media, string output, string id, Action<double>? progress, CancellationToken ct)
    {
        Assert.False(File.Exists(output + "." + id + ".partial"));
        await File.WriteAllTextAsync(output, "proxy:" + id, ct); return new(output, await FileOperations.HashAsync(output, ct));
    }
    public async Task<bool> OwnsProxyAsync(string path, string id, CancellationToken ct) => await File.ReadAllTextAsync(path, ct) == "proxy:" + id;
}
