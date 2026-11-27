using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task ProcessAsync(JobRecord job, CancellationToken ct)
    {
        var nameLock = nameLocks.GetOrAdd(job.ClipName, _ => new SemaphoreSlim(1));
        await nameLock.WaitAsync(ct);
        try
        {
            if (job.Retiring) return;
            if (job.TerminalFailure) { await MoveFailedAsync(job, ct); return; }
            PreparedMedia? prepared = null;
            if (!job.Wrapped)
            {
                // Exclusive deny-write readiness guard: don't quarantine a producer's open/growing file.
                FileStream sourceGuard;
                try { sourceGuard = new(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { return; }
                await using (sourceGuard)
                {
                    if (!SourceMatches(job)) return;
                    var clipPath = Path.Combine(options.Folders.Abekas, job.ClipName + ".clip");
                    var proxyPath = Path.Combine(options.Folders.Proxy, job.ClipName + ".mp4");
                    var finishedPath = Path.Combine(options.Folders.Finished, job.OriginalName);
                    if (File.Exists(finishedPath) || File.Exists(proxyPath) && store.Owner(proxyPath) != job.Id || File.Exists(clipPath) && !FileOperations.ClipOwned(clipPath, job.Id))
                        throw new IOException("Name conflict: an archived source or local output already exists. Nothing was overwritten.");
                    if (!store.Claim(clipPath, job.Id, "clip") || !store.Claim(proxyPath, job.Id, "proxy"))
                        throw new IOException("Name conflict: output reserved by another source generation.");
                    Update(job, () => { job.ArtifactPath = clipPath; job.ArtifactUuid = Guid.Parse(job.Id).ToString("D"); job.ProxyPath = proxyPath; job.WorkPath = Path.Combine(options.Folders.Work, job.Id); job.Stage = JobStage.Queued; job.Error = null; });
                    RemoveOwnedPartials(job);
                    if (File.Exists(clipPath))
                    {
                        DeleteWork(job, throwOnError: true);
                        prepared = await media.ExtractClipAsync(clipPath, job.WorkPath!, ct);
                        var recoveredHash = await FileOperations.HashAsync(clipPath, ct);
                        Update(job, () => { job.Wrapped = true; job.ArtifactSha256 = recoveredHash; if (job.Mode == "Unknown") job.Mode = prepared.Native ? "Native" : "Transcoded"; }, eventKind: "processed");
                    }
                    else
                    {
                        DeleteWork(job, throwOnError: true);
                        await using (var probing = await AcquireAsync(nativeSlots, "native", ct))
                        {
                            Update(job, () => job.Stage = JobStage.Probing);
                            var inspection = await MeasureAsync(job, "probe", () => media.InspectAsync(job.SourcePath, ct));
                            Update(job, () => job.MediaSummary = inspection.Summary);
                            if (inspection.Native)
                            {
                                Update(job, () => { job.Mode = "Native"; job.Stage = JobStage.Wrapping; });
                                prepared = await MeasureAsync(job, "prepare", () => media.PrepareAsync(job.SourcePath, job.WorkPath!, p => Progress(job, p), ct));
                                await FinishWrapAsync(job, prepared, ct);
                            }
                            else
                            {
                                if (!options.Media.AllowNormalization) throw new MediaMismatchException("Format mismatch: incoming normalization is disabled.");
                                Update(job, () => { job.Mode = "Transcoded"; job.Stage = JobStage.Queued; });
                            }
                        }
                        if (prepared is null)
                        {
                            await using var normalizing = await AcquireAsync(normalizeSlots, "normalize", ct);
                            Update(job, () => { job.Mode = "Transcoded"; job.Stage = JobStage.Transcoding; });
                            prepared = await MeasureAsync(job, "prepare", () => media.PrepareAsync(job.SourcePath, job.WorkPath!, p => Progress(job, p), ct));
                            await FinishWrapAsync(job, prepared, ct);
                        }
                    }
                }
            }
            if (job.ArtifactPath is null || !File.Exists(job.ArtifactPath) || !FileOperations.ClipOwned(job.ArtifactPath, job.Id))
                throw new IOException("Verified local clip is missing or ownership changed.");
            if (job.ArtifactSha256 is null)
            {
                var recoveredHash = await FileOperations.HashAsync(job.ArtifactPath, ct);
                Update(job, () => job.ArtifactSha256 = recoveredHash);
            }
            if (!job.Archived)
            {
                ct.ThrowIfCancellationRequested();
                if (!SourceMatches(job)) throw new SourceGenerationChangedException();
                var target = Path.Combine(options.Folders.Finished, job.OriginalName);
                var resumePublished = job.MoveLocation == SourceLocation.Finished && string.Equals(job.MoveTarget, target, StringComparison.OrdinalIgnoreCase);
                Update(job, () => { job.Stage = JobStage.Archiving; job.MoveTarget = target; job.MoveLocation = SourceLocation.Finished; });
                await MeasureAsync(job, "archive", () => FileOperations.MoveVerifiedAsync(job.SourcePath, target, job.Id, ct, resumePublished));
                Update(job, () => { RefreshMovedSource(job, target, SourceLocation.Finished); job.Archived = true; job.MoveTarget = null; job.MoveLocation = null; });
            }
            // Separate worker pools: neither branch owns a native/normalization slot.
            await Task.WhenAll(DeliverOutputsAsync(job, ct), CreateProxyOutputAsync(job, prepared, ct));
            Update(job, () => { job.Stage = JobStage.Done; job.Progress = null; if (job.ProxyStatus != "Failed") job.Error = null; });
            DeleteWork(job);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceGenerationChangedException ex)
        {
            // A new producer file at the same name is not this job's failed source.
            // Discovery creates its own generation; two healthy scans retire this one.
            Update(job, () => { job.Error = ex.Message; job.TerminalFailure = true; job.Stage = JobStage.MoveFailed; job.Progress = null; });
        }
        catch (Exception ex)
        {
            if (job.Retiring) return;
            if (job.Archived)
            {
                Update(job, () => { job.Stage = JobStage.Failed; job.Error = ex.Message; job.ProxyStatus = "Failed"; job.TerminalFailure = true; job.Progress = null; }, eventKind: "processingFailure");
                DeleteWork(job);
            }
            else
            {
                Update(job, () => { job.Error = ex.Message; job.TerminalFailure = true; job.Progress = null; }, eventKind: "processingFailure");
                await MoveFailedAsync(job, ct);
            }
            logger.LogWarning("Job {Job}: {Error}", job.Id, ex.Message);
        }
        finally { nameLock.Release(); }
    }
    private async Task FinishWrapAsync(JobRecord job, PreparedMedia prepared, CancellationToken ct)
    {
        if (!SourceMatches(job)) throw new SourceGenerationChangedException();
        Update(job, () => { job.MediaSummary = prepared.Summary; job.Mode = prepared.Native ? "Native" : "Transcoded"; job.Stage = JobStage.Wrapping; job.Progress = null; });
        var artifact = await MeasureAsync(job, "wrap", () => media.WrapAsync(prepared, job.ArtifactPath!, job.Id, ct));
        Update(job, () => { job.ArtifactSha256 = artifact.Sha256; job.ArtifactUuid = artifact.Uuid; job.Wrapped = true; }, eventKind: "processed");
    }
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
            await MeasureAsync(job, "delivery:" + destination.Id, () => ClipPublisher.PublishAsync(job.ArtifactPath!, target, partial, job.ArtifactSha256!, resumePublication, ct,
                measurement => Update(job, () =>
                {
                    job.Timings["copy-write:" + destination.Id] = new(measurement.WriteStartedUtc, measurement.VerifyStartedUtc, measurement.WriteSeconds, true);
                    job.Timings["copy-publish:" + destination.Id] = new(measurement.VerifyStartedUtc, DateTimeOffset.UtcNow, measurement.VerifySeconds, true);
                })));
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
            DeleteWork(job);
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
        return new Slot(() => { semaphore.Release(); lock (gate) { activeWorkers[name]--; Changed(); } });
    }
    private sealed class Slot(Action release) : IAsyncDisposable
    { public ValueTask DisposeAsync() { release(); return ValueTask.CompletedTask; } }
}
