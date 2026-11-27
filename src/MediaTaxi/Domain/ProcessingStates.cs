using System.Text.Json.Serialization;

namespace MediaTaxi.Domain;

public enum ProxyState { Pending, Queued, Working, Ready, Failed, Untracked }
public enum CacheLifecycle
{
    None, Reserved, Working, Ready, Released, Lost,
    [JsonStringEnumMemberName("Cleanup pending")] CleanupPending
}
public enum HiresState
{
    Pending, Probing, Wrapping, Transcoding, Archiving, Delivering, Complete, Failed,
    [JsonStringEnumMemberName("Waiting for cache")] WaitingForCache
}
public static class ProcessingStateText
{
    public static string Text(this ProxyState value) => value.ToString();
    public static string Text(this CacheLifecycle value) => value == CacheLifecycle.CleanupPending ? "Cleanup pending" : value.ToString();
    public static string Text(this HiresState value) => value == HiresState.WaitingForCache ? "Waiting for cache" : value.ToString();
}
