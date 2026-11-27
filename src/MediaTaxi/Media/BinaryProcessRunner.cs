using System.Diagnostics;
using System.Text;

namespace MediaTaxi.Media;

public sealed partial class MediaProcessRunner
{
    public async Task RunBinaryAsync(string executable, IEnumerable<string> arguments, int timeoutSeconds,
        Func<Stream, CancellationToken, Task> consume, CancellationToken ct, double? duration = null, Action<double>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = new Process { StartInfo = new(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var errors = new StringBuilder();
        var drain = DrainAsync(process.StandardError, errors, ErrorLimit, timeout.Token, line =>
        {
            if (duration is > 0 && line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us))
                progress?.Invoke(Math.Clamp(us / 1e6 / duration.Value * 100, 0, 100));
        });
        try
        {
            // Consumer backpressure is the only video queue. No unbounded byte channel.
            await consume(process.StandardOutput.BaseStream, timeout.Token);
            await process.WaitForExitAsync(timeout.Token); await drain;
            if (process.ExitCode != 0) throw new IOException($"FFmpeg exited {process.ExitCode}: {errors}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"FFmpeg exceeded {timeoutSeconds} seconds."); }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await timeout.CancelAsync();
            try { await drain; } catch (OperationCanceledException) { }
        }
    }
}
