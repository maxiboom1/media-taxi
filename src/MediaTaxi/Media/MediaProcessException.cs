namespace MediaTaxi.Media;

public sealed class MediaProcessException(string executable, int exitCode, string diagnostics)
    : IOException($"{Path.GetFileName(executable)} exited {exitCode}: {DiagnosticText.Summary(diagnostics)}")
{
    public int ExitCode { get; } = exitCode;
    public string Diagnostics { get; } = DiagnosticText.Tail(diagnostics);
}

public static class DiagnosticText
{
    public static string Summary(string text) => text.Length <= 1024 ? text : text[..1021] + "…";
    public static string Tail(string text) => text.Length <= 65536 ? text : text[^65536..];
}
