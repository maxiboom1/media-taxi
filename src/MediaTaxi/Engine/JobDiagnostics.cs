using MediaTaxi.Domain;
using MediaTaxi.Media;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private void SetJobError(JobRecord job, Exception error)
    {
        var process = error as MediaProcessException ?? error.InnerException as MediaProcessException;
        var summary = DiagnosticText.Summary(error.Message);
        Update(job, () =>
        {
            job.Error = summary;
            job.Diagnostic = new(job.Stage.ToString(), summary, DiagnosticText.Tail(process?.Diagnostics ?? error.Data["mediaDiagnostics"] as string ?? error.ToString()), process?.ExitCode);
        });
    }
    private void WaitForSource(JobRecord job, SourceWaitCode code, string reason)
    {
        if (job.WaitCode == code && job.WaitReason == reason) return;
        Update(job, () =>
        {
            if (job.WaitCode != code) job.WaitSinceUtc = DateTimeOffset.UtcNow;
            job.WaitCode = code; job.WaitReason = reason; job.WaitSinceUtc ??= DateTimeOffset.UtcNow;
        });
    }
    private static void ClearWait(JobRecord job)
    { job.WaitCode = SourceWaitCode.None; job.WaitReason = null; job.WaitSinceUtc = null; }
    private bool IsStalled(JobRecord job) => job.WaitCode != SourceWaitCode.None && !job.TerminalFailure &&
        job.WaitSinceUtc is { } since && DateTimeOffset.UtcNow - since >= TimeSpan.FromSeconds(options.Timing.StalledSourceWarningSeconds);
}
