using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using MediaTaxi.Configuration;

namespace MediaTaxi.Media;

public sealed record MediaProbe(double Duration, double Rate, int AudioChannels, bool Native,
    bool HasAudio, bool Interlaced, long TimecodeStartFrames, string Summary);

public sealed class MediaInspector(TaxiOptions options, MediaProcessRunner runner)
{
    public async Task<MediaProbe> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(options.Media.FfprobePath,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], options.Media.TimeoutSeconds, cancellationToken);
        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();
        var videos = streams.Where(x => Text(x, "codec_type") == "video" &&
            !(x.TryGetProperty("disposition", out var d) && Number(d, "attached_pic") == 1)).ToArray();
        var audios = streams.Where(x => Text(x, "codec_type") == "audio").ToArray();
        if (videos.Length != 1) throw new MediaMismatchException("Exactly one non-attached video stream is required");
        if (audios.Length > 1) throw new MediaMismatchException("Multiple audio streams require an explicit channel mapping and are unsupported");
        var video = videos[0];
        var transfer = Text(video, "color_transfer"); var primaries = Text(video, "color_primaries");
        if (transfer is "smpte2084" or "arib-std-b67" || primaries == "bt2020")
            throw new MediaMismatchException("HDR/wide-gamut input requires an explicit color conversion policy");
        var rate = Rational(Text(video, "avg_frame_rate"));
        if (rate <= 0) rate = Rational(Text(video, "r_frame_rate"));
        var format = root.TryGetProperty("format", out var f) ? f : default;
        var duration = Value(video, "duration");
        if (duration <= 0) duration = Value(video, "duration_ts") * Rational(Text(video, "time_base"));
        if (duration <= 0 && format.ValueKind == JsonValueKind.Object) duration = Value(format, "duration");
        if (duration <= 0 || rate <= 0 || !double.IsFinite(duration) || duration > 24 * 3600)
            throw new MediaMismatchException("Video duration/frame rate is missing or outside supported bounds");
        var channels = audios.Length == 0 ? 0 : Number(audios[0], "channels");
        if (audios.Length > 0 && channels is < 1 or > 8)
            throw new MediaMismatchException("Audio requires 1..8 ordered channels; automatic downmix is disabled");
        var fieldOrder = Text(video, "field_order");
        var interlaced = fieldOrder is "tt" or "bb" or "tb" or "bt";
        var sampleAspect = Text(video, "sample_aspect_ratio");
        var native = Text(video, "codec_name") == "h264" && Number(video, "width") == 1920
            && Number(video, "height") == 1080 && Math.Abs(rate - 50) < 0.0001
            && !interlaced && Text(video, "pix_fmt") == "yuv422p10le"
            && Text(video, "profile") == "High 4:2:2 Intra"
            && sampleAspect is "" or "N/A" or "1:1";
        if (audios.Length > 0)
            native &= Text(audios[0], "codec_name") is "pcm_s16le" or "pcm_s24le"
                && Number(audios[0], "sample_rate") == 48000;
        if (native)
        {
            var packet = await runner.RunAsync(options.Media.FfprobePath,
                ["-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#1", "-show_entries", "packet=size", "-of", "json", path],
                options.Media.TimeoutSeconds, cancellationToken);
            using var packetJson = JsonDocument.Parse(packet.StandardOutput);
            native = packetJson.RootElement.TryGetProperty("packets", out var packets)
                && packets.GetArrayLength() > 0 && Number(packets[0], "size") == AvciValidator.FrameBytes;
        }
        var timecode = Tag(format, "timecode");
        if (string.IsNullOrWhiteSpace(timecode)) timecode = Tag(video, "timecode");
        var start = await ReadTimecodeAsync(path, timecode, rate, cancellationToken);
        var summary = $"{Text(format, "format_name")} / {Text(video, "codec_name")} {Text(video, "profile")} {Number(video, "width")}x{Number(video, "height")} {rate:0.###}fps / {channels}ch";
        return new MediaProbe(duration, rate, channels, native, audios.Length > 0, interlaced, start, summary);
    }

    private static string Tag(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty("tags", out var tags) ? Text(tags, key) : "";
    public static string Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static int Number(JsonElement element, string key) => int.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Value(JsonElement element, string key) => double.TryParse(Text(element, key), CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Rational(string text)
    {
        var parts = text.Replace(':', '/').Split('/');
        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator)) return 0;
        if (parts.Length == 1) return numerator;
        return double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator) && denominator != 0 ? numerator / denominator : 0;
    }

    public static async Task<long> ReadTimecodeAsync(string path, string text, double videoRate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        if (text.Contains(';')) throw new MediaMismatchException("Drop-frame source timecode needs an explicit conversion policy for 50fps output");
        var parts = text.Split(':');
        if (parts.Length != 4 || parts.Any(x => !int.TryParse(x, out _))) throw new MediaMismatchException("Unsupported source timecode");
        var numbers = parts.Select(int.Parse).ToArray();
        var rate = (int)Math.Round(videoRate);
        if (Path.GetExtension(path).Equals(".mxf", StringComparison.OrdinalIgnoreCase))
        {
            // SMPTE MXF TimecodeComponent UL/local tags. Bounded metadata scan; never
            // infer a 25/50 timecode base solely from the picture's frame rate.
            await using var source = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(source.Length, 4 * 1024 * 1024)];
            await source.ReadExactlyAsync(buffer, cancellationToken);
            var candidates = ReadMxfTimecodes(buffer);
            var matching = candidates.Where(x => FormatTimecode(x.Start, x.Rate) == text && !x.DropFrame).Distinct().ToArray();
            if (matching.Length != 1 && numbers.Any(x => x != 0))
                throw new MediaMismatchException("Ambiguous or missing MXF timecode base");
            if (matching.Length > 0) return checked((long)Math.Round(matching[0].Start * 50d / matching[0].Rate));
        }
        if (numbers[0] is < 0 or > 23 || numbers[1] is < 0 or > 59 || numbers[2] is < 0 or > 59
            || rate <= 0 || numbers[3] < 0 || numbers[3] >= rate)
            throw new MediaMismatchException("Invalid source timecode");
        return (long)Math.Round((((numbers[0] * 60 + numbers[1]) * 60 + numbers[2]) * rate + numbers[3]) * 50d / rate);
    }

    private static string FormatTimecode(long frames, int rate) => rate <= 0 ? "" :
        $"{frames / (rate * 3600):00}:{frames / (rate * 60) % 60:00}:{frames / rate % 60:00}:{frames % rate:00}";

    private static List<(long Start, int Rate, bool DropFrame)> ReadMxfTimecodes(ReadOnlySpan<byte> buffer)
    {
        ReadOnlySpan<byte> key = [6, 14, 43, 52, 2, 83, 1, 1, 13, 1, 1, 1, 1, 1, 20, 0];
        var values = new List<(long, int, bool)>();
        for (var position = 0; position + 17 < buffer.Length; position++)
        {
            if (!buffer.Slice(position, 16).SequenceEqual(key)) continue;
            var cursor = position + 16; long size = buffer[cursor++];
            if ((size & 128) != 0)
            {
                var count = (int)(size & 127); size = 0;
                if (count is < 1 or > 4 || cursor + count > buffer.Length) continue;
                for (var i = 0; i < count; i++) size = size * 256 + buffer[cursor++];
            }
            if (size > 4096 || cursor + size > buffer.Length) continue;
            var end = cursor + (int)size; long? start = null; var rate = 0; var drop = false;
            while (cursor + 4 <= end)
            {
                var tag = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(cursor, 2));
                var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(cursor + 2, 2)); cursor += 4;
                if (cursor + length > end) break;
                if (tag == 0x1501 && length == 8) start = BinaryPrimitives.ReadInt64BigEndian(buffer.Slice(cursor, 8));
                if (tag == 0x1502 && length == 2) rate = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(cursor, 2));
                if (tag == 0x1503 && length == 1) drop = buffer[cursor] != 0;
                cursor += length;
            }
            if (start is >= 0 && rate > 0) values.Add((start.Value, rate, drop));
        }
        return values;
    }
}
