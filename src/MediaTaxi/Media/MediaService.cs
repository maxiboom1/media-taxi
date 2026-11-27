using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;

namespace MediaTaxi.Media;

public sealed partial class MediaService(TaxiOptions options) : IMediaService
{
    private readonly MediaProcessRunner runner = new();
    private readonly SemaphoreSlim normalization = new(Math.Max(1, options.Workers.Normalize));

    public async Task<bool> IsNativeAsync(string source, CancellationToken cancellationToken) =>
        (await new MediaInspector(options, runner).ProbeAsync(source, cancellationToken)).Native;

    public async Task<MediaInspection> InspectAsync(string source, CancellationToken cancellationToken)
    {
        var probe = await new MediaInspector(options, runner).ProbeAsync(source, cancellationToken);
        return new MediaInspection(probe.Native, probe.Summary, probe);
    }

    public async Task<PreparedMedia> PrepareAsync(string source, string workDirectory, Action<double>? progress, CancellationToken cancellationToken)
    {
        var probe = await new MediaInspector(options, runner).ProbeAsync(source, cancellationToken);
        return await PrepareInspectedAsync(source, probe, workDirectory, progress, cancellationToken);
    }
    private async Task<PreparedMedia> PrepareInspectedAsync(string source, MediaProbe probe, string workDirectory, Action<double>? progress, CancellationToken cancellationToken)
    {
        if (!probe.Native && !options.Media.AllowNormalization)
            throw new MediaMismatchException("Format mismatch: AVC-Intra100 1080p50 required and incoming normalization is disabled");
        Directory.CreateDirectory(workDirectory);
        var video = Path.Combine(workDirectory, "video.h264");
        var audio = Path.Combine(workDirectory, "audio.wav");
        if (File.Exists(video) || File.Exists(audio)) throw new IOException("Preparation work directory already contains media");
        var normalize = !probe.Native;
        if (normalize) await normalization.WaitAsync(cancellationToken);
        try
        {
            var videoArguments = CommonArguments();
            videoArguments.AddRange(["-i", source, "-map", "0:v:0", "-an"]);
            if (normalize)
            {
                var filter = NormalizationFilter(probe);
                videoArguments.AddRange(["-vf", filter, "-c:v", "libx264", "-pix_fmt", "yuv422p10le", "-profile:v", "high422",
                    "-x264-params", "avcintra-class=100", "-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture), "-fps_mode", "cfr"]);
            }
            else videoArguments.AddRange(["-c:v", "copy", "-bsf:v", "h264_mp4toannexb"]);
            videoArguments.AddRange(["-f", "h264", video]);
            // Native video and PCM are demultiplexed in one source pass. Video
            // remains stream-copy; no picture decoding or encoding is introduced.
            var nativeAudio = !normalize && probe.HasAudio;
            var pan = "pan=8c|" + string.Join('|', Enumerable.Range(0, probe.AudioChannels).Select(i => $"c{i}=c{i}"));
            if (nativeAudio)
                videoArguments.AddRange(["-map", "0:a:0", "-vn", "-af", pan, "-c:a", "pcm_s32le", "-rf64", "auto", audio]);
            await runner.RunAsync(options.Media.FfmpegPath, videoArguments, options.Media.TimeoutSeconds, cancellationToken, probe.Duration, progress);
            var bytes = new FileInfo(video).Length;
            if (bytes <= 0 || bytes % AvciValidator.FrameBytes != 0)
                throw new MediaMismatchException("Video does not have the tested AVC-Intra100 fixed frame size");
            var frames = bytes / AvciValidator.FrameBytes;
            // Full per-frame validation occurs while the final clip is written.
            var firstFrame = new byte[AvciValidator.FrameBytes];
            await using (var stream = File.OpenRead(video)) await stream.ReadExactlyAsync(firstFrame, cancellationToken);
            AvciValidator.ValidateFrame(firstFrame);
            if (probe.HasAudio)
            {
                if (!nativeAudio)
                {
                    var audioArguments = CommonArguments();
                    audioArguments.AddRange(["-i", source, "-map", "0:a:0", "-vn"]);
                    pan += $",aresample=48000,apad,atrim=end_sample={frames * 960}";
                    audioArguments.AddRange(["-af", pan, "-c:a", "pcm_s32le", "-rf64", "auto", audio]);
                    await runner.RunAsync(options.Media.FfmpegPath, audioArguments, options.Media.TimeoutSeconds, cancellationToken);
                }
                var info = PcmWave.ReadInfo(audio);
                if (info.Samples != frames * 960) throw new MediaMismatchException("Native audio/video durations differ");
                if (normalize) await QuantizeAudioAsync(audio, info, cancellationToken);
            }
            else await WriteSilenceAsync(audio, frames * 960, cancellationToken);
            progress?.Invoke(100);
            return new PreparedMedia(video, audio, frames, 8, probe.TimecodeStartFrames, probe.Native, probe.Summary);
        }
        finally { if (normalize) normalization.Release(); }
    }

    public Task<ClipArtifact> WrapAsync(PreparedMedia media, string outputPath, string jobId, CancellationToken cancellationToken) =>
        FlatClip.WriteAsync(media, outputPath, jobId, cancellationToken);

    public Task<PreparedMedia> ExtractClipAsync(string clipPath, string workDirectory, CancellationToken cancellationToken) =>
        FlatClip.ExtractAsync(clipPath, workDirectory, cancellationToken);

    public async Task<ProxyArtifact> CreateProxyAsync(PreparedMedia media, string outputPath, string jobId,
        Action<double>? progress, CancellationToken cancellationToken)
    {
        var identity = Guid.Parse(jobId).ToString("N");
        var temporary = outputPath + "." + identity + ".partial";
        if (File.Exists(outputPath)) throw new IOException($"Proxy already exists: {outputPath}");
        if (options.Proxy.Channels.Length is < 1 or > 2 || options.Proxy.Channels.Any(c => c is < 1 or > 8))
            throw new MediaMismatchException("Proxy requires one or two source channel indexes in 1..8");
        var args = CommonArguments();
        args.AddRange(["-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture),
            "-r", "50", "-f", "h264", "-i", media.VideoPath, "-i", media.AudioPath,
            "-map", "0:v:0", "-map", "1:a:0", "-vf", $"scale={options.Proxy.Width}:{options.Proxy.Height}:flags=lanczos",
            "-c:v", options.Proxy.Codec == "h265" ? "libx265" : "libx264", "-preset", options.Proxy.Preset,
            "-pix_fmt", "yuv420p", "-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr", "-r", options.Proxy.Fps.ToString(CultureInfo.InvariantCulture)]);
        if (options.Proxy.Codec == "h265")
            args.AddRange(["-x265-params", "pools=none:frame-threads=" + options.Media.Threads.ToString(CultureInfo.InvariantCulture)]);
        args.AddRange(options.Proxy.RateControl == "crf" ? ["-crf", options.Proxy.Crf.ToString(CultureInfo.InvariantCulture)] : ["-b:v", options.Proxy.VideoBitrate]);
        var pan = "pan=" + (options.Proxy.Channels.Length == 1 ? "mono" : "stereo") + "|"
            + string.Join('|', options.Proxy.Channels.Select((channel, index) => $"c{index}=c{channel - 1}"));
        args.AddRange(["-af", pan, "-c:a", "aac", "-b:a", options.Proxy.AudioBitrate,
            "-map_metadata", "-1", "-metadata", "media_taxi_job_id=" + identity,
            "-movflags", "+faststart+use_metadata_tags", "-f", "mp4", temporary]);
        try
        {
            await runner.RunAsync(options.Media.FfmpegPath, args, options.Media.TimeoutSeconds, cancellationToken, media.FrameCount / 50d, progress);
            cancellationToken.ThrowIfCancellationRequested();
            string sha;
            await using (var stream = File.OpenRead(temporary))
                sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            File.Move(temporary, outputPath, overwrite: false);
            progress?.Invoke(100);
            return new ProxyArtifact(outputPath, sha);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
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
