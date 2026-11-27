using MediaTaxi.Domain;
using MediaTaxi.Storage;
using MediaTaxi.Media;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task DeliverOutputsAsync(JobRecord job, CancellationToken ct)
    {
        if (job.DeliveryCompleted) return;
        Update(job, () => { job.Stage = JobStage.Delivering; job.HiresPhase = HiresState.Delivering; job.Progress = null; });
        await Task.WhenAll(options.Destinations.Select(d => DeliverAsync(job, d, ct)));
        var failed = job.Destinations.All(d => d.CopyStatus != CopyState.Succeeded);
        Update(job, () => job.DeliveryCompleted = true, eventKind: failed ? "deliveryFailure" : null);
    }
    private bool HiresBusy() => jobs.Values.Any(j => j.Managed && !j.Retiring && j.AbsentScans == 0 && !j.TerminalFailure && !j.DeliveryCompleted &&
        (j.Location == SourceLocation.Watch || j.Location == SourceLocation.Finished && j.Wrapped));
    private void ScheduleProxies(CancellationToken ct)
    {
        var admitted = new List<(JobRecord Job, TaskCompletionSource Done, CancellationTokenSource Cancellation)>();
        try
        {
            Mutate(batch =>
            {
                var defer = options.Proxy.DeferWhileHiresBusy && HiresBusy();
                foreach (var job in jobs.Values.Where(j => j.Managed && !j.Retiring && j.AbsentScans == 0 && j.Archived && j.DeliveryCompleted &&
                             !j.TerminalFailure && j.ProxyStatus is not (ProxyState.Ready or ProxyState.Failed)))
                {
                    if (proxyRunning.ContainsKey(job.Id)) continue;
                    job.ProxyStatus = ProxyState.Queued;
                    job.ProxyDeferralReason = defer ? "High-resolution processing/delivery has priority" : "Waiting for proxy worker";
                }
                if (defer) return;
                foreach (var job in jobs.Values.Where(j => j.Managed && !j.Retiring && j.AbsentScans == 0 && j.Archived && j.DeliveryCompleted &&
                             !j.TerminalFailure && j.ProxyStatus is not (ProxyState.Ready or ProxyState.Failed) && !proxyRunning.ContainsKey(j.Id))
                             .OrderBy(j => j.ProxyEligibleUtc ?? j.FirstSeenUtc))
                {
                    if (activeWorkers["proxy"] >= options.Workers.Proxy) break;
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    if (!proxyRunning.TryAdd(job.Id, (done.Task, cancellation))) { cancellation.Dispose(); continue; }
                    activeWorkers["proxy"]++; job.ProxyDeferralReason = null; job.ProxyStatus = ProxyState.Working;
                    batch.Add(JobCheckpoint.Save(job));
                    admitted.Add((job, done, cancellation));
                }
            });
        }
        catch
        {
            lock (gate)
            foreach (var item in admitted)
            {
                activeWorkers["proxy"]--; proxyRunning.TryRemove(item.Job.Id, out _);
                item.Cancellation.Dispose(); item.Done.TrySetResult();
            }
            throw;
        }
        foreach (var item in admitted) _ = RunProxyOwnedAsync(item.Job, item.Done, item.Cancellation);
    }
    private async Task RunProxyOwnedAsync(JobRecord job, TaskCompletionSource done, CancellationTokenSource cancellation)
    {
        try { await CreateProxyOutputAsync(job, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (CheckpointException ex) { logger.LogError(ex, "Proxy paused: state unavailable for {Job}", job.Id); }
        finally
        {
            lock (gate) { activeWorkers["proxy"]--; proxyRunning.TryRemove(job.Id, out _); Changed(); }
            cancellation.Dispose(); done.TrySetResult(); wakeEngine?.Invoke();
        }
    }
    private async Task CreateProxyOutputAsync(JobRecord job, CancellationToken ct)
    {
        try
        {
            if (!SourceMatches(job)) throw new SourceGenerationChangedException();
            var output = job.ProxyPath ?? Path.Combine(options.Folders.Proxy, job.ClipName + ".mp4");
            if (!store.Claim(output, job.Id, "proxy")) throw new IOException("Proxy name is owned by another generation.");
            Update(job, () => { job.ProxyPath = output; job.Stage = JobStage.Proxy; job.ProxyProgress = null; });
            if (File.Exists(output) && await media.OwnsProxyAsync(output, job.Id, ct))
            {
                var sha = await FileOperations.HashAsync(output, ct);
                Update(job, () => { job.ProxySha256 = sha; job.ProxyStatus = ProxyState.Ready; job.ProxyProgress = 100; }, eventKind: "proxy");
            }
            else
            {
                RemoveOwnedPartials(job);
                var artifact = await MeasureAsync(job, "proxy", () => media.CreateSourceProxyAsync(job.SourcePath, job.Inspection, job.ExpectedFrameCount,
                    output, job.Id, p => Update(job, () => job.ProxyProgress = Math.Clamp(p, 0, 100), false), ct));
                if (!SourceMatches(job)) throw new SourceGenerationChangedException();
                Update(job, () => { job.ProxySha256 = artifact.Sha256; job.ProxyStatus = ProxyState.Ready; job.ProxyProgress = 100; }, eventKind: "proxy");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CheckpointException) { throw; }
        catch (Exception ex) when (ex is SourceGenerationChangedException || (ex is FileNotFoundException or DirectoryNotFoundException) && !SourceMatches(job))
        {
            // Nightly source removal is reconciled by healthy scans, not counted as an encoder failure.
            Update(job, () => { job.ProxyStatus = ProxyState.Queued; job.ProxyDeferralReason = "Source changed; awaiting folder reconciliation"; });
            return;
        }
        catch (Exception ex)
        {
            SetJobError(job, ex);
            Update(job, () => { job.ProxyStatus = ProxyState.Failed; job.ProxyProgress = null; job.Error = DiagnosticText.Summary("Proxy failed: " + ex.Message); }, eventKind: "processingFailure");
            logger.LogWarning("Proxy for job {Job}: {Error}", job.Id, ex.Message);
        }
        Update(job, () => { job.Stage = JobStage.Done; job.ProxyDeferralReason = null; });
    }
}
