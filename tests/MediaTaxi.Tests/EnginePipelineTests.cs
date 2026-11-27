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

public sealed class EnginePipelineTests
{
    [Fact]
    public async Task RunningProxyContinuesWhileSixNativeJobsDeliverAndWaitingSourceDefersFurtherProxies()
    {
        await using var fixture = new PipelineFixture(); fixture.Media.BlockProxy = true;
        await fixture.AddWatchAsync("on-air.mxf");
        await fixture.WaitUntilAsync(() => fixture.Media.ProxyCalls == 1);
        for (var n = 0; n < 6; n++) await fixture.AddWatchAsync($"new-{n}.mxf");
        var growing = Path.Combine(fixture.Options.Folders.Watch, "growing.mxf");
        await using var producer = new FileStream(growing, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await producer.WriteAsync(new byte[] { 1, 2, 3 }); await producer.FlushAsync();
        await fixture.WaitUntilAsync(() => fixture.AllJobs().Count(j => j.Destinations.All(d => d.CopyStatus == "Succeeded")) == 7);
        Assert.Equal(7, fixture.Media.WrapCalls); Assert.Equal(1, fixture.Media.ProxyCalls);
        Assert.False(fixture.Media.ProxyCancelled);
        Assert.All(fixture.Store.Load().Where(j => j.DeliveryCompleted), j => Assert.False(File.Exists(j.ArtifactPath)));
        fixture.Media.ReleaseProxies.TrySetResult();
        await fixture.WaitUntilAsync(() => fixture.AllJobs().Any(j => j.ClipName == "on-air" && j.ProxyStatus == "Ready"));
        for (var n = 0; n < 3; n++) await fixture.ScanAsync();
        Assert.Equal(1, fixture.Media.ProxyCalls);
        Assert.All(fixture.AllJobs().Where(j => j.ClipName.StartsWith("new-")), j => Assert.Contains("priority", j.ProxyDeferralReason));
        await producer.DisposeAsync(); File.Delete(growing);
        await fixture.WaitUntilAsync(() => fixture.AllJobs().Count(j => j.Stage == "Done") == 7);
    }
    [Fact]
    public async Task SourceMovedDuringFolderScanDoesNotCreatePhantomArrival()
    {
        await using var fixture = new PipelineFixture(); await fixture.AddWatchAsync("moving.mxf");
        await fixture.WaitForAsync(j => j.Stage == "Done");
        var scan = new FolderScanner(fixture.Options).Scan();
        var archived = Assert.Single(scan.Files);
        fixture.ScanSnapshotOverride = scan with { Files = scan.Files.Append(archived with { Path = Path.Combine(fixture.Options.Folders.Watch, "moving.mxf"), Location = SourceLocation.Watch }).ToArray() };
        await fixture.ScanAsync();
        Assert.Single(fixture.AllJobs());
        Assert.Equal(1, fixture.Media.WrapCalls);
    }

    [Fact]
    public async Task TenNativeClipsFinishAllDeliveriesWhileIndependentProxyQueueIsBlocked()
    {
        await using var fixture = new PipelineFixture();
        fixture.Options.Workers.Native = 1;
        await fixture.RestartAsync();
        fixture.Media.BlockProxy = true;
        for (var n = 0; n < 10; n++) await fixture.AddWatchAsync($"independent-{n:D2}.mxf");
        await fixture.WaitUntilAsync(() => fixture.AllJobs().Length == 10 && fixture.AllJobs().All(j => j.Destinations.All(d => d.CopyStatus == "Succeeded")));
        Assert.Equal(10, fixture.Media.WrapCalls);
        Assert.Equal(1, fixture.Media.ProxyCalls);
        Assert.Equal(10, Directory.EnumerateFiles(fixture.Options.Folders.Finished).Count());
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.Folders.Watch));
        Assert.Equal(10, fixture.Engine.GetStatistics().Imported);
        Assert.Equal(0, fixture.Engine.GetStatistics().ProxyCompletions);
        var jobs = fixture.AllJobs();
        Assert.All(jobs, j => Assert.Equal("Released", j.CacheState));
        Assert.Single(jobs, j => j.ProxyStatus == "Working");
        Assert.Equal(9, jobs.Count(j => j.ProxyStatus == "Queued"));
        foreach (var job in jobs)
        foreach (var destination in fixture.Options.Destinations)
            Assert.True(job.Timings!["archive"].FinishedUtc <= job.Timings["delivery:" + destination.Id].StartedUtc);
        foreach (var destination in fixture.Options.Destinations)
        {
            var copies = jobs.Select(j => j.Timings!["delivery:" + destination.Id]).OrderBy(t => t.StartedUtc).ToArray();
            for (var n = 1; n < copies.Length; n++) Assert.True(copies[n - 1].FinishedUtc <= copies[n].StartedUtc);
        }
        fixture.Media.ReleaseProxies.TrySetResult();
        await fixture.WaitUntilAsync(() => fixture.AllJobs().All(j => j.Stage == "Done"));
        Assert.Equal(10, fixture.Engine.GetStatistics().ProxyCompletions);
    }

    [Fact]
    public async Task FailedProxyDoesNotAbortDeliveriesOrHoldTheNextNativeWorker()
    {
        await using var fixture = new PipelineFixture(); fixture.Media.FailProxy = true;
        for (var n = 0; n < 3; n++) await fixture.AddWatchAsync($"bad-proxy-{n}.mxf");
        await fixture.WaitUntilAsync(() => fixture.AllJobs().Length == 3 && fixture.AllJobs().All(j => j.Stage == "Done"));
        Assert.All(fixture.AllJobs(), j =>
        {
            Assert.Equal("Failed", j.ProxyStatus);
            Assert.Equal("Finished", j.Location);
            Assert.All(j.Destinations, d => Assert.Equal("Succeeded", d.CopyStatus));
            Assert.Contains("Proxy failed", j.Error);
            Assert.False(j.Timings!["proxy"].Succeeded);
        });
        Assert.Equal(3, fixture.Engine.GetStatistics().Imported);
        Assert.Equal(3, fixture.Engine.GetStatistics().ProcessingFailures);
        await fixture.RestartAsync();
        for (var n = 0; n < 4; n++) await fixture.ScanAsync();
        Assert.Equal(3, fixture.Media.ProxyCalls);
        Assert.Equal(3, fixture.Media.PrepareCalls);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(2, 2)]
    [InlineData(4, 0)]
    public async Task EveryDeliveryOutcomeStillArchivesAndCreatesProxy(int failedDestinations, int successfulDestinations)
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("news.mxf");
        foreach (var destination in fixture.Options.Destinations.Take(failedDestinations))
            await File.WriteAllTextAsync(Path.Combine(destination.Path, "news.clip"), "protected opener");

        var job = await fixture.WaitForAsync(j => j.OriginalName == "news.mxf" && j.Stage == "Done");
        Assert.Equal("Finished", job.Location);
        Assert.Equal("Ready", job.ProxyStatus);
        Assert.Equal(successfulDestinations, job.Destinations.Count(d => d.CopyStatus == "Succeeded"));
        Assert.Equal(failedDestinations, job.Destinations.Count(d => d.CopyStatus == "Failed"));
        Assert.All(job.Destinations, d => Assert.Equal("Unknown", d.Availability));
        Assert.False(File.Exists(Path.Combine(fixture.Options.Folders.Watch, "news.mxf")));
        Assert.True(File.Exists(Path.Combine(fixture.Options.Folders.Finished, "news.mxf")));
        Assert.False(File.Exists(fixture.Clip("news")));
        Assert.Equal("Released", job.CacheState);
        Assert.True(File.Exists(fixture.Proxy("news")));
        foreach (var destination in fixture.Options.Destinations.Take(failedDestinations))
            Assert.Equal("protected opener", await File.ReadAllTextAsync(Path.Combine(destination.Path, "news.clip")));
        var statistics = fixture.Engine.GetStatistics();
        Assert.Equal(1, statistics.Processed);
        Assert.Equal(1, statistics.NativeProcessed);
        Assert.Equal(successfulDestinations == 0 ? 0 : 1, statistics.Imported);
        Assert.Equal(failedDestinations == 4 ? 1 : 0, statistics.DeliveryFailures);
        Assert.Equal(1, statistics.ProxyCompletions);
        Assert.Equal(1, fixture.Media.PrepareCalls);
        Assert.Equal(1, fixture.Media.WrapCalls);
        Assert.Equal(1, fixture.Media.ProxyCalls);
    }

    [Fact]
    public async Task FormatMismatchMovesOriginalToFailedWithoutWrappingOrProxy()
    {
        await using var fixture = new PipelineFixture();
        fixture.Media.Native = false;
        await fixture.AddWatchAsync("unsupported.mxf");
        var job = await fixture.WaitForAsync(j => j.Stage == "Failed");
        Assert.Equal("Failed", job.Location);
        Assert.Contains("Format mismatch", job.Error);
        Assert.True(File.Exists(Path.Combine(fixture.Options.Folders.Failed, "unsupported.mxf")));
        Assert.False(File.Exists(fixture.Clip("unsupported")));
        Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.Equal(0, fixture.Media.ProxyCalls);
    }

    [Fact]
    public async Task FinishedNameConflictQuarantinesIncomingAndPreservesArchivedSource()
    {
        await using var fixture = new PipelineFixture();
        var archived = Path.Combine(fixture.Options.Folders.Finished, "news.mxf");
        await File.WriteAllTextAsync(archived, "yesterday source");
        await fixture.AddWatchAsync("news.mxf");
        var job = await fixture.WaitForAsync(j => j.Stage == "Failed");
        Assert.Contains("Name conflict", job.Error);
        Assert.Equal("yesterday source", await File.ReadAllTextAsync(archived));
        Assert.True(File.Exists(Path.Combine(fixture.Options.Folders.Failed, "news.mxf")));
        Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.Equal(2, fixture.AllJobs().Length);
    }

    [Fact]
    public async Task DuplicateScansDoNotCreateAnotherJobOrRepeatConversion()
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("once.mxf");
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => fixture.ScanAsync()));
        var job = await fixture.WaitForAsync(j => j.Stage == "Done");
        for (var index = 0; index < 8; index++) await fixture.ScanAsync();
        Assert.Equal(job.Id, Assert.Single(fixture.AllJobs()).Id);
        Assert.Equal(1, fixture.Media.PrepareCalls);
        Assert.Equal(1, fixture.Media.ProxyCalls);
        Assert.Equal(1, fixture.Engine.GetStatistics().Imported);
    }

    [Fact]
    public async Task StartupFinishedAndFailedAreObservationOnlyAndDoNotInventStatistics()
    {
        await using var fixture = new PipelineFixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.Folders.Finished, "existing.mxf"), "production");
        await File.WriteAllTextAsync(Path.Combine(fixture.Options.Folders.Failed, "rejected.mxf"), "unsupported");
        await fixture.ScanAsync();
        await Task.Delay(150);
        await fixture.ScanAsync();
        Assert.Empty(fixture.Engine.GetJobs("active").Jobs);
        var observed = fixture.Engine.GetJobs("older").Jobs;
        Assert.Equal(2, observed.Count);
        Assert.All(observed, j => { Assert.Equal("Existing", j.Stage); Assert.Equal("Untracked", j.ProxyStatus); Assert.All(j.Destinations, d => Assert.Equal("NotAttempted", d.CopyStatus)); });
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.Folders.Work, "*.clip", SearchOption.AllDirectories));
        Assert.All(fixture.Options.Destinations, d => Assert.Empty(Directory.EnumerateFiles(d.Path)));
        Assert.Equal(0, fixture.Media.PrepareCalls);
        Assert.Equal(0, fixture.Engine.GetStatistics().Processed);
        Assert.Equal(0, fixture.Engine.GetStatistics().Imported);
    }

    [Fact]
    public async Task ActiveIncludesOldUnresolvedWatchAndOlderTracksFinishedAndFailedUntilRemoval()
    {
        await using var fixture = new PipelineFixture();
        fixture.Options.Timing.StabilitySeconds = 60;
        fixture.Seed("old-watch.mxf", SourceLocation.Watch, JobStage.Probing, DateTimeOffset.UtcNow.AddHours(-2));
        fixture.Seed("old-finished.mxf", SourceLocation.Finished, JobStage.Done, DateTimeOffset.UtcNow.AddHours(-2));
        var failed = fixture.Seed("old-failed.mxf", SourceLocation.Failed, JobStage.Failed, DateTimeOffset.UtcNow.AddHours(-3));
        fixture.Seed("recent-failed.mxf", SourceLocation.Failed, JobStage.Failed, DateTimeOffset.UtcNow.AddMinutes(-10));
        await fixture.RestartAsync();
        await fixture.ScanAsync();
        Assert.Equal(new[] { "recent-failed.mxf", "old-watch.mxf" }, fixture.Engine.GetJobs("active").Jobs.Select(j => j.OriginalName));
        Assert.Equal(new[] { "old-finished.mxf", "old-failed.mxf" }, fixture.Engine.GetJobs("older").Jobs.Select(j => j.OriginalName));
        File.Delete(failed.SourcePath);
        await fixture.ScanAsync();
        Assert.Contains(fixture.Engine.GetJobs("older").Jobs, j => j.Id == failed.Id);
        await Task.Delay(120);
        await fixture.ScanAsync();
        Assert.DoesNotContain(fixture.AllJobs(), j => j.Id == failed.Id);
    }

    [Fact]
    public async Task TwoSeparatedCompleteScansRetireLocalArtifactsAndLeaveServerFilesAndCounters()
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("nightly.mxf");
        var job = await fixture.WaitForAsync(j => j.Stage == "Done");
        File.Delete(Path.Combine(fixture.Options.Folders.Finished, "nightly.mxf"));
        await fixture.ScanAsync();
        Assert.False(File.Exists(fixture.Clip("nightly")));
        Assert.True(File.Exists(fixture.Proxy("nightly")));
        Assert.Contains(fixture.AllJobs(), j => j.Id == job.Id);
        await fixture.ScanAsync();
        Assert.False(File.Exists(fixture.Clip("nightly")));
        await Task.Delay(120);
        await fixture.ScanAsync();
        Assert.Empty(fixture.AllJobs());
        Assert.False(File.Exists(fixture.Clip("nightly")));
        Assert.False(File.Exists(fixture.Proxy("nightly")));
        Assert.All(fixture.Options.Destinations, d => Assert.True(File.Exists(Path.Combine(d.Path, "nightly.clip"))));
        Assert.Equal(1, fixture.Engine.GetStatistics().Imported);
    }

    [Fact]
    public async Task InaccessibleSourceRootPausesCleanupRatherThanTreatingItAsEmpty()
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("outage.mxf");
        var job = await fixture.WaitForAsync(j => j.Stage == "Done");
        var finished = fixture.Options.Folders.Finished;
        Directory.Move(finished, finished + "-offline");
        await fixture.ScanAsync();
        await Task.Delay(120);
        await fixture.ScanAsync();
        Assert.False(fixture.Engine.IsReady);
        Assert.Contains(fixture.AllJobs(), j => j.Id == job.Id);
        Assert.False(File.Exists(fixture.Clip("outage")));
        Assert.True(File.Exists(fixture.Proxy("outage")));
        Assert.Contains(fixture.Engine.GetSystem().Errors, e => e.Contains("Cleanup is paused", StringComparison.OrdinalIgnoreCase));
        Directory.Move(finished + "-offline", finished);
        await fixture.ScanAsync();
        Assert.True(fixture.Engine.IsReady);
    }

    [Fact]
    public async Task RestartDoesNotRetryFinishedFailedDestinationOrRepeatSuccessfulStages()
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("retry.mxf");
        var blocked = Path.Combine(fixture.Options.Destinations[0].Path, "retry.clip");
        await File.WriteAllTextAsync(blocked, "protected");
        var original = await fixture.WaitForAsync(j => j.Stage == "Done");
        File.Delete(blocked);
        await fixture.RestartAsync();
        await fixture.ScanAsync();
        await Task.Delay(150);
        await fixture.ScanAsync();
        var restored = Assert.Single(fixture.AllJobs());
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal("Failed", restored.Destinations[0].CopyStatus);
        Assert.False(File.Exists(blocked));
        Assert.Equal(1, fixture.Media.PrepareCalls);
        Assert.Equal(1, fixture.Media.ProxyCalls);
        Assert.Equal(1, fixture.Engine.GetStatistics().Imported);
    }

    [Fact]
    public async Task GrowingSourceHeldOpenIsNotProcessedUntilProducerClosesIt()
    {
        await using var fixture = new PipelineFixture();
        var path = Path.Combine(fixture.Options.Folders.Watch, "growing.mxf");
        await using (var producer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            await producer.WriteAsync(Encoding.UTF8.GetBytes("first chunk")); await producer.FlushAsync();
            await fixture.ScanAsync(); await Task.Delay(120); await fixture.ScanAsync();
            Assert.Equal(0, fixture.Media.PrepareCalls);
            Assert.Equal("Watch", Assert.Single(fixture.AllJobs()).Location);
            await producer.WriteAsync(Encoding.UTF8.GetBytes(" second chunk")); await producer.FlushAsync();
            await fixture.ScanAsync();
            Assert.Equal(0, fixture.Media.PrepareCalls);
        }
        await fixture.WaitForAsync(j => j.Stage == "Done");
        Assert.Equal("first chunk second chunk", await File.ReadAllTextAsync(Path.Combine(fixture.Options.Folders.Finished, "growing.mxf")));
        Assert.Equal(1, fixture.Media.PrepareCalls);
    }

    [Fact]
    public async Task LockedCleanupWaitsAndDoesNotDeleteNextFilenameGeneration()
    {
        await using var fixture = new PipelineFixture();
        await fixture.AddWatchAsync("daily.mxf");
        var yesterday = await fixture.WaitForAsync(j => j.Stage == "Done");
        File.Delete(Path.Combine(fixture.Options.Folders.Finished, "daily.mxf"));
        using (var locked = new FileStream(fixture.Proxy("daily"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await fixture.ScanAsync(); await Task.Delay(120); await fixture.ScanAsync();
            Assert.Single(fixture.Engine.GetSystem().CleanupPending);
            Assert.True(File.Exists(fixture.Proxy("daily")));
            await fixture.AddWatchAsync("daily.mxf");
            await fixture.ScanAsync(); await Task.Delay(120); await fixture.ScanAsync();
            Assert.True(await fixture.Media.OwnsProxyAsync(fixture.Proxy("daily"), yesterday.Id, default));
            Assert.Equal(1, fixture.Media.WrapCalls);
        }
        var today = await fixture.WaitForAsync(j => j.Id != yesterday.Id && j.Stage == "Done");
        Assert.False(File.Exists(fixture.Clip("daily")));
        Assert.True(await fixture.Media.OwnsProxyAsync(fixture.Proxy("daily"), today.Id, default));
        for (var scan = 0; scan < 3; scan++) await fixture.ScanAsync();
        Assert.False(File.Exists(fixture.Clip("daily")));
        Assert.Empty(fixture.Engine.GetSystem().CleanupPending);
        Assert.All(fixture.Options.Destinations, d => Assert.True(FileOperations.ClipOwned(Path.Combine(d.Path, "daily.clip"), yesterday.Id)));
    }

    [Fact]
    public async Task NightlyRemovalCancelsActiveProxyBeforeLocalArtifactsAreCleaned()
    {
        await using var fixture = new PipelineFixture();
        fixture.Media.BlockProxy = true;
        await fixture.AddWatchAsync("cancel.mxf");
        await fixture.WaitForAsync(j => j.Stage == "Proxy");
        await fixture.Media.ProxyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        File.Delete(Path.Combine(fixture.Options.Folders.Finished, "cancel.mxf"));
        await fixture.ScanAsync(); await Task.Delay(120); await fixture.ScanAsync();
        await fixture.WaitUntilAsync(() => fixture.Store.Load().Count == 0);
        Assert.True(fixture.Media.ProxyCancelled);
        Assert.False(File.Exists(fixture.Clip("cancel")));
        Assert.False(File.Exists(fixture.Proxy("cancel")));
        Assert.All(fixture.Options.Destinations, d => Assert.True(File.Exists(Path.Combine(d.Path, "cancel.clip"))));
    }

    [Theory]
    [InlineData(50, 50)]
    [InlineData(500, 25)]
    public async Task BurstAndSimulatedDayKeepOneJobAndImportCountPerClip(int clipCount, int batchSize)
    {
        await using var fixture = new PipelineFixture();
        for (var start = 0; start < clipCount; start += batchSize)
        {
            var end = Math.Min(clipCount, start + batchSize);
            await Task.WhenAll(Enumerable.Range(start, end - start).Select(n => fixture.AddWatchAsync($"day-{n:D4}.mxf")));
            await fixture.WaitUntilAsync(() => fixture.AllJobs().Count(j => j.Stage == "Done") == end);
        }
        Assert.Equal(clipCount, fixture.AllJobs().Length);
        Assert.Equal(clipCount, fixture.Media.PrepareCalls);
        Assert.Equal(clipCount, fixture.Media.ProxyCalls);
        Assert.Empty(Directory.EnumerateFiles(fixture.Options.Folders.Watch));
        Assert.Equal(clipCount, Directory.EnumerateFiles(fixture.Options.Folders.Finished).Count());
        Assert.All(fixture.Options.Destinations, d => Assert.Equal(clipCount, Directory.EnumerateFiles(d.Path).Count()));
        var statistics = fixture.Engine.GetStatistics();
        Assert.Equal(clipCount, statistics.Processed);
        Assert.Equal(clipCount, statistics.Imported);
        Assert.Equal(clipCount, statistics.ProxyCompletions);
        Assert.Equal(0, statistics.ProcessingFailures);
    }

    [Fact]
    public async Task SystemStageCountsIncludeCurrentManagedJobsOnly()
    {
        await using var fixture = new PipelineFixture();
        fixture.Seed("first.mxf", SourceLocation.Watch, JobStage.Probing, DateTimeOffset.UtcNow);
        fixture.Seed("second.mxf", SourceLocation.Watch, JobStage.Probing, DateTimeOffset.UtcNow);
        fixture.Seed("proxy.mxf", SourceLocation.Finished, JobStage.Proxy, DateTimeOffset.UtcNow);
        fixture.Seed("done.mxf", SourceLocation.Finished, JobStage.Done, DateTimeOffset.UtcNow);
        fixture.Seed("failed.mxf", SourceLocation.Failed, JobStage.Failed, DateTimeOffset.UtcNow);
        var observed = fixture.Seed("observed.mxf", SourceLocation.Finished, JobStage.Probing, DateTimeOffset.UtcNow);
        observed.Managed = false; fixture.Store.Save(observed);
        var retired = fixture.Seed("retiring.mxf", SourceLocation.Finished, JobStage.Wrapping, DateTimeOffset.UtcNow);
        retired.Retiring = true; fixture.Store.Save(retired);
        await fixture.RestartAsync();
        var stages = fixture.Engine.GetSystem().Stages!;
        Assert.Equal(2, stages.Count);
        Assert.Equal(2, stages["Queued"]);
        Assert.Equal(1, stages["Proxy"]);
    }

    private sealed class PipelineFixture : IAsyncDisposable
    {
        private readonly TemporaryFolder temporary = new();
        private readonly CancellationTokenSource cancellation = new();
        public TaxiOptions Options { get; }
        public FakeMedia Media { get; } = new();
        public JobStore Store { get; private set; }
        public JobEngine Engine { get; private set; }
        public FolderScan? ScanSnapshotOverride { get; set; }
        private InventoryService inventory;

        public PipelineFixture()
        {
            string Root(string name) { var path = Path.Combine(temporary.Path, name); Directory.CreateDirectory(path); return path; }
            Options = new() { Folders = new() { Watch = Root("watch"), Finished = Root("finished"), Failed = Root("failed"), Proxy = Root("proxy"), Work = Root("work"), State = Root("state") }, Timing = new() { StabilitySeconds = .1, ReconcileSeconds = .1 } };
            Options.Destinations = Enumerable.Range(1, 4).Select(n => new DestinationOptions { Id = "tria" + n, Name = "TLV" + n, Path = Root("tria" + n), Host = "127.0.0.1" }).ToList();
            Store = new(Options.Folders.State);
            inventory = new(Options, NullLogger<InventoryService>.Instance);
            Engine = new(Options, Store, Media, inventory, NullLogger<JobEngine>.Instance, scanOverride: () => ScanSnapshotOverride ?? new FolderScanner(Options).Scan());
        }
        public string Clip(string name) => Store.Load().FirstOrDefault(j => j.ClipName == name)?.ArtifactPath ?? Path.Combine(Options.Folders.Work, name + ".clip");
        public string Proxy(string name) => Path.Combine(Options.Folders.Proxy, name + ".mp4");
        public Task AddWatchAsync(string name) => File.WriteAllTextAsync(Path.Combine(Options.Folders.Watch, name), "production media");
        public Task ScanAsync() => Engine.ReconcileAsync(cancellation.Token);
        public JobView[] AllJobs() => Engine.GetJobs("active").Jobs.Concat(Engine.GetJobs("older").Jobs).ToArray();
        public async Task<JobView> WaitForAsync(Func<JobView, bool> predicate)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(8))
            {
                await ScanAsync();
                var match = AllJobs().FirstOrDefault(predicate);
                if (match is not null) { await Task.Delay(25); return match; }
                await Task.Delay(40);
            }
            Assert.Fail("Pipeline did not reach expected stage: " + string.Join("; ", AllJobs().Select(j => $"{j.OriginalName} {j.Stage} {j.Location}: {j.Error}")));
            throw new InvalidOperationException();
        }
        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.Elapsed < TimeSpan.FromSeconds(45))
            {
                await ScanAsync();
                if (predicate()) { await Task.Delay(25); return; }
                await Task.Delay(50);
            }
            Assert.Fail("Pipeline condition timed out: " + string.Join("; ", AllJobs().Where(j => j.Stage != "Done").Select(j => $"{j.OriginalName} {j.Stage}: {j.Error}")));
        }
        public JobRecord Seed(string name, SourceLocation location, JobStage stage, DateTimeOffset arrived)
        {
            var root = location == SourceLocation.Watch ? Options.Folders.Watch : location == SourceLocation.Finished ? Options.Folders.Finished : Options.Folders.Failed;
            var path = Path.Combine(root, name); File.WriteAllText(path, "existing source"); var file = new FileInfo(path);
            var job = new JobRecord { OriginalName = name, ClipName = Path.GetFileNameWithoutExtension(name), SourcePath = path, Location = location, Stage = stage, FirstSeenUtc = arrived, SourceSize = file.Length, SourceWriteUtc = file.LastWriteTimeUtc, SourceCreatedUtc = file.CreationTimeUtc };
            Store.Save(job); return job;
        }
        public async Task RestartAsync()
        {
            await Task.Delay(40);
            Engine.Dispose(); inventory.Dispose(); Store.Dispose();
            Store = new(Options.Folders.State);
            inventory = new(Options, NullLogger<InventoryService>.Instance);
            Engine = new(Options, Store, Media, inventory, NullLogger<JobEngine>.Instance);
        }
        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            await Task.Delay(50);
            Engine.Dispose(); inventory.Dispose(); Store.Dispose(); cancellation.Dispose(); temporary.Dispose();
        }
    }

    private sealed class FakeMedia : IMediaService
    {
        public bool Native { get; set; } = true;
        public bool BlockProxy { get; set; }
        public bool FailProxy { get; set; }
        public TaskCompletionSource ReleaseProxies { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ProxyCancelled { get; private set; }
        public TaskCompletionSource ProxyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PrepareCalls, WrapCalls, ProxyCalls;
        public Task<MediaInspection> InspectAsync(string source, CancellationToken cancellationToken) => Task.FromResult(new MediaInspection(Native, "AVC-Intra 100 / 1080p50 / PCM stereo"));
        public async Task<WrappedSource> WrapSourceAsync(string source, MediaInspection inspection, string work, string output, string id, Action<double>? progress, CancellationToken ct)
        {
            var prepared = await PrepareAsync(source, work, progress, ct);
            return new(await WrapAsync(prepared, output, id, ct), prepared.FrameCount);
        }
        public Task<ProxyArtifact> CreateSourceProxyAsync(string source, MediaInspection? inspection, long frames, string output, string id, Action<double>? progress, CancellationToken ct) =>
            CreateProxyAsync(new PreparedMedia(source, source, frames, 8, 0, Native, ""), output, id, progress, ct);
        public Task<MediaHealth> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new MediaHealth(true, "fake ffmpeg", "fake ffprobe", ["libx264", "aac"], null));
        public Task<PreparedMedia> PrepareAsync(string source, string workDirectory, Action<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref PrepareCalls); progress?.Invoke(50);
            return Task.FromResult(new PreparedMedia(source, source, 50, 2, 0, Native, "AVC-Intra 100 / 1080p50 / PCM stereo"));
        }
        public async Task<ClipArtifact> WrapAsync(PreparedMedia media, string outputPath, string jobId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref WrapCalls);
            var bytes = new byte[32768]; Encoding.UTF8.GetBytes($"<AbekasClipMeta1><UUID>{Guid.Parse(jobId):D}</UUID></AbekasClipMeta1>").CopyTo(bytes, 0);
            await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
            return new(outputPath, await FileOperations.HashAsync(outputPath, cancellationToken), jobId);
        }
        public async Task<ProxyArtifact> CreateProxyAsync(PreparedMedia media, string outputPath, string jobId, Action<double>? progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ProxyCalls); progress?.Invoke(50);
            ProxyStarted.TrySetResult();
            if (BlockProxy)
            {
                try { await ReleaseProxies.Task.WaitAsync(cancellationToken); }
                catch (OperationCanceledException) { ProxyCancelled = true; throw; }
            }
            if (FailProxy) throw new IOException("Test proxy encoder failed");
            await File.WriteAllTextAsync(outputPath, "proxy:" + jobId, cancellationToken);
            return new(outputPath, await FileOperations.HashAsync(outputPath, cancellationToken));
        }
        public async Task<bool> OwnsProxyAsync(string proxy, string jobId, CancellationToken cancellationToken) => await File.ReadAllTextAsync(proxy, cancellationToken) == "proxy:" + jobId;
    }
}
