using MediaTaxi.Domain;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    public JobsSnapshot GetJobs(string view)
    {
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            bool Active(JobRecord j) => j.Managed && (now - j.FirstSeenUtc < TimeSpan.FromHours(1) || j.Location == SourceLocation.Watch);
            var visible = jobs.Values.Where(j => !j.Retiring).ToArray();
            var chosen = visible.Where(j => view == "active" ? Active(j) : !Active(j)).OrderByDescending(j => j.FirstSeenUtc).ThenBy(j => j.Id)
                .Select(ToView).ToArray();
            return new(revision, options.Web.TimeZone, visible.Count(Active), visible.Count(j => !Active(j)), chosen);
        }
    }
    private JobView ToView(JobRecord j) => new(j.Id, j.OriginalName, j.ClipName, j.FirstSeenUtc, j.Location.ToString(), j.Stage.ToString(), j.Progress, j.Mode, j.MediaSummary,
        j.Error, j.ProxyStatus.Text(), options.Destinations.Select(d =>
        {
            var record = j.Destinations.FirstOrDefault(r => r.Id == d.Id);
            return new DestinationView(d.Id, d.Name, record?.CopyStatus.ToString() ?? "NotAttempted", record?.Availability.ToString() ?? "Unknown", record?.Error, record?.InventoryConfirmed ?? false);
        }).ToArray(), j.ProxyProgress, new Dictionary<string, StageTiming>(j.Timings), j.HiresPhase.Text(), j.CacheWaitReason, j.ProxyDeferralReason, j.CacheState.Text(),
        j.WaitCode.ToString(), j.WaitReason, j.WaitSinceUtc, IsStalled(j));
    public JobDetail? GetJob(string id)
    {
        lock (gate) return jobs.TryGetValue(id, out var j) && !j.Retiring
            ? new(ToView(j), j.SourcePath, j.MoveTarget, j.ProxyEligibleUtc, j.Diagnostic) : null;
    }
    public StatisticsSnapshot GetStatistics() => Volatile.Read(ref statistics);
    public SystemSnapshot GetSystem()
    {
        lock (gate)
        {
            var servers = inventory.GetSnapshots().Select(s => new ServerHealth(s.Id, s.Name, s.Connected, s.Ready, s.LastInventoryUtc, s.Error)).ToArray();
            var cleanup = jobs.Values.Where(j => j.Stage == JobStage.CleanupPending || j.CacheState == CacheLifecycle.CleanupPending).Select(j => $"{j.OriginalName}: {j.CacheError ?? j.Error}").ToArray();
            var errors = folders.Where(f => f.Error is not null).Select(f => f.Id + ": " + f.Error)
                .Concat(mediaHealth.Error is not null ? [mediaHealth.Error] : Array.Empty<string>())
                .Concat(scanError is not null ? [scanError] : Array.Empty<string>())
                .Concat(jobs.Values.Where(j => !j.Retiring && j.Stage == JobStage.MoveFailed).Select(j => j.OriginalName + ": " + j.Error)).Distinct().ToArray();
            var pause = processingReady ? null : errors.FirstOrDefault() ?? "Required folders/media tools are not ready.";
            var stages = jobs.Values.Where(j => j.Managed && !j.Retiring && j.Stage is not (JobStage.Done or JobStage.Existing or JobStage.Failed))
                .GroupBy(j => j.Stage).OrderBy(group => group.Key).ToDictionary(group => group.Key.ToString(), group => group.Count());

            return new(AppVersion.Current, (DateTimeOffset.UtcNow - started).TotalSeconds, processingReady && checkpoints.Error is null, checkpoints.Error ?? pause, folders, mediaHealth, servers,
                new Dictionary<string, int>(activeWorkers), jobs.Values.Count(j => !j.Retiring && j.Managed && j.Stage is JobStage.Waiting or JobStage.Queued), cleanup, errors, stages,
                cacheMetrics, lastSourceScanUtc, jobs.Values.Where(j => !j.Retiring && !j.TerminalFailure && j.WaitCode != SourceWaitCode.None)
                    .Select(j => $"{j.OriginalName}: {j.WaitReason} (since {j.WaitSinceUtc:O})" + (IsStalled(j) ? "; stalled; defers new proxies under strict priority" : "")).ToArray(),
                checkpoints.Error, checkpoints.Pending, fileLog?.Health);
        }
    }
}
