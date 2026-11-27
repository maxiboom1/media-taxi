using System.Buffers;
using MediaTaxi.Media;
namespace MediaTaxi.Tests;

internal static class ClipTestSupport
{
    public static async Task<PreparedMedia> ExtractAsync(string path, string workDirectory, CancellationToken cancellationToken)
    {
        var info = await FlatClip.ReadInfoAsync(path, cancellationToken);
        Directory.CreateDirectory(workDirectory);
        var videoPath = Path.Combine(workDirectory, "proxy-video.h264");
        var audioPath = Path.Combine(workDirectory, "proxy-audio.wav");
        var record = ArrayPool<byte>.Shared.Rent(FlatClip.VideoStride);
        var pcm = new byte[960 * 8 * 4];
        try
        {
            await using var source = File.OpenRead(path);
            await using (var video = new FileStream(videoPath, FileMode.CreateNew, FileAccess.Write))
            {
                source.Position = info.VideoOffset;
                for (long frame = 0; frame < info.FrameCount; frame++)
                {
                    await source.ReadExactlyAsync(record.AsMemory(0, FlatClip.VideoStride), cancellationToken);
                    AvciValidator.ValidateFrame(record.AsSpan(0, AvciValidator.FrameBytes));
                    if (record.AsSpan(AvciValidator.FrameBytes, 512).ContainsAnyExcept((byte)0)) throw new MediaMismatchException("Unsupported nonzero video trailer");
                    await video.WriteAsync(record.AsMemory(0, AvciValidator.FrameBytes), cancellationToken);
                }
            }
            await using var audio = new FileStream(audioPath, FileMode.CreateNew, FileAccess.Write);
            await audio.WriteAsync(PcmWave.Header(info.FrameCount * 960, 8), cancellationToken);
            if (info.HasAudio) source.Position = info.AudioOffset;
            for (long frame = 0; frame < info.FrameCount; frame++)
            {
                if (info.HasAudio)
                {
                    await source.ReadExactlyAsync(record.AsMemory(0, FlatClip.AudioStride), cancellationToken);
                    InterleaveRecord(record.AsSpan(0, FlatClip.AudioStride), pcm);
                }
                await audio.WriteAsync(pcm, cancellationToken);
            }
            return new PreparedMedia(videoPath, audioPath, info.FrameCount, 8, info.TimecodeStartFrames, true, "Recovered native AVC-Intra100 1080p50 clip");
        }
        finally { ArrayPool<byte>.Shared.Return(record); }
    }


    public static async Task<long> ValidateFileAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || length % AvciValidator.FrameBytes != 0)
            throw new MediaMismatchException($"AVC-Intra essence must contain whole {AvciValidator.FrameBytes}-byte frames");
        var buffer = ArrayPool<byte>.Shared.Rent(AvciValidator.FrameBytes);
        try
        {
            await using var source = File.OpenRead(path);
            for (long index = 0; index < length / AvciValidator.FrameBytes; index++)
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, AvciValidator.FrameBytes), cancellationToken);
                AvciValidator.ValidateFrame(buffer.AsSpan(0, AvciValidator.FrameBytes));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return length / AvciValidator.FrameBytes;
    }


    public static void InterleaveRecord(ReadOnlySpan<byte> auk, Span<byte> pcm)
    {
        ReadOnlySpan<byte> magic = [6, 165, 236, 171];
        for (var channel = 0; channel < 8; channel++)
        {
            var block = auk.Slice(channel * 4096, 4096);
            if (!block[..4].SequenceEqual(magic) || block[6] != channel || block[7] != 1)
                throw new MediaMismatchException("Invalid AUK channel header");
            for (var sample = 0; sample < 960; sample++)
                block.Slice(32 + sample * 4, 4).CopyTo(pcm.Slice((sample * 8 + channel) * 4, 4));
        }
    }

}
