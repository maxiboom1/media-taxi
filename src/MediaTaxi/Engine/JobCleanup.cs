using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task RetireAbsentAsync(IReadOnlyList<SourceFile> snapshot, CancellationToken ct)
    {
        List<JobRecord> candidates = [];
        var cancel = new List<CancellationTokenSource>();
        Mutate(batch =>
        {
            foreach (var job in jobs.Values)
            {
                var present = snapshot.Any(f => SameSourceGeneration(job, f));
                var movePresent = job.MoveTarget is not null && snapshot.Any(f => string.Equals(f.Path, job.MoveTarget, StringComparison.OrdinalIgnoreCase) && f.Size == job.SourceSize);
                if (present || movePresent) { job.AbsentScans = 0; job.LastAbsentScanUtc = null; continue; }
                if (job.LastAbsentScanUtc is null || DateTimeOffset.UtcNow - job.LastAbsentScanUtc >= TimeSpan.FromSeconds(options.Timing.ReconcileSeconds))
                { job.AbsentScans++; job.LastAbsentScanUtc = DateTimeOffset.UtcNow; batch.Add(JobCheckpoint.Save(job)); }
                if (job.AbsentScans >= 2)
                {
                    job.Retiring = true;
                    if (running.TryGetValue(job.Id, out var task)) cancel.Add(task.Cancellation);
                    if (proxyRunning.TryGetValue(job.Id, out var proxy)) cancel.Add(proxy.Cancellation);
                    batch.Add(JobCheckpoint.Save(job));
                }
            }
            candidates = jobs.Values.Where(j => j.Retiring && !running.ContainsKey(j.Id) && !proxyRunning.ContainsKey(j.Id)).ToList();
        });
        foreach (var c in cancel) { try { c.Cancel(); } catch (ObjectDisposedException) { } }
        foreach (var job in candidates)
        {
            var nameLock = nameLocks.GetOrAdd(job.ClipName, _ => new SemaphoreSlim(1));
            if (!await nameLock.WaitAsync(0, ct)) continue;
            try
            {
                if (!job.Managed)
                {
                    DeleteJob(job);
                    continue;
                }
                lock (gate)
                {
                    foreach (var (id, path) in new[] { ("Work", job.ArtifactPath), ("Proxy", job.ProxyPath), ("Work", job.WorkPath) })
                        if (path is not null && folders.FirstOrDefault(f => f.Id == id) is not { Readable: true, Writable: true })
                            throw new IOException($"{id} folder is unavailable; owned cleanup is deferred.");
                }
                if (job.ArtifactPath is not null && store.Owner(job.ArtifactPath) == job.Id && File.Exists(job.ArtifactPath))
                {
                    if (!FileOperations.ClipOwned(job.ArtifactPath, job.Id)) throw new IOException("Cleanup ownership mismatch: local clip retained.");
                    File.Delete(job.ArtifactPath);
                }
                if (job.ProxyPath is not null && store.Owner(job.ProxyPath) == job.Id && File.Exists(job.ProxyPath))
                {
                    var owned = job.ProxySha256 is not null ? await FileOperations.HashAsync(job.ProxyPath, ct) == job.ProxySha256 : await media.OwnsProxyAsync(job.ProxyPath, job.Id, ct);
                    if (!owned) throw new IOException("Cleanup ownership mismatch: MP4 retained.");
                    File.Delete(job.ProxyPath);
                }
                RemoveOwnedPartials(job);
                RemoveOwnedDestinationPartials(job);
                var cacheSnapshot = Snapshot(job);
                if (!cache.Release(cacheSnapshot, out var cacheError)) throw new IOException(cacheError);
                DeleteJob(job);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Update(job, () => { job.Stage = JobStage.CleanupPending; job.Error = ex.Message; }); }
            finally { nameLock.Release(); }
        }
    }
    private void RemoveOwnedDestinationPartials(JobRecord job)
    {
        foreach (var record in job.Destinations.Where(d => d.TempPath is not null))
        {
            var destination = options.Destinations.FirstOrDefault(d => d.Id == record.Id)
                ?? throw new IOException("Destination configuration is unavailable; owned partial cleanup is deferred.");
            var expected = Path.Combine(destination.Path, job.ClipName + ".clip") + "." + job.Id + ".partial";
            if (!string.Equals(Path.GetFullPath(record.TempPath!), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Destination partial ownership mismatch; file was retained.");
            lock (gate)
                if (folders.FirstOrDefault(f => string.Equals(f.Path, destination.Path, StringComparison.OrdinalIgnoreCase)) is not { Readable: true, Writable: true })
                    throw new IOException($"Destination {destination.Name} is unavailable; owned partial cleanup is deferred.");
            if (File.Exists(expected))
            {
                if (File.GetAttributes(expected).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing cleanup of a destination partial-file link.");
                File.Delete(expected);
            }
            Update(job, () => record.TempPath = null);
        }
    }
    private void RemoveOwnedPartials(JobRecord job)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new IOException("Invalid job identity for partial cleanup.");
        foreach (var output in new[] { job.ArtifactPath, job.ProxyPath })
        {
            if (output is null || store.Owner(output) != job.Id) continue;
            var partial = output + "." + job.Id + ".partial";
            if (!File.Exists(partial)) continue;
            if (File.GetAttributes(partial).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing cleanup of a partial-file link.");
            File.Delete(partial);
        }
    }
}
