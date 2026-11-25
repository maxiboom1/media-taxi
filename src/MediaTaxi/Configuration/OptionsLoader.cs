using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaTaxi.Configuration;

public static partial class OptionsLoader
{
    public static TaxiOptions Load(string path)
    {
        path = Path.GetFullPath(path);
        var c = new ConfigurationBuilder().AddIniFile(path, optional: false, reloadOnChange: false).Build();
        string Value(string key, string fallback = "") => c[key] ?? fallback;
        int Number(string key, int fallback, int min = 1, int max = 64) =>
            int.TryParse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), out var n) && n >= min && n <= max
                ? n : throw new InvalidDataException($"{key} must be an integer between {min} and {max}.");
        double Seconds(string key, double fallback) =>
            double.TryParse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0.1 && n <= 86400
                ? n : throw new InvalidDataException($"{key} must be 0.1..86400 seconds.");
        bool Boolean(string key, bool fallback) => bool.TryParse(Value(key, fallback.ToString()), out var b)
            ? b : throw new InvalidDataException($"{key} must be true or false.");
        string Folder(string key)
        {
            var value = Value(key);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{key} is required.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, Path.GetDirectoryName(path)!));
        }
        string Executable(string key, string fallback)
        {
            var value = Value(key, fallback);
            return Path.GetFullPath(value, Path.GetDirectoryName(path)!);
        }
        var o = new TaxiOptions
        {
            ConfigPath = path,
            LogLevel = Value("logging:level", "Information"),
            Folders = new() { Watch = Folder("folders:watch"), Finished = Folder("folders:finished"), Failed = Folder("folders:failed"), Abekas = Folder("folders:abekas"), Proxy = Folder("folders:proxy"), Work = Folder("folders:work"), State = Folder("folders:state") },
            Workers = new() { Native = Number("workers:native", 2), Normalize = Number("workers:normalize", 1), Proxy = Number("workers:proxy", 1), Copy = Number("workers:copy", 4), CopiesPerDestination = Number("workers:copies_per_destination", 1) },
            Media = new() { FfmpegPath = Executable("media:ffmpeg", "tools/ffmpeg.exe"), FfprobePath = Executable("media:ffprobe", "tools/ffprobe.exe"), AllowNormalization = Boolean("media:allow_normalization", false), Threads = Number("media:threads", 4, 1, 256), TimeoutSeconds = Number("media:timeout_seconds", 3600, 10, 86400) },
            Timing = new() { StabilitySeconds = Seconds("timing:stability_seconds", 10), ReconcileSeconds = Seconds("timing:reconcile_seconds", 10), InventoryPollSeconds = Seconds("timing:inventory_poll_seconds", 30), InventoryTimeoutSeconds = Seconds("timing:inventory_timeout_seconds", 5), ReconnectSeconds = Seconds("timing:reconnect_seconds", 5) },
            Web = new() { Listen = Value("web:listen", "http://0.0.0.0:8080"), TimeZone = Value("web:time_zone", "Asia/Jerusalem") },
            Proxy = new() { Codec = Value("proxy:codec", "h264").ToLowerInvariant(), Width = Number("proxy:width", 640, 2, 3840), Height = Number("proxy:height", 360, 2, 2160), Fps = Number("proxy:fps", 50, 1, 50), RateControl = Value("proxy:rate_control", "bitrate").ToLowerInvariant(), VideoBitrate = Value("proxy:video_bitrate", "1200k"), AudioBitrate = Value("proxy:audio_bitrate", "128k"), Crf = Number("proxy:crf", 26, 0, 51), Preset = Value("proxy:preset", "fast") }
        };
        try { o.Proxy.Channels = Value("proxy:channels", "1,2").Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray(); }
        catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException("proxy:channels must be comma-separated channel indexes."); }
        if (o.Proxy.Channels.Length is < 1 or > 2 || o.Proxy.Channels.Distinct().Count() != o.Proxy.Channels.Length || o.Proxy.Channels.Any(n => n is < 1 or > 8))
            throw new InvalidDataException("proxy:channels must select one or two unique channels in 1..8.");
        foreach (var section in c.GetSection("destinations").GetChildren())
        {
            if (!Boolean($"destinations:{section.Key}:enabled", true)) continue;
            if (!IdPattern().IsMatch(section.Key)) throw new InvalidDataException("Destination IDs may contain letters, digits, dash and underscore only.");
            var key = $"destinations:{section.Key}:";
            var host = Value(key + "host");
            if (string.IsNullOrWhiteSpace(host)) throw new InvalidDataException(key + "host is required.");
            o.Destinations.Add(new() { Id = section.Key, Name = Value(key + "name", section.Key), Path = Folder(key + "path"), Host = host, Port = Number(key + "port", 5254, 1, 65535), InventoryFolder = Value(key + "inventory_folder", "IMPORTS") });
        }
        Validate(o);
        return o;
    }

    public static void Validate(TaxiOptions o)
    {
        if (!Enum.TryParse<Microsoft.Extensions.Logging.LogLevel>(o.LogLevel, true, out var logLevel) || !Enum.IsDefined(logLevel))
            throw new InvalidDataException("logging:level must be Trace, Debug, Information, Warning, Error, Critical or None.");
        if (o.Destinations.Count is < 1 or > 4 || o.Destinations.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != o.Destinations.Count)
            throw new InvalidDataException("Configure one to four destinations with unique IDs.");
        if (o.Proxy.Codec is not ("h264" or "h265") || o.Proxy.RateControl is not ("bitrate" or "crf") || o.Proxy.Width % 2 != 0 || o.Proxy.Height % 2 != 0)
            throw new InvalidDataException("Proxy codec must be h264/h265, rate_control bitrate/crf, dimensions even.");
        if (!BitratePattern().IsMatch(o.Proxy.VideoBitrate) || !BitratePattern().IsMatch(o.Proxy.AudioBitrate))
            throw new InvalidDataException("Bitrates must be positive numeric bits/second, optionally followed by k or M.");
        if (!new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" }.Contains(o.Proxy.Preset))
            throw new InvalidDataException("Unsupported proxy encoder preset.");
        if (!Uri.TryCreate(o.Web.Listen, UriKind.Absolute, out var url) || url.Scheme != "http" || !string.IsNullOrEmpty(url.Query) || url.AbsolutePath != "/")
            throw new InvalidDataException("web:listen must be an HTTP address with a port and no path/query.");
        _ = TimeZoneInfo.FindSystemTimeZoneById(o.Web.TimeZone);
        var sources = new[] { o.Folders.Watch, o.Folders.Finished, o.Folders.Failed };
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3 || Within(o.Folders.Finished, o.Folders.Failed) || Within(o.Folders.Failed, o.Folders.Finished) || Within(o.Folders.Watch, o.Folders.Finished) || Within(o.Folders.Watch, o.Folders.Failed))
            throw new InvalidDataException("Source roots must be distinct; Finished/Failed may be children of Watch, not ancestors of it or each other.");
        var outputs = new[] { o.Folders.Abekas, o.Folders.Proxy, o.Folders.Work, o.Folders.State }.Concat(o.Destinations.Select(d => d.Path)).ToArray();
        foreach (var output in outputs)
        foreach (var source in sources)
            if (Overlaps(output, source)) throw new InvalidDataException($"Output and source folders overlap: {output}, {source}");
        for (var i = 0; i < outputs.Length; i++)
        for (var j = i + 1; j < outputs.Length; j++)
            if (Overlaps(outputs[i], outputs[j])) throw new InvalidDataException($"Output folders overlap: {outputs[i]}, {outputs[j]}");
    }
    private static string Canonical(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool Within(string candidate, string parent)
    {
        var root = Canonical(parent);
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return !string.Equals(Canonical(candidate), root, StringComparison.OrdinalIgnoreCase)
            && Canonical(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
    private static bool Overlaps(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase) || Within(a, b) || Within(b, a);
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[1-9][0-9]*(?:\\.[0-9]+)?[kKmM]?$")] private static partial Regex BitratePattern();
}
