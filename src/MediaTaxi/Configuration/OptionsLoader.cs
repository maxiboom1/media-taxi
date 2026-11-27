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
        double Gibibytes(string key, double fallback) =>
            double.TryParse(Value(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && n >= 0.1 && n <= 86400
                ? n : throw new InvalidDataException($"{key} must be 0.1..86400 GiB.");
        bool Boolean(string key, bool fallback) => bool.TryParse(Value(key, fallback.ToString()), out var b)
            ? b : throw new InvalidDataException($"{key} must be true or false.");
        string Folder(string key, string fallback = "")
        {
            var value = Value(key, fallback);
            if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException($"{key} is required.");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, Path.GetDirectoryName(path)!));
        }
        string Executable(string key, string fallback)
        {
            var value = Value(key, fallback);
            return Path.GetFullPath(value, Path.GetDirectoryName(path)!);
        }
        var defaults = new TaxiOptions();
        var o = new TaxiOptions
        {
            ConfigPath = path,
            LogLevel = Value("logging:level", defaults.LogLevel),
            FileLog = new() { Enabled = Boolean("logging:file_enabled", defaults.FileLog.Enabled), Directory = Folder("logging:directory", defaults.FileLog.Directory),
                MaxFiles = Number("logging:max_files", defaults.FileLog.MaxFiles, 1, 20), MaxMiB = Number("logging:max_mib", defaults.FileLog.MaxMiB, 1, 100) },
            Folders = new() { Watch = Folder("folders:watch"), Finished = Folder("folders:finished"), Failed = Folder("folders:failed"), Proxy = Folder("folders:proxy"), Work = Folder("folders:work", defaults.Folders.Work), State = Folder("folders:state") },
            Cache = new() { MaxGiB = Gibibytes("cache:max_gib", defaults.Cache.MaxGiB), MinFreeGiB = Number("cache:min_free_gib", (int)defaults.Cache.MinFreeGiB, 0, 65536) },
            Workers = new() { Native = Number("workers:native", defaults.Workers.Native), Normalize = Number("workers:normalize", defaults.Workers.Normalize), Proxy = Number("workers:proxy", defaults.Workers.Proxy), Copy = Number("workers:copy", defaults.Workers.Copy), CopiesPerDestination = Number("workers:copies_per_destination", defaults.Workers.CopiesPerDestination) },
            Media = new() { FfmpegPath = Executable("media:ffmpeg", defaults.Media.FfmpegPath), FfprobePath = Executable("media:ffprobe", defaults.Media.FfprobePath), AllowNormalization = Boolean("media:allow_normalization", defaults.Media.AllowNormalization), Threads = Number("media:threads", defaults.Media.Threads, 1, 256), TimeoutSeconds = Number("media:timeout_seconds", defaults.Media.TimeoutSeconds, 10, 86400) },
            Timing = new() { StabilitySeconds = Seconds("timing:stability_seconds", defaults.Timing.StabilitySeconds), ReconcileSeconds = Seconds("timing:reconcile_seconds", defaults.Timing.ReconcileSeconds), InventoryPollSeconds = Seconds("timing:inventory_poll_seconds", defaults.Timing.InventoryPollSeconds), InventoryTimeoutSeconds = Seconds("timing:inventory_timeout_seconds", defaults.Timing.InventoryTimeoutSeconds), ReconnectSeconds = Seconds("timing:reconnect_seconds", defaults.Timing.ReconnectSeconds) },
            Web = new() { Listen = Value("web:listen", defaults.Web.Listen), TimeZone = Value("web:time_zone", defaults.Web.TimeZone) },
            Proxy = new() { Codec = Value("proxy:codec", defaults.Proxy.Codec).ToLowerInvariant(), Width = Number("proxy:width", defaults.Proxy.Width, 2, 3840), Height = Number("proxy:height", defaults.Proxy.Height, 2, 2160), Fps = Number("proxy:fps", defaults.Proxy.Fps, 1, 50), RateControl = Value("proxy:rate_control", defaults.Proxy.RateControl).ToLowerInvariant(), VideoBitrate = Value("proxy:video_bitrate", defaults.Proxy.VideoBitrate), AudioBitrate = Value("proxy:audio_bitrate", defaults.Proxy.AudioBitrate), Crf = Number("proxy:crf", defaults.Proxy.Crf, 0, 51), Preset = Value("proxy:preset", defaults.Proxy.Preset) }
        };
        o.Timing.SourceScanTimeoutSeconds = Number("timing:source_scan_timeout_seconds", (int)defaults.Timing.SourceScanTimeoutSeconds, 1, 300);
        o.Timing.HealthCheckTimeoutSeconds = Number("timing:health_check_timeout_seconds", (int)defaults.Timing.HealthCheckTimeoutSeconds, 1, 300);
        o.Timing.HealthCheckIntervalSeconds = Number("timing:health_check_interval_seconds", (int)defaults.Timing.HealthCheckIntervalSeconds, 5, 3600);
        o.Timing.StalledSourceWarningSeconds = Number("timing:stalled_source_warning_seconds", (int)defaults.Timing.StalledSourceWarningSeconds, 1, 86400);
        o.Proxy.DeferWhileHiresBusy = Boolean("proxy:defer_while_hires_busy", defaults.Proxy.DeferWhileHiresBusy);
        if (c["folders:abekas"] is not null) o.Warnings.Add("folders.abekas is obsolete and ignored. Delivery clips now use the temporary work cache.");
        try { o.Proxy.Channels = Value("proxy:channels", string.Join(',', defaults.Proxy.Channels)).Split(',').Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToArray(); }
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
        if (!double.IsFinite(o.Cache.MaxGiB) || o.Cache.MaxGiB <= 0 || o.Cache.MaxGiB > 86400 || !double.IsFinite(o.Cache.MinFreeGiB) || o.Cache.MinFreeGiB < 0)
            throw new InvalidDataException("Cache max_gib must be positive and min_free_gib nonnegative.");
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
        var outputs = new[] { o.Folders.Proxy, o.Folders.Work, o.Folders.State }.Concat(o.Destinations.Select(d => d.Path)).ToArray();
        foreach (var output in outputs)
        foreach (var source in sources)
            if (Overlaps(output, source)) throw new InvalidDataException($"Output and source folders overlap: {output}, {source}");
        for (var i = 0; i < outputs.Length; i++)
        for (var j = i + 1; j < outputs.Length; j++)
            if (Overlaps(outputs[i], outputs[j])) throw new InvalidDataException($"Output folders overlap: {outputs[i]}, {outputs[j]}");
        if (o.FileLog.Enabled)
            foreach (var folder in sources.Concat(new[] { o.Folders.Proxy, o.Folders.Work }).Concat(o.Destinations.Select(d => d.Path)))
                if (Overlaps(o.FileLog.Directory, folder)) throw new InvalidDataException("logging:directory must be separate from source, proxy, cache and destination folders.");
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
