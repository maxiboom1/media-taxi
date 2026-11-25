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
        Assert.Single(options.Destinations); Assert.Equal(2, options.Workers.Native); Assert.False(options.Media.AllowNormalization);
        Assert.Equal(System.IO.Path.Combine(t.Path, "tools", "ffmpeg.exe"), options.Media.FfmpegPath);
    }
    [Fact]
    public void OutputInsideSourceIsRejected()
    {
        using var t = new TemporaryFolder();
        var config = new TaxiOptions { Folders = new() { Watch = t.Path + "/watch", Finished = t.Path + "/finished", Failed = t.Path + "/failed", Abekas = t.Path + "/watch/output", Proxy = t.Path + "/proxy", Work = t.Path + "/work", State = t.Path + "/state" }, Destinations = [new() { Id = "one", Path = t.Path + "/destination" }] };
        Assert.Throws<InvalidDataException>(() => OptionsLoader.Validate(config));
    }
}
