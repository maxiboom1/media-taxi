using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class ClipPublisherTests
{
    [Fact]
    public async Task PublishAndRecoveryNeedNoRemoteMediaReadHandle()
    {
        using var folder = new TemporaryFolder();
        var source = Path.Combine(folder.Path, "local.clip"); var target = Path.Combine(folder.Path, "remote.clip"); var partial = target + ".owned.partial";
        await File.WriteAllBytesAsync(source, new byte[32768]); var hash = await FileOperations.HashAsync(source, default);
        await ClipPublisher.PublishAsync(source, target, partial, hash, false, default);
        Assert.Equal(new FileInfo(source).Length, new FileInfo(target).Length);
        // Exclusive write-only handle: a readback would fail with a sharing violation.
        using var remoteLock = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
        await ClipPublisher.PublishAsync(source, target, partial, hash, true, default);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task FreshAttemptNeverAdoptsOrOverwritesAnExistingSameSizeFile()
    {
        using var folder = new TemporaryFolder(); var source = Path.Combine(folder.Path, "local.clip"); var target = Path.Combine(folder.Path, "remote.clip");
        await File.WriteAllTextAsync(source, "source"); await File.WriteAllTextAsync(target, "banner");
        await Assert.ThrowsAsync<IOException>(() => ClipPublisher.PublishAsync(source, target, target + ".partial", "unused", false, default));
        Assert.Equal("banner", await File.ReadAllTextAsync(target));
    }

    [Fact]
    public async Task ChangedLocalSourceIsNotPublished()
    {
        using var folder = new TemporaryFolder(); var source = Path.Combine(folder.Path, "local.clip"); var target = Path.Combine(folder.Path, "remote.clip");
        await File.WriteAllTextAsync(source, "changed");
        await Assert.ThrowsAsync<IOException>(() => ClipPublisher.PublishAsync(source, target, target + ".partial", new string('0',64), false, default));
        Assert.False(File.Exists(target)); Assert.False(File.Exists(target + ".partial"));
    }
}
