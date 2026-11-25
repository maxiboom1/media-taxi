using MediaTaxi.Configuration;
using MediaTaxi.Domain;

namespace MediaTaxi.Storage;

public sealed record SourceFile(string Path, SourceLocation Location, long Size, DateTimeOffset WriteUtc, DateTimeOffset CreatedUtc);
public sealed record FolderScan(bool Complete, IReadOnlyList<SourceFile> Files, IReadOnlyList<FolderHealth> Health);

public sealed class FolderScanner(TaxiOptions options)
{
    public FolderScan Scan()
    {
        var files = new List<SourceFile>(); var health = new List<FolderHealth>(); var complete = true;
        foreach (var (location, path) in new[] { (SourceLocation.Watch, options.Folders.Watch), (SourceLocation.Finished, options.Folders.Finished), (SourceLocation.Failed, options.Folders.Failed) })
        {
            var own = new List<SourceFile>();
            try
            {
                // Enumerate completely before accepting this root's snapshot; never File.Exists(false) => deletion.
                foreach (var name in Directory.EnumerateFiles(path, "*", new EnumerationOptions { IgnoreInaccessible = false, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    if (Temporary(name)) continue;
                    var f = new FileInfo(name); own.Add(new(f.FullName, location, f.Length, f.LastWriteTimeUtc, f.CreationTimeUtc));
                }
                var h = FolderAccess.Check(location.ToString(), path, true); health.Add(h);
                if (!h.Readable) complete = false;
                files.AddRange(own);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { complete = false; health.Add(new(location.ToString(), path, false, false, null, ex.Message)); }
        }
        return new(complete, files, health);
    }
    public static bool Temporary(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return name.StartsWith('.') || name.StartsWith('~') || new[] { ".tmp", ".part", ".partial", ".crdownload" }.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }
}

public static class FolderAccess
{
    public static FolderHealth Check(string id, string path, bool testWrite)
    {
        string? probe = null;
        try
        {
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToArray();
            if (testWrite)
            {
                probe = Path.Combine(path, $".media-taxi-health-{Guid.NewGuid():N}.tmp");
                using (var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) file.WriteByte(0);
                File.Delete(probe); probe = null;
            }
            long? free = null;
            try { free = new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
            return new(id, path, true, testWrite, free, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(id, path, Directory.Exists(path), false, null, ex.Message); }
        finally { if (probe is not null) try { File.Delete(probe); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
}
