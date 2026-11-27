using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using MediaTaxi.Media;

namespace MediaTaxi.Storage;

/// <summary>Three bounded buffers overlap local reads/integrity work with one ordered network writer.</summary>
public static class VerifiedCopyPipeline
{
    public const int BlockBytes = 1024 * 1024;
    public const int BufferCount = 3;
    public const int MaximumBufferBytes = BlockBytes * BufferCount;

    public static async Task<long> CopyAsync(Stream input, Stream output, string expectedFullHash,
        string? expectedHeader, string? expectedPayload, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lifetime.Token;
        var available = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(BufferCount) { SingleReader = true, SingleWriter = true });
        var ready = Channel.CreateBounded<(byte[] Buffer, int Count)>(new BoundedChannelOptions(BufferCount - 1) { SingleReader = true, SingleWriter = true });
        for (var n = 0; n < BufferCount; n++) available.Writer.TryWrite(new byte[BlockBytes]);
        Exception? producerError = null;
        var producer = Task.Run(async () =>
        {
            // Exactly one digest per payload byte. Full hashing is used only when
            // the current recovery checkpoint has no separate header/payload digests.
            using var payload = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var header = expectedHeader is null ? null : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long position = 0;
            try
            {
                while (true)
                {
                    var buffer = await available.Reader.ReadAsync(token);
                    var count = await input.ReadAsync(buffer, token);
                    if (count == 0) break;
                    if (header is null) payload.AppendData(buffer.AsSpan(0, count));
                    else
                    {
                        var head = (int)Math.Min(count, Math.Max(0, FlatClip.HeaderBytes - position));
                        header.AppendData(buffer.AsSpan(0, head)); payload.AppendData(buffer.AsSpan(head, count - head));
                    }
                    position += count;
                    await ready.Writer.WriteAsync((buffer, count), token);
                }
                if (header is not null && Convert.ToHexStringLower(header.GetHashAndReset()) != expectedHeader ||
                    Convert.ToHexStringLower(payload.GetHashAndReset()) != (header is null ? expectedFullHash : expectedPayload))
                    throw new IOException("Local clip changed during delivery.");
                ready.Writer.TryComplete();
                return position;
            }
            catch (Exception ex)
            {
                Volatile.Write(ref producerError, ex); ready.Writer.TryComplete(ex);
                await lifetime.CancelAsync(); throw;
            }
        }, CancellationToken.None);
        try
        {
            await foreach (var chunk in ready.Reader.ReadAllAsync(token))
            {
                await output.WriteAsync(chunk.Buffer.AsMemory(0, chunk.Count), token);
                await available.Writer.WriteAsync(chunk.Buffer, token);
            }
            return await producer;
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            if (Volatile.Read(ref producerError) is { } error && error is not OperationCanceledException)
                ExceptionDispatchInfo.Capture(error).Throw();
            throw;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await producer; } catch (Exception) { /* The originating read/write/cancellation error is propagated above. */ }
        }
    }
}
