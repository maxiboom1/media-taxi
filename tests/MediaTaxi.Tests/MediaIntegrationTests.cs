using System.Text.Json;
using MediaTaxi.Configuration;
using MediaTaxi.Media;

namespace MediaTaxi.Tests;

public sealed class RequiresFfmpegAttribute : FactAttribute
{
    public RequiresFfmpegAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("MEDIATAXI_FFMPEG"))
            || !File.Exists(Environment.GetEnvironmentVariable("MEDIATAXI_FFPROBE")))
            Skip = "Set MEDIATAXI_FFMPEG and MEDIATAXI_FFPROBE to run real codec integration tests";
    }
}

public sealed class MediaIntegrationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "media-taxi-codec-" + Guid.NewGuid().ToString("N"));
    public MediaIntegrationTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    private static TaxiOptions Options(bool allowNormalization = false) => new()
    {
        Media = new MediaOptions
        {
            FfmpegPath = Environment.GetEnvironmentVariable("MEDIATAXI_FFMPEG")!,
            FfprobePath = Environment.GetEnvironmentVariable("MEDIATAXI_FFPROBE")!,
            AllowNormalization = allowNormalization, Threads = 2, TimeoutSeconds = 120
        }
    };

    [RequiresFfmpeg]
    public async Task NativeMxfWrapExtractAndProxyPreserveFramesAudioTimecodeAndOwnership()
    {
        var options = Options(); var runner = new MediaProcessRunner();
        var mxf = Path.Combine(directory, "source.mxf");
        await runner.RunAsync(options.Media.FfmpegPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=50",
             "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "0.2", "-map", "0:v:0", "-map", "1:a:0",
             "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv422p10le", "-profile:v", "high422", "-x264-params", "avcintra-class=100",
             "-c:a", "pcm_s24le", "-ac", "2", "-timecode", "00:09:49:02", "-f", "mxf", mxf], 120, CancellationToken.None);
        var service = new MediaService(options);
        Assert.True((await service.CheckHealthAsync(CancellationToken.None)).Ready);
        Assert.True(await service.IsNativeAsync(mxf, CancellationToken.None));
        var prepared = await service.PrepareAsync(mxf, Path.Combine(directory, "prepare"), null, CancellationToken.None);
        Assert.True(prepared.Native); Assert.Equal(10, prepared.FrameCount); Assert.Equal(29452, prepared.TimecodeStartFrames);
        Assert.Equal(9600, PcmWave.ReadInfo(prepared.AudioPath).Samples);
        var jobId = Guid.NewGuid().ToString("N"); var clip = Path.Combine(directory, "result.clip");
        await service.WrapAsync(prepared, clip, jobId, CancellationToken.None);
        var recovered = await service.ExtractClipAsync(clip, Path.Combine(directory, "recovered"), CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(prepared.VideoPath), await File.ReadAllBytesAsync(recovered.VideoPath));
        var originalWave = PcmWave.ReadInfo(prepared.AudioPath); var recoveredWave = PcmWave.ReadInfo(recovered.AudioPath);
        await using (var one = File.OpenRead(prepared.AudioPath))
        await using (var two = File.OpenRead(recovered.AudioPath))
        {
            one.Position = originalWave.DataOffset; two.Position = recoveredWave.DataOffset;
            var originalPcm = new byte[(int)originalWave.DataBytes]; var recoveredPcm = new byte[(int)recoveredWave.DataBytes];
            await one.ReadExactlyAsync(originalPcm); await two.ReadExactlyAsync(recoveredPcm); Assert.Equal(originalPcm, recoveredPcm);
        }
        var proxy = Path.Combine(directory, "result.mp4");
        await service.CreateProxyAsync(recovered, proxy, jobId, null, CancellationToken.None);
        Assert.True(await service.OwnsProxyAsync(proxy, jobId, CancellationToken.None));
        Assert.False(await service.OwnsProxyAsync(proxy, Guid.NewGuid().ToString("N"), CancellationToken.None));
        var probe = await runner.RunAsync(options.Media.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=nb_read_frames,width,height", "-of", "json", proxy], 120, CancellationToken.None);
        using var json = JsonDocument.Parse(probe.StandardOutput); var video = json.RootElement.GetProperty("streams")[0];
        Assert.Equal("10", video.GetProperty("nb_read_frames").GetString());
        Assert.Equal(640, video.GetProperty("width").GetInt32()); Assert.Equal(360, video.GetProperty("height").GetInt32());
        options.Proxy.Codec = "h265";
        Assert.True((await service.CheckHealthAsync(CancellationToken.None)).Ready);
        var hevc = Path.Combine(directory, "result-hevc.mp4");
        await service.CreateProxyAsync(recovered, hevc, jobId, null, CancellationToken.None);
        Assert.True(await service.OwnsProxyAsync(hevc, jobId, CancellationToken.None));
        var hevcProbe = await runner.RunAsync(options.Media.FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,nb_read_frames", "-of", "json", hevc], 120, CancellationToken.None);
        using var hevcJson = JsonDocument.Parse(hevcProbe.StandardOutput);
        Assert.Equal("hevc", hevcJson.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString());
        Assert.Equal("10", hevcJson.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString());
    }

    [RequiresFfmpeg]
    public async Task MismatchRequiresOptInAndNormalizationProducesExactAvciUnits()
    {
        var options = Options(); var runner = new MediaProcessRunner(); var source = Path.Combine(directory, "small.mp4");
        await runner.RunAsync(options.Media.FfmpegPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
             "-t", "0.2", "-an", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", source], 120, CancellationToken.None);
        var service = new MediaService(options);
        Assert.False(await service.IsNativeAsync(source, CancellationToken.None));
        var inspection = await service.InspectAsync(source, CancellationToken.None);
        Assert.False(inspection.Native); Assert.Contains("h264", inspection.Summary); Assert.Contains("320x180", inspection.Summary);
        await Assert.ThrowsAsync<MediaMismatchException>(() => service.PrepareAsync(source, Path.Combine(directory, "disabled"), null, CancellationToken.None));
        options.Media.AllowNormalization = true;
        Assert.True((await service.CheckHealthAsync(CancellationToken.None)).Ready);
        var prepared = await service.PrepareAsync(source, Path.Combine(directory, "enabled"), null, CancellationToken.None);
        Assert.False(prepared.Native); Assert.Equal(10, prepared.FrameCount);
        Assert.Equal(10, await AvciValidator.ValidateFileAsync(prepared.VideoPath, CancellationToken.None));
        Assert.Equal(9600, PcmWave.ReadInfo(prepared.AudioPath).Samples);
        var artifact = await service.WrapAsync(prepared, Path.Combine(directory, "normalized.clip"), Guid.NewGuid().ToString("N"), CancellationToken.None);
        Assert.True(File.Exists(artifact.Path));
    }

    [Fact]
    public async Task TimeoutTerminatesChildProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new MediaProcessRunner();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], 1, CancellationToken.None));
    }
}
