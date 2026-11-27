using System.Text;
using System.Threading.Channels;
using MediaTaxi.Configuration;
using MediaTaxi.Domain;
using MediaTaxi.Media;

namespace MediaTaxi.Diagnostics;

/// <summary>Bounded local logging. A slow/failing log disk never holds an ingest or view lock.</summary>
public sealed class RollingFileLogger : ILoggerProvider
{
    private readonly FileLogOptions options;
    private readonly Channel<string> lines = Channel.CreateBounded<string>(1024);
    private readonly Task writer;
    private string? error;
    private long dropped;
    public LogHealth Health => new(options.Enabled, options.Directory, Volatile.Read(ref error), Interlocked.Read(ref dropped));
    public RollingFileLogger(FileLogOptions options)
    {
        this.options = options;
        writer = Task.Run(async () =>
        {
            await foreach (var line in lines.Reader.ReadAllAsync())
            {
                try
                {
                    Directory.CreateDirectory(options.Directory);
                    Storage.WorkCache.RejectLink(options.Directory);
                    var path = Path.Combine(options.Directory, "media-taxi.log");
                    var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    if (File.Exists(path))
                    {
                        Storage.WorkCache.RejectLink(path);
                        if (new FileInfo(path).Length + bytes.Length > options.MaxMiB * 1024L * 1024)
                        {
                            for (var n = options.MaxFiles - 1; n >= 1; n--)
                            {
                                var old = n == 1 ? path : path + "." + (n - 1);
                                var next = path + "." + n;
                                if (File.Exists(next)) { Storage.WorkCache.RejectLink(next); File.Delete(next); }
                                if (File.Exists(old)) { Storage.WorkCache.RejectLink(old); File.Move(old, next); }
                            }
                            if (options.MaxFiles == 1) File.Delete(path);
                        }
                    }
                    await using var output = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                    await output.WriteAsync(bytes);
                    Volatile.Write(ref error, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { Volatile.Write(ref error, DiagnosticText.Summary(ex.Message)); Interlocked.Increment(ref dropped); }
            }
        });
    }
    public ILogger CreateLogger(string categoryName) => new Sink(this, categoryName);
    private sealed class Sink(RollingFileLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => owner.options.Enabled && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var text = $"{DateTimeOffset.UtcNow:O} {level} {category}: {formatter(state, exception)}";
            if (exception is not null) text += Environment.NewLine + exception;
            if (text.Length > 16384) text = text[..16384];
            if (!owner.lines.Writer.TryWrite(text)) Interlocked.Increment(ref owner.dropped);
        }
    }
    public void Dispose() { lines.Writer.TryComplete(); writer.GetAwaiter().GetResult(); }
}
