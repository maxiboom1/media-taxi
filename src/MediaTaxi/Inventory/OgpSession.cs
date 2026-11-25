using System.Diagnostics;
using System.Net.Sockets;
using MediaTaxi.Configuration;

namespace MediaTaxi.Inventory;

internal sealed class OgpSession(DestinationOptions destination, TimingOptions timing,
    Action<bool, bool, string?, IReadOnlyList<string>?> publish)
{
    private static readonly byte[] Handshake = OgpProtocol.Encode("handshake", new { trusted = false, force = false, detail = "full" });
    private static readonly byte[] InventoryRequest = OgpProtocol.Encode("value-request", new { oid = "explorer.raw_clip_list", element = false });

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        publish(false, false, null, null);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var client = new TcpClient();
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        using (var connecting = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
        {
            connecting.CancelAfter(TimeSpan.FromSeconds(timing.InventoryTimeoutSeconds));
            await client.ConnectAsync(destination.Host, destination.Port, connecting.Token);
        }
        publish(true, false, null, null);
        using var stream = client.GetStream();
        var framer = new OgpFramer();
        byte[] buffer = new byte[64 * 1024];
        await stream.WriteAsync(Handshake, connection.Token);
        long lastReceived = Stopwatch.GetTimestamp();
        long lastRequest = lastReceived;
        long? pendingSince = lastReceived;
        bool ready = false, fullReadPending = false;
        Task<int>? reading = null;
        try
        {
            reading = stream.ReadAsync(buffer, connection.Token).AsTask();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task tick = Task.Delay(100, connection.Token);
                if (await Task.WhenAny(reading, tick) == reading)
                {
                    int received = await reading;
                    if (received == 0) throw new IOException("OGP connection closed.");
                    lastReceived = Stopwatch.GetTimestamp();
                    foreach (byte[] frame in framer.Push(buffer.AsSpan(0, received)))
                    {
                        var message = OgpProtocol.Decode(frame);
                        if (message is null) continue;
                        var inventory = OgpProtocol.ReadInventory(message);
                        if (inventory.Kind == InventoryMessageKind.Partial && !fullReadPending)
                        {
                            await stream.WriteAsync(InventoryRequest, connection.Token);
                            fullReadPending = true;
                            pendingSince = lastRequest = Stopwatch.GetTimestamp();
                        }
                        else if (inventory.Kind == InventoryMessageKind.Complete)
                        {
                            pendingSince = null;
                            fullReadPending = false;
                            ready = true;
                            publish(true, true, null, inventory.Paths);
                        }
                    }
                    reading = stream.ReadAsync(buffer, connection.Token).AsTask();
                }
                if (pendingSince is long pending && Stopwatch.GetElapsedTime(pending).TotalSeconds >= timing.InventoryTimeoutSeconds)
                    throw new IOException("OGP complete inventory response timed out.");
                if (Stopwatch.GetElapsedTime(lastReceived).TotalSeconds >= 15)
                    throw new IOException("OGP connection became silent.");
                if (ready && !fullReadPending && Stopwatch.GetElapsedTime(lastRequest).TotalSeconds >= timing.InventoryPollSeconds)
                {
                    await stream.WriteAsync(InventoryRequest, connection.Token);
                    fullReadPending = true;
                    pendingSince = lastRequest = Stopwatch.GetTimestamp();
                }
            }
        }
        finally
        {
            await connection.CancelAsync();
            client.Dispose();
            if (reading is not null)
            {
                try { await reading; }
                catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
            }
        }
    }
}
