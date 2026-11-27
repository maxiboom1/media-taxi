namespace MediaTaxi;

public static class AppVersion
{
    public static string Current { get; } = typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "unknown";
}
