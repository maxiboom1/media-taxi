using System.Globalization;
using System.Text;

namespace MediaTaxi.Inventory;

/// <summary>Incremental Tria OGP netstrings, including the JSON,binary length form.</summary>
public sealed class OgpFramer
{
    public const int MaximumPayloadBytes = 32 * 1024 * 1024;
    private const int MaximumBufferedBytes = MaximumPayloadBytes + 64 * 1024 + 32;
    private byte[] buffer = new byte[4096];
    private int count;

    public IReadOnlyList<byte[]> Push(ReadOnlySpan<byte> incoming)
    {
        if ((long)count + incoming.Length > MaximumBufferedBytes)
            throw new InvalidDataException("OGP receive buffer limit exceeded.");
        if (buffer.Length < count + incoming.Length)
            Array.Resize(ref buffer, Math.Min(MaximumBufferedBytes, Math.Max(count + incoming.Length, buffer.Length * 2)));
        incoming.CopyTo(buffer.AsSpan(count));
        count += incoming.Length;

        List<byte[]> frames = [];
        int consumed = 0;
        while (consumed < count)
        {
            int relativeColon = buffer.AsSpan(consumed, count - consumed).IndexOf((byte)':');
            if (relativeColon < 0)
            {
                if (count - consumed > 30) throw new InvalidDataException("Invalid OGP netstring header.");
                break;
            }
            if (relativeColon > 30) throw new InvalidDataException("Invalid OGP netstring header.");
            var lengths = ParseLengths(buffer.AsSpan(consumed, relativeColon));
            int frameLength = checked(relativeColon + 1 + lengths.Json + lengths.Binary + 1);
            if (count - consumed < frameLength) break;
            if (buffer[consumed + frameLength - 1] != (byte)',')
                throw new InvalidDataException("Invalid OGP netstring terminator.");
            frames.Add(buffer.AsSpan(consumed, frameLength).ToArray());
            consumed += frameLength;
        }
        if (consumed > 0)
        {
            Buffer.BlockCopy(buffer, consumed, buffer, 0, count - consumed);
            count -= consumed;
        }
        return frames;
    }

    internal static (int Json, int Binary, bool HasBinary) ParseLengths(ReadOnlySpan<byte> header)
    {
        if (header.IsEmpty || header.Length > 30) throw new InvalidDataException("Invalid OGP length header.");
        int comma = header.IndexOf((byte)',');
        int json = ParseNumber(comma < 0 ? header : header[..comma]);
        int binary = comma < 0 ? 0 : ParseNumber(header[(comma + 1)..]);
        if ((long)json + binary > MaximumPayloadBytes) throw new InvalidDataException("OGP frame exceeds the payload limit.");
        return (json, binary, comma >= 0);
    }

    private static int ParseNumber(ReadOnlySpan<byte> digits)
    {
        if (digits.IsEmpty) throw new InvalidDataException("Missing OGP length.");
        foreach (byte digit in digits)
            if (digit is < (byte)'0' or > (byte)'9') throw new InvalidDataException("Invalid OGP length.");
        if (!int.TryParse(Encoding.ASCII.GetString(digits), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
            throw new InvalidDataException("Invalid OGP length.");
        return length;
    }
}
