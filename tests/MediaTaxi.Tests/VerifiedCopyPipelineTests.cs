using System.Security.Cryptography;
using MediaTaxi.Media;
using MediaTaxi.Storage;

namespace MediaTaxi.Tests;

public sealed class VerifiedCopyPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PipelinePreservesByteOrderAndChecksExactlyTheRequiredDigest(bool separateDigests)
    {
        var bytes = new byte[5 * 1024 * 1024 + 123]; Random.Shared.NextBytes(bytes);
        using var input = new MemoryStream(bytes); using var output = new MemoryStream();
        var full = Hash(bytes); var header = separateDigests ? Hash(bytes.AsSpan(0, FlatClip.HeaderBytes)) : null;
        var body = separateDigests ? Hash(bytes.AsSpan(FlatClip.HeaderBytes)) : null;
        var count = await VerifiedCopyPipeline.CopyAsync(input, output, separateDigests ? "unused" : full, header, body, default);
        Assert.Equal(bytes.Length, count); Assert.Equal(bytes, output.ToArray());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HeaderOrPayloadCorruptionNeverPublishesFinalFile(bool corruptHeader)
    {
        using var temp = new TemporaryFolder(); var bytes = new byte[2 * 1024 * 1024 + 17]; Random.Shared.NextBytes(bytes);
        var header = Hash(bytes.AsSpan(0, FlatClip.HeaderBytes)); var payload = Hash(bytes.AsSpan(FlatClip.HeaderBytes));
        bytes[corruptHeader ? 10 : bytes.Length - 1] ^= 1;
        var source = Path.Combine(temp.Path, "source.clip"); var final = Path.Combine(temp.Path, "target.clip"); var partial = final + ".partial";
        await File.WriteAllBytesAsync(source, bytes);
        var error = await Assert.ThrowsAsync<IOException>(() => ClipPublisher.PublishAsync(source, final, partial, "", false, default, null, header, payload).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("changed", error.Message); Assert.False(File.Exists(final)); Assert.False(File.Exists(partial));
    }
    [Fact]
    public async Task SlowDestinationKeepsReadAheadBoundedAndCancellationStopsBothWorkers()
    {
        var bytes = new byte[16 * 1024 * 1024];
        using var input = new ObservedInput(bytes); using var output = new SlowOutput(); using var cancel = new CancellationTokenSource();
        var copying = VerifiedCopyPipeline.CopyAsync(input, output, Hash(bytes), null, null, cancel.Token);
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var until = DateTime.UtcNow.AddSeconds(3);
        while (input.BytesRead < VerifiedCopyPipeline.MaximumBufferBytes && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.InRange(input.BytesRead, 1, VerifiedCopyPipeline.MaximumBufferBytes);
        Assert.True(input.BytesRead < bytes.Length); Assert.False(copying.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => copying.WaitAsync(TimeSpan.FromSeconds(3)));
        var stoppedAt = input.BytesRead; await Task.Delay(30); Assert.Equal(stoppedAt, input.BytesRead);
    }
    [Fact]
    public async Task DestinationFailureCancelsBlockedReadAheadWithoutHanging()
    {
        var bytes = new byte[16 * 1024 * 1024]; using var input = new MemoryStream(bytes); using var output = new FailingOutput();
        var error = await Assert.ThrowsAsync<IOException>(() => VerifiedCopyPipeline.CopyAsync(input, output, Hash(bytes), null, null, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Contains("offline", error.Message);
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed class ObservedInput(byte[] bytes) : MemoryStream(bytes)
    {
        private int readCount;
        public int BytesRead => Volatile.Read(ref readCount);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var n = await base.ReadAsync(buffer, ct); Interlocked.Add(ref readCount, n); return n; }
    }
    private sealed class SlowOutput : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
    }
    private sealed class FailingOutput : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => ValueTask.FromException(new IOException("Destination offline"));
    }
}
