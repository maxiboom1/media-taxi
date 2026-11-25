using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace MediaTaxi.Inventory;

public sealed record OgpMessage(string Type, JsonElement Payload);
public enum InventoryMessageKind { Ignore, Partial, Complete }
public sealed record InventoryMessage(InventoryMessageKind Kind, IReadOnlyList<string> Paths);

/// <summary>Read-only inventory subset of the tested Tria OGP protocol.</summary>
public static class OgpProtocol
{
    public static byte[] Encode(string type, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { slot = 0, type, payload });
        byte[] prefix = Encoding.ASCII.GetBytes(body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
        byte[] frame = new byte[prefix.Length + body.Length + 1];
        prefix.CopyTo(frame, 0);
        body.CopyTo(frame, prefix.Length);
        frame[^1] = (byte)',';
        return frame;
    }

    public static OgpMessage? Decode(ReadOnlyMemory<byte> frame)
    {
        int colon = frame.Span.IndexOf((byte)':');
        if (colon < 0) throw new InvalidDataException("Missing OGP frame header.");
        var lengths = OgpFramer.ParseLengths(frame.Span[..colon]);
        if (frame.Length != colon + 1 + lengths.Json + lengths.Binary + 1 || frame.Span[^1] != (byte)',')
            throw new InvalidDataException("Incomplete or invalid OGP frame.");
        if (lengths.Json == 0) return null;

        using JsonDocument document = JsonDocument.Parse(frame.Slice(colon + 1, lengths.Json));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid OGP message type.");
        JsonElement payload = root.TryGetProperty("payload", out var value) ? value.Clone() : default;
        if (lengths.HasBinary)
        {
            if (!root.TryGetProperty("zip", out var zip) || zip.GetString() != "gzip")
                throw new InvalidDataException("Unsupported OGP binary encoding.");
            using var compressed = new MemoryStream(frame.Slice(colon + 1 + lengths.Json, lengths.Binary).ToArray(), false);
            using var unzip = new GZipStream(compressed, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = unzip.Read(chunk)) != 0)
            {
                if (output.Length + read > OgpFramer.MaximumPayloadBytes)
                    throw new InvalidDataException("Expanded OGP payload exceeds the limit.");
                output.Write(chunk, 0, read);
            }
            using JsonDocument binary = JsonDocument.Parse(output.ToArray());
            payload = binary.RootElement.Clone();
        }
        return new OgpMessage(type.GetString()!, payload);
    }

    public static InventoryMessage ReadInventory(OgpMessage message)
    {
        JsonElement payload = message.Payload;
        if (payload.ValueKind != JsonValueKind.Object) return Ignored();
        JsonElement parameter;
        if (message.Type == "device")
        {
            if (!payload.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
                !parameters.TryGetProperty("_d_explorer.raw_clip_list", out parameter)) return Ignored();
        }
        else if (message.Type == "value" && payload.TryGetProperty("oid", out var oid) && oid.ValueKind == JsonValueKind.String)
        {
            string name = oid.GetString()!;
            if (name.StartsWith("explorer.raw_clip_list.", StringComparison.Ordinal)) return Partial();
            if (name != "explorer.raw_clip_list") return Ignored();
            if (payload.TryGetProperty("index", out _) ||
                (payload.TryGetProperty("element", out var element) && element.ValueKind != JsonValueKind.False)) return Partial();
            parameter = payload;
        }
        else return Ignored();

        if (parameter.ValueKind != JsonValueKind.Object || !parameter.TryGetProperty("value", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Invalid OGP inventory array.");
        List<string> paths = [];
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid OGP inventory row.");
            string? path = null;
            if (row.TryGetProperty("clip_path", out var direct) && direct.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                if (direct.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid OGP clip path.");
                path = direct.GetString();
            }
            else if (row.TryGetProperty("_d_clip_path", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object && wrapped.TryGetProperty("value", out var inner))
            {
                if (inner.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid OGP clip path.");
                path = inner.GetString();
            }
            if (string.IsNullOrEmpty(path) || path.Contains('\0')) throw new InvalidDataException("Invalid OGP clip path.");
            paths.Add(path);
        }
        return new InventoryMessage(InventoryMessageKind.Complete, paths.AsReadOnly());
    }

    private static InventoryMessage Ignored() => new(InventoryMessageKind.Ignore, Array.Empty<string>());
    private static InventoryMessage Partial() => new(InventoryMessageKind.Partial, Array.Empty<string>());
}
