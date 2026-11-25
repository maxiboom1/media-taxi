using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class StorageTests
{
    [Fact]
    public void StatisticsSurviveJobRetirementAndCountDestinationsOnce()
    {
        using var temp = new TemporaryFolder();
        using var store = new JobStore(temp.Path);
        var j = new JobRecord { OriginalName = "news.mxf" }; store.Save(j);
        store.Record(j.Id, "processed", "Native"); store.Record(j.Id, "imported", "Native"); store.Record(j.Id, "imported", "Native");
        store.Record("expired", "imported", "Transcoded", DateTimeOffset.UtcNow.AddHours(-25));
        store.Delete(j.Id);
        Assert.Empty(store.Load()); Assert.Equal(1, store.Statistics().Imported); Assert.Equal(1, store.Statistics().NativeImported); Assert.Equal(0, store.Statistics().TranscodedImported);
    }
    [Fact]
    public void ArtifactClaimsCannotBeStolenByNewFilenameGeneration()
    {
        using var temp = new TemporaryFolder(); using var store = new JobStore(temp.Path);
        Assert.True(store.Claim("daily.clip", "yesterday", "clip")); Assert.False(store.Claim("DAILY.clip", "today", "clip"));
        store.Delete("yesterday"); Assert.True(store.Claim("daily.clip", "today", "clip"));
    }
    [Fact]
    public void InstanceLockPreventsSecondWriter()
    {
        using var temp = new TemporaryFolder(); using var first = new JobStore(temp.Path);
        Assert.Throws<IOException>(() => new JobStore(temp.Path));
    }
    [Fact]
    public void FailedEnumerationIsNotAnEmptyAuthoritativeSnapshot()
    {
        using var temp = new TemporaryFolder();
        var options = new TaxiOptions { Folders = new() { Watch = temp.Path, Finished = System.IO.Path.Combine(temp.Path, "unavailable"), Failed = temp.Path } };
        var scan = new FolderScanner(options).Scan(); Assert.False(scan.Complete); Assert.Contains(scan.Health, h => !h.Readable);
    }
    [Fact]
    public async Task PublicationNeverOverwritesDifferentDestinationAndChecksIdenticalResume()
    {
        using var temp = new TemporaryFolder(); var source = System.IO.Path.Combine(temp.Path, "source"); var dest = System.IO.Path.Combine(temp.Path, "destination");
        await File.WriteAllTextAsync(source, "new-content"); await File.WriteAllTextAsync(dest, "existing-banner");
        var hash = await FileOperations.HashAsync(source, default);
        await Assert.ThrowsAsync<IOException>(() => FileOperations.CopyVerifiedAsync(source, dest, dest + ".partial", hash, default));
        Assert.Equal("existing-banner", await File.ReadAllTextAsync(dest));
        File.Delete(dest); await FileOperations.CopyVerifiedAsync(source, dest, dest + ".partial", hash, default);
        await FileOperations.CopyVerifiedAsync(source, dest, dest + ".partial", hash, default);
        Assert.Equal("new-content", await File.ReadAllTextAsync(dest)); Assert.False(File.Exists(dest + ".partial"));
    }
}

public sealed class TemporaryFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "media-taxi-tests-" + Guid.NewGuid().ToString("N"));
    public TemporaryFolder() => Directory.CreateDirectory(Path);
    public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
}
