using System.Security.Cryptography;

namespace MediaTaxi.Media;

public static class StreamingClipWriter
{
    // One fixed AU plus bounded FileStream/OS pipe buffers, well below 8 MiB.
    public const int MaximumManagedBufferBytes = 2 * 1024 * 1024;
    public static async Task<WrappedSource> WriteAsync(string outputPath, string jobId, long timecode,
        string audioPath, Func<Func<Stream, CancellationToken, Task>, Task> produce,
        long maximumFrames, CancellationToken ct)
    {
        var temporary = outputPath + "." + jobId + ".partial";
        var uuid = Guid.Parse(jobId).ToString("D");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var frame = new byte[AvciValidator.FrameBytes]; var padding = new byte[512];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long frames = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(new byte[FlatClip.HeaderBytes], ct);
                await produce(async (video, token) =>
                {
                    while (true)
                    {
                        var count = await video.ReadAtLeastAsync(frame, frame.Length, throwOnEndOfStream: false, token);
                        if (count == 0) break;
                        if (count != frame.Length) throw new MediaMismatchException("Incomplete final AVC-Intra access unit.");
                        if (++frames > maximumFrames) throw new MediaMismatchException("Video exceeds the probed duration/cache reservation.");
                        AvciValidator.ValidateFrame(frame);
                        await WriteAsync(output, frame, hash, token); await WriteAsync(output, padding, hash, token);
                    }
                });
                if (frames == 0) throw new MediaMismatchException("No AVC-Intra video frames received.");
                if (!File.Exists(audioPath))
                {
                    await using var silence = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write);
                    await silence.WriteAsync(PcmWave.Header(frames * 960, 8), ct);
                    var zero = new byte[64 * 1024]; long left = frames * 960 * 8 * 4;
                    while (left > 0) { var n = (int)Math.Min(left, zero.Length); await silence.WriteAsync(zero.AsMemory(0, n), ct); left -= n; }
                }
                var audio = PcmWave.ReadInfo(audioPath);
                if (audio.Samples != frames * 960) throw new MediaMismatchException("Native audio/video durations differ.");
                var pcm = new byte[960 * audio.Channels * 4]; var auk = new byte[FlatClip.AudioStride];
                await using (var input = File.OpenRead(audioPath))
                {
                    input.Position = audio.DataOffset;
                    for (long n = 0; n < frames; n++)
                    {
                        await input.ReadExactlyAsync(pcm, ct);
                        PcmWave.PackRecord(pcm, audio.Channels, auk, unchecked((uint)(created + n)));
                        await WriteAsync(output, auk, hash, ct);
                    }
                }
                var header = FlatClip.BuildHeader(frames, uuid, timecode, created);
                output.Position = 0; await output.WriteAsync(header, ct);
                await output.FlushAsync(ct); output.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            var headerHash = Convert.ToHexStringLower(SHA256.HashData(FlatClip.BuildHeader(frames, uuid, timecode, created)));
            var payloadHash = Convert.ToHexStringLower(hash.GetHashAndReset());
            File.Move(temporary, outputPath, false);
            return new(new(outputPath, "", uuid, headerHash, payloadHash), frames);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task WriteAsync(Stream output, ReadOnlyMemory<byte> bytes, IncrementalHash hash, CancellationToken ct)
    { hash.AppendData(bytes.Span); await output.WriteAsync(bytes, ct); }
}
