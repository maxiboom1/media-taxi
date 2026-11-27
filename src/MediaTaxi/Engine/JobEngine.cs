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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> nameLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim nativeSlots, normalizeSlots, proxySlots, copySlots;
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
    private Task<FolderScan>? sourceScanTask;
    private DateTimeOffset sourceScanStarted;

    public JobEngine(TaxiOptions options, JobStore store, IMediaService media, InventoryService inventory, ILogger<JobEngine> logger,
        Func<FolderScan>? scanOverride = null, Func<string, string, bool, FolderHealth>? folderProbeOverride = null)
    {
        this.options = options; this.store = store; this.media = media; this.inventory = inventory; this.logger = logger;
        scanSources = scanOverride ?? new FolderScanner(options).Scan;
        probeFolder = folderProbeOverride ?? FolderAccess.Check;
        jobs = store.Load().ToDictionary(j => j.Id);
        foreach (var job in jobs.Values.Where(j => j.Managed && j.Location == SourceLocation.Watch && !j.TerminalFailure && !j.Wrapped &&
                     j.Stage is JobStage.Probing or JobStage.Transcoding or JobStage.Wrapping))
        { job.Stage = JobStage.Queued; job.Progress = null; store.Save(job); }
        nativeSlots = new(options.Workers.Native); normalizeSlots = new(options.Workers.Normalize); proxySlots = new(options.Workers.Proxy); copySlots = new(options.Workers.Copy);
        destinationSlots = options.Destinations.ToDictionary(d => d.Id, _ => new SemaphoreSlim(options.Workers.CopiesPerDestination));
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public long Revision { get { lock (gate) return revision; } }
    public bool IsReady { get { lock (gate) return processingReady; } }
    private void Changed()
    {
        revision++; var previous = signal; signal = NewSignal(); previous.TrySetResult();
    }
    private void Update(JobRecord job, Action action, bool persist = true, string? eventKind = null)
    {
        lock (gate) { action(); if (persist) { if (eventKind is null) store.Save(job); else store.SaveWithEvent(job, eventKind); } Changed(); }
    }
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
            try { await Task.WhenAll(running.Values.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(12)); }
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
        var outputHealth = new[] { ("Abekas", options.Folders.Abekas), ("Proxy", options.Folders.Proxy), ("Work", options.Folders.Work), ("State", options.Folders.State) }
            .Select(p => PollFolder(p.Item1, p.Item2)).Concat(options.Destinations.Select(d => PollFolder("Destination " + d.Name, d.Path))).ToArray();
        lock (gate)
        {
            folders = scan.Health.Concat(outputHealth).ToArray(); sourceSnapshotComplete = scan.Complete;
            processingReady = scan.Complete && scan.Health.All(h => h.Readable && h.Writable) && outputHealth.Take(4).All(h => h.Readable && h.Writable) && mediaHealth.Ready;
            scanError = scan.Complete ? null : "A source folder is inaccessible or its enumeration failed. Cleanup is paused.";
            if (scan.Complete) Discover(scan.Files);
            ApplyInventory(); Changed();
        }
        if (!scan.Complete) return;
        await RetireAbsentAsync(scan.Files, ct);
        if (!IsReady) return;
        List<JobRecord> eligible;
        lock (gate) eligible = jobs.Values.Where(j => !j.Retiring && j.Managed && j.AbsentScans == 0 &&
            (j.Location == SourceLocation.Watch && (j.Stage is JobStage.Waiting or JobStage.Queued or JobStage.MoveFailed || j.Wrapped) || j.Location == SourceLocation.Finished && j.Wrapped && j.Stage != JobStage.Done && !j.TerminalFailure) &&
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
    }
    private bool ReservationRetiring(JobRecord job)
    {
        var owner = store.Owner(Path.Combine(options.Folders.Abekas, job.ClipName + ".clip"));
        lock (gate) return owner is not null && owner != job.Id && jobs.TryGetValue(owner, out var old) && (old.Retiring || old.AbsentScans > 0);
    }
    private async Task RunOwnedAsync(JobRecord job, TaskCompletionSource completion, CancellationTokenSource cancellation)
    {
        try { await ProcessAsync(job, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected job error {Job}", job.Id);
            Update(job, () => { job.Error = ex.Message; job.Stage = JobStage.MoveFailed; job.TerminalFailure = true; });
        }
        finally { running.TryRemove(job.Id, out _); cancellation.Dispose(); completion.TrySetResult(); }
    }
    private void Discover(IReadOnlyList<SourceFile> files)
    {
        foreach (var f in files)
        {
            var existing = jobs.Values.FirstOrDefault(j => !j.Retiring && SameSourceGeneration(j, f));
            if (existing is not null) continue;
            // A producer may still grow/replace an unclaimed waiting source. Once processing
            // begins, a changed fingerprint is a different generation, never the old job's input.
            existing = jobs.Values.FirstOrDefault(j => !j.Retiring && j.Location == SourceLocation.Watch && !j.Wrapped && !j.TerminalFailure &&
                (j.Stage is JobStage.Waiting or JobStage.Queued) && !running.ContainsKey(j.Id) &&
                string.Equals(j.SourcePath, f.Path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                RefreshSource(existing, f); existing.StableSinceUtc = DateTimeOffset.UtcNow; store.Save(existing);
                continue;
            }
            // Interrupted archive move: ownership follows its committed target without re-ingestion.
            existing = jobs.Values.FirstOrDefault(j => !j.Retiring && string.Equals(j.MoveTarget, f.Path, StringComparison.OrdinalIgnoreCase) && j.SourceSize == f.Size &&
                !files.Any(source => SameSourceGeneration(j, source)));
            if (existing is not null)
            { RefreshSource(existing, f); existing.Archived = f.Location == SourceLocation.Finished; existing.MoveTarget = null; existing.MoveLocation = null; store.Save(existing); continue; }
            // Publication may have committed just before a cross-volume move crashed.
            // Keep its target reserved while the original still exists, so the worker
            // can verify both copies and finish the existing move instead of ingesting it.
            if (jobs.Values.Any(j => !j.Retiring && string.Equals(j.MoveTarget, f.Path, StringComparison.OrdinalIgnoreCase) &&
                files.Any(source => SameSourceGeneration(j, source)))) continue;
            // Our archive move may finish after Watch was enumerated. Its stale
            // path is not another arrival; this never authorizes retirement.
            if (!File.Exists(f.Path)) continue;
            var j = new JobRecord { OriginalName = Path.GetFileName(f.Path), ClipName = Path.GetFileNameWithoutExtension(f.Path), SourcePath = f.Path, Location = f.Location, SourceSize = f.Size, SourceWriteUtc = f.WriteUtc, SourceCreatedUtc = f.CreatedUtc,
                Managed = f.Location == SourceLocation.Watch, Stage = f.Location == SourceLocation.Watch ? JobStage.Waiting : JobStage.Existing, ProxyStatus = f.Location == SourceLocation.Watch ? "Pending" : "Untracked",
                Destinations = options.Destinations.Select(d => new DestinationRecord { Id = d.Id }).ToList() };
            jobs[j.Id] = j; store.Save(j);
        }
    }
    private static bool SameSourceGeneration(JobRecord job, SourceFile file) =>
        string.Equals(job.SourcePath, file.Path, StringComparison.OrdinalIgnoreCase) && job.SourceSize == file.Size &&
        job.SourceWriteUtc == file.WriteUtc && job.SourceCreatedUtc == file.CreatedUtc;

    private static void RefreshSource(JobRecord job, SourceFile source)
    {
        job.SourcePath = source.Path; job.Location = source.Location; job.SourceSize = source.Size;
        job.SourceWriteUtc = source.WriteUtc; job.SourceCreatedUtc = source.CreatedUtc;
    }

    private static void RefreshMovedSource(JobRecord job, string target, SourceLocation location)
    {
        var file = new FileInfo(target); file.Refresh();
        if (!file.Exists) throw new IOException("Moved source is no longer available.");
        RefreshSource(job, new(file.FullName, location, file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc));
    }

    private sealed class HealthProbe<T> where T : class
    {
        public Task<(T Value, DateTimeOffset Completed)>? Task;
        public DateTimeOffset Started;
        public DateTimeOffset NextCheck;
        public T? Value;
    }

    private static T PollHealth<T>(HealthProbe<T> state, Func<Task<T>> action, Func<string, T> unavailable) where T : class
    {
        var now = DateTimeOffset.UtcNow;
        if (state.Task?.IsCompleted == true)
        {
            try
            {
                var completed = state.Task.GetAwaiter().GetResult();
                state.Value = completed.Completed - state.Started <= TimeSpan.FromSeconds(2)
                    ? completed.Value : unavailable("Health check exceeded its two-second budget.");
            }
            catch (Exception ex) { state.Value = unavailable(ex.Message); }
            state.Task = null; state.NextCheck = now.AddSeconds(60);
        }
        if (state.Task is null && now >= state.NextCheck)
        {
            state.Started = now;
            state.Task = Task.Run(async () => (await action(), DateTimeOffset.UtcNow));
        }
        if (state.Task is not null && now - state.Started > TimeSpan.FromSeconds(2))
            return unavailable("Health check timed out; waiting for the existing check to finish.");
        return state.Value ?? unavailable("Health check pending.");
    }

    private FolderHealth PollFolder(string id, string path)
    {
        if (!folderProbes.TryGetValue(path, out var state)) folderProbes[path] = state = new();
        return PollHealth(state, () => Task.FromResult(probeFolder(id, path, true)),
            error => new FolderHealth(id, path, false, false, null, error));
    }

    private async Task<FolderScan> ScanBoundedAsync(CancellationToken ct)
    {
        // Never launch a second filesystem enumeration while a timed-out OS/SMB call
        // is still running. Late snapshots are discarded instead of authorizing cleanup.
        if (sourceScanTask?.IsCompleted == true && DateTimeOffset.UtcNow - sourceScanStarted > TimeSpan.FromSeconds(2)) sourceScanTask = null;
        if (sourceScanTask is null)
        { sourceScanStarted = DateTimeOffset.UtcNow; sourceScanTask = Task.Run(scanSources); }
        try
        {
            var result = await sourceScanTask.WaitAsync(TimeSpan.FromSeconds(2), ct);
            sourceScanTask = null;
            return result;
        }
        catch (TimeoutException)
        {
            return new(false, [], new[] { ("Watch", options.Folders.Watch), ("Finished", options.Folders.Finished), ("Failed", options.Folders.Failed) }
                .Select(p => new FolderHealth(p.Item1, p.Item2, false, false, null, "Source scan timed out; cleanup paused until a fresh complete scan succeeds.")).ToArray());
        }
    }

    public override void Dispose()
    {
        healthLifetime.Cancel(); healthLifetime.Dispose();
        base.Dispose();
    }
    private void ApplyInventory()
    {
        var snapshots = inventory.GetSnapshots().ToDictionary(s => s.Id);
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
                    if (InventoryMatching.Contains(s, dest.InventoryFolder, j.ClipName))
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
            if (changed) store.Save(j);
        }
    }
}
