using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace MediaTaxi.Media;

public sealed record FlatClipInfo(string Uuid, long FrameCount, long VideoOffset,
    long AudioOffset, bool HasAudio, long TimecodeStartFrames);

public static class FlatClip
{
    public const int HeaderBytes = 32768;
    public const int VideoStride = AvciValidator.FrameBytes + 512;
    public const int AudioStride = 32768;

    public static async Task<ClipArtifact> WriteAsync(PreparedMedia media, string outputPath,
        string jobId, CancellationToken cancellationToken)
    {
        var uuid = Guid.Parse(jobId).ToString("D");
        var temporary = outputPath + "." + Guid.Parse(jobId).ToString("N") + ".partial";
        if (File.Exists(outputPath)) throw new IOException($"Output already exists: {outputPath}");
        var audio = PcmWave.ReadInfo(media.AudioPath);
        if (audio.Samples != checked(media.FrameCount * 960)) throw new MediaMismatchException($"Audio/video duration mismatch: expected {media.FrameCount * 960} PCM samples, got {audio.Samples}; delta {audio.Samples - media.FrameCount * 960} samples (strict matching)");
        if (new FileInfo(media.VideoPath).Length != checked(media.FrameCount * AvciValidator.FrameBytes))
            throw new MediaMismatchException("Video length changed after preparation");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        var videoBuffer = ArrayPool<byte>.Shared.Rent(AvciValidator.FrameBytes);
        var audioBuffer = ArrayPool<byte>.Shared.Rent(960 * audio.Channels * 4);
        var auk = new byte[AudioStride];
        var padding = new byte[512];
        var createdUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var payloadHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await WriteHashAsync(output, BuildHeader(media.FrameCount, uuid, media.TimecodeStartFrames, createdUnix), hash, cancellationToken);
                await using (var video = File.OpenRead(media.VideoPath))
                {
                    for (long frame = 0; frame < media.FrameCount; frame++)
                    {
                        await video.ReadExactlyAsync(videoBuffer.AsMemory(0, AvciValidator.FrameBytes), cancellationToken);
                        AvciValidator.ValidateFrame(videoBuffer.AsSpan(0, AvciValidator.FrameBytes));
                        await WriteHashAsync(output, videoBuffer.AsMemory(0, AvciValidator.FrameBytes), hash, cancellationToken, payloadHash);
                        await WriteHashAsync(output, padding, hash, cancellationToken, payloadHash);
                    }
                }
                await using var input = File.OpenRead(media.AudioPath);
                input.Position = audio.DataOffset;
                var sequence = unchecked((uint)createdUnix);
                for (long frame = 0; frame < media.FrameCount; frame++)
                {
                    await input.ReadExactlyAsync(audioBuffer.AsMemory(0, 960 * audio.Channels * 4), cancellationToken);
                    PcmWave.PackRecord(audioBuffer.AsSpan(0, 960 * audio.Channels * 4), audio.Channels, auk, unchecked(sequence + (uint)frame));
                    await WriteHashAsync(output, auk, hash, cancellationToken, payloadHash);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath, overwrite: false);
            return new ClipArtifact(outputPath, Convert.ToHexStringLower(hash.GetHashAndReset()), uuid,
                Convert.ToHexStringLower(SHA256.HashData(BuildHeader(media.FrameCount, uuid, media.TimecodeStartFrames, createdUnix))),
                Convert.ToHexStringLower(payloadHash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(videoBuffer); ArrayPool<byte>.Shared.Return(audioBuffer);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static byte[] BuildHeader(long frames, string uuid, long timecode, long? createdUnix = null)
    {
        if (frames <= 0 || timecode is < 0 or >= 4_320_000) throw new MediaMismatchException("Invalid duration or 50fps start timecode");
        var now = createdUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        XElement Track(string kind, long start, long length) => new("TrackMeta",
            new XElement("TrackType", kind), new XElement("VFile", new XElement("Partition",
                new XElement("Start", start), new XElement("Length", length), new XElement("VFile", new XElement("ThisFile")))));
        var videoBytes = checked(frames * VideoStride);
        var root = new XElement("AbekasClipMeta1", new XElement("VideoStandard", "1080_50p"),
            new XElement("UUID", "{" + Guid.Parse(uuid).ToString("D") + "}"),
            new XElement("ClipLastContentEdited", now), new XElement("ClipLastMetaEdited", now),
            new XElement("TrackMetaVector", Track("avci100-1080p", 0, videoBytes), Track("auk", videoBytes, checked(frames * AudioStride))),
            new XElement("TimecodeSource", "striped"), new XElement("Striped_TC_Offset", timecode), new XElement("Striped_TC_DF", "ndf"));
        var xml = Encoding.UTF8.GetBytes(root.ToString(SaveOptions.DisableFormatting));
        if (xml.Length > HeaderBytes) throw new MediaMismatchException("Clip metadata exceeds header");
        var result = new byte[HeaderBytes]; xml.CopyTo(result, 0); return result;
    }

    public static async Task<FlatClipInfo> ReadInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderBytes];
        await using var source = File.OpenRead(path);
        await source.ReadExactlyAsync(header, cancellationToken);
        var terminator = "</AbekasClipMeta1>"u8;
        var end = header.AsSpan().IndexOf(terminator);
        if (end < 0) throw new MediaMismatchException("Missing AbekasClipMeta1 header");
        using var xmlReader = XmlReader.Create(new MemoryStream(header, 0, end + terminator.Length),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = HeaderBytes });
        var root = XElement.Load(xmlReader);
        if ((string?)root.Element("VideoStandard") != "1080_50p") throw new MediaMismatchException("Unsupported clip video standard");
        var allowed = new HashSet<string>(["VideoStandard", "UUID", "ClipLastContentEdited", "ClipLastMetaEdited", "ClipLastLoaded", "ClipLastPlayed", "TrackMetaVector", "TimecodeSource", "Striped_TC_Offset", "Striped_TC_DF"]);
        if (root.Elements().Any(x => !allowed.Contains(x.Name.LocalName))) throw new MediaMismatchException("Edited/trimmed clip metadata is unsupported");
        var tracks = root.Element("TrackMetaVector")?.Elements("TrackMeta").ToArray() ?? [];
        if (tracks.Length is < 1 or > 2) throw new MediaMismatchException("Unsupported clip tracks");
        (long Offset, long Length) Extent(XElement track)
        {
            var vfile = track.Element("VFile"); var part = vfile?.Element("Partition");
            if (!track.Elements().Select(x => x.Name.LocalName).SequenceEqual(["TrackType", "VFile"])
                || part is null || vfile!.Elements().Count() != 1
                || !part.Elements().Select(x => x.Name.LocalName).SequenceEqual(["Start", "Length", "VFile"])
                || part.Element("VFile")?.Element("ThisFile") is null
                || part.Element("VFile")!.Elements().Count() != 1
                || !string.IsNullOrWhiteSpace((string?)part.Element("VFile")!.Element("ThisFile")))
                throw new MediaMismatchException("Sparse, external or virtual clip storage is unsupported");
            var offset = checked(HeaderBytes + (long)part.Element("Start")!); var length = (long)part.Element("Length")!;
            if (offset < HeaderBytes || length <= 0 || checked(offset + length) > source.Length)
                throw new MediaMismatchException("Invalid or incomplete clip extent");
            return (offset, length);
        }
        if ((string?)tracks[0].Element("TrackType") != "avci100-1080p") throw new MediaMismatchException("Unsupported video track");
        var video = Extent(tracks[0]);
        if (video.Length % VideoStride != 0) throw new MediaMismatchException("Invalid native video record size");
        var frames = video.Length / VideoStride;
        long audioOffset = 0;
        if (tracks.Length == 2)
        {
            if ((string?)tracks[1].Element("TrackType") != "auk") throw new MediaMismatchException("Unsupported audio track");
            var audio = Extent(tracks[1]);
            if (audio.Length != frames * AudioStride || audio.Offset < video.Offset + video.Length)
                throw new MediaMismatchException("Invalid audio extent or duration");
            audioOffset = audio.Offset;
        }
        var tcSource = (string?)root.Element("TimecodeSource");
        if (tcSource is not (null or "striped" or "zerobased")) throw new MediaMismatchException("Unsupported clip timecode source");
        var tc = tcSource == "striped" ? (long?)root.Element("Striped_TC_Offset") ?? 0 : 0;
        if (tc is < 0 or >= 4_320_000 || (string?)root.Element("Striped_TC_DF") is not (null or "ndf"))
            throw new MediaMismatchException("Unsupported clip timecode value or drop-frame flag");
        return new FlatClipInfo(Guid.Parse((string?)root.Element("UUID") ?? "").ToString("D"), frames, video.Offset, audioOffset, tracks.Length == 2, tc);
    }

    private static async Task WriteHashAsync(Stream output, ReadOnlyMemory<byte> data, IncrementalHash hash, CancellationToken token, IncrementalHash? payload = null)
    { await output.WriteAsync(data, token); hash.AppendData(data.Span); payload?.AppendData(data.Span); }
}
