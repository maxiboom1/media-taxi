using System.Diagnostics;
using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;
using MediaTaxi.Engine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MediaTaxi.Tests;

public sealed class CleanupReliabilityTests
{
    [Fact]
    public async Task SharedEngineRegistrationsStopAndDisposeCleanly()
    {
        using var fixture = new RecoveryFixture();
        var engine = fixture.CreateEngine();
        using var host = new HostBuilder().ConfigureServices(services =>
        {
            services.AddSingleton(engine);
            services.AddSingleton<IApplicationView>(p => p.GetRequiredService<JobEngine>());
            services.AddHostedService(p => p.GetRequiredService<JobEngine>());
        }).Build();
        _ = host.Services.GetRequiredService<IApplicationView>();
        await host.StartAsync(); await host.StopAsync();
        host.Dispose(); engine.Dispose();
    }
    [Fact]
    public async Task CheckpointsAreImmutableOrderedBoundedAndAcknowledgedAfterCommit()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<JobCheckpoint>();
        using var writer = new CheckpointWriter(batch => { entered.TrySetResult(); release.Wait(); written.AddRange(batch); }, 1);
        var job = new JobRecord { Stage = JobStage.Probing };
        using var first = writer.Reserve();
        var acknowledged = first.Submit([JobCheckpoint.Save(job)]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        job.Stage = JobStage.Done;
        var blocked = Task.Run(() => writer.Reserve());
        try
        {
            await Task.Delay(50);
            Assert.False(acknowledged.IsCompleted); Assert.False(blocked.IsCompleted);
        }
        finally { release.Set(); }
        await acknowledged;
        using var second = await blocked;
        await second.Submit([JobCheckpoint.Save(job, "processed")]);
        Assert.Equal(2, written.Count);
        Assert.Contains("Probing", written[0].Payload); Assert.Contains("Done", written[1].Payload);
        Assert.Equal("processed", written[1].EventKind);
    }

    [Fact]
    public async Task DelayedCheckpointDoesNotHoldAnyDashboardLockOrStartProcessing()
    {
        using var fixture = new RecoveryFixture(); fixture.Seed("waiting.mxf", SourceLocation.Watch, JobStage.Waiting);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var engine = fixture.CreateEngine(checkpoint: batch => { entered.TrySetResult(); release.Wait(); fixture.Store.WriteBatch(batch); });
        var reconcile = Task.Run(() => engine.ReconcileAsync(default));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var clock = Stopwatch.StartNew();
            var job = Assert.Single(engine.GetJobs("active").Jobs);
            Assert.NotNull(engine.GetJob(job.Id)); engine.GetSystem(); engine.GetStatistics(); _ = engine.Revision;
            Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(500));
            Assert.Equal(0, fixture.Media.PrepareCalls);
            Assert.True(File.Exists(Path.Combine(fixture.Options.Folders.Watch, "waiting.mxf")));
        }
        finally { release.Set(); }
        await reconcile;
    }

    [Fact]
    public async Task CheckpointFailurePausesSideEffectsAndIsVisibleInHealth()
    {
        using var fixture = new RecoveryFixture(); fixture.Seed("paused.mxf", SourceLocation.Watch, JobStage.Waiting);
        using var engine = fixture.CreateEngine(checkpoint: _ => throw new IOException("state disk unavailable"));
        await Assert.ThrowsAsync<CheckpointException>(() => engine.ReconcileAsync(default));
        Assert.False(engine.IsReady); Assert.Contains("state disk unavailable", engine.GetSystem().PersistenceError);
        Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.True(File.Exists(Path.Combine(fixture.Options.Folders.Watch, "paused.mxf")));
    }

    [Fact]
    public async Task SlowButInBudgetScanIsAcceptedAndHealthChecksAreNotRepeatedPerScan()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StabilitySeconds = 3600;
        fixture.Seed("slow.mxf", SourceLocation.Watch, JobStage.Waiting);
        var calls = 0; var probes = 0;
        FolderScan Scan() { Interlocked.Increment(ref calls); Thread.Sleep(2200); return new FolderScanner(fixture.Options).Scan(); }
        FolderHealth Probe(string id, string path, bool write)
        { Interlocked.Increment(ref probes); return new(id, path, true, true, 100, null, DateTimeOffset.UtcNow); }
        using var engine = fixture.CreateEngine(scan: Scan, folderProbe: Probe);
        await engine.ReconcileAsync(default); Assert.False(engine.IsReady);
        await Task.Delay(300);
        await engine.ReconcileAsync(default);
        Assert.True(engine.IsReady); Assert.NotNull(engine.GetSystem().SourceScanUtc);
        Assert.Equal(1, calls); Assert.Equal(7, probes);
    }

    [Fact]
    public async Task LockedSourceShowsStalledReasonWithoutProbeOrQuarantine()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.Timing.StalledSourceWarningSeconds = .01;
        var job = fixture.Seed("locked.mxf", SourceLocation.Watch, JobStage.Waiting);
        using var held = new FileStream(job.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        using var engine = fixture.CreateEngine();
        await fixture.PumpUntilAsync(engine, () => engine.GetJobs("active").Jobs.Single().WaitCode == "SharingViolation");
        await Task.Delay(30);
        var waiting = Assert.Single(engine.GetJobs("active").Jobs);
        Assert.True(waiting.Stalled); Assert.Contains("producer", waiting.WaitReason);
        Assert.NotEmpty(engine.GetSystem().SourceBlockers!); Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.True(File.Exists(job.SourcePath)); Assert.Empty(Directory.EnumerateFiles(fixture.Options.Folders.Failed));
    }

    [Fact]
    public async Task ProgressOnStderrDoesNotHideRealErrorsOrPolluteDiagnostics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new MediaProcessRunner();
        var error = await Assert.ThrowsAsync<MediaProcessException>(() => runner.RunBinaryAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "1..2500 | ForEach-Object { [Console]::Error.WriteLine('frame=' + $_); [Console]::Error.WriteLine('out_time_us=1000'); [Console]::Error.WriteLine('progress=continue') }; [Console]::Error.WriteLine('codec=value failed'); [Console]::Error.WriteLine('frame=real diagnostic'); exit 7"],
            20, (source, ct) => source.CopyToAsync(Stream.Null, ct), default, 1));
        Assert.Equal(7, error.ExitCode); Assert.Contains("codec=value failed", error.Diagnostics);
        Assert.Contains("frame=real diagnostic", error.Diagnostics); Assert.DoesNotContain("frame=2500", error.Diagnostics); Assert.DoesNotContain("progress=", error.Diagnostics);
        Assert.True(error.Message.Length < 1100);
    }

    [Fact]
    public async Task ImmediateInheritedChildExitRetainsItsExitCodeAndDiagnostic()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temporary = new TemporaryFolder(); var input = Path.Combine(temporary.Path, "input"); File.WriteAllText(input, "input");
        for (var n = 0; n < 5; n++)
        {
            var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32/WindowsPowerShell/v1.0/powershell.exe");
            var error = await Assert.ThrowsAsync<MediaProcessException>(() => new MediaProcessRunner().RunSourceAsync(executable,
                ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Error.WriteLine('immediate-error'); exit 23"], input, 10, default, 1, null));
            Assert.Equal(23, error.ExitCode); Assert.Contains("immediate-error", error.Diagnostics);
        }
    }

    [Fact]
    public void UnknownCacheFilesAreRetainedAndKeepTheirReservation()
    {
        using var temporary = new TemporaryFolder();
        var options = new Configuration.TaxiOptions { Folders = new() { Work = temporary.Path, State = Path.Combine(temporary.Path, "state") }, Cache = new() { MinFreeGiB = 0 } };
        using var cache = new WorkCache(options); var job = new JobRecord();
        Assert.True(cache.Reserve(job, [job])); cache.Prepare(job);
        var unknown = Path.Combine(job.WorkPath!, "operator-file.txt"); File.WriteAllText(unknown, "retain");
        Assert.False(cache.Release(job, out var error)); Assert.Contains("Untracked", error);
        Assert.Equal("retain", File.ReadAllText(unknown)); Assert.True(job.CacheReservationBytes > 0);
    }
}
