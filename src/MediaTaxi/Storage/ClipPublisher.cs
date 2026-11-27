using System.Diagnostics;

namespace MediaTaxi.Storage;

/// <summary>Write-only media delivery. Remote metadata queries never read video/audio.</summary>
public static class ClipPublisher
{
    public static async Task PublishAsync(string source, string destination, string partial, string expectedHash,
        bool resumePublication, CancellationToken ct, Action<CopyMeasurement>? measurement = null,
        string? expectedHeader = null, string? expectedPayload = null)
    {
        var length = new FileInfo(source).Length;
        if (File.Exists(destination))
        {
            // Only a persisted interrupted attempt may reconcile an existing final.
            // Presence/duration confirmation is independent OGP state, not an SMB readback.
            if (resumePublication && new FileInfo(destination).Length == length) return;
            throw new IOException("Destination name conflict: existing file was retained.");
        }
        try
        {
            if (File.Exists(partial)) File.Delete(partial);
            var started = DateTimeOffset.UtcNow; var clock = Stopwatch.GetTimestamp();
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var copied = await VerifiedCopyPipeline.CopyAsync(input, output, expectedHash, expectedHeader, expectedPayload, ct);
                if (copied != length) throw new IOException("Local clip byte length changed during delivery.");
                await output.FlushAsync(ct); output.Flush(true);
            }
            var seconds = Stopwatch.GetElapsedTime(clock).TotalSeconds;
            var publishStarted = DateTimeOffset.UtcNow; clock = Stopwatch.GetTimestamp();
            if (new FileInfo(partial).Length != length) throw new IOException("Published file byte length differs from the local clip.");
            ct.ThrowIfCancellationRequested(); File.Move(partial, destination, overwrite: false);
            measurement?.Invoke(new(started, seconds, publishStarted, Stopwatch.GetElapsedTime(clock).TotalSeconds));
        }
        catch
        {
            try { File.Delete(partial); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
