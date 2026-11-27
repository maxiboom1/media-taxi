using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class WorkCacheTests
{
    [Fact]
    public void CapacityWaitQuotaReleaseAndLockedCleanupKeepReservationsHonest()
    {
        using var temp = new TemporaryFolder();
        var options = new TaxiOptions { Folders = new() { Work = Path.Combine(temp.Path, "cache"), State = Path.Combine(temp.Path, "state") }, Cache = new() { MaxGiB = .05, MinFreeGiB = 0 } };
        long free = 1L << 40;
        using var cache = new WorkCache(options, () => free);
        var first = new JobRecord(); var second = new JobRecord();
        Assert.True(cache.Reserve(first, [first, second])); cache.Prepare(first);
        Assert.False(cache.Reserve(second, [first, second])); Assert.Contains("capacity", second.CacheWaitReason);
        var clip = Path.Combine(first.WorkPath!, "delivery.clip"); File.WriteAllText(clip, "temporary");
        using (var held = File.Open(clip, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(cache.Release(first, out var error)); Assert.NotNull(error);
            Assert.True(first.CacheReservationBytes > 0); Assert.Equal("Cleanup pending", first.CacheState);
        }
        Assert.True(cache.Release(first, out _)); Assert.Equal(0, first.CacheReservationBytes);
        Assert.True(cache.Reserve(second, [first, second]));
        free = 0; var third = new JobRecord(); Assert.False(cache.Reserve(third, [first, third]));
        var huge = new JobRecord { Inspection = new(true, "", new(60, 50, 2, true, true, false, 0, "")) };
        Assert.Contains("Cache limit", Assert.Throws<IOException>(() => cache.Reserve(huge, [huge])).Message);
    }
    [Fact]
    public void RootIsExclusiveAndUnknownFilesOutsideOwnedDirectoriesSurvive()
    {
        using var temp = new TemporaryFolder();
        var options = new TaxiOptions { Folders = new() { Work = temp.Path, State = Path.Combine(temp.Path, "state") } };
        using var cache = new WorkCache(options);
        var foreign = Path.Combine(temp.Path, "keep.clip"); File.WriteAllText(foreign, "not owned");
        Assert.Throws<IOException>(() => new WorkCache(options));
        var job = new JobRecord { WorkPath = temp.Path };
        Assert.False(cache.Release(job, out _)); Assert.True(File.Exists(foreign));
    }
    [Fact]
    public void NormalizationPeakIncludesElementaryVideoAndOtherJobsOutstandingReservations()
    {
        using var temp = new TemporaryFolder();
        var options = new TaxiOptions { Folders = new() { Work = temp.Path, State = Path.Combine(temp.Path, "state") }, Cache = new() { MinFreeGiB = 1 } };
        long free = 1100L * 1024 * 1024;
        using var cache = new WorkCache(options, () => free);
        var native = new JobRecord { Inspection = new(true, "") }; var normalized = new JobRecord { Inspection = new(false, "") };
        Assert.True(cache.Estimate(normalized) > cache.Estimate(native));
        Assert.True(cache.Reserve(native, [native]));
        free = 1050L * 1024 * 1024;
        Assert.False(cache.Reserve(normalized, [native, normalized]));
    }
}
