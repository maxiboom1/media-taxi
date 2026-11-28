using System.Security.Cryptography;
using System.Text.Json;
using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class DashboardProgressTests
{
    [Theory]
    [InlineData(91.99, "1:31")]
    [InlineData(.2, "0:00")]
    [InlineData(3601, "60:01")]
    public void SourceFormatUsesOriginalMetadataAndMinuteSecondDuration(double seconds, string duration)
    {
        var probe = new MediaProbe(seconds, 50, 2, true, true, false, 0, "mxf / h264 High 4:2:2 Intra 1920x1080 50fps / 2ch");
        Assert.Equal("MXF/ Intra 1920x1080 50fps / 2ch / Dur: " + duration, SourceFormatLabel.Format(probe, ""));
        var other = probe with { Rate = 25, Native = false, Summary = "mov,mp4,m4a,3gp,3g2,mj2 / hevc Main 1280x720 25fps / 2ch" };
        Assert.Contains("MOV/MP4/ HEVC 1280x720 25fps", SourceFormatLabel.Format(other, ""));
        Assert.Equal("—", SourceFormatLabel.Format(null, ""));
    }

    [Fact]
    public async Task AllViewCombinesTrackedSourcesWithoutChangingTheirOrderOrRetention()
    {
        using var fixture = new RecoveryFixture();
        var older = fixture.Seed("old.mxf", SourceLocation.Finished, JobStage.Existing); older.Managed = false; older.FirstSeenUtc = DateTimeOffset.UtcNow.AddHours(-4); fixture.Store.Save(older);
        var newer = fixture.Seed("new.mxf", SourceLocation.Watch, JobStage.Waiting);
        using var engine = fixture.CreateEngine();
        var all = engine.GetJobs("all"); Assert.Equal(new[] { newer.Id, older.Id }, all.Jobs.Select(j => j.Id));
        Assert.Single(engine.GetJobs("active").Jobs); Assert.Single(engine.GetJobs("older").Jobs);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task CopyProgressCountsCompletedWritesAndStaysBelowPublicationCompletion()
    {
        using var temp = new TemporaryFolder(); var source = Path.Combine(temp.Path, "source.clip");
        var bytes = new byte[4 * 1024 * 1024 + 71]; Random.Shared.NextBytes(bytes); await File.WriteAllBytesAsync(source, bytes);
        var target = Path.Combine(temp.Path, "target.clip"); var values = new List<double>();
        await ClipPublisher.PublishAsync(source, target, target + ".partial", Convert.ToHexStringLower(SHA256.HashData(bytes)), false, default, progress: value =>
        {
            Assert.False(File.Exists(target)); Assert.InRange(value, 0, 99); values.Add(value);
        });
        Assert.NotEmpty(values); Assert.Equal(99, values[^1]); Assert.Equal(values.Order(), values); Assert.Equal(bytes, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public void DestinationProgressIsTransientButFullErrorsSurviveCheckpoints()
    {
        var destination = new DestinationRecord { CopyStatus = CopyState.InProgress, CopyProgress = 42, Error = "short", ErrorDetail = new string('x', 3000) };
        var json = JsonSerializer.Serialize(destination, JobStore.JsonOptions);
        Assert.DoesNotContain("copyProgress", json);
        var restored = JsonSerializer.Deserialize<DestinationRecord>(json, JobStore.JsonOptions)!;
        Assert.Null(restored.CopyProgress); Assert.Equal(destination.ErrorDetail, restored.ErrorDetail);
    }
}
