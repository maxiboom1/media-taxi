namespace MediaTaxi.Inventory;

public sealed record InventorySnapshot(string Id, string Name, bool Connected, bool Ready,
    DateTimeOffset? LastInventoryUtc, string? Error, IReadOnlyList<string> Paths);

public static class InventoryMatching
{
    public static bool Contains(InventorySnapshot snapshot, string inventoryFolder, string clipName)
    {
        if (!snapshot.Ready) return false;
        return BuildIndex(snapshot).Contains(Key(inventoryFolder, clipName));
    }
    public static HashSet<string> BuildIndex(InventorySnapshot snapshot) => snapshot.Paths
        .Select(path => StripClipExtension(path.Replace('\\', '/').Trim('/'))).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public static string Key(string folder, string clipName)
    {
        folder = folder.Replace('\\', '/').Trim('/');
        var name = StripClipExtension(clipName.Replace('\\', '/'));
        return folder.Length == 0 ? name : folder + "/" + name;
    }

    private static string StripClipExtension(string value) => value.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) ? value[..^5] : value;
}
