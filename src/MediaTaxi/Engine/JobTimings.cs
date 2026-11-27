using System.Diagnostics;
using MediaTaxi.Domain;

namespace MediaTaxi.Engine;

public sealed partial class JobEngine
{
    private async Task<T> MeasureAsync<T>(JobRecord job, string phase, Func<Task<T>> work)
    {
        var started = DateTimeOffset.UtcNow;
        Update(job, () => job.Timings[phase] = new(started, null, null, null));
        var clock = Stopwatch.GetTimestamp();
        var succeeded = false;
        try { var result = await work(); succeeded = true; return result; }
        finally
        {
            var seconds = Stopwatch.GetElapsedTime(clock).TotalSeconds;
            Update(job, () => job.Timings[phase] = new(started, DateTimeOffset.UtcNow, seconds, succeeded));
            logger.LogInformation("Job {Job} | {Phase} | {Seconds:F3}s | Success={Succeeded}", job.Id, phase, seconds, succeeded);
        }
    }

    private Task MeasureAsync(JobRecord job, string phase, Func<Task> work) =>
        MeasureAsync(job, phase, async () => { await work(); return true; });
}
