using System.Text.Json;
using MediaTaxi.Configuration;
using MediaTaxi.Media;
using MediaTaxi.Domain;
using MediaTaxi.Engine;
using MediaTaxi.Inventory;
using MediaTaxi.Storage;
using Microsoft.Extensions.Logging.Abstractions;

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
        var inspection = await service.InspectAsync(mxf, default); Assert.True(inspection.Native);
        var referenceVideo = Path.Combine(directory, "reference.h264"); var referenceAudio = Path.Combine(directory, "reference.wav");
        await runner.RunAsync(options.Media.FfmpegPath, ["-v", "error", "-i", mxf, "-map", "0:v:0", "-an", "-c:v", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", referenceVideo,
            "-map", "0:a:0", "-vn", "-af", "pan=8c|c0=c0|c1=c1", "-c:a", "pcm_s32le", referenceAudio], 120, default);
        var prepared = new PreparedMedia(referenceVideo, referenceAudio, 10, 8, inspection.Probe!.TimecodeStartFrames, true, inspection.Summary);
        Assert.True(prepared.Native); Assert.Equal(10, prepared.FrameCount); Assert.Equal(29452, prepared.TimecodeStartFrames);
        Assert.Equal(9600, PcmWave.ReadInfo(prepared.AudioPath).Samples);
        var streamingWork = Path.Combine(directory, "stream"); Directory.CreateDirectory(streamingWork);
        var streamingPath = Path.Combine(directory, "stream.clip");
        var streamed = await service.WrapSourceAsync(mxf, await service.InspectAsync(mxf, default), streamingWork, streamingPath, Guid.NewGuid().ToString("N"), null, default);
        Assert.Equal(10, streamed.FrameCount);
        Assert.DoesNotContain(Directory.EnumerateFiles(streamingWork), f => f.EndsWith(".h264", StringComparison.OrdinalIgnoreCase));
        var streamedMedia = await ClipTestSupport.ExtractAsync(streamingPath, Path.Combine(directory, "stream-extract"), default);
        Assert.Equal(await File.ReadAllBytesAsync(prepared.VideoPath), await File.ReadAllBytesAsync(streamedMedia.VideoPath));
        var preparedPcm = await File.ReadAllBytesAsync(prepared.AudioPath); var streamedPcm = await File.ReadAllBytesAsync(streamedMedia.AudioPath);
        Assert.Equal(preparedPcm.AsSpan((int)PcmWave.ReadInfo(prepared.AudioPath).DataOffset).ToArray(), streamedPcm.AsSpan((int)PcmWave.ReadInfo(streamedMedia.AudioPath).DataOffset).ToArray());
        Assert.Equal(29452, streamedMedia.TimecodeStartFrames);
        Assert.NotNull(streamed.Artifact.HeaderSha256); Assert.NotNull(streamed.Artifact.PayloadSha256);
        var published = Path.Combine(directory, "publish.clip");
        await Storage.ClipPublisher.PublishAsync(streamingPath, published, published + ".partial", "", false, default, null, streamed.Artifact.HeaderSha256, streamed.Artifact.PayloadSha256);
        Assert.Equal(await File.ReadAllBytesAsync(streamingPath), await File.ReadAllBytesAsync(published));
        var jobId = Guid.NewGuid().ToString("N"); var clip = Path.Combine(directory, "result.clip");
        await FlatClip.WriteAsync(prepared, clip, jobId, CancellationToken.None);
        var recovered = await ClipTestSupport.ExtractAsync(clip, Path.Combine(directory, "recovered"), CancellationToken.None);
        Assert.Equal(await File.ReadAllBytesAsync(prepared.VideoPath), await File.ReadAllBytesAsync(recovered.VideoPath));
        var originalWave = PcmWave.ReadInfo(prepared.AudioPath); var recoveredWave = PcmWave.ReadInfo(recovered.AudioPath);
        await using (var one = File.OpenRead(prepared.AudioPath))
        await using (var two = File.OpenRead(recovered.AudioPath))
        {
            one.Position = originalWave.DataOffset; two.Position = recoveredWave.DataOffset;
            var originalPcm = new byte[(int)originalWave.DataBytes]; var recoveredPcm = new byte[(int)recoveredWave.DataBytes];
            await one.ReadExactlyAsync(originalPcm); await two.ReadExactlyAsync(recoveredPcm); Assert.Equal(originalPcm, recoveredPcm);
        }
        var sourceProxy = Path.Combine(directory, "source-proxy.mp4");
        File.Delete(streamingPath);
        await service.CreateSourceProxyAsync(mxf, await service.InspectAsync(mxf, default), 10, sourceProxy, jobId, null, default);
        Assert.True(await service.OwnsProxyAsync(sourceProxy, jobId, default));
        var sourceProbe = await runner.RunAsync(options.Media.FfprobePath, ["-v", "error", "-count_frames", "-show_entries", "stream=nb_read_frames,codec_type,channels", "-of", "json", sourceProxy], 30, default);
        using var sourceJson = JsonDocument.Parse(sourceProbe.StandardOutput);
        Assert.Equal("10", sourceJson.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString());
        Assert.Equal(2, sourceJson.RootElement.GetProperty("streams")[1].GetProperty("channels").GetInt32());
        options.Proxy.Codec = "h265";
        var sourceHevc = Path.Combine(directory, "source-hevc.mp4");
        await service.CreateSourceProxyAsync(mxf, await service.InspectAsync(mxf, default), 10, sourceHevc, jobId, null, default);
        var sourceHevcProbe = await runner.RunAsync(options.Media.FfprobePath, ["-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=codec_name,nb_read_frames", "-of", "json", sourceHevc], 30, default);
        using var sourceHevcJson = JsonDocument.Parse(sourceHevcProbe.StandardOutput);
        Assert.Equal("hevc", sourceHevcJson.RootElement.GetProperty("streams")[0].GetProperty("codec_name").GetString());
        Assert.Equal("10", sourceHevcJson.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString());
        // Keep the inherited handle actively seeking through loops while moving/deleting its name.
        var handleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inherited = runner.RunSourceAsync(options.Media.FfmpegPath, ["-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-stats_period", "0.05", "-re", "-stream_loop", "-1", "-i", "handle:", "-t", "1", "-f", "null", "-"], mxf, 30, default, 1, _ => handleStarted.TrySetResult());
        await handleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(inherited.IsCompleted);
        File.Move(mxf, mxf + ".moved"); File.Delete(mxf + ".moved");
        await inherited;
    }

    [RequiresFfmpeg]
    public async Task MismatchRequiresOptInAndNormalizationProducesExactAvciUnits()
    {
        var options = Options(); var runner = new MediaProcessRunner(); var source = Path.Combine(directory, "small.mp4");
        await runner.RunAsync(options.Media.FfmpegPath,
            ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25",
             "-t", "0.2", "-an", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv420p", source], 120, CancellationToken.None);
        var service = new MediaService(options);
        var inspection = await service.InspectAsync(source, CancellationToken.None);
        Assert.False(inspection.Native); Assert.Contains("h264", inspection.Summary); Assert.Contains("320x180", inspection.Summary);
        await Assert.ThrowsAsync<MediaMismatchException>(() => service.WrapSourceAsync(source, inspection, Path.Combine(directory, "disabled"), Path.Combine(directory, "disabled.clip"), Guid.NewGuid().ToString("N"), null, default));
        options.Media.AllowNormalization = true;
        Assert.True((await service.CheckHealthAsync(CancellationToken.None)).Ready);
        var wrapped = await service.WrapSourceAsync(source, inspection, Path.Combine(directory, "enabled"), Path.Combine(directory, "normalized.clip"), Guid.NewGuid().ToString("N"), null, default);
        var prepared = await ClipTestSupport.ExtractAsync(wrapped.Artifact.Path, Path.Combine(directory, "extracted"), default);
        Assert.Equal(10, prepared.FrameCount);
        Assert.Equal(10, await ClipTestSupport.ValidateFileAsync(prepared.VideoPath, CancellationToken.None));
        Assert.Equal(9600, PcmWave.ReadInfo(prepared.AudioPath).Samples);
        var sourceProxy = Path.Combine(directory, "normalized-source.mp4");
        await service.CreateSourceProxyAsync(source, await service.InspectAsync(source, default), prepared.FrameCount, sourceProxy, Guid.NewGuid().ToString("N"), null, default);
        var result = await runner.RunAsync(options.Media.FfprobePath, ["-v", "error", "-count_frames", "-show_entries", "stream=codec_type,nb_read_frames,channels", "-of", "json", sourceProxy], 30, default);
        using var normalizedJson = JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("10", normalizedJson.RootElement.GetProperty("streams")[0].GetProperty("nb_read_frames").GetString());
        Assert.Equal(2, normalizedJson.RootElement.GetProperty("streams")[1].GetProperty("channels").GetInt32());
    }

    [Fact]
    public async Task TimeoutTerminatesChildProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        var runner = new MediaProcessRunner();
        await Assert.ThrowsAsync<TimeoutException>(() => runner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], 1, CancellationToken.None));
    }

    [RequiresFfmpeg]
    public async Task RealMediaServiceRunsThroughEngineDeliveryArchivalAndSourceProxy()
    {
        var options = Options();
        string Folder(string name) { var path = Path.Combine(directory, name); Directory.CreateDirectory(path); return path; }
        options.Folders = new() { Watch = Folder("watch"), Finished = Folder("finished"), Failed = Folder("failed"), Work = Folder("work"), Proxy = Folder("proxy"), State = Folder("state") };
        options.Cache.MinFreeGiB = 0; options.Timing.StabilitySeconds = 0; options.Timing.ReconcileSeconds = .02;
        options.Proxy.Channels = [7, 8];
        options.Destinations = [new() { Id = "test", Name = "Test", Host = "127.0.0.1", Path = Folder("destination") }];
        var source = Path.Combine(options.Folders.Watch, "engine.mxf"); var runner = new MediaProcessRunner();
        await runner.RunAsync(options.Media.FfmpegPath,
            ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=50", "-f", "lavfi", "-i", "sine=frequency=700:sample_rate=48000",
             "-t", "0.2", "-c:v", "libx264", "-threads", "2", "-pix_fmt", "yuv422p10le", "-profile:v", "high422", "-x264-params", "avcintra-class=100",
             "-c:a", "pcm_s24le", "-ac", "2", "-f", "mxf", source], 120, default);
        using var store = new JobStore(options.Folders.State);
        using var inventory = new InventoryService(options, NullLogger<InventoryService>.Instance);
        using var engine = new JobEngine(options, store, new MediaService(options), inventory, NullLogger<JobEngine>.Instance);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await engine.ReconcileAsync(default);
            if (store.Load().SingleOrDefault()?.Stage == JobStage.Done) break;
            await Task.Delay(30);
        }
        var job = Assert.Single(store.Load()); Assert.Equal(JobStage.Done, job.Stage); Assert.Null(job.Error);
        Assert.Equal(ProxyState.Ready, job.ProxyStatus); Assert.Equal(CopyState.Succeeded, job.Destinations[0].CopyStatus);
        Assert.True(File.Exists(Path.Combine(options.Folders.Finished, "engine.mxf"))); Assert.False(File.Exists(job.ArtifactPath));
        Assert.Equal(CacheLifecycle.Released, job.CacheState); Assert.Equal(1, engine.GetStatistics().Imported);
        var info = await FlatClip.ReadInfoAsync(Path.Combine(options.Destinations[0].Path, "engine.clip")); Assert.Equal(10, info.FrameCount);
        var pcm = Path.Combine(directory, "silent-proxy.pcm");
        await runner.RunAsync(options.Media.FfmpegPath, ["-v", "error", "-i", job.ProxyPath!, "-map", "0:a:0", "-f", "s16le", pcm], 30, default);
        var samples = await File.ReadAllBytesAsync(pcm); Assert.NotEmpty(samples); Assert.All(samples, sample => Assert.Equal(0, sample));
    }
}
