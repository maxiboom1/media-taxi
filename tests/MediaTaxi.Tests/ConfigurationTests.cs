using MediaTaxi.Configuration;

namespace MediaTaxi.Tests;

public sealed class ConfigurationTests
{
    [Fact]
    public void IniCommentsAndPathsAreResolvedAgainstConfigNotWorkingDirectory()
    {
        using var t = new TemporaryFolder();
        var config = System.IO.Path.Combine(t.Path, "media-taxi.ini");
        File.WriteAllText(config, """
            ; operator comment
            [folders]
            watch=watch
            finished=watch/finished
            failed=watch/failed
            abekas=abekas
            proxy=proxy
            work=work
            state=state
            [destinations:one]
            path=destination
            host=127.0.0.1
            """);
        var options = OptionsLoader.Load(config);
        Assert.Equal(System.IO.Path.Combine(t.Path, "watch"), options.Folders.Watch);
        Assert.Single(options.Destinations); Assert.Equal(1, options.Workers.Native); Assert.False(options.Media.AllowNormalization);
        Assert.Equal(System.IO.Path.Combine(t.Path, "tools", "ffmpeg.exe"), options.Media.FfmpegPath);
        Assert.Contains("obsolete", Assert.Single(options.Warnings));
        Assert.True(options.Proxy.DeferWhileHiresBusy); Assert.Equal(64, options.Cache.MaxGiB);
        Assert.Equal(30, options.Timing.SourceScanTimeoutSeconds); Assert.Equal(10, options.Timing.HealthCheckTimeoutSeconds);
        Assert.Equal(60, options.Timing.HealthCheckIntervalSeconds); Assert.Equal(300, options.Timing.StalledSourceWarningSeconds);
        Assert.True(options.FileLog.Enabled); Assert.Equal(Path.Combine(t.Path, "runtime", "logs"), options.FileLog.Directory);
        Assert.Equal(5, options.FileLog.MaxFiles); Assert.Equal(10, options.FileLog.MaxMiB);
    }
    [Theory]
    [InlineData("timing", "source_scan_timeout_seconds", "0")]
    [InlineData("timing", "health_check_timeout_seconds", "301")]
    [InlineData("timing", "health_check_interval_seconds", "4")]
    [InlineData("timing", "stalled_source_warning_seconds", "86401")]
    [InlineData("logging", "max_files", "0")]
    [InlineData("logging", "max_mib", "101")]
    public void InvalidDiagnosticAndDeadlineSettingsFailClearly(string section, string key, string value)
    {
        using var temporary = new TemporaryFolder(); var path = Path.Combine(temporary.Path, "media-taxi.ini");
        File.WriteAllText(path, $"[folders]\nwatch=watch\nfinished=finished\nfailed=failed\nproxy=proxy\nstate=state\n[{section}]\n{key}={value}\n");
        Assert.Contains(key, Assert.Throws<InvalidDataException>(() => OptionsLoader.Load(path)).Message);
    }
    [Fact]
    public void OutputInsideSourceIsRejected()
    {
        using var t = new TemporaryFolder();
        var config = new TaxiOptions { Folders = new() { Watch = t.Path + "/watch", Finished = t.Path + "/finished", Failed = t.Path + "/failed", Proxy = t.Path + "/watch/output", Work = t.Path + "/work", State = t.Path + "/state" }, Destinations = [new() { Id = "one", Path = t.Path + "/destination" }] };
        Assert.Throws<InvalidDataException>(() => OptionsLoader.Validate(config));
    }
    [Fact]
    public void LogsCannotBecomeIncomingMedia()
    {
        using var fixture = new RecoveryFixture(); fixture.Options.FileLog.Directory = Path.Combine(fixture.Options.Folders.Watch, "logs");
        Assert.Contains("logging:directory", Assert.Throws<InvalidDataException>(() => OptionsLoader.Validate(fixture.Options)).Message);
    }
}
