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
            if (process.ExitCode != 0) throw new MediaProcessException(executable, process.ExitCode, errors.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"FFmpeg exceeded {timeoutSeconds} seconds."); }
        catch (Exception ex) when (ex is not OperationCanceledException and not MediaProcessException)
        {
            var exited = process.HasExited;
            await FinishAsync(process, timeout, drain);
            if (exited && process.ExitCode != 0) throw new MediaProcessException(executable, process.ExitCode, errors.ToString());
            if (errors.Length > 0) ex.Data["mediaDiagnostics"] = DiagnosticText.Tail(errors.ToString());
            throw;
        }
        finally
        {
            await FinishAsync(process, timeout, drain);
        }
    }
}
