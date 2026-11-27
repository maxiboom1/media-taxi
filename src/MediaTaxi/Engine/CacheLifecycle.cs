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
            ClearLegacyWork(job);
            if (!cache.Release(job, out var error)) throw new IOException(error);
            Update(job, () => { job.CacheState = "Released"; job.CacheError = null; });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Update(job, () => { job.CacheState = "Cleanup pending"; job.CacheError = ex.Message; }); }
    }
    private void ClearLegacyWork(JobRecord job)
    {
        if (job.WorkPath is null || string.Equals(Path.GetFullPath(job.WorkPath), cache.DirectoryFor(job), StringComparison.OrdinalIgnoreCase)) return;
        var directory = Path.GetFullPath(job.WorkPath);
        if (!Guid.TryParseExact(job.Id, "N", out _) || !string.Equals(Path.GetFileName(directory), job.Id, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Legacy work ownership mismatch; files retained.");
        if (Directory.Exists(directory))
        {
            WorkCache.RejectLink(directory);
            foreach (var name in new[] { "video.h264", "audio.wav", "proxy-video.h264", "proxy-audio.wav" })
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) { WorkCache.RejectLink(path); File.Delete(path); }
            }
            if (Directory.EnumerateFileSystemEntries(directory).Any()) throw new IOException("Legacy work contains untracked files; retained for review.");
            Directory.Delete(directory);
        }
        Update(job, () => job.WorkPath = null);
    }
}
