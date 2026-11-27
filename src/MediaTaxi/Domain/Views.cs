namespace MediaTaxi.Domain;

public sealed record DestinationView(string Id, string Name, string CopyStatus, string Availability, string? Error,
    bool InventoryConfirmed = false);
public sealed record JobView(string Id, string OriginalName, string ClipName, DateTimeOffset ArrivalUtc,
    string Location, string Stage, double? Progress, string Mode, string MediaSummary,
    string? Error, string ProxyStatus, IReadOnlyList<DestinationView> Destinations,
    double? ProxyProgress = null, IReadOnlyDictionary<string, StageTiming>? Timings = null,
    string? HiresPhase = null, string? CacheWaitReason = null, string? ProxyDeferralReason = null, string? CacheState = null);
public sealed record JobsSnapshot(long Revision, string TimeZone, int ActiveCount, int OlderCount, IReadOnlyList<JobView> Jobs);
public sealed record FolderHealth(string Id, string Path, bool Readable, bool Writable, long? FreeBytes, string? Error);
public sealed record MediaHealth(bool Ready, string FfmpegVersion, string FfprobeVersion, string[] Capabilities, string? Error);
public sealed record ServerHealth(string Id, string Name, bool Connected, bool Ready, DateTimeOffset? LastInventoryUtc, string? Error);
public sealed record StatisticsSnapshot(int Processed, int NativeProcessed, int TranscodedProcessed,
    int Imported, int NativeImported, int TranscodedImported, int ProcessingFailures, int DeliveryFailures, int ProxyCompletions);
public sealed record SystemSnapshot(string Version, double UptimeSeconds, bool ProcessingReady,
    string? PauseReason, IReadOnlyList<FolderHealth> Folders, MediaHealth Media,
    IReadOnlyList<ServerHealth> Servers, IReadOnlyDictionary<string, int> Workers,
    int QueueCount, IReadOnlyList<string> CleanupPending, IReadOnlyList<string> Errors,
    IReadOnlyDictionary<string, int>? Stages = null, CacheSnapshot? Cache = null);
public sealed record CacheSnapshot(string Path, long UsageBytes, long ReservedBytes, long QuotaBytes, long FreeBytes, long MinimumFreeBytes);

public interface IApplicationView
{
    long Revision { get; }
    bool IsReady { get; }
    JobsSnapshot GetJobs(string view);
    SystemSnapshot GetSystem();
    StatisticsSnapshot GetStatistics();
    Task WaitForChangeAsync(long since, CancellationToken cancellationToken);
}
