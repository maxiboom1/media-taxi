using System.Globalization;
using System.Text;

namespace MediaTaxi.Media;

public sealed partial class MediaProcessRunner
{
    public async Task RunSourceAsync(string executable, IEnumerable<string> arguments, string source,
        int timeoutSeconds, CancellationToken ct, double duration, Action<double>? progress)
    {
        if (!OperatingSystem.IsWindows())
        {
            await RunAsync(executable, arguments.Select(a => a == "handle:" ? source : a), timeoutSeconds, ct, duration, progress); return;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var child = WindowsInputProcess.Start(executable, arguments, source);
        var stdout = new StringBuilder(); var stderr = new StringBuilder();
        // Synchronous Windows pipes use dedicated draining tasks, including before first output.
        var output = Task.Run(() => DrainAsync(child.Output, stdout, OutputLimit, timeout.Token, line =>
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out var us))
                progress?.Invoke(Math.Clamp(us / 1e6 / duration * 100, 0, 100));
        }));
        var error = Task.Run(() => DrainAsync(child.Error, stderr, ErrorLimit, timeout.Token));
        try
        {
            await child.Process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error);
            if (child.Process.ExitCode != 0) throw new MediaProcessException(executable, child.Process.ExitCode, stderr.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException($"FFmpeg exceeded {timeoutSeconds} seconds."); }
        finally
        {
            await FinishAsync(child.Process, timeout, output, error);
        }
    }
}
