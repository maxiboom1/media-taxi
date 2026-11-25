using MediaTaxi.Configuration;

namespace MediaTaxi.Inventory;

/// <summary>Independent, read-only OGP sessions. Cached lists are never authoritative while not ready.</summary>
public sealed class InventoryService : BackgroundService
{
    private readonly TaxiOptions options;
    private readonly ILogger<InventoryService> logger;
    private readonly object sync = new();
    private readonly Dictionary<string, InventorySnapshot> snapshots = new(StringComparer.OrdinalIgnoreCase);

    public InventoryService(TaxiOptions options, ILogger<InventoryService> logger)
    {
        this.options = options;
        this.logger = logger;
        foreach (var destination in options.Destinations)
            snapshots[destination.Id] = new(destination.Id, destination.Name, false, false, null, null, Array.Empty<string>());
    }

    public IReadOnlyList<InventorySnapshot> GetSnapshots()
    {
        lock (sync) return options.Destinations.Select(destination => snapshots[destination.Id]).ToArray();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(options.Destinations.Select(destination => MonitorAsync(destination, stoppingToken)));

    private async Task MonitorAsync(DestinationOptions destination, CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(destination.Host))
        {
            Update(destination.Id, false, false, "Tria host is not configured.");
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await new OgpSession(destination, options.Timing,
                    (connected, ready, error, paths) => Update(destination.Id, connected, ready, error, paths))
                    .RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Net.Sockets.SocketException or OperationCanceledException or
                                          System.Text.Json.JsonException or InvalidOperationException)
            {
                Update(destination.Id, false, false, error.Message);
                logger.LogWarning("OGP inventory unavailable for {Destination}: {Error}", destination.Name, error.Message);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(options.Timing.ReconnectSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        Update(destination.Id, false, false, "Inventory monitoring stopped.");
    }

    private void Update(string id, bool connected, bool ready, string? error, IReadOnlyList<string>? paths = null)
    {
        lock (sync)
        {
            var previous = snapshots[id];
            snapshots[id] = previous with
            {
                Connected = connected,
                Ready = ready,
                Error = error,
                Paths = paths ?? previous.Paths,
                LastInventoryUtc = paths is null ? previous.LastInventoryUtc : DateTimeOffset.UtcNow
            };
        }
    }
}
