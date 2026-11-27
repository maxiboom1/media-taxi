using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;

namespace MediaTaxi.Media;

public sealed partial class MediaService(TaxiOptions options) : IMediaService
{
    private readonly MediaProcessRunner runner = new();
    public async Task<MediaInspection> InspectAsync(string source, CancellationToken cancellationToken)
    {
        var probe = await new MediaInspector(options, runner).ProbeAsync(source, cancellationToken);
        return new MediaInspection(probe.Native, probe.Summary, probe);
    }

    private async Task<PreparedMedia> PrepareInspectedAsync(string source, MediaProbe probe, string workDirectory, Action<double>? progress, CancellationToken cancellationToken)
    {
        if (!probe.Native && !options.Media.AllowNormalization)
            throw new MediaMismatchException("Format mismatch: AVC-Intra100 1080p50 required and incoming normalization is disabled");
        Directory.CreateDirectory(workDirectory);
        var video = Path.Combine(workDirectory, "video.h264");
        var audio = Path.Combine(workDirectory, "audio.wav");
        if (File.Exists(video) || File.Exists(audio)) throw new IOException("Preparation work directory already contains media");
        var videoArguments = CommonArguments();
        videoArguments.AddRange(["-i", source, "-map", "0:v:0", "-an", "-vf", NormalizationFilter(probe),
            "-c:v", "libx264", "-pix_fmt", "yuv422p10le", "-profile:v", "high422", "-x264-params", "avcintra-class=100",
            "-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture), "-fps_mode", "cfr", "-f", "h264", video]);
        await runner.RunAsync(options.Media.FfmpegPath, videoArguments, options.Media.TimeoutSeconds, cancellationToken, probe.Duration, progress);
        var bytes = new FileInfo(video).Length;
        if (bytes <= 0 || bytes % AvciValidator.FrameBytes != 0)
            throw new MediaMismatchException("Video does not have the tested AVC-Intra100 fixed frame size");
        var frames = bytes / AvciValidator.FrameBytes;
        var firstFrame = new byte[AvciValidator.FrameBytes];
        await using (var stream = File.OpenRead(video)) await stream.ReadExactlyAsync(firstFrame, cancellationToken);
        AvciValidator.ValidateFrame(firstFrame);
        if (probe.HasAudio)
        {
            var pan = "pan=8c|" + string.Join('|', Enumerable.Range(0, probe.AudioChannels).Select(i => $"c{i}=c{i}")) +
                $",aresample=48000,apad,atrim=end_sample={frames * 960}";
            var audioArguments = CommonArguments();
            audioArguments.AddRange(["-i", source, "-map", "0:a:0", "-vn", "-af", pan, "-c:a", "pcm_s32le", "-rf64", "auto", audio]);
            await runner.RunAsync(options.Media.FfmpegPath, audioArguments, options.Media.TimeoutSeconds, cancellationToken);
            var info = PcmWave.ReadInfo(audio);
            if (info.Samples != frames * 960)
                throw new MediaMismatchException($"Audio/video duration mismatch: expected {frames * 960} PCM samples, got {info.Samples}; delta {info.Samples - frames * 960} samples (strict matching)");
            await QuantizeAudioAsync(audio, info, cancellationToken);
        }
        else await WriteSilenceAsync(audio, frames * 960, cancellationToken);
        progress?.Invoke(100);
        return new PreparedMedia(video, audio, frames, 8, probe.TimecodeStartFrames, false, probe.Summary);
    }

    public async Task<bool> OwnsProxyAsync(string proxy, string jobId, CancellationToken cancellationToken)
    {
        if (!File.Exists(proxy)) return false;
        var result = await runner.RunAsync(options.Media.FfprobePath,
            ["-v", "error", "-show_entries", "format_tags=media_taxi_job_id", "-of", "json", proxy], 30, cancellationToken);
        using var json = JsonDocument.Parse(result.StandardOutput);
        return json.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("tags", out var tags)
            && tags.TryGetProperty("media_taxi_job_id", out var id) && id.GetString() == Guid.Parse(jobId).ToString("N");
    }

    public async Task<MediaHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var ffmpeg = "Unavailable"; var ffprobe = "Unavailable";
        try
        {
            var one = await runner.RunAsync(options.Media.FfmpegPath, ["-version"], 15, cancellationToken);
            ffmpeg = one.StandardOutput.Split('\n')[0].Trim();
            var two = await runner.RunAsync(options.Media.FfprobePath, ["-version"], 15, cancellationToken);
            ffprobe = two.StandardOutput.Split('\n')[0].Trim();
            var encoders = await runner.RunAsync(options.Media.FfmpegPath, ["-hide_banner", "-encoders"], 15, cancellationToken);
            var protocols = await runner.RunAsync(options.Media.FfmpegPath, ["-hide_banner", "-protocols"], 15, cancellationToken);
            if (OperatingSystem.IsWindows() && !protocols.StandardOutput.Split('\n').Any(l => l.Trim() == "handle"))
                return new(false, ffmpeg, ffprobe, [], "FFmpeg requires the seekable Windows handle: input protocol.");
            var wanted = new List<string> { "libx264", "aac", "pcm_s32le" };
            if (options.Proxy.Codec == "h265") wanted.Add("libx265");
            var found = wanted.Where(x => encoders.StandardOutput.Contains(x, StringComparison.Ordinal)).ToArray();
            var missing = wanted.Except(found).ToArray();
            if (options.Media.AllowNormalization && missing.Length == 0)
            {
                var avci = await runner.RunAsync(options.Media.FfmpegPath, ["-hide_banner", "-h", "encoder=libx264"], 15, cancellationToken);
                if (!avci.StandardOutput.Contains("yuv422p10le", StringComparison.Ordinal)
                    || !avci.StandardOutput.Contains("avcintra-class", StringComparison.Ordinal))
                    return new MediaHealth(false, ffmpeg, ffprobe, found, "libx264 lacks required 10-bit 4:2:2 AVC-Intra support");
            }
            return new MediaHealth(missing.Length == 0, ffmpeg, ffprobe, found.Concat(["pipe", "handle"]).ToArray(), missing.Length == 0 ? null : "Missing encoders: " + string.Join(", ", missing));
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or TimeoutException or InvalidOperationException)
        { return new MediaHealth(false, ffmpeg, ffprobe, [], error.Message); }
    }

    private static List<string> CommonArguments() => ["-hide_banner", "-nostdin", "-loglevel", "warning", "-n", "-progress", "pipe:1", "-stats_period", "0.5"];

    private static async Task WriteSilenceAsync(string path, long samples, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await output.WriteAsync(PcmWave.Header(samples, 8), cancellationToken);
        var zero = new byte[64 * 1024]; var bytes = checked(samples * 8 * 4);
        while (bytes > 0)
        {
            var count = (int)Math.Min(zero.Length, bytes);
            await output.WriteAsync(zero.AsMemory(0, count), cancellationToken); bytes -= count;
        }
    }

    private static async Task QuantizeAudioAsync(string path, WaveInfo info, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var buffer = new byte[64 * 1024]; var remaining = info.DataBytes; stream.Position = info.DataOffset;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining); var position = stream.Position;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            for (var offset = 0; offset < count; offset += 4) buffer[offset] = 0;
            stream.Position = position; await stream.WriteAsync(buffer.AsMemory(0, count), cancellationToken); remaining -= count;
        }
    }
}
