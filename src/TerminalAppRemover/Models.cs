namespace TerminalAppRemover;

public sealed class PackageEntry
{
    public required string Name { get; init; }
    public string? Id { get; init; }
    public string? Version { get; init; }
    public required string Manager { get; init; }
    public required string UninstallCommand { get; init; }
    public long? SizeBytes { get; init; }

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "\u2014" : Version;
    public string DisplayId => string.IsNullOrWhiteSpace(Id) ? "\u2014" : Id;
    public string DisplaySize => SizeBytes.HasValue ? SizeFormatter.Format(SizeBytes.Value) : "Unknown";
}

internal static class SizeFormatter
{
    public static string Format(long bytes)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        return bytes switch
        {
            < 1024 => $"{bytes} B",
            _ when bytes < mb => $"{bytes / kb:0.##} KB",
            _ when bytes < gb => $"{bytes / mb:0.##} MB",
            _ => $"{bytes / gb:0.##} GB"
        };
    }
}
