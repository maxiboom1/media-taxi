using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task DeliverOutputsAsync(JobRecord job, CancellationToken ct)
    {
        if (!job.DeliveryCompleted)
        {
            Update(job, () => { job.Stage = JobStage.Delivering; job.Progress = null; });
            await Task.WhenAll(options.Destinations.Select(d => DeliverAsync(job, d, ct)));
            var failed = job.Destinations.All(d => d.CopyStatus != CopyState.Succeeded);
            Update(job, () => job.DeliveryCompleted = true, eventKind: failed ? "deliveryFailure" : null);
        }
        Update(job, () => { if (job.ProxyStatus is not ("Ready" or "Failed")) job.Stage = JobStage.Proxy; });
    }

    private async Task CreateProxyOutputAsync(JobRecord job, PreparedMedia? prepared, CancellationToken ct)
    {
        if (job.ProxyStatus is "Ready" or "Failed") return;
        Update(job, () => { job.ProxyStatus = "Queued"; job.ProxyProgress = null; });
        await using var proxy = await AcquireAsync(proxySlots, "proxy", ct);
        try
        {
            Update(job, () => { job.ProxyStatus = "Working"; job.ProxyProgress = null; });
            if (job.ProxyPath is not null && File.Exists(job.ProxyPath) && await media.OwnsProxyAsync(job.ProxyPath, job.Id, ct))
            {
                var recoveredHash = await FileOperations.HashAsync(job.ProxyPath, ct);
                Update(job, () => { job.ProxySha256 = recoveredHash; job.ProxyStatus = "Ready"; job.ProxyProgress = 100; }, eventKind: "proxy");
            }
            else
            {
                job.WorkPath ??= Path.Combine(options.Folders.Work, job.Id);
                if (prepared is null)
                {
                    DeleteWork(job, throwOnError: true);
                    prepared = await media.ExtractClipAsync(job.ArtifactPath!, job.WorkPath, ct);
                }
                var output = job.ProxyPath ?? Path.Combine(options.Folders.Proxy, job.ClipName + ".mp4");
                if (!store.Claim(output, job.Id, "proxy")) throw new IOException("Proxy name is owned by another generation.");
                Update(job, () => job.ProxyPath = output);
                RemoveOwnedPartials(job);
                var artifact = await MeasureAsync(job, "proxy", () => media.CreateProxyAsync(prepared, output, job.Id,
                    p => Update(job, () => job.ProxyProgress = Math.Clamp(p, 0, 100), persist: false), ct));
                Update(job, () => { job.ProxySha256 = artifact.Sha256; job.ProxyStatus = "Ready"; job.ProxyProgress = 100; }, eventKind: "proxy");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A proxy failure must not abort, cancel, or repeat high-resolution delivery.
            Update(job, () => { job.ProxyStatus = "Failed"; job.ProxyProgress = null; job.Error = "Proxy failed: " + ex.Message; }, eventKind: "processingFailure");
            logger.LogWarning("Proxy for job {Job}: {Error}", job.Id, ex.Message);
        }
    }
}
