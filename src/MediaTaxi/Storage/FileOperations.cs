using System.Security.Cryptography;
using System.Xml.Linq;
using System.Diagnostics;

namespace MediaTaxi.Storage;

public sealed record CopyMeasurement(DateTimeOffset WriteStartedUtc, double WriteSeconds, DateTimeOffset VerifyStartedUtc, double VerifySeconds);

public static class FileOperations
{
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
    public static bool ClipOwned(string path, string jobId)
    {
        try
        {
            using var file = File.OpenRead(path); var header = new byte[32768]; file.ReadExactly(header);
            var text = System.Text.Encoding.UTF8.GetString(header);
            var end = text.IndexOf("</AbekasClipMeta1>", StringComparison.Ordinal);
            if (end < 0) return false;
            using var reader = System.Xml.XmlReader.Create(new StringReader(text[..(end + "</AbekasClipMeta1>".Length)]),
                new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 32768 });
            var xml = XElement.Load(reader);
            return Guid.TryParse(xml.Element("UUID")?.Value, out var uuid) && Guid.TryParse(jobId, out var owner) && uuid == owner;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or EndOfStreamException or FormatException)
        { return false; }
    }
    public static async Task CopyVerifiedAsync(string source, string destination, string partial, string expectedHash, CancellationToken ct,
        Action<CopyMeasurement>? measurement = null)
    {
        // A completed previous publication is the only safe repeat-success case.
        if (File.Exists(destination))
        {
            if (await HashAsync(destination, ct) == expectedHash) return;
            throw new IOException("Destination name conflict: different file already exists.");
        }
        try
        {
            if (File.Exists(partial)) File.Delete(partial);
            var writeStarted = DateTimeOffset.UtcNow;
            var clock = Stopwatch.GetTimestamp();
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, ct); await output.FlushAsync(ct); output.Flush(true);
            }
            var writeSeconds = Stopwatch.GetElapsedTime(clock).TotalSeconds;
            var verifyStarted = DateTimeOffset.UtcNow; clock = Stopwatch.GetTimestamp();
            if (await HashAsync(partial, ct) != expectedHash) throw new IOException("Transferred file checksum mismatch.");
            measurement?.Invoke(new(writeStarted, writeSeconds, verifyStarted, Stopwatch.GetElapsedTime(clock).TotalSeconds));
            ct.ThrowIfCancellationRequested();
            File.Move(partial, destination, overwrite: false);
        }
        catch { try { File.Delete(partial); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } throw; }
    }
    public static async Task MoveVerifiedAsync(string source, string destination, string jobId, CancellationToken ct, bool resumePublishedTarget = false)
    {
        if (File.Exists(destination))
        {
            if (!resumePublishedTarget) throw new IOException("Archive name conflict: destination already exists.");
            var original = new FileInfo(source); var size = original.Length; var writeUtc = original.LastWriteTimeUtc; var createdUtc = original.CreationTimeUtc;
            var originalHash = await HashAsync(source, ct);
            if (await HashAsync(destination, ct) != originalHash) throw new IOException("Interrupted archive target differs from the source; both files were retained.");
            original.Refresh();
            if (!original.Exists || original.Length != size || original.LastWriteTimeUtc != writeUtc || original.CreationTimeUtc != createdUtc)
                throw new IOException("Source changed during archive recovery; both files were retained.");
            ct.ThrowIfCancellationRequested(); File.Delete(source); return;
        }
        try { File.Move(source, destination, overwrite: false); return; }
        catch (IOException) when (!File.Exists(destination) && File.Exists(source)) { }
        var before = new FileInfo(source); var length = before.Length; var write = before.LastWriteTimeUtc;
        var hash = await HashAsync(source, ct); var partial = destination + "." + jobId + ".partial";
        await CopyVerifiedAsync(source, destination, partial, hash, ct);
        var after = new FileInfo(source);
        if (after.Length != length || after.LastWriteTimeUtc != write || await HashAsync(source, ct) != hash)
            throw new IOException("Source changed during cross-volume move; original retained.");
        ct.ThrowIfCancellationRequested(); File.Delete(source);
    }
}
