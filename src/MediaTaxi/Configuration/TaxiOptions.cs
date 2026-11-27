namespace MediaTaxi.Configuration;

public sealed class TaxiOptions
{
    public string ConfigPath { get; set; } = "";
    public FolderOptions Folders { get; set; } = new();
    public WorkerOptions Workers { get; set; } = new();
    public MediaOptions Media { get; set; } = new();
    public ProxyOptions Proxy { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public TimingOptions Timing { get; set; } = new();
    public WebOptions Web { get; set; } = new();
    public List<DestinationOptions> Destinations { get; set; } = [];
    public FileLogOptions FileLog { get; set; } = new();
    public string LogLevel { get; set; } = "Information";
}

public sealed class FolderOptions
{
    public string Watch { get; set; } = "";
    public string Finished { get; set; } = "";
    public string Failed { get; set; } = "";
    public string Proxy { get; set; } = "";
    public string Work { get; set; } = "C:/MediaTaxiCache";
    public string State { get; set; } = "";
}
public sealed class WorkerOptions
{
    public int Native { get; set; } = 1;
    public int Normalize { get; set; } = 1;
    public int Proxy { get; set; } = 1;
    public int Copy { get; set; } = 4;
    public int CopiesPerDestination { get; set; } = 1;
}
public sealed class MediaOptions
{
    public string FfmpegPath { get; set; } = "tools/ffmpeg.exe";
    public string FfprobePath { get; set; } = "tools/ffprobe.exe";
    public bool AllowNormalization { get; set; }
    public int Threads { get; set; } = 4;
    public int TimeoutSeconds { get; set; } = 3600;
}
public sealed class ProxyOptions
{
    public bool DeferWhileHiresBusy { get; set; } = true;
    public string Codec { get; set; } = "h264";
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 360;
    public int Fps { get; set; } = 50;
    public string RateControl { get; set; } = "bitrate";
    public string VideoBitrate { get; set; } = "1200k";
    public string AudioBitrate { get; set; } = "128k";
    public int Crf { get; set; } = 26;
    public string Preset { get; set; } = "fast";
    public int[] Channels { get; set; } = [1, 2];
}
public sealed class CacheOptions
{
    public double MaxGiB { get; set; } = 64;
    public double MinFreeGiB { get; set; } = 32;
    public long MaxBytes => checked((long)(MaxGiB * 1073741824));
    public long MinFreeBytes => checked((long)(MinFreeGiB * 1073741824));
}
public sealed class TimingOptions
{
    public double StabilitySeconds { get; set; } = 10;
    public double ReconcileSeconds { get; set; } = 10;
    public double InventoryPollSeconds { get; set; } = 30;
    public double InventoryTimeoutSeconds { get; set; } = 5;
    public double SourceScanTimeoutSeconds { get; set; } = 30;
    public double HealthCheckTimeoutSeconds { get; set; } = 10;
    public double HealthCheckIntervalSeconds { get; set; } = 60;
    public double StalledSourceWarningSeconds { get; set; } = 300;
    public double ReconnectSeconds { get; set; } = 5;
}
public sealed class WebOptions
{
    public string Listen { get; set; } = "http://0.0.0.0:8080";
    public string TimeZone { get; set; } = "Asia/Jerusalem";
}
public sealed class DestinationOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 5254;
    public string InventoryFolder { get; set; } = "IMPORTS";
}

public sealed class FileLogOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "runtime/logs";
    public int MaxFiles { get; set; } = 5;
    public int MaxMiB { get; set; } = 10;
}
