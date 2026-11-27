using System.Globalization;
using System.Security.Cryptography;
using MediaTaxi.Storage;

namespace MediaTaxi.Media;

public sealed partial class MediaService
{
    private static string NormalizationFilter(MediaProbe probe) =>
        (probe.Interlaced ? "bwdif=mode=send_field:parity=auto:deint=all," : "") +
        "fps=50,scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";

    public async Task<WrappedSource> WrapSourceAsync(string source, MediaInspection inspection, string work,
        string output, string id, Action<double>? progress, CancellationToken ct)
    {
        var probe = inspection.Probe ?? (await InspectAsync(source, ct)).Probe!;
        if (!probe.Native)
        {
            var prepared = await PrepareInspectedAsync(source, probe, work, progress, ct);
            return new(await FlatClip.WriteAsync(prepared, output, id, ct), prepared.FrameCount);
        }
        Directory.CreateDirectory(work);
        var audio = Path.Combine(work, "audio.wav");
        var args = CommonArguments();
        args[args.IndexOf("pipe:1")] = "pipe:2";
        args.AddRange(["-i", source, "-map", "0:v:0", "-an", "-c:v", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", "pipe:1"]);
        if (probe.HasAudio)
        {
            var pan = "pan=8c|" + string.Join('|', Enumerable.Range(0, probe.AudioChannels).Select(i => $"c{i}=c{i}"));
            args.AddRange(["-map", "0:a:0", "-vn", "-af", pan, "-c:a", "pcm_s32le", "-rf64", "auto", audio]);
        }
        return await StreamingClipWriter.WriteAsync(output, id, probe.TimecodeStartFrames, audio,
            consume => runner.RunBinaryAsync(options.Media.FfmpegPath, args, options.Media.TimeoutSeconds,
                consume, ct, probe.Duration, progress), checked((long)Math.Ceiling(probe.Duration * 50) + 2), ct);
    }

    public async Task<ProxyArtifact> CreateSourceProxyAsync(string source, MediaInspection? inspection, long frames,
        string output, string id, Action<double>? progress, CancellationToken ct)
    {
        var probe = inspection?.Probe ?? (await InspectAsync(source, ct)).Probe!;
        if (frames <= 0) frames = checked((long)Math.Round(probe.Duration * 50));
        var duration = frames / 50d;
        var temporary = output + "." + id + ".partial";
        if (File.Exists(output)) throw new IOException("Proxy already exists; nothing was overwritten.");
        var args = CommonArguments();
        args.AddRange(["-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture), "-i", "handle:"]);
        if (!probe.HasAudio) args.AddRange(["-f", "lavfi", "-i", "anullsrc=r=48000:cl=7.1"]);
        var videoFilter = (probe.Native ? "fps=50,setsar=1" : NormalizationFilter(probe)) +
            $",setpts=PTS-STARTPTS,scale={options.Proxy.Width}:{options.Proxy.Height}:flags=lanczos";
        var audioFilter = (probe.HasAudio ? "pan=8c|" + string.Join('|', Enumerable.Range(0, probe.AudioChannels).Select(i => $"c{i}=c{i}")) + "," : "") +
            $"aresample=48000,asetpts=PTS-STARTPTS,apad,atrim=end_sample={frames * 960},pan=" +
            (options.Proxy.Channels.Length == 1 ? "mono" : "stereo") + "|" +
            string.Join('|', options.Proxy.Channels.Select((channel, index) => $"c{index}=c{channel - 1}"));
        args.AddRange(["-map", "0:v:0", "-map", probe.HasAudio ? "0:a:0" : "1:a:0", "-vf", videoFilter,
            "-c:v", options.Proxy.Codec == "h265" ? "libx265" : "libx264", "-preset", options.Proxy.Preset,
            "-pix_fmt", "yuv420p", "-threads", options.Media.Threads.ToString(CultureInfo.InvariantCulture),
            "-fps_mode", "cfr", "-r", options.Proxy.Fps.ToString(CultureInfo.InvariantCulture), "-t", duration.ToString("R", CultureInfo.InvariantCulture)]);
        if (options.Proxy.Codec == "h265") args.AddRange(["-x265-params", $"pools=none:frame-threads={options.Media.Threads}"]);
        args.AddRange(options.Proxy.RateControl == "crf" ? ["-crf", options.Proxy.Crf.ToString(CultureInfo.InvariantCulture)] : ["-b:v", options.Proxy.VideoBitrate]);
        args.AddRange(["-af", audioFilter, "-c:a", "aac", "-b:a", options.Proxy.AudioBitrate,
            "-map_metadata", "-1", "-metadata", "media_taxi_job_id=" + id,
            "-movflags", "+faststart+use_metadata_tags", "-f", "mp4", temporary]);
        try
        {
            await runner.RunSourceAsync(options.Media.FfmpegPath, args, source, options.Media.TimeoutSeconds, ct, duration, progress);
            ct.ThrowIfCancellationRequested();
            var sha = await FileOperations.HashAsync(temporary, ct);
            File.Move(temporary, output, false); progress?.Invoke(100); return new(output, sha);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
