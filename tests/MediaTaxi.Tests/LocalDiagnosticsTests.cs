using MediaTaxi.Configuration;
using MediaTaxi.Diagnostics;
using MediaTaxi.Domain;
using MediaTaxi.Media;
using MediaTaxi.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MediaTaxi.Tests;

public sealed class LocalDiagnosticsTests
{
    [Fact]
    public void FileLogsRotateWithinBoundsAndUnavailableLogDiskDoesNotThrowIntoIngest()
    {
        using var temp = new TemporaryFolder();
        var options = new FileLogOptions { Directory = Path.Combine(temp.Path, "logs"), MaxFiles = 3, MaxMiB = 1 };
        using (var logs = new RollingFileLogger(options))
        {
            var logger = logs.CreateLogger("test");
            for (var n = 0; n < 300; n++) logger.LogInformation("{Text}", new string('x', 15000));
        }
        var files = Directory.GetFiles(options.Directory);
        Assert.Equal(3, files.Length); Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1048576));
        var blocked = Path.Combine(temp.Path, "blocked"); File.WriteAllText(blocked, "file in place of log directory");
        var unavailable = new RollingFileLogger(new() { Directory = blocked });
        unavailable.CreateLogger("test").LogError("Keep ingest alive"); unavailable.Dispose();
        Assert.NotNull(unavailable.Health.Error); Assert.Equal(1, unavailable.Health.DroppedMessages);
    }

    [Fact]
    public void StatisticsReadDoesNotPerformRetentionWrites()
    {
        using var temp = new TemporaryFolder(); using var store = new JobStore(temp.Path);
        store.SaveWithEvent(new JobRecord { Mode = "Native" }, "processed", DateTimeOffset.UtcNow.AddHours(-25));
        using var connection = new SqliteConnection($"Data Source={Path.Combine(temp.Path, "media-taxi.db")};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = "CREATE TRIGGER reject_delete BEFORE DELETE ON statistics BEGIN SELECT RAISE(ABORT, 'GET must not delete'); END;";
        command.ExecuteNonQuery(); Assert.Equal(0, store.Statistics().Processed);
        Assert.Throws<SqliteException>(() => store.PruneStatistics());
    }

    [Fact]
    public async Task NativeAudioMismatchRemainsStrictAndReportsExactSampleDelta()
    {
        using var temp = new TemporaryFolder(); var audio = Path.Combine(temp.Path, "mismatch.wav");
        await File.WriteAllBytesAsync(audio, PcmWave.Header(961, 2).Concat(new byte[961 * 8]).ToArray());
        var prepared = new PreparedMedia("unused.h264", audio, 1, 2, 0, true, "");
        var error = await Assert.ThrowsAsync<MediaMismatchException>(() => FlatClip.WriteAsync(prepared, Path.Combine(temp.Path, "clip.clip"), Guid.NewGuid().ToString("N"), default));
        Assert.Contains("expected 960", error.Message); Assert.Contains("got 961", error.Message); Assert.Contains("delta 1", error.Message);
    }
}
