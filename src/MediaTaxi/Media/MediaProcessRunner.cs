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
                throw new IOException($"{Path.GetFileName(executable)} exited {process.ExitCode}: {stderr.ToString().Trim()}");
            return new ProcessResult(stdout.ToString(), stderr.ToString());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded {timeoutSeconds} seconds");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
            try { await Task.WhenAll(outTask, errorTask); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, int limit,
        CancellationToken cancellationToken, Action<string>? observe = null)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            observe?.Invoke(line);
            buffer.AppendLine(line);
            if (buffer.Length > limit) buffer.Remove(0, buffer.Length - limit);
        }
    }
}
