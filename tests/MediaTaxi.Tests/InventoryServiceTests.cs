using System.Net;
using System.Net.Sockets;
using System.Text;
using MediaTaxi.Configuration;
using MediaTaxi.Inventory;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaTaxi.Tests;

public sealed class InventoryServiceTests
{
    [Fact]
    public void ConstructorStartsWithIndependentDisconnectedBaselines()
    {
        using var service = new InventoryService(new TaxiOptions
        {
            Destinations = [new() { Id = "one", Name = "TLV1" }, new() { Id = "two", Name = "TLV2" }]
        }, NullLogger<InventoryService>.Instance);
        Assert.Equal(new[] { "TLV1", "TLV2" }, service.GetSnapshots().Select(x => x.Name));
        Assert.All(service.GetSnapshots(), snapshot =>
        {
            Assert.False(snapshot.Connected);
            Assert.False(snapshot.Ready);
            Assert.Null(snapshot.LastInventoryUtc);
            Assert.Empty(snapshot.Paths);
        });
    }

    [Fact]
    public void MatchingPreservesSignificantSpacesAndUsesConfiguredFolder()
    {
        var snapshot = new InventorySnapshot("a", "A", true, true, DateTimeOffset.UtcNow, null,
            new[] { "IMPORTS\\ name .clip", "IMPORTS/two", "OTHER/three" });
        Assert.True(InventoryMatching.Contains(snapshot, "imports", " name "));
        Assert.True(InventoryMatching.Contains(snapshot, "/IMPORTS/", "TWO.clip"));
        Assert.False(InventoryMatching.Contains(snapshot, "IMPORTS", "name"));
        Assert.False(InventoryMatching.Contains(snapshot, "IMPORTS", "three"));
        Assert.False(InventoryMatching.Contains(snapshot with { Ready = false }, "IMPORTS", "two"));
    }

    [Fact]
    public async Task LoopbackSessionRefreshesPartialUpdatesPreservesCacheAndAcceptsEmptyBaseline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var service = new InventoryService(new TaxiOptions
        {
            Timing = new() { InventoryTimeoutSeconds = 1, InventoryPollSeconds = 60, ReconnectSeconds = .05 },
            Destinations = [new() { Id = "a", Name = "Test", Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port }]
        }, NullLogger<InventoryService>.Instance);
        try
        {
            await service.StartAsync(deadline.Token);
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = client.GetStream();
            var handshake = await ReadMessageAsync(stream, deadline.Token);
            Assert.Equal("handshake", handshake.Type);
            Assert.Equal("full", handshake.Payload.GetProperty("detail").GetString());
            Assert.False(handshake.Payload.GetProperty("trusted").GetBoolean());
            await stream.WriteAsync(FullList("IMPORTS/TEST"), deadline.Token);
            await WaitUntilAsync(() => service.GetSnapshots()[0].Ready, deadline.Token);
            Assert.Equal("IMPORTS/TEST", Assert.Single(service.GetSnapshots()[0].Paths));

            var previousInventory = service.GetSnapshots()[0].LastInventoryUtc;
            service.RequestRefresh("a");
            var afterPublication = await ReadMessageAsync(stream, deadline.Token);
            Assert.Equal("value-request", afterPublication.Type);
            Assert.Equal("explorer.raw_clip_list", afterPublication.Payload.GetProperty("oid").GetString());
            await stream.WriteAsync(FullList("IMPORTS/TEST"), deadline.Token);
            await WaitUntilAsync(() => service.GetSnapshots()[0].LastInventoryUtc > previousInventory, deadline.Token);

            await stream.WriteAsync(OgpProtocol.Encode("value", new { oid = "explorer.raw_clip_list.0", value = "changed" }), deadline.Token);
            var requested = await ReadMessageAsync(stream, deadline.Token);
            Assert.Equal("value-request", requested.Type);
            Assert.Equal("explorer.raw_clip_list", requested.Payload.GetProperty("oid").GetString());
            Assert.False(requested.Payload.GetProperty("element").GetBoolean());

            // Malformed inventory kills this generation without replacing its valid cached list.
            await stream.WriteAsync(OgpProtocol.Encode("value", new { oid = "explorer.raw_clip_list", value = new[] { 42 } }), deadline.Token);
            await WaitUntilAsync(() => !service.GetSnapshots()[0].Ready, deadline.Token);
            Assert.Equal("IMPORTS/TEST", Assert.Single(service.GetSnapshots()[0].Paths));

            using var reconnected = await listener.AcceptTcpClientAsync(deadline.Token);
            using var newStream = reconnected.GetStream();
            Assert.Equal("handshake", (await ReadMessageAsync(newStream, deadline.Token)).Type);
            await newStream.WriteAsync(FullList(), deadline.Token);
            await WaitUntilAsync(() => service.GetSnapshots()[0].Ready, deadline.Token);
            Assert.Empty(service.GetSnapshots()[0].Paths);
            Assert.NotNull(service.GetSnapshots()[0].LastInventoryUtc);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            listener.Stop();
        }
    }

    [Fact]
    public async Task MissingHostIsUnhealthyWithoutAttemptingAConnection()
    {
        using var service = new InventoryService(new TaxiOptions { Destinations = [new() { Id = "a", Name = "A" }] }, NullLogger<InventoryService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => service.GetSnapshots()[0].Error is not null, CancellationToken.None);
        Assert.False(service.GetSnapshots()[0].Ready);
        Assert.Contains("not configured", service.GetSnapshots()[0].Error!);
        await service.StopAsync(CancellationToken.None);
    }

    private static byte[] FullList(params string[] paths) => OgpProtocol.Encode("device", new
    {
        @params = new Dictionary<string, object> { ["_d_explorer.raw_clip_list"] = new { value = paths.Select(path => new { clip_path = path }).ToArray() } }
    });

    private static async Task<OgpMessage> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        var framer = new OgpFramer();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token);
            if (read == 0) throw new IOException("Mock OGP stream closed.");
            foreach (var frame in framer.Push(buffer.AsSpan(0, read)))
            {
                var message = OgpProtocol.Decode(frame);
                if (message is not null) return message;
            }
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}
