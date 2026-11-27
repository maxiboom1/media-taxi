using MediaTaxi.Domain;

namespace MediaTaxi.Media;

public sealed record PreparedMedia(string VideoPath, string AudioPath, long FrameCount,
    int AudioChannels, long TimecodeStartFrames, bool Native, string Summary);
public sealed record ClipArtifact(string Path, string Sha256, string Uuid, string? HeaderSha256 = null, string? PayloadSha256 = null);
public sealed record ProxyArtifact(string Path, string Sha256);
public sealed record MediaInspection(bool Native, string Summary, MediaProbe? Probe = null);
public sealed record WrappedSource(ClipArtifact Artifact, long FrameCount);

public interface IMediaService
{
    Task<MediaInspection> InspectAsync(string source, CancellationToken cancellationToken);
    Task<WrappedSource> WrapSourceAsync(string source, MediaInspection inspection, string work, string output, string id, Action<double>? progress, CancellationToken ct);
    Task<ProxyArtifact> CreateSourceProxyAsync(string source, MediaInspection? inspection, long frames, string output, string id, Action<double>? progress, CancellationToken ct);
    Task<MediaHealth> CheckHealthAsync(CancellationToken cancellationToken);
    Task<bool> OwnsProxyAsync(string proxy, string jobId, CancellationToken cancellationToken);
}

public sealed class MediaMismatchException(string message) : Exception(message);

public sealed record ProcessResult(string StandardOutput, string StandardError);
