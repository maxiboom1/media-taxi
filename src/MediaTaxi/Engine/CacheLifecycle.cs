using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private void ReleaseDeliveryCache(JobRecord job)
    {
        try
        {
            if (job.ArtifactPath is not null && File.Exists(job.ArtifactPath))
            {
                if (store.Owner(job.ArtifactPath) != job.Id || !FileOperations.ClipOwned(job.ArtifactPath, job.Id))
                    throw new IOException("Cache clip ownership changed; cleanup retained it.");
                WorkCache.RejectLink(job.ArtifactPath); File.Delete(job.ArtifactPath);
            }
            var snapshot = Snapshot(job);
            if (!cache.Release(snapshot, out var error)) throw new IOException(error);
            Update(job, () => { job.CacheState = CacheLifecycle.Released; job.CacheReservationBytes = 0; job.CacheError = null; });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Update(job, () => { job.CacheState = CacheLifecycle.CleanupPending; job.CacheError = ex.Message; }); }
    }
}
