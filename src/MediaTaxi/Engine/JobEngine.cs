using System.Collections.Concurrent;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Inventory;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine : BackgroundService, IApplicationView
{
    private readonly TaxiOptions options;
    private readonly JobStore store;
    private readonly IMediaService media;
    private readonly InventoryService inventory;
    private readonly ILogger<JobEngine> logger;
    private readonly Func<FolderScan> scanSources;
    private readonly Func<string, string, bool, FolderHealth> probeFolder;
    private readonly object gate = new();
    private readonly SemaphoreSlim reconciliation = new(1, 1);
    private readonly Dictionary<string, JobRecord> jobs;
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cancellation)> running = new();
    private readonly ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cancellation)> proxyRunning = new();
    private readonly WorkCache cache;
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private readonly CheckpointWriter checkpoints;
    private StatisticsSnapshot statistics;
    private CacheSnapshot? cacheMetrics;
    private DateTimeOffset? lastSourceScanUtc;
    private DateTimeOffset nextHousekeeping;
    private readonly Diagnostics.RollingFileLogger? fileLog;
    private Action? wakeEngine;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> nameLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim nativeSlots, normalizeSlots, copySlots;
    private readonly Dictionary<string, SemaphoreSlim> destinationSlots;
    private readonly Dictionary<string, int> activeWorkers = new() { ["native"] = 0, ["normalize"] = 0, ["proxy"] = 0, ["copy"] = 0 };
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private TaskCompletionSource signal = NewSignal();
    private long revision;
    private MediaHealth mediaHealth = new(false, "Unknown", "Unknown", [], "Media health check pending.");
    private IReadOnlyList<FolderHealth> folders = [];
    private string? scanError;
    private bool sourceSnapshotComplete;
    private bool processingReady;
    private readonly CancellationTokenSource healthLifetime = new();
    private readonly Dictionary<string, HealthProbe<FolderHealth>> folderProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HealthProbe<MediaHealth> mediaProbe = new();
    private readonly Dictionary<string, (IReadOnlyList<string> Paths, HashSet<string> Index)> inventoryIndexes = new();
    private Task<(FolderScan Scan, DateTimeOffset Completed)>? sourceScanTask;
    private DateTimeOffset sourceScanStarted;
    private int disposed;

    public JobEngine(TaxiOptions options, JobStore store, IMediaService media, InventoryService inventory, ILogger<JobEngine> logger,
        Func<FolderScan>? scanOverride = null, Func<string, string, bool, FolderHealth>? folderProbeOverride = null, Action<IReadOnlyList<JobCheckpoint>>? checkpointOverride = null, Diagnostics.RollingFileLogger? fileLog = null)
    {
        this.options = options; this.store = store; this.media = media; this.inventory = inventory; this.logger = logger;
        scanSources = scanOverride ?? new FolderScanner(options).Scan;
        probeFolder = folderProbeOverride ?? FolderAccess.Check;
        jobs = store.Load().ToDictionary(j => j.Id);
        cache = new(options); this.fileLog = fileLog;
        statistics = store.Statistics();
        checkpoints = new(batch => { (checkpointOverride ?? store.WriteBatch)(batch); Volatile.Write(ref statistics, store.Statistics()); });
        foreach (var job in jobs.Values.Where(j => j.Location == SourceLocation.Watch))
            job.WatchedSource ??= new(job.SourcePath, job.SourceSize, job.SourceWriteUtc, job.SourceCreatedUtc);
        foreach (var job in jobs.Values.Where(j => j.Managed))
        {
            job.ProcessedCheckpoint |= job.Wrapped;
            if (job.ProxyStatus == ProxyState.Working) job.ProxyStatus = ProxyState.Queued;
            if (job.DeliveryCompleted) job.HiresPhase = HiresState.Complete;
        }
        foreach (var job in jobs.Values.Where(j => j.Managed && j.Location == SourceLocation.Watch && !j.TerminalFailure && !j.Wrapped &&
                     j.Stage is JobStage.Probing or JobStage.Transcoding or JobStage.Wrapping))
        { job.Stage = JobStage.Queued; job.Progress = null; store.Save(job); }
        nativeSlots = new(options.Workers.Native); normalizeSlots = new(options.Workers.Normalize); copySlots = new(options.Workers.Copy);
        destinationSlots = options.Destinations.ToDictionary(d => d.Id, _ => new SemaphoreSlim(options.Workers.CopiesPerDestination));
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Revision { get { lock (gate) return revision; } }
    public bool IsReady { get { lock (gate) return processingReady && checkpoints.Error is null; } }
    private void Changed()
    {
        revision++; var previous = signal; signal = NewSignal(); previous.TrySetResult();
    }
    private void Update(JobRecord job, Action action, bool persist = true, string? eventKind = null)
    {
        if (!persist) { lock (gate) { action(); Changed(); } return; }
        Mutate(batch => { action(); batch.Add(JobCheckpoint.Save(job, eventKind)); });
    }
    private void Mutate(Action<List<JobCheckpoint>> action)
    {
        using var lease = checkpoints.Reserve();
        Task durable;
        lock (gate)
        {
            var batch = new List<JobCheckpoint>(); action(batch);
            durable = lease.Submit(batch); Changed();
        }
        durable.GetAwaiter().GetResult();
    }
    private JobRecord Snapshot(JobRecord job)
    {
        lock (gate) return System.Text.Json.JsonSerializer.Deserialize<JobRecord>(
            System.Text.Json.JsonSerializer.Serialize(job, JobStore.JsonOptions), JobStore.JsonOptions)!;
    }
    private void DeleteJob(JobRecord job) => Mutate(batch => { jobs.Remove(job.Id); batch.Add(JobCheckpoint.Delete(job.Id)); });
    public async Task WaitForChangeAsync(long since, CancellationToken cancellationToken)
    {
        Task wait; lock (gate) { if (revision != since) return; wait = signal.Task; }
        await wait.WaitAsync(cancellationToken);
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var watchers = new List<FileSystemWatcher>();
        using var wake = new SemaphoreSlim(0, 1);
        void Wake() { try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
        wakeEngine = Wake;
        inventory.Changed += Wake;
        foreach (var path in new[] { options.Folders.Watch, options.Folders.Finished, options.Folders.Failed })
        {
            try
            {
                var w = new FileSystemWatcher(path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                w.Created += (_, e) => { if (!FolderScanner.Temporary(e.FullPath)) Wake(); };
                w.Changed += (_, e) => { if (!FolderScanner.Temporary(e.FullPath)) Wake(); };
                w.Deleted += (_, e) => { if (!FolderScanner.Temporary(e.FullPath)) Wake(); };
                w.Renamed += (_, e) => { if (!FolderScanner.Temporary(e.FullPath) || !FolderScanner.Temporary(e.OldFullPath)) Wake(); };
                w.Error += (_, _) => Wake();
                w.EnableRaisingEvents = true; watchers.Add(w);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { logger.LogWarning("Watcher unavailable for {Path}: {Error}; scans remain active.", path, ex.Message); }
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await ReconcileAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { lock (gate) { scanError = ex.Message; processingReady = false; Changed(); } logger.LogError(ex, "Reconciliation failed; destructive cleanup paused."); }
                if (await wake.WaitAsync(TimeSpan.FromSeconds(options.Timing.ReconcileSeconds), stoppingToken))
                    await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            inventory.Changed -= Wake;
            await healthLifetime.CancelAsync();
            foreach (var w in watchers) w.Dispose();
            foreach (var item in running.Values) item.Cancellation.Cancel();
            foreach (var item in proxyRunning.Values) item.Cancellation.Cancel();
            try { await Task.WhenAll(running.Values.Concat(proxyRunning.Values).Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await reconciliation.WaitAsync(ct);
        try { await ReconcileCoreAsync(ct); }
        finally { reconciliation.Release(); }
    }
    private async Task ReconcileCoreAsync(CancellationToken ct)
    {
        var h = PollHealth(mediaProbe, () => media.CheckHealthAsync(healthLifetime.Token),
            error => new MediaHealth(false, "Unknown", "Unknown", [], error));
        lock (gate) mediaHealth = h;
        var scan = await ScanBoundedAsync(ct);
        var outputHealth = new[] { ("Proxy", options.Folders.Proxy), ("Work", options.Folders.Work), ("State", options.Folders.State) }
            .Select(p => PollFolder(p.Item1, p.Item2)).Concat(options.Destinations.Select(d => PollFolder("Destination " + d.Name, d.Path))).ToArray();
        var sourceHealth = new[] { ("Watch", options.Folders.Watch), ("Finished", options.Folders.Finished), ("Failed", options.Folders.Failed) }
            .Select(p => PollFolder(p.Item1, p.Item2)).ToArray();
        var snapshots = inventory.GetSnapshots().ToDictionary(s => s.Id);
        foreach (var snapshot in snapshots.Values)
            if (!inventoryIndexes.TryGetValue(snapshot.Id, out var cached) || !ReferenceEquals(cached.Paths, snapshot.Paths))
                inventoryIndexes[snapshot.Id] = (snapshot.Paths, InventoryMatching.BuildIndex(snapshot));
        var indexes = inventoryIndexes.ToDictionary(s => s.Key, s => s.Value.Index);
        Mutate(batch =>
        {
            folders = scan.Health.Select(read =>
            {
                var write = sourceHealth.First(h => h.Id == read.Id);
                return read with { Writable = write.Writable, WriteCheckedUtc = write.WriteCheckedUtc, FreeBytes = write.FreeBytes, Error = read.Error ?? write.Error };
            }).Concat(outputHealth).ToArray(); sourceSnapshotComplete = scan.Complete;
            processingReady = scan.Complete && folders.Take(6).All(h => h.Readable && h.Writable) && mediaHealth.Ready;
            scanError = scan.Complete ? null : "A source folder is inaccessible or its enumeration is pending/failed. Cleanup is paused.";
            if (scan.Complete) Discover(scan.Files, batch);
            ApplyInventory(snapshots, indexes, batch);
        });
        RefreshCacheMetrics();
        if (DateTimeOffset.UtcNow >= nextHousekeeping)
        { store.PruneStatistics(); Volatile.Write(ref statistics, store.Statistics()); nextHousekeeping = DateTimeOffset.UtcNow.AddSeconds(60); }
        if (!scan.Complete) return;
        await RetireAbsentAsync(scan.Files, ct);
        JobRecord[] current; lock (gate) current = jobs.Values.ToArray();
        foreach (var job in current.Where(j => j.Managed && !j.Retiring && !running.ContainsKey(j.Id) &&
                     (j.DeliveryCompleted || j.TerminalFailure) && j.CacheState != CacheLifecycle.Released)) ReleaseDeliveryCache(job);
        if (!IsReady) return;
        List<JobRecord> eligible;
        lock (gate) eligible = jobs.Values.Where(j => !j.Retiring && j.Managed && j.AbsentScans == 0 &&
            (j.Location == SourceLocation.Watch && (j.Stage is JobStage.Waiting or JobStage.Queued or JobStage.MoveFailed || j.Wrapped) || j.Location == SourceLocation.Finished && j.Wrapped && !j.DeliveryCompleted && !j.TerminalFailure) &&
            DateTimeOffset.UtcNow - j.StableSinceUtc >= TimeSpan.FromSeconds(options.Timing.StabilitySeconds)).OrderBy(j => j.FirstSeenUtc).ToList();
        foreach (var job in eligible)
        {
            if (running.ContainsKey(job.Id)) continue;
            if (ReservationRetiring(job)) continue;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!running.TryAdd(job.Id, (completion.Task, cancellation))) { cancellation.Dispose(); continue; }
            _ = RunOwnedAsync(job, completion, cancellation);
        }
        ScheduleProxies(ct);
    }
    private bool ReservationRetiring(JobRecord job)
    {
        var owner = store.Owner(Path.Combine(options.Folders.Proxy, job.ClipName + ".mp4"));
        lock (gate) return owner is not null && owner != job.Id && jobs.TryGetValue(owner, out var old) && (old.Retiring || old.AbsentScans > 0);
    }
    private async Task RunOwnedAsync(JobRecord job, TaskCompletionSource completion, CancellationTokenSource cancellation)
    {
        try { await ProcessAsync(job, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (CheckpointException ex) { logger.LogError(ex, "Processing paused for {Job}: state is unavailable", job.Id); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected job error {Job}", job.Id);
            SetJobError(job, ex);
            Update(job, () => { job.Stage = JobStage.Failed; job.TerminalFailure = true; });
        }
        finally { running.TryRemove(job.Id, out _); cancellation.Dispose(); completion.TrySetResult(); wakeEngine?.Invoke(); }
    }
    private void Discover(IReadOnlyList<SourceFile> files, List<JobCheckpoint> batch)
    {
        foreach (var f in files)
        {
            var existing = jobs.Values.FirstOrDefault(j => !j.Retiring && SameSourceGeneration(j, f));
            if (existing is not null) continue;
            // A producer may still grow/replace an unclaimed waiting source. Once processing
            // begins, a changed fingerprint is a different generation, never the old job's input.
            existing = jobs.Values.FirstOrDefault(j => !j.Retiring && j.Location == SourceLocation.Watch && !j.Wrapped && !j.TerminalFailure && j.Inspection is null &&
                (j.Stage is JobStage.Waiting or JobStage.Queued) && !running.ContainsKey(j.Id) &&
                string.Equals(j.SourcePath, f.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                RefreshSource(existing, f); existing.StableSinceUtc = DateTimeOffset.UtcNow; batch.Add(JobCheckpoint.Save(existing));
                continue;
            }
            // Interrupted archive move: ownership follows its committed target without re-ingestion.
            existing = jobs.Values.FirstOrDefault(j => !j.Retiring && string.Equals(j.MoveTarget, f.Path, StringComparison.OrdinalIgnoreCase) && j.SourceSize == f.Size &&
                !files.Any(source => SameSourceGeneration(j, source)));
            if (existing is not null)
            { RefreshSource(existing, f); existing.Archived = f.Location == SourceLocation.Finished; existing.MoveTarget = null; existing.MoveLocation = null; batch.Add(JobCheckpoint.Save(existing)); continue; }
            // Publication may have committed just before a cross-volume move crashed.
            // Keep its target reserved while the original still exists, so the worker
            // can verify both copies and finish the existing move instead of ingesting it.
            if (jobs.Values.Any(j => !j.Retiring && string.Equals(j.MoveTarget, f.Path, StringComparison.OrdinalIgnoreCase) &&
                files.Any(source => SameSourceGeneration(j, source)))) continue;
            // Our archive move may finish after Watch was enumerated. Its stale
            // path is not another arrival; this never authorizes retirement.
            if (jobs.Values.Any(j => j.Location != SourceLocation.Watch && j.WatchedSource is { } original &&
                string.Equals(original.Path, f.Path, StringComparison.OrdinalIgnoreCase) && original.Size == f.Size &&
                original.WriteUtc == f.WriteUtc && original.CreatedUtc == f.CreatedUtc)) continue;
            var j = new JobRecord { OriginalName = Path.GetFileName(f.Path), ClipName = Path.GetFileNameWithoutExtension(f.Path), SourcePath = f.Path, Location = f.Location, SourceSize = f.Size, SourceWriteUtc = f.WriteUtc, SourceCreatedUtc = f.CreatedUtc,
                Managed = f.Location == SourceLocation.Watch, Stage = f.Location == SourceLocation.Watch ? JobStage.Waiting : JobStage.Existing, ProxyStatus = f.Location == SourceLocation.Watch ? ProxyState.Pending : ProxyState.Untracked,
                Destinations = options.Destinations.Select(d => new DestinationRecord { Id = d.Id }).ToList() };
            if (j.Location == SourceLocation.Watch)
            {
                j.WatchedSource = new(f.Path, f.Size, f.WriteUtc, f.CreatedUtc);
                j.WaitCode = SourceWaitCode.Stability; j.WaitReason = "Waiting for stable source size/mtime"; j.WaitSinceUtc = j.FirstSeenUtc;
            }
            jobs[j.Id] = j; batch.Add(JobCheckpoint.Save(j));
        }
    }
    private static bool SameSourceGeneration(JobRecord job, SourceFile file) =>
        string.Equals(job.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase) && job.SourceSize == file.Size &&
        job.SourceWriteUtc == file.WriteUtc && job.SourceCreatedUtc == file.CreatedUtc;

    private static void RefreshSource(JobRecord job, SourceFile source)
    {
        job.SourcePath = source.Path; job.Location = source.Location; job.SourceSize = source.Size;
        job.SourceWriteUtc = source.WriteUtc; job.SourceCreatedUtc = source.CreatedUtc;
        if (source.Location == SourceLocation.Watch) job.WatchedSource = new(source.Path, source.Size, source.WriteUtc, source.CreatedUtc);
    }

    private void CompleteMove(JobRecord job, string target, SourceLocation location, Action finish)
    {
        var file = new FileInfo(target); file.Refresh();
        if (!file.Exists) throw new IOException("Moved source is no longer available.");
        var source = new SourceFile(file.FullName, location, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc);
        Update(job, () => { RefreshSource(job, source); finish(); });
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        healthLifetime.Cancel(); healthLifetime.Dispose();
        foreach (var item in running.Values.Concat(proxyRunning.Values))
            try { item.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        checkpoints.Dispose(); cache.Dispose();
        base.Dispose();
    }
    private void ApplyInventory(IReadOnlyDictionary<string, InventorySnapshot> snapshots, IReadOnlyDictionary<string, HashSet<string>> indexes, List<JobCheckpoint> batch)
    {
        foreach (var j in jobs.Values.Where(j => !j.Retiring))
        {
            var changed = false;
            foreach (var dest in options.Destinations)
            {
                var d = j.Destinations.FirstOrDefault(d => d.Id == dest.Id);
                if (d is null) { d = new() { Id = dest.Id }; j.Destinations.Add(d); changed = true; }
                var next = Availability.Unknown;
                if (snapshots.TryGetValue(dest.Id, out var s) && s.Ready)
                {
                    if (indexes[dest.Id].Contains(InventoryMatching.Key(dest.InventoryFolder, j.ClipName)))
                    {
                        next = Availability.OK;
                        if (!d.SeenOnline) { d.SeenOnline = true; changed = true; }
                        if (!d.InventoryConfirmed && d.CopyStatus == CopyState.Succeeded &&
                            (d.PublishedUtc is null || s.LastInventoryUtc >= d.PublishedUtc))
                        { d.InventoryConfirmed = true; changed = true; }
                    }
                    else next = d.SeenOnline ? Availability.Removed : Availability.Missing;
                }
                if (next != d.Availability) { d.Availability = next; changed = true; }
            }
            if (changed) batch.Add(JobCheckpoint.Save(j));
        }
    }
}
