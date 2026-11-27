using System.Security.Cryptography;
using System.Text;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Media;

namespace MediaTaxi.Storage;

/// <summary>Exclusive, state-owned, bounded transient storage. Never sweeps a directory.</summary>
public sealed class WorkCache : IDisposable
{
    private readonly TaxiOptions options;
    private readonly FileStream rootLock;
    private readonly Func<long> freeBytes;
    public string Owner { get; }
    public WorkCache(TaxiOptions options, Func<long>? freeBytes = null)
    {
        this.options = options;
        Directory.CreateDirectory(options.Folders.Work);
        RejectLink(options.Folders.Work);
        rootLock = new(Path.Combine(options.Folders.Work, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Owner = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(options.Folders.State).ToUpperInvariant())))[..16];
        this.freeBytes = freeBytes ?? (() => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(options.Folders.Work))!).AvailableFreeSpace);
    }
    public long FreeBytes => freeBytes();
    public string DirectoryFor(JobRecord job) => Path.Combine(options.Folders.Work, Owner, job.Id);
    public long Estimate(JobRecord job)
    {
        var frames = checked((long)Math.Ceiling((job.Inspection?.Probe?.Duration ?? 1) * 50) + 2);
        // Final clip + PCM spool; normalization also holds the elementary video.
        return checked(4 * 1024 * 1024 + frames * (FlatClip.VideoStride + FlatClip.AudioStride + 960 * 8 * 4L
            + (job.Inspection?.Native == false ? AvciValidator.FrameBytes : 0)));
    }
    public bool Reserve(JobRecord job, IEnumerable<JobRecord> jobs)
    {
        var estimate = Math.Max(Estimate(job), job.CacheReservationBytes);
        if (estimate > options.Cache.MaxBytes) throw new IOException($"Cache limit: this job needs approximately {estimate / 1073741824d:0.00} GiB; cache.max_gib is {options.Cache.MaxGiB}.");
        var all = jobs.Where(j => j.Id != job.Id).ToArray();
        var reserved = all.Sum(j => j.CacheReservationBytes);
        var outstanding = all.Sum(j => Math.Max(0, j.CacheReservationBytes - Usage(j)));
        var ownUsage = Usage(job);
        if (reserved + estimate > options.Cache.MaxBytes || FreeBytes - outstanding - Math.Max(0, estimate - ownUsage) < options.Cache.MinFreeBytes)
        {
            job.CacheWaitReason = "Waiting for cache capacity/free-space reserve. Check /admin cache usage and C: free space.";
            return false;
        }
        job.CacheReservationBytes = estimate; job.CacheOwner = Owner; job.CacheState = "Reserved"; job.CacheWaitReason = null;
        return true;
    }
    public void Prepare(JobRecord job)
    {
        var expected = DirectoryFor(job);
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!); RejectLink(Path.GetDirectoryName(expected)!);
        if (Directory.Exists(expected)) Verify(job, expected);
        else
        {
            Directory.CreateDirectory(expected);
            File.WriteAllText(Path.Combine(expected, ".owner"), Owner + ":" + job.Id);
        }
        job.WorkPath = expected;
    }
    public bool Release(JobRecord job, out string? error)
    {
        try
        {
            if (job.WorkPath is not null && Directory.Exists(job.WorkPath))
            {
                Verify(job, job.WorkPath);
                // A single level only. A link or unexpected subdirectory is never followed.
                foreach (var file in Directory.EnumerateFileSystemEntries(job.WorkPath))
                {
                    RejectLink(file);
                    if (Directory.Exists(file)) throw new IOException("Unexpected cache subdirectory; cleanup retained it.");
                }
                foreach (var file in Directory.EnumerateFiles(job.WorkPath).Where(p => Path.GetFileName(p) != ".owner")) File.Delete(file);
                File.Delete(Path.Combine(job.WorkPath, ".owner"));
                try { Directory.Delete(job.WorkPath); }
                catch
                {
                    // Keep deletion retryable if the directory itself is held open.
                    if (Directory.Exists(job.WorkPath) && !File.Exists(Path.Combine(job.WorkPath, ".owner")))
                        File.WriteAllText(Path.Combine(job.WorkPath, ".owner"), Owner + ":" + job.Id);
                    throw;
                }
            }
            job.CacheReservationBytes = 0; job.CacheState = "Released"; job.CacheError = null; error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { error = ex.Message; job.CacheState = "Cleanup pending"; job.CacheError = error; return false; }
    }
    public void DeleteEssence(JobRecord job)
    {
        if (job.WorkPath is null || !Directory.Exists(job.WorkPath)) return;
        Verify(job, job.WorkPath);
        foreach (var name in new[] { "video.h264", "audio.wav", "proxy-video.h264", "proxy-audio.wav" })
        {
            var path = Path.Combine(job.WorkPath, name);
            if (File.Exists(path)) { RejectLink(path); File.Delete(path); }
        }
    }
    private void Verify(JobRecord job, string directory)
    {
        if (!Guid.TryParseExact(job.Id, "N", out _) || !string.Equals(Path.GetFullPath(directory), DirectoryFor(job), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cache generation/path ownership mismatch; retained files.");
        RejectLink(Path.GetDirectoryName(directory)!); RejectLink(directory);
        if (File.ReadAllText(Path.Combine(directory, ".owner")) != Owner + ":" + job.Id)
            throw new IOException("Cache ownership marker mismatch; retained files.");
    }
    public long Usage(JobRecord job)
    {
        if (job.WorkPath is null || !Directory.Exists(job.WorkPath)) return 0;
        try { Verify(job, job.WorkPath); return Directory.EnumerateFiles(job.WorkPath).Sum(p => new FileInfo(p).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return job.CacheReservationBytes; }
    }
    public static void RejectLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Refusing a linked cache/artifact path.");
    }
    public void Dispose() => rootLock.Dispose();
}
