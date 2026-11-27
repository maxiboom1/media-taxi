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
                .Select(j => new JobView(j.Id, j.OriginalName, j.ClipName, j.FirstSeenUtc, j.Location.ToString(), j.Stage.ToString(), j.Progress, j.Mode, j.MediaSummary,
                    j.Error, j.ProxyStatus, options.Destinations.Select(d =>
                    {
                        var record = j.Destinations.FirstOrDefault(r => r.Id == d.Id);
                        return new DestinationView(d.Id, d.Name, record?.CopyStatus.ToString() ?? "NotAttempted", record?.Availability.ToString() ?? "Unknown", record?.Error, record?.InventoryConfirmed ?? false);
                    }).ToArray(), j.ProxyProgress, new Dictionary<string, StageTiming>(j.Timings))).ToArray();
            return new(revision, options.Web.TimeZone, visible.Count(Active), visible.Count(j => !Active(j)), chosen);
        }
    }
    public StatisticsSnapshot GetStatistics() => store.Statistics();
    public SystemSnapshot GetSystem()
    {
        lock (gate)
        {
            var servers = inventory.GetSnapshots().Select(s => new ServerHealth(s.Id, s.Name, s.Connected, s.Ready, s.LastInventoryUtc, s.Error)).ToArray();
            var cleanup = jobs.Values.Where(j => j.Stage == JobStage.CleanupPending).Select(j => $"{j.OriginalName}: {j.Error}").ToArray();
            var errors = folders.Where(f => f.Error is not null).Select(f => f.Id + ": " + f.Error)
                .Concat(mediaHealth.Error is not null ? [mediaHealth.Error] : Array.Empty<string>())
                .Concat(scanError is not null ? [scanError] : Array.Empty<string>())
                .Concat(jobs.Values.Where(j => !j.Retiring && j.Stage == JobStage.MoveFailed).Select(j => j.OriginalName + ": " + j.Error)).Distinct().ToArray();
            var pause = processingReady ? null : errors.FirstOrDefault() ?? "Required folders/media tools are not ready.";
            var stages = jobs.Values.Where(j => j.Managed && !j.Retiring && j.Stage is not (JobStage.Done or JobStage.Existing or JobStage.Failed))
                .GroupBy(j => j.Stage).OrderBy(group => group.Key).ToDictionary(group => group.Key.ToString(), group => group.Count());
            return new("0.2.1", (DateTimeOffset.UtcNow - started).TotalSeconds, processingReady, pause, folders, mediaHealth, servers,
                new Dictionary<string, int>(activeWorkers), jobs.Values.Count(j => !j.Retiring && j.Managed && j.Stage is JobStage.Waiting or JobStage.Queued), cleanup, errors, stages);
        }
    }
}
