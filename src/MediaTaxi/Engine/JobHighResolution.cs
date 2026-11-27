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
            var needsDelivery = job.Destinations.Any(d => d.CopyStatus is CopyState.NotAttempted or CopyState.InProgress);
            if (job.Wrapped && needsDelivery && (job.ArtifactPath is null || !File.Exists(job.ArtifactPath)))
                Update(job, () => { job.Wrapped = false; job.CacheState = "Lost"; });
            if (!job.Wrapped)
            {
                FileStream sourceGuard;
                try { sourceGuard = new(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
                catch (IOException) { return; }
                await using (sourceGuard)
                {
                    if (!SourceMatches(job)) return;
                    var proxyPath = Path.Combine(options.Folders.Proxy, job.ClipName + ".mp4");
                    var finishedPath = Path.Combine(options.Folders.Finished, job.OriginalName);
                    if (!job.Archived && File.Exists(finishedPath) || File.Exists(proxyPath) && store.Owner(proxyPath) != job.Id)
                        throw new IOException("Name conflict: an archived source or local output already exists. Nothing was overwritten.");
                    if (!store.Claim(proxyPath, job.Id, "proxy")) throw new IOException("Name conflict: output reserved by another source generation.");
                    Update(job, () => { job.ProxyPath = proxyPath; job.Stage = JobStage.Queued; job.Error = null; });
                    await using (var probing = await AcquireAsync(nativeSlots, "native", ct))
                    {
                        if (job.Inspection is null)
                        {
                            Update(job, () => { job.Stage = JobStage.Probing; job.HiresPhase = "Probing"; });
                            var inspection = await MeasureAsync(job, "probe", () => media.InspectAsync(job.SourcePath, ct));
                            Update(job, () => { job.Inspection = inspection; job.MediaSummary = inspection.Summary; job.Mode = inspection.Native ? "Native" : "Transcoded"; });
                        }
                        if (!job.Inspection!.Native && !options.Media.AllowNormalization)
                            throw new MediaMismatchException("Format mismatch: incoming normalization is disabled.");
                        bool reserved;
                        lock (gate) { reserved = cache.Reserve(job, jobs.Values); store.Save(job); Changed(); }
                        if (!reserved) { Update(job, () => { job.Stage = JobStage.Queued; job.HiresPhase = "Waiting for cache"; }); return; }
                        RemoveOwnedPartials(job);
                        ClearLegacyWork(job);
                        cache.Prepare(job);
                        var clip = Path.Combine(job.WorkPath!, "delivery.clip");
                        if (!store.Claim(clip, job.Id, "clip")) throw new IOException("Cache output ownership conflict.");
                        Update(job, () => { job.ArtifactPath = clip; job.ArtifactUuid = Guid.Parse(job.Id).ToString("D"); });
                        RemoveOwnedPartials(job);
                        cache.DeleteEssence(job);
                        if (File.Exists(clip))
                        {
                            if (!FileOperations.ClipOwned(clip, job.Id)) throw new IOException("Cache clip ownership mismatch.");
                            var info = await FlatClip.ReadInfoAsync(clip, ct);
                            var sha = await FileOperations.HashAsync(clip, ct);
                            var recordProcessed = !job.ProcessedCheckpoint;
                            Update(job, () => { job.ExpectedFrameCount = info.FrameCount; job.ArtifactSha256 = sha; job.HeaderSha256 = null; job.PayloadSha256 = null;
                                job.Wrapped = true; job.ProcessedCheckpoint = true; job.CacheState = "Ready"; }, eventKind: recordProcessed ? "processed" : null);
                        }
                        else if (job.Inspection.Native) await WrapSourceAsync(job, ct);
                    }
                    if (!job.Wrapped)
                    {
                        await using var normalizing = await AcquireAsync(normalizeSlots, "normalize", ct);
                        await WrapSourceAsync(job, ct);
                    }
                }
            }
            if (needsDelivery)
            {
                if (job.ArtifactPath is null || !FileOperations.ClipOwned(job.ArtifactPath, job.Id)) throw new IOException("Owned delivery cache is missing or changed.");
                if (job.HeaderSha256 is null && job.ArtifactSha256 is null)
                {
                    var sha = await FileOperations.HashAsync(job.ArtifactPath, ct);
                    Update(job, () => job.ArtifactSha256 = sha);
                }
            }
            if (!job.Archived)
            {
                ct.ThrowIfCancellationRequested();
                if (!SourceMatches(job)) throw new SourceGenerationChangedException();
                var target = Path.Combine(options.Folders.Finished, job.OriginalName);
                var resume = job.MoveLocation == SourceLocation.Finished && string.Equals(job.MoveTarget, target, StringComparison.OrdinalIgnoreCase);
                Update(job, () => { job.Stage = JobStage.Archiving; job.HiresPhase = "Archiving"; job.MoveTarget = target; job.MoveLocation = SourceLocation.Finished; });
                await MeasureAsync(job, "archive", () => FileOperations.MoveVerifiedAsync(job.SourcePath, target, job.Id, ct, resume));
                Update(job, () => { RefreshMovedSource(job, target, SourceLocation.Finished); job.Archived = true; job.MoveTarget = null; job.MoveLocation = null; });
            }
            await DeliverOutputsAsync(job, ct);
            ReleaseDeliveryCache(job);
            Update(job, () => { job.HiresPhase = "Complete"; job.Stage = job.ProxyStatus is "Ready" or "Failed" ? JobStage.Done : JobStage.Proxy; job.ProxyReadyUtc ??= DateTimeOffset.UtcNow; job.Progress = null; });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (SourceGenerationChangedException ex)
        { Update(job, () => { job.Error = ex.Message; job.TerminalFailure = true; job.Stage = JobStage.MoveFailed; job.HiresPhase = "Failed"; job.Progress = null; }); }
        catch (Exception ex)
        {
            if (job.Retiring) return;
            Update(job, () => { job.Error = ex.Message; job.TerminalFailure = true; job.HiresPhase = "Failed"; job.Progress = null; }, eventKind: "processingFailure");
            if (!job.Archived) await MoveFailedAsync(job, ct);
            else Update(job, () => job.Stage = JobStage.Failed);
            ReleaseDeliveryCache(job);
            logger.LogWarning("Job {Job}: {Error}", job.Id, ex.Message);
        }
        finally { nameLock.Release(); }
    }

    private async Task WrapSourceAsync(JobRecord job, CancellationToken ct)
    {
        Update(job, () => { job.Stage = job.Inspection!.Native ? JobStage.Wrapping : JobStage.Transcoding; job.HiresPhase = job.Stage.ToString(); job.CacheState = "Working"; });
        using var quota = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var operation = MeasureAsync(job, "wrap", () => media.WrapSourceAsync(job.SourcePath, job.Inspection!, job.WorkPath!, job.ArtifactPath!, job.Id, p => Progress(job, p), quota.Token));
        try
        {
            while (!operation.IsCompleted)
            {
                await Task.WhenAny(operation, Task.Delay(200, ct));
                ct.ThrowIfCancellationRequested();
                if (cache.Usage(job) > job.CacheReservationBytes || cache.FreeBytes < options.Cache.MinFreeBytes)
                {
                    await quota.CancelAsync();
                    try { await operation; } catch (OperationCanceledException) { }
                    throw new IOException("Cache limit/free-space reserve reached while processing.");
                }
            }
            var wrapped = await operation;
            if (!SourceMatches(job)) throw new SourceGenerationChangedException();
            var artifact = wrapped.Artifact;
            var recordProcessed = !job.ProcessedCheckpoint;
            Update(job, () => { job.ArtifactSha256 = string.IsNullOrEmpty(artifact.Sha256) ? null : artifact.Sha256; job.HeaderSha256 = artifact.HeaderSha256;
                job.PayloadSha256 = artifact.PayloadSha256; job.ArtifactUuid = artifact.Uuid; job.ExpectedFrameCount = wrapped.FrameCount;
                job.Wrapped = true; job.ProcessedCheckpoint = true; job.CacheState = "Ready"; }, eventKind: recordProcessed ? "processed" : null);
            try { cache.DeleteEssence(job); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Update(job, () => job.CacheError = ex.Message); }
        }
        finally
        {
            await quota.CancelAsync();
            try { await operation; } catch (OperationCanceledException) { }
        }
    }
}
