using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MediaTaxi.Media;

namespace MediaTaxi.Tests;

public sealed class MediaLayoutTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "media-taxi-media-" + Guid.NewGuid().ToString("N"));
    public MediaLayoutTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, recursive: true);

    internal static byte[] Frame()
    {
        // Known synthetic AVC-Intra SPS; picture bytes are deliberately opaque in
        // these container tests. Real decoding is covered by FFmpeg integration tests.
        var parameterSets = Convert.FromHexString("00000001091000000001677a1032b6d420223319c6632321011198ce331918210256b93d7d7e4fe33f11f19e08b88c5443c0780227e2701100000300010000030064840000000168ce3348d00000000165888400");
        var bytes = new byte[AvciValidator.FrameBytes]; parameterSets.CopyTo(bytes, 0); return bytes;
    }

    [Fact]
    public async Task NativeContainerRoundtripPreservesEveryEssenceAndPcmByteAndTimecode()
    {
        var video = Path.Combine(directory, "video.h264");
        var audio = Path.Combine(directory, "audio.wav");
        var frames = Frame().Concat(Frame()).ToArray(); await File.WriteAllBytesAsync(video, frames);
        var pcm = new byte[1920 * 8 * 4];
        for (var i = 0; i < pcm.Length / 4; i++) BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(i * 4, 4), (i - 1000) << 8);
        await File.WriteAllBytesAsync(audio, PcmWave.Header(1920, 8).Concat(pcm).ToArray());
        var job = Guid.NewGuid().ToString("N"); var output = Path.Combine(directory, "native.clip");
        var artifact = await FlatClip.WriteAsync(new PreparedMedia(video, audio, 2, 8, 29452, true, "test"), output, job, CancellationToken.None);
        var info = await FlatClip.ReadInfoAsync(output);
        Assert.Equal(29452, info.TimecodeStartFrames); Assert.Equal(2, info.FrameCount);
        Assert.Equal(Guid.Parse(job).ToString("D"), info.Uuid);
        Assert.Equal(32768 + 2L * (569344 + 32768), new FileInfo(output).Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(output))).ToLowerInvariant(), artifact.Sha256);
        var extracted = await FlatClip.ExtractAsync(output, Path.Combine(directory, "extracted"), CancellationToken.None);
        Assert.Equal(frames, await File.ReadAllBytesAsync(extracted.VideoPath));
        var wave = PcmWave.ReadInfo(extracted.AudioPath);
        using var stream = File.OpenRead(extracted.AudioPath); stream.Position = wave.DataOffset;
        var actual = new byte[pcm.Length]; stream.ReadExactly(actual); Assert.Equal(pcm, actual);
    }

    [Fact]
    public void StereoChannelsArePackedInOrderWithSixSilentChannels()
    {
        var pcm = new byte[960 * 2 * 4];
        for (var i = 0; i < 960; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(i * 8, 4), i << 8);
            BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(i * 8 + 4, 4), -i << 8);
        }
        var auk = new byte[32768]; PcmWave.PackRecord(pcm, 2, auk, 100);
        var decoded = new byte[960 * 8 * 4]; PcmWave.InterleaveRecord(auk, decoded);
        for (var sample = 0; sample < 960; sample++)
        {
            Assert.Equal(sample << 8, BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(sample * 32, 4)));
            Assert.Equal(-sample << 8, BinaryPrimitives.ReadInt32LittleEndian(decoded.AsSpan(sample * 32 + 4, 4)));
            Assert.True(decoded.AsSpan(sample * 32 + 8, 24).ContainsAnyExcept((byte)0) == false);
        }
    }

    [Fact]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        var output = Path.Combine(directory, "keep.clip"); await File.WriteAllTextAsync(output, "keep");
        await Assert.ThrowsAsync<IOException>(() => FlatClip.WriteAsync(new PreparedMedia("missing", "missing", 1, 2, 0, true, ""), output, Guid.NewGuid().ToString("N"), CancellationToken.None));
        Assert.Equal("keep", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public void RejectsWrongProfileAndIncompleteAccessUnit()
    {
        Assert.Throws<MediaMismatchException>(() => AvciValidator.ValidateFrame(new byte[100]));
        var frame = Frame(); frame[11] = 100;
        Assert.Throws<MediaMismatchException>(() => AvciValidator.ValidateFrame(frame));
    }

    [Fact]
    public void ReadsEightChannelPcmWaveFormatExtensible()
    {
        var output = Path.Combine(directory, "extensible.wav");
        using (var writer = new BinaryWriter(File.Create(output)))
        {
            writer.Write("RIFF"u8); writer.Write(100U); writer.Write("WAVEfmt "u8); writer.Write(40U);
            writer.Write((ushort)0xfffe); writer.Write((ushort)8); writer.Write(48000); writer.Write(1536000);
            writer.Write((ushort)32); writer.Write((ushort)32); writer.Write((ushort)22); writer.Write((ushort)24); writer.Write(0U);
            writer.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray());
            writer.Write("data"u8); writer.Write(32U); writer.Write(new byte[32]);
        }
        var info = PcmWave.ReadInfo(output); Assert.Equal(8, info.Channels); Assert.Equal(1, info.Samples);
    }

    [Fact]
    public async Task MxfTimecodeUsesItsOwnBaseRatherThanPictureRate()
    {
        var path = Path.Combine(directory, "timecode.mxf");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Convert.FromHexString("060e2b34025301010d01010101011400")); writer.Write((byte)23);
            writer.Write(Convert.FromHexString("15010008"));
            var value = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(value, 14727); writer.Write(value);
            writer.Write(Convert.FromHexString("1502000200191503000100"));
        }
        Assert.Equal(29454, await MediaInspector.ReadTimecodeAsync(path, "00:09:49:02", 50, CancellationToken.None));
    }

    [Fact]
    public async Task CancelledWrappingDoesNotPublishOrLeaveAnOwnedPartial()
    {
        var video = Path.Combine(directory, "cancel.h264"); var audio = Path.Combine(directory, "cancel.wav");
        await File.WriteAllBytesAsync(video, Frame());
        await File.WriteAllBytesAsync(audio, PcmWave.Header(960, 8).Concat(new byte[960 * 32]).ToArray());
        var output = Path.Combine(directory, "cancel.clip"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FlatClip.WriteAsync(new PreparedMedia(video, audio, 1, 8, 0, true, ""), output, Guid.NewGuid().ToString("N"), cancellation.Token));
        Assert.False(File.Exists(output)); Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }
}
