using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaTaxi.Media;

/// <summary>Presentation of the inspector's stored metadata; performs no media I/O.</summary>
public static partial class SourceFormatLabel
{
    public static string Format(MediaProbe? probe, string fallback)
    {
        if (probe is null) return string.IsNullOrWhiteSpace(fallback) ? "—" : fallback;
        var match = SummaryPattern().Match(probe.Summary);
        if (!match.Success) return fallback;
        var format = match.Groups["container"].Value.ToUpperInvariant();
        if (format.Contains(',')) format = format.Contains("MP4", StringComparison.Ordinal) ? "MOV/MP4" : format.Split(',')[0];
        var video = match.Groups["codec"].Value;
        var codec = video.StartsWith("h264 ", StringComparison.OrdinalIgnoreCase) && video.EndsWith(" Intra", StringComparison.OrdinalIgnoreCase)
            ? "Intra" : video.Split(' ')[0].ToUpperInvariant();
        var seconds = Math.Max(0, (long)Math.Floor(probe.Duration));
        return FormattableString.Invariant($"{format}/ {codec} {match.Groups["size"].Value} {probe.Rate:0.###}fps / {probe.AudioChannels}ch / Dur: {seconds / 60}:{seconds % 60:00}");
    }
    [GeneratedRegex(@"^(?<container>.+?) / (?<codec>.+?) (?<size>\d+x\d+) [\d.,]+fps / \d+ch$")]
    private static partial Regex SummaryPattern();
}
