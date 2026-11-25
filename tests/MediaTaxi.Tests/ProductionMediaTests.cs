using System.Security.Cryptography;
using System.Text.Json;
using MediaTaxi.Configuration;
using MediaTaxi.Media;

namespace MediaTaxi.Tests;

public sealed class RequiresProductionMediaAttribute : FactAttribute
{
    public RequiresProductionMediaAttribute()
    {
        if (!Directory.Exists(Environment.GetEnvironmentVariable("MEDIATAXI_PRODUCTION_MEDIA"))
            || !File.Exists(Environment.GetEnvironmentVariable("MEDIATAXI_FFMPEG"))
            || !File.Exists(Environment.GetEnvironmentVariable("MEDIATAXI_FFPROBE")))
            Skip = "Set MEDIATAXI_PRODUCTION_MEDIA plus MEDIATAXI_FFMPEG/FFPROBE for read-only production-fixture validation";
    }
}

public sealed class ProductionMediaTests
{
    [RequiresProductionMedia]
    public async Task AllSixProductionMxfFilesPreserveVideoPcmAndTimecodeAndOpenInRossReader()
    {
        var sourceDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("MEDIATAXI_PRODUCTION_MEDIA")!);
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(sourceDirectory, "REWRAP_RESULTS.json")));
        var options = new TaxiOptions
        {
            Media = new MediaOptions
            {
                FfmpegPath = Environment.GetEnvironmentVariable("MEDIATAXI_FFMPEG")!,
                FfprobePath = Environment.GetEnvironmentVariable("MEDIATAXI_FFPROBE")!, TimeoutSeconds = 600
            }
        };
        var project = FindProjectRoot();
        var outputRoot = Path.Combine(project, ".tools", "production-validation",
            "run-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(outputRoot);
        var service = new MediaService(options); var process = new MediaProcessRunner();
        var reports = new List<object>();
        var exampleRoot = Directory.GetParent(sourceDirectory)!.Parent!.FullName;
        var vendor = Path.Combine(exampleRoot, "app", "from program files(x86)", "Abekas", "Abekas Tria+");
        var rossHelper = Path.Combine(project, ".tools", "production-validation", "validate-ross.ps1");
        foreach (var item in reference.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = item.GetProperty("input_file").GetString()!;
            var source = Path.Combine(sourceDirectory, name);
            var work = Path.Combine(outputRoot, Path.GetFileNameWithoutExtension(name));
            var extracted = Path.Combine(work, "extracted");
            var output = Path.Combine(outputRoot, Path.ChangeExtension(name, ".clip"));
            var job = Guid.NewGuid().ToString("N");
            Assert.True(await service.IsNativeAsync(source, CancellationToken.None));
            var media = await service.PrepareAsync(source, work, null, CancellationToken.None);
            Assert.True(media.Native);
            Assert.Equal(item.GetProperty("frames").GetInt64(), media.FrameCount);
            Assert.Equal(item.GetProperty("timecode_offset_frames").GetInt64(), media.TimecodeStartFrames);
            Assert.Equal(item.GetProperty("source_video_sha256").GetString(), await HashAsync(media.VideoPath));
            var sourceChannels = item.GetProperty("source_audio_channels").GetInt32();
            Assert.Equal(item.GetProperty("source_pcm_s32le_sha256").GetString(), await HashSelectedPcmAsync(media.AudioPath, sourceChannels));
            var artifact = await service.WrapAsync(media, output, job, CancellationToken.None);
            var info = await FlatClip.ReadInfoAsync(output);
            Assert.Equal(media.FrameCount, info.FrameCount); Assert.Equal(media.TimecodeStartFrames, info.TimecodeStartFrames);
            Assert.Equal(Guid.Parse(job).ToString("D"), info.Uuid);
            var recovered = await service.ExtractClipAsync(output, extracted, CancellationToken.None);
            Assert.Equal(item.GetProperty("source_video_sha256").GetString(), await HashAsync(recovered.VideoPath));
            Assert.Equal(item.GetProperty("source_pcm_s32le_sha256").GetString(), await HashSelectedPcmAsync(recovered.AudioPath, sourceChannels));
            var metadata = await process.RunAsync(Path.Combine(vendor, "clipmeta.exe"), [output], 120, CancellationToken.None);
            Assert.Contains("<video_type>avci</video_type>", metadata.StandardOutput);
            object? nativeReader = null;
            if (OperatingSystem.IsWindows() && File.Exists(rossHelper))
            {
                var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", "WindowsPowerShell", "v1.0", "powershell.exe");
                var native = await process.RunAsync(powershell,
                    ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", rossHelper, "-DllDirectory", vendor, "-Clip", output],
                    600, CancellationToken.None);
                using var json = JsonDocument.Parse(native.StandardOutput);
                var value = json.RootElement;
                Assert.Equal(11, value.GetProperty("standard").GetInt32());
                Assert.Equal(media.FrameCount, value.GetProperty("frames").GetInt64());
                Assert.Equal(media.TimecodeStartFrames, value.GetProperty("timecode").GetInt64());
                Assert.False(value.GetProperty("dropFrame").GetBoolean());
                Assert.Equal(item.GetProperty("source_video_sha256").GetString(), value.GetProperty("videoSha256").GetString());
                Assert.Equal(8, value.GetProperty("audioChannels").GetInt32());
                nativeReader = value.Clone();
            }
            Assert.Equal(item.GetProperty("input_sha256").GetString(), await HashAsync(source));
            reports.Add(new
            {
                input = name, output, media.FrameCount, media.TimecodeStartFrames,
                artifact.Sha256, artifact.Uuid, sourceChannels,
                videoBitExact = true, pcmSampleExact = true, originalUnchanged = true,
                rossClipmetaAccepted = true, rossNativeRead = nativeReader
            });
            await File.WriteAllTextAsync(Path.Combine(outputRoot, "results.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            SafeDeleteOwnedDirectory(work, outputRoot);
            Console.WriteLine($"Verified C# production clip: {name}, {media.FrameCount} frames, TC {media.TimecodeStartFrames}");
        }
        Assert.Equal(6, reports.Count);
        Console.WriteLine("Production C# outputs and report: " + outputRoot);
    }

    private static async Task<string> HashAsync(string path)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
    }

    private static async Task<string> HashSelectedPcmAsync(string path, int count)
    {
        var info = PcmWave.ReadInfo(path); Assert.Equal(8, info.Channels);
        await using var source = File.OpenRead(path); source.Position = info.DataOffset;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[960 * 8 * 4]; var selected = new byte[960 * count * 4];
        for (long samples = 0; samples < info.Samples; samples += 960)
        {
            await source.ReadExactlyAsync(buffer);
            for (var index = 0; index < 960; index++)
            {
                buffer.AsSpan(index * 32, count * 4).CopyTo(selected.AsSpan(index * count * 4));
                Assert.False(buffer.AsSpan(index * 32 + count * 4, (8 - count) * 4).ContainsAnyExcept((byte)0));
            }
            sha.AppendData(selected);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static string FindProjectRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null)
        {
            if (File.Exists(Path.Combine(folder.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(folder.FullName, "src", "MediaTaxi"))) return folder.FullName;
            folder = folder.Parent;
        }
        throw new DirectoryNotFoundException("Media Taxi source root not found");
    }

    private static void SafeDeleteOwnedDirectory(string directory, string root)
    {
        var full = Path.GetFullPath(directory); var parent = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Test cleanup outside owned output root refused");
        Directory.Delete(full, recursive: true);
    }
}
