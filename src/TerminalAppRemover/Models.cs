namespace TerminalAppRemover;

/// <summary>
/// Structured uninstall invocation. Execution never re-parses a display string:
/// the process is started from <see cref="FileName"/> + <see cref="Arguments"/>
/// (ProcessStartInfo.ArgumentList), or, when <see cref="FileToDelete"/> is set,
/// the file is deleted in-process and its disappearance is verified.
/// For tools that only exist as cmd shims (npm.cmd, pnpm.cmd, scoop), FileName is
/// "cmd.exe" and Arguments is exactly ["/c", innerCommandLine] where the inner
/// command line was built with cmd-safe quoting (see CommandLine).
/// </summary>
public sealed record UninstallSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    bool Elevate = false,
    string? FileToDelete = null)
{
    /// <summary>Human-readable rendering for the confirmation dialog only.</summary>
    public string Display => FileToDelete is not null
        ? $"Delete file: {FileToDelete}"
        : FileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && Arguments.Count == 2 && Arguments[0] == "/c"
            ? $"cmd.exe /c {Arguments[1]}"
            : CommandLine.Join(FileName, Arguments);
}

public sealed class PackageEntry
{
    public required string Name { get; init; }
    public string? Id { get; init; }
    public string? Version { get; init; }
    public required string Manager { get; init; }
    public required UninstallSpec Uninstall { get; init; }

    /// <summary>Origin reported by the manager itself (e.g. winget Source column: winget / msix / ARP), when known.</summary>
    public string? Source { get; init; }

    /// <summary>Protected entries (winget itself, the package managers, this app) cannot be uninstalled from the UI.</summary>
    public bool IsProtected { get; init; }

    /// <summary>Extra caution text shown in the confirmation dialog (library packages, non-winget sources, truncated ids...).</summary>
    public string? Warning { get; init; }

    public long? SizeBytes { get; set; }

    public bool CanUninstall => !IsProtected;

    /// <summary>Display string kept for the confirmation dialog / status text.</summary>
    public string UninstallCommand => Uninstall.Display;

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? "\u2014" : Version;
    public string DisplayId => string.IsNullOrWhiteSpace(Id) ? "\u2014" : Id;
    public string DisplaySize => SizeBytes.HasValue ? SizeFormatter.Format(SizeBytes.Value) : "Unknown";
}

/// <summary>Result of a full scan: packages plus one human-readable warning per failed/timed-out manager.</summary>
public sealed record ScanOutcome(IReadOnlyList<PackageEntry> Packages, IReadOnlyList<string> Warnings);

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

/// <summary>Windows command-line quoting helpers. Used for display strings and for the
/// single inner string handed to <c>cmd.exe /c</c>; direct process launches use ArgumentList instead.</summary>
internal static class CommandLine
{
    /// <summary>Quote one argument following CommandLineToArgvW rules.</summary>
    public static string QuoteArg(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return value;

        var sb = new System.Text.StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(string fileName, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { QuoteArg(fileName) }.Concat(arguments.Select(QuoteArg)));

    /// <summary>
    /// Build the single command string for <c>cmd.exe /c</c>. Every argument is double-quoted
    /// when needed; '%' is doubled so cmd cannot expand environment variables inside the
    /// quoted string (the classic cmd.exe injection path for file/package names).
    /// </summary>
    public static string BuildCmdInner(string fileName, IEnumerable<string> arguments) =>
        Join(fileName, arguments).Replace("%", "%%", StringComparison.Ordinal);
}
