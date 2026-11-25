using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MediaTaxi.Inventory;

namespace MediaTaxi.Tests;

public sealed class InventoryProtocolTests
{
    [Fact]
    public void FramingHandlesFragmentsCoalescedFramesAndHeartbeats()
    {
        byte[] first = OgpProtocol.Encode("handshake", new { detail = "full" });
        byte[] second = OgpProtocol.Encode("value", new { oid = "other" });
        byte[] all = [.. first, .. Encoding.ASCII.GetBytes("0:,"), .. second];
        var framer = new OgpFramer();
        List<byte[]> actual = [];
        for (int index = 0; index < all.Length; index += 3)
            actual.AddRange(framer.Push(all.AsSpan(index, Math.Min(3, all.Length - index))));
        Assert.Equal(3, actual.Count);
        Assert.Equal(first, actual[0]);
        Assert.Null(OgpProtocol.Decode(actual[1]));
        Assert.Equal("value", OgpProtocol.Decode(actual[2])!.Type);
    }

    [Theory]
    [InlineData("1:x;")]
    [InlineData("-1:x,")]
    [InlineData("1,2,3:xxx,")]
    [InlineData("33554433:")]
    [InlineData("999999999999999999999999999999999")]
    public void FramingRejectsMalformedOrOversizedMessages(string wire)
    {
        Assert.Throws<InvalidDataException>(() => new OgpFramer().Push(Encoding.ASCII.GetBytes(wire)));
    }

    [Fact]
    public void CompressedPayloadProducesAValidatedCompleteInventory()
    {
        byte[] frame = BinaryFrame("gzip", """
            {"oid":"explorer.raw_clip_list","element":false,"value":[{"clip_path":"IMPORTS/ one "},{"_d_clip_path":{"value":"IMPORTS/two"}}]}
            """);
        var framer = new OgpFramer();
        Assert.Empty(framer.Push(frame.AsSpan(0, 15)));
        var full = Assert.Single(framer.Push(frame.AsSpan(15)));
        var inventory = OgpProtocol.ReadInventory(OgpProtocol.Decode(full)!);
        Assert.Equal(InventoryMessageKind.Complete, inventory.Kind);
        Assert.Equal(new[] { "IMPORTS/ one ", "IMPORTS/two" }, inventory.Paths);
    }

    [Fact]
    public void DeviceBaselineAndValidEmptyInventoryAreAuthoritative()
    {
        var message = Decode("""{"type":"device","payload":{"params":{"_d_explorer.raw_clip_list":{"value":[]}}}}""");
        var inventory = OgpProtocol.ReadInventory(message);
        Assert.Equal(InventoryMessageKind.Complete, inventory.Kind);
        Assert.Empty(inventory.Paths);
    }

    [Fact]
    public void NullDirectPathCanUseTheWrappedParameterShape()
    {
        var message = Decode("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":[{"clip_path":null,"_d_clip_path":{"value":"IMPORTS/ONE"}}]}}""");
        Assert.Equal("IMPORTS/ONE", Assert.Single(OgpProtocol.ReadInventory(message).Paths));
    }

    [Theory]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list.0","value":[]}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","element":true,"value":[]}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","index":0,"value":[]}}""")]
    public void PartialUpdatesNeverBecomeAReplacementList(string json)
    {
        Assert.Equal(InventoryMessageKind.Partial, OgpProtocol.ReadInventory(Decode(json)).Kind);
    }

    [Theory]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":null}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":[42]}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":[{}]}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":[{"clip_path":""}]}}""")]
    [InlineData("""{"type":"value","payload":{"oid":"explorer.raw_clip_list","value":[{"clip_path":"bad\u0000name"}]}}""")]
    public void MalformedInventoryCannotClearCachedState(string json)
    {
        Assert.Throws<InvalidDataException>(() => OgpProtocol.ReadInventory(Decode(json)));
    }

    [Fact]
    public void NonInventoryTrafficIsIgnored()
    {
        Assert.Equal(InventoryMessageKind.Ignore, OgpProtocol.ReadInventory(Decode("""{"type":"value","payload":{"oid":"channel.0","value":3}}""")).Kind);
    }

    [Fact]
    public void BinaryEncodingAndExpandedSizeAreBounded()
    {
        Assert.Throws<InvalidDataException>(() => OgpProtocol.Decode(BinaryFrame("unknown", "{}")));
        string huge = new(' ', OgpFramer.MaximumPayloadBytes + 1);
        Assert.Throws<InvalidDataException>(() => OgpProtocol.Decode(BinaryFrame("gzip", huge)));
    }

    private static OgpMessage Decode(string json)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        byte[] frame = [.. Encoding.ASCII.GetBytes(bytes.Length + ":"), .. bytes, (byte)','];
        return OgpProtocol.Decode(frame)!;
    }

    private static byte[] BinaryFrame(string encoding, string json)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
        {
            byte[] value = Encoding.UTF8.GetBytes(json);
            gzip.Write(value);
        }
        byte[] header = JsonSerializer.SerializeToUtf8Bytes(new { type = "value", zip = encoding });
        byte[] binary = compressed.ToArray();
        return [.. Encoding.ASCII.GetBytes($"{header.Length},{binary.Length}:"), .. header, .. binary, (byte)','];
    }
}
