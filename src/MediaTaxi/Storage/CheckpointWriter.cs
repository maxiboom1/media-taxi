using System.Threading.Channels;
using System.Text.Json;
using MediaTaxi.Domain;

namespace MediaTaxi.Storage;

public sealed record JobCheckpoint(string Id, string? Payload, string Mode = "Unknown", string? EventKind = null)
{
    public static JobCheckpoint Save(JobRecord job, string? eventKind = null) =>
        new(job.Id, JsonSerializer.Serialize(job, JobStore.JsonOptions), job.Mode, eventKind);
    public static JobCheckpoint Delete(string id) => new(id, null);
}

public sealed class CheckpointException(Exception cause) : IOException("State checkpoint failed; new processing/publication is paused. " + cause.Message, cause);

/// <summary>Reserve bounded capacity before taking the engine lock; enqueue immutable batches
/// under that lock, then await the durable acknowledgment outside it.</summary>
public sealed class CheckpointWriter : IDisposable
{
    private readonly Channel<Work> queue = Channel.CreateUnbounded<Work>(new() { SingleReader = true });
    private readonly SemaphoreSlim capacity;
    private readonly Task worker;
    private Exception? failure;
    public string? Error => Volatile.Read(ref failure)?.Message;
    public int Pending => Volatile.Read(ref pending);
    private int pending;

    public CheckpointWriter(Action<IReadOnlyList<JobCheckpoint>> write, int limit = 64)
    {
        capacity = new(limit, limit);
        worker = Task.Factory.StartNew(() =>
        {
            while (queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            while (queue.Reader.TryRead(out var item))
            {
                try
                {
                    if (failure is not null) throw new CheckpointException(failure);
                    write(item.Batch); item.Done.TrySetResult();
                }
                catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); item.Done.TrySetException(new CheckpointException(ex)); }
                finally { Interlocked.Decrement(ref pending); capacity.Release(); }
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
    public Lease Reserve()
    {
        capacity.Wait();
        if (failure is not null) { capacity.Release(); throw new CheckpointException(failure); }
        return new(this);
    }
    public sealed class Lease(CheckpointWriter owner) : IDisposable
    {
        private bool submitted;
        public Task Submit(IReadOnlyList<JobCheckpoint> batch)
        {
            if (submitted) throw new InvalidOperationException("Checkpoint lease already used.");
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Increment(ref owner.pending);
            if (!owner.queue.Writer.TryWrite(new(batch, done)))
            { Interlocked.Decrement(ref owner.pending); throw new IOException("Checkpoint writer is closed."); }
            submitted = true;
            return done.Task;
        }
        public void Dispose() { if (!submitted) owner.capacity.Release(); }
    }
    private sealed record Work(IReadOnlyList<JobCheckpoint> Batch, TaskCompletionSource Done);
    public void Dispose() { queue.Writer.TryComplete(); worker.GetAwaiter().GetResult(); capacity.Dispose(); }
}
