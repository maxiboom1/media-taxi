using System.Buffers.Binary;
using System.Text;

namespace MediaTaxi.Media;

public sealed record WaveInfo(long DataOffset, long DataBytes, int Channels, int SampleRate, int Bits)
{
    public long Samples => DataBytes / (Channels * Bits / 8);
}

/// <summary>Bounded RIFF/RF64 parser; supports integer PCM and PCM WAVEFORMATEXTENSIBLE.</summary>
public static class PcmWave
{
    public static WaveInfo ReadInfo(string path)
    {
        using var source = File.OpenRead(path);
        using var reader = new BinaryReader(source, Encoding.ASCII, leaveOpen: true);
        var riff = Encoding.ASCII.GetString(reader.ReadBytes(4));
        if (riff is not ("RIFF" or "RF64")) throw new MediaMismatchException("Audio must be a little-endian PCM WAV");
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") throw new MediaMismatchException("Missing WAVE signature");
        long? rfData = null;
        var channels = 0; var rate = 0; var bits = 0; var pcm = false;
        while (source.Position + 8 <= source.Length)
        {
            var tag = Encoding.ASCII.GetString(reader.ReadBytes(4));
            long size = reader.ReadUInt32();
            var start = source.Position;
            if (tag == "ds64")
            {
                if (size < 28) throw new MediaMismatchException("Invalid RF64 size chunk");
                reader.ReadUInt64(); rfData = checked((long)reader.ReadUInt64());
            }
            else if (tag == "fmt ")
            {
                if (size < 16 || size > 4096) throw new MediaMismatchException("Invalid WAV format chunk");
                var format = reader.ReadUInt16(); channels = reader.ReadUInt16(); rate = reader.ReadInt32();
                reader.ReadUInt32(); reader.ReadUInt16(); bits = reader.ReadUInt16();
                pcm = format == 1;
                if (format == 0xfffe && size >= 40)
                {
                    reader.ReadUInt16(); var validBits = reader.ReadUInt16(); reader.ReadUInt32();
                    var guid = new Guid(reader.ReadBytes(16));
                    pcm = guid == new Guid("00000001-0000-0010-8000-00aa00389b71") && validBits <= bits;
                }
            }
            else if (tag == "data")
            {
                if (size == uint.MaxValue) size = rfData ?? throw new MediaMismatchException("RF64 data size missing");
                if (!pcm || channels is < 1 or > 8 || rate != 48000 || bits != 32)
                    throw new MediaMismatchException("Prepared audio must be 48 kHz signed PCM32 with 1..8 ordered channels");
                if (size < 0 || source.Position + size > source.Length || size % (channels * 4) != 0)
                    throw new MediaMismatchException("Truncated or misaligned WAV audio");
                return new WaveInfo(source.Position, size, channels, rate, bits);
            }
            var next = checked(start + size + (size & 1));
            if (next > source.Length) throw new MediaMismatchException("Truncated WAV chunk");
            source.Position = next;
        }
        throw new MediaMismatchException("WAV contains no PCM data");
    }

    public static byte[] Header(long samples, int channels)
    {
        var dataBytes = checked(samples * channels * 4);
        var rf64 = dataBytes + 36 > uint.MaxValue;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes(rf64 ? "RF64" : "RIFF"));
        writer.Write(rf64 ? uint.MaxValue : checked((uint)(dataBytes + 36)));
        writer.Write("WAVE"u8);
        if (rf64)
        {
            writer.Write("ds64"u8); writer.Write(28U); writer.Write(checked((ulong)(dataBytes + 72)));
            writer.Write((ulong)dataBytes); writer.Write((ulong)samples); writer.Write(0U);
        }
        writer.Write("fmt "u8); writer.Write(16U); writer.Write((ushort)1); writer.Write((ushort)channels);
        writer.Write(48000); writer.Write(channels * 48000 * 4); writer.Write((ushort)(channels * 4)); writer.Write((ushort)32);
        writer.Write("data"u8); writer.Write(rf64 ? uint.MaxValue : checked((uint)dataBytes));
        return stream.ToArray();
    }

    public static void PackRecord(ReadOnlySpan<byte> pcm, int inputChannels, Span<byte> auk, uint sequence)
    {
        auk.Clear();
        for (var channel = 0; channel < 8; channel++)
        {
            var block = auk.Slice(channel * 4096, 4096);
            block[0] = 6; block[1] = 165; block[2] = 236; block[3] = 171;
            block[6] = (byte)channel; block[7] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(block.Slice(8, 4), sequence);
            if (channel >= inputChannels) continue;
            for (var sample = 0; sample < 960; sample++)
                pcm.Slice((sample * inputChannels + channel) * 4, 4).CopyTo(block.Slice(32 + sample * 4, 4));
        }
    }
}
