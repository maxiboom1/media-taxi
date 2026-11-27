using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MediaTaxi.Media;

/// <summary>Shell-free child process execution with concurrent pipe draining and tree cancellation.</summary>
public sealed partial class MediaProcessRunner
{
    private const int OutputLimit = 4 * 1024 * 1024;
    private const int ErrorLimit = 64 * 1024;

    public async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        int timeoutSeconds, CancellationToken cancellationToken, double? durationSeconds = null,
        Action<double>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new IOException($"Could not start {Path.GetFileName(executable)}");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = DrainAsync(process.StandardOutput, stdout, OutputLimit, timeout.Token, line =>
        {
            if (durationSeconds is > 0 && progress is not null && line.StartsWith("out_time_us=", StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var us))
                progress(Math.Clamp(us / 1_000_000d / durationSeconds.Value * 100, 0, 100));
        });
        var errorTask = DrainAsync(process.StandardError, stderr, ErrorLimit, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(outTask, errorTask);
            if (process.ExitCode != 0)
                throw new MediaProcessException(executable, process.ExitCode, stderr.ToString().Trim());
            return new ProcessResult(stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded {timeoutSeconds} seconds");
        }
        finally
        {
            await FinishAsync(process, timeout, outTask, errorTask);
        }
    }

    private static async Task FinishAsync(Process process, CancellationTokenSource timeout, params Task[] drains)
    {
        if (!process.HasExited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
        }
        await timeout.CancelAsync();
        try { await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException) { }
    }
    private static readonly HashSet<string> ProgressKeys = new(StringComparer.Ordinal)
    {
        "frame", "fps", "stream_0_0_q", "bitrate", "total_size", "out_time_us", "out_time_ms", "out_time",
        "dup_frames", "drop_frames", "speed", "progress"
    };
    private static bool IsProgress(string line)
    {
        var equal = line.IndexOf('=');
        if (equal <= 0 || !ProgressKeys.Contains(line[..equal])) return false;
        var key = line[..equal]; var value = line[(equal + 1)..].Trim();
        if (key == "progress") return value is "continue" or "end";
        if (value == "N/A") return true;
        if (key == "out_time") return TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out _);
        if (key == "speed") value = value.TrimEnd('x').Trim();
        if (key == "bitrate") value = value.Replace("kbits/s", "", StringComparison.Ordinal).Trim();
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, int limit,
        CancellationToken cancellationToken, Action<string>? observe = null)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            observe?.Invoke(line);
            if (IsProgress(line)) continue;
            buffer.AppendLine(line);
            if (buffer.Length > limit) buffer.Remove(0, buffer.Length - limit);
        }
    }
}
