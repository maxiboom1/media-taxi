using System.Buffers;

namespace MediaTaxi.Media;

/// <summary>Verifies the exact fixed-size AVC-Intra profile accepted by the tested Tria.</summary>
public static class AvciValidator
{
    public const int FrameBytes = 568832;
    public static ReadOnlySpan<byte> Aud => [0, 0, 0, 1, 9, 16];

    public static async Task<long> ValidateFileAsync(string path, CancellationToken cancellationToken)
    {
        var length = new FileInfo(path).Length;
        if (length == 0 || length % FrameBytes != 0)
            throw new MediaMismatchException($"AVC-Intra essence must contain whole {FrameBytes}-byte frames");
        var buffer = ArrayPool<byte>.Shared.Rent(FrameBytes);
        try
        {
            await using var source = File.OpenRead(path);
            for (long index = 0; index < length / FrameBytes; index++)
            {
                await source.ReadExactlyAsync(buffer.AsMemory(0, FrameBytes), cancellationToken);
                ValidateFrame(buffer.AsSpan(0, FrameBytes));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return length / FrameBytes;
    }

    public static void ValidateFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != FrameBytes || !frame.StartsWith(Aud))
            throw new MediaMismatchException("Expected a complete AVC-Intra access unit beginning with AUD");
        // AVC-Intra parameter sets precede slices. A bounded prefix avoids repeatedly
        // scanning half a megabyte of slice payload merely to verify unchanged SPS.
        // The tested AVC-Intra access units include VANC SEI before their first
        // IDR slice, which begins at byte9728. Keep parameter/slice inspection bounded.
        var prefix = frame[..Math.Min(frame.Length, 16384)];
        var nals = new List<(int Start, int End)>();
        for (var i = 0; i + 3 < prefix.Length; i++)
        {
            var code = prefix[i] == 0 && prefix[i + 1] == 0 && prefix[i + 2] == 1 ? 3
                : i + 4 < prefix.Length && prefix[i] == 0 && prefix[i + 1] == 0 && prefix[i + 2] == 0 && prefix[i + 3] == 1 ? 4 : 0;
            if (code == 0) continue;
            if (nals.Count > 0) nals[^1] = (nals[^1].Start, i);
            nals.Add((i + code, prefix.Length));
            i += code - 1;
        }
        var hasIdr = false;
        foreach (var nal in nals) if ((prefix[nal.Start] & 31) == 5) hasIdr = true;
        if (nals.Count < 4 || (prefix[nals[0].Start] & 31) != 9 || (prefix[nals[1].Start] & 31) != 7
            || (prefix[nals[2].Start] & 31) != 8 || !hasIdr)
            throw new MediaMismatchException("AVC-Intra frame requires AUD, SPS, PPS and IDR data");
        ValidateSps(prefix[nals[1].Start..nals[1].End]);
    }

    public static void ValidateSps(ReadOnlySpan<byte> nal)
    {
        var rbsp = new List<byte>(nal.Length);
        for (var i = 1; i < nal.Length; i++)
        {
            if (i >= 3 && nal[i] == 3 && nal[i - 1] == 0 && nal[i - 2] == 0) continue;
            rbsp.Add(nal[i]);
        }
        var bits = new BitReader(rbsp.ToArray());
        var profile = bits.Read(8); var constraints = bits.Read(8); bits.Read(8); bits.Ue();
        if (profile != 122 || (constraints & 0x10) == 0)
            throw new MediaMismatchException("Video must be High 4:2:2 Intra AVC");
        var chroma = bits.Ue();
        if (chroma == 3) bits.Read(1);
        var lumaDepth = bits.Ue() + 8; var chromaDepth = bits.Ue() + 8;
        bits.Read(1);
        if (bits.Read(1) == 1)
            for (var index = 0; index < (chroma == 3 ? 12 : 8); index++)
                if (bits.Read(1) == 1)
                {
                    var last = 8; var next = 8;
                    for (var j = 0; j < (index < 6 ? 16 : 64); j++)
                    {
                        if (next != 0) next = (last + bits.Se() + 256) % 256;
                        last = next == 0 ? last : next;
                    }
                }
        bits.Ue(); var poc = bits.Ue();
        if (poc == 0) bits.Ue();
        else if (poc == 1)
        {
            bits.Read(1); bits.Se(); bits.Se();
            var count = bits.Ue(); if (count > 256) throw new MediaMismatchException("Invalid AVC SPS cycle count");
            for (var i = 0; i < count; i++) bits.Se();
        }
        else if (poc != 2) throw new MediaMismatchException("Invalid AVC picture order count");
        bits.Ue(); bits.Read(1);
        var width = (bits.Ue() + 1) * 16; var height = (bits.Ue() + 1) * 16;
        var progressive = bits.Read(1); height *= 2 - progressive;
        if (progressive == 0) bits.Read(1);
        bits.Read(1);
        if (bits.Read(1) == 1)
        {
            var left = bits.Ue(); var right = bits.Ue(); var top = bits.Ue(); var bottom = bits.Ue();
            width -= (chroma is 1 or 2 ? 2 : 1) * (left + right);
            height -= (chroma == 1 ? 2 : 1) * (2 - progressive) * (top + bottom);
        }
        if (chroma != 2 || lumaDepth != 10 || chromaDepth != 10 || width != 1920 || height != 1080 || progressive != 1)
            throw new MediaMismatchException("Video SPS must describe progressive 1920x1080, 10-bit 4:2:2");
    }

    private sealed class BitReader(byte[] bytes)
    {
        private int offset;
        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                if (offset >= bytes.Length * 8) throw new MediaMismatchException("Truncated AVC SPS");
                value = (value << 1) | ((bytes[offset / 8] >> (7 - offset % 8)) & 1); offset++;
            }
            return value;
        }
        public int Ue()
        {
            var zeros = 0;
            while (Read(1) == 0) if (++zeros > 24) throw new MediaMismatchException("Invalid AVC Exp-Golomb value");
            return (1 << zeros) - 1 + Read(zeros);
        }
        public int Se() { var value = Ue(); return (value & 1) == 1 ? (value + 1) / 2 : -value / 2; }
    }
}
