namespace MediaTaxi.Inventory;

public sealed record InventorySnapshot(string Id, string Name, bool Connected, bool Ready,
    DateTimeOffset? LastInventoryUtc, string? Error, IReadOnlyList<string> Paths);

public static class InventoryMatching
{
    public static bool Contains(InventorySnapshot snapshot, string inventoryFolder, string clipName)
    {
        if (!snapshot.Ready) return false;
        string folder = inventoryFolder.Replace('\\', '/').Trim('/');
        string name = StripClipExtension(clipName.Replace('\\', '/'));
        string expected = folder.Length == 0 ? name : folder + "/" + name;
        return snapshot.Paths.Any(path => string.Equals(
            StripClipExtension(path.Replace('\\', '/').Trim('/')), expected, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripClipExtension(string value) => value.EndsWith(".clip", StringComparison.OrdinalIgnoreCase) ? value[..^5] : value;
}
