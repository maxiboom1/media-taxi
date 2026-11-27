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
    Task<bool> IsNativeAsync(string source, CancellationToken cancellationToken);
    async Task<MediaInspection> InspectAsync(string source, CancellationToken cancellationToken) =>
        new(await IsNativeAsync(source, cancellationToken), "");
    Task<PreparedMedia> PrepareAsync(string source, string workDirectory, Action<double>? progress, CancellationToken cancellationToken);
    Task<ClipArtifact> WrapAsync(PreparedMedia media, string outputPath, string jobId, CancellationToken cancellationToken);
    Task<PreparedMedia> ExtractClipAsync(string clipPath, string workDirectory, CancellationToken cancellationToken);
    Task<ProxyArtifact> CreateProxyAsync(PreparedMedia media, string outputPath, string jobId, Action<double>? progress, CancellationToken cancellationToken);
    Task<MediaHealth> CheckHealthAsync(CancellationToken cancellationToken);
    Task<bool> OwnsProxyAsync(string proxy, string jobId, CancellationToken cancellationToken);
    async Task<WrappedSource> WrapSourceAsync(string source, MediaInspection inspection, string work, string output, string id, Action<double>? progress, CancellationToken ct)
    {
        var prepared = await PrepareAsync(source, work, progress, ct);
        return new(await WrapAsync(prepared, output, id, ct), prepared.FrameCount);
    }
    Task<ProxyArtifact> CreateSourceProxyAsync(string source, MediaInspection? inspection, long frames, string output, string id, Action<double>? progress, CancellationToken ct) =>
        CreateProxyAsync(new PreparedMedia(source, source, frames, 8, 0, inspection?.Native ?? true, inspection?.Summary ?? ""), output, id, progress, ct);
}

public sealed class MediaMismatchException(string message) : Exception(message);

public sealed record ProcessResult(string StandardOutput, string StandardError);
