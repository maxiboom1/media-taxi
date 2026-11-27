using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task DeliverAsync(JobRecord job, Configuration.DestinationOptions destination, CancellationToken ct)
    {
        var record = job.Destinations.First(d => d.Id == destination.Id);
        if (record.CopyStatus is CopyState.Succeeded or CopyState.Failed) return;
        await destinationSlots[destination.Id].WaitAsync(ct);
        try
        {
            // A job waiting for this server must not occupy a global copy slot.
            await using var slot = await AcquireAsync(copySlots, "copy", ct);
            ct.ThrowIfCancellationRequested();
            if (job.Retiring || !SourceMatches(job)) throw new OperationCanceledException("Source is no longer current.", ct);
            var target = Path.Combine(destination.Path, job.ClipName + ".clip");
            var partial = target + "." + job.Id + ".partial";
            var resumePublication = record.CopyStatus == CopyState.InProgress;
            Update(job, () => { record.CopyStatus = CopyState.InProgress; record.TempPath = partial; });
            await MeasureAsync(job, "delivery:" + destination.Id, () => ClipPublisher.PublishAsync(job.ArtifactPath!, target, partial, job.ArtifactSha256 ?? "", resumePublication, ct,
                measurement => Update(job, () =>
                {
                    job.Timings["copy-write:" + destination.Id] = new(measurement.WriteStartedUtc, measurement.VerifyStartedUtc, measurement.WriteSeconds, true);
                    job.Timings["copy-publish:" + destination.Id] = new(measurement.VerifyStartedUtc, DateTimeOffset.UtcNow, measurement.VerifySeconds, true);
                }), job.HeaderSha256, job.PayloadSha256));
            Update(job, () => { record.CopyStatus = CopyState.Succeeded; record.PublishedUtc = DateTimeOffset.UtcNow; record.TempPath = null; record.Error = null; }, eventKind: "imported");
            inventory.RequestRefresh(destination.Id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        { Update(job, () => { record.CopyStatus = CopyState.Failed; record.Error = ex.Message; }); }
        finally { destinationSlots[destination.Id].Release(); }
    }
    private async Task MoveFailedAsync(JobRecord job, CancellationToken ct)
    {
        if (job.Location != SourceLocation.Watch) return;
        if (!SourceMatches(job)) { Update(job, () => job.Stage = JobStage.MoveFailed); return; }
        try
        {
            var resumePublished = job.MoveLocation == SourceLocation.Failed && job.MoveTarget is not null;
            var path = resumePublished ? job.MoveTarget! : Path.Combine(options.Folders.Failed, job.OriginalName);
            if (!resumePublished && File.Exists(path)) path = Path.Combine(options.Folders.Failed, $"{job.ClipName}__{DateTimeOffset.UtcNow:yyyyMMddTHHmmss}_{job.Id[..8]}{Path.GetExtension(job.OriginalName)}");
            Update(job, () => { job.MoveTarget = path; job.MoveLocation = SourceLocation.Failed; });
            await FileOperations.MoveVerifiedAsync(job.SourcePath, path, job.Id, ct, resumePublished);
            Update(job, () => { RefreshMovedSource(job, path, SourceLocation.Failed); job.Stage = JobStage.Failed; job.MoveTarget = null; job.MoveLocation = null; });
            ReleaseDeliveryCache(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Update(job, () => { job.Stage = JobStage.MoveFailed; job.Error = $"{job.Error} | Could not move source to Failed: {ex.Message}"; }); }
    }
    private static bool SourceMatches(JobRecord j)
    {
        try { var f = new FileInfo(j.SourcePath); return f.Exists && f.Length == j.SourceSize && f.LastWriteTimeUtc == j.SourceWriteUtc.UtcDateTime && f.CreationTimeUtc == j.SourceCreatedUtc.UtcDateTime; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
    private sealed class SourceGenerationChangedException() : IOException("Source generation changed; the replacement was not moved or quarantined by the old job.");
    private void Progress(JobRecord job, double progress) => Update(job, () => job.Progress = Math.Clamp(progress, 0, 100), persist: false);
    private async Task<IAsyncDisposable> AcquireAsync(SemaphoreSlim semaphore, string name, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct); lock (gate) { activeWorkers[name]++; Changed(); }
        return new Slot(() => { lock (gate) { activeWorkers[name]--; semaphore.Release(); Changed(); } });
    }
    private sealed class Slot(Action release) : IAsyncDisposable
    { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
}
