namespace MediaTaxi.Domain;

public enum SourceLocation { Watch, Finished, Failed }
public enum JobStage { Waiting, Queued, Probing, Wrapping, Transcoding, Delivering, Archiving, Proxy, Done, Failed, MoveFailed, CleanupPending, Existing }
public enum CopyState { NotAttempted, InProgress, Succeeded, Failed }
public enum Availability { Unknown, OK, Missing, Removed }

public sealed class JobRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalName { get; set; } = "";
    public string ClipName { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public SourceLocation Location { get; set; }
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public long SourceSize { get; set; }
    public DateTimeOffset SourceWriteUtc { get; set; }
    public DateTimeOffset SourceCreatedUtc { get; set; }
    public DateTimeOffset StableSinceUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Managed { get; set; } = true;
    public JobStage Stage { get; set; } = JobStage.Waiting;
    public double? Progress { get; set; }
    public string Mode { get; set; } = "Unknown";
    public string MediaSummary { get; set; } = "";
    public string? Error { get; set; }
    public string? ArtifactPath { get; set; }
    public string? ArtifactSha256 { get; set; }
    public string? ArtifactUuid { get; set; }
    public string? ProxyPath { get; set; }
    public string? ProxySha256 { get; set; }
    public string ProxyStatus { get; set; } = "Pending";
    public string? WorkPath { get; set; }
    public string? MoveTarget { get; set; }
    public SourceLocation? MoveLocation { get; set; }
    public bool Wrapped { get; set; }
    public bool Archived { get; set; }
    public bool TerminalFailure { get; set; }
    public bool Retiring { get; set; }
    public int AbsentScans { get; set; }
    public DateTimeOffset? LastAbsentScanUtc { get; set; }
    public List<DestinationRecord> Destinations { get; set; } = [];
}
public sealed class DestinationRecord
{
    public string Id { get; set; } = "";
    public CopyState CopyStatus { get; set; }
    public Availability Availability { get; set; }
    public bool SeenOnline { get; set; }
    public string? Error { get; set; }
    public string? TempPath { get; set; }
}
