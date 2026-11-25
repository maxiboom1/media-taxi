using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task RetireAbsentAsync(IReadOnlyList<SourceFile> snapshot, CancellationToken ct)
    {
        List<JobRecord> candidates;
        lock (gate)
        {
            foreach (var job in jobs.Values)
            {
                var present = snapshot.Any(f => SameSourceGeneration(job, f));
                var movePresent = job.MoveTarget is not null && snapshot.Any(f => string.Equals(f.Path, job.MoveTarget, StringComparison.OrdinalIgnoreCase) && f.Size == job.SourceSize);
                if (present || movePresent) { job.AbsentScans = 0; job.LastAbsentScanUtc = null; continue; }
                if (job.LastAbsentScanUtc is null || DateTimeOffset.UtcNow - job.LastAbsentScanUtc >= TimeSpan.FromSeconds(options.Timing.ReconcileSeconds))
                { job.AbsentScans++; job.LastAbsentScanUtc = DateTimeOffset.UtcNow; store.Save(job); }
                if (job.AbsentScans >= 2)
                {
                    job.Retiring = true;
                    if (running.TryGetValue(job.Id, out var task)) task.Cancellation.Cancel();
                    store.Save(job);
                }
            }
            candidates = jobs.Values.Where(j => j.Retiring && !running.ContainsKey(j.Id)).ToList();
        }
        foreach (var job in candidates)
        {
            var nameLock = nameLocks.GetOrAdd(job.ClipName, _ => new SemaphoreSlim(1));
            if (!await nameLock.WaitAsync(0, ct)) continue;
            try
            {
                if (!job.Managed)
                {
                    lock (gate) { jobs.Remove(job.Id); store.Delete(job.Id); Changed(); }
                    continue;
                }
                lock (gate)
                {
                    foreach (var (id, path) in new[] { ("Abekas", job.ArtifactPath), ("Proxy", job.ProxyPath), ("Work", job.WorkPath) })
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
                DeleteWork(job, throwOnError: true);
                lock (gate) { jobs.Remove(job.Id); store.Delete(job.Id); Changed(); }
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
    private void DeleteWork(JobRecord job, bool throwOnError = false)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _)) throw new IOException("Invalid job identity for work cleanup.");
        var expected = Path.Combine(options.Folders.Work, job.Id);
        if (job.WorkPath is null || !string.Equals(Path.GetFullPath(job.WorkPath), expected, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            if (!Directory.Exists(expected)) return;
            if (new DirectoryInfo(expected).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing cleanup of a work-directory link.");
            Directory.Delete(expected, recursive: true);
        }
        catch (Exception ex) when (!throwOnError && ex is IOException or UnauthorizedAccessException)
        { logger.LogWarning("Work cleanup pending for {Job}: {Error}", job.Id, ex.Message); }
    }
}
