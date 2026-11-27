using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private sealed class HealthProbe<T> where T : class
    {
        public Task<(T Value, DateTimeOffset Completed)>? Task;
        public DateTimeOffset Started;
        public DateTimeOffset NextCheck;
        public T? Value;
    }

    private T PollHealth<T>(HealthProbe<T> state, Func<Task<T>> action, Func<string, T> unavailable) where T : class
    {
        var now = DateTimeOffset.UtcNow;
        if (state.Task?.IsCompleted == true)
        {
            try
            {
                var completed = state.Task.GetAwaiter().GetResult();
                state.Value = completed.Completed - state.Started <= TimeSpan.FromSeconds(options.Timing.HealthCheckTimeoutSeconds)
                    ? completed.Value : unavailable("Health check exceeded its configured budget.");
            }
            catch (Exception ex) { state.Value = unavailable(ex.Message); }
            state.Task = null; state.NextCheck = now.AddSeconds(options.Timing.HealthCheckIntervalSeconds);
        }
        if (state.Task is null && now >= state.NextCheck)
        {
            state.Started = now;
            state.Task = Task.Run(async () => (await action(), DateTimeOffset.UtcNow));
        }
        if (state.Task is not null && now - state.Started > TimeSpan.FromSeconds(options.Timing.HealthCheckTimeoutSeconds))
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
        // Completion time, not the next polling time, determines validity. Keep one OS call outstanding.
        if (sourceScanTask is null)
        {
            sourceScanStarted = DateTimeOffset.UtcNow;
            sourceScanTask = Task.Run(() => (scanSources(), DateTimeOffset.UtcNow));
        }
        try
        {
            var result = await sourceScanTask.WaitAsync(TimeSpan.FromSeconds(Math.Min(2, options.Timing.SourceScanTimeoutSeconds)), ct);
            sourceScanTask = null;
            if (result.Completed - sourceScanStarted <= TimeSpan.FromSeconds(options.Timing.SourceScanTimeoutSeconds))
            { if (result.Scan.Complete) lastSourceScanUtc = result.Completed; return result.Scan; }
        }
        catch (TimeoutException) { }
        return new(false, [], new[] { ("Watch", options.Folders.Watch), ("Finished", options.Folders.Finished), ("Failed", options.Folders.Failed) }
            .Select(p => new FolderHealth(p.Item1, p.Item2, false, false, null,
                "Source scan pending or exceeded its configured deadline; cleanup paused until a fresh complete scan succeeds.")).ToArray());
    }

    private void RefreshCacheMetrics()
    {
        JobRecord[] snapshot; lock (gate) snapshot = jobs.Values.Select(Snapshot).ToArray();
        long free = 0; try { free = cache.FreeBytes; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var metrics = new CacheSnapshot(options.Folders.Work, snapshot.Sum(cache.Usage), snapshot.Sum(j => j.CacheReservationBytes), options.Cache.MaxBytes, free, options.Cache.MinFreeBytes);
        lock (gate) cacheMetrics = metrics;
    }

}
