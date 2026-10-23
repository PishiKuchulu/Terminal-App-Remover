using System.Globalization;
using System.Text.RegularExpressions;

namespace TerminalAppRemover;

/// <summary>
/// Pure, unit-testable parsing and matching logic for every package manager.
/// No process, file-system (except through caller-supplied lines) or registry access here.
/// </summary>
internal static class Parsers
{
    private static readonly Regex WingetSeparator = new(@"^\s*-{3,}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceColumns = new(@"\s{2,}", RegexOptions.Compiled);
    private static readonly Regex PipNameSeparators = new(@"[-_.]+", RegexOptions.Compiled);

    public static bool IsPackageName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Package Id", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Package", StringComparison.OrdinalIgnoreCase);

    public static bool IsPackageVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Equals("Version", StringComparison.OrdinalIgnoreCase);

    /// <summary>Stricter check for whitespace-token parsers: a version token must
    /// start with a digit (or 'v' + digit), so a neighbouring column's word
    /// (e.g. Scoop's "main" bucket) can never be mistaken for a version.</summary>
    public static bool IsVersionToken(string value) =>
        IsPackageVersion(value) &&
        (char.IsDigit(value[0]) ||
         (value.Length > 1 && (value[0] == 'v' || value[0] == 'V') && char.IsDigit(value[1])));

    // ------------------------------------------------------------------ WinGet

    public sealed record WingetParseResult(IReadOnlyList<PackageEntry> Entries, string? Warning);

    /// <summary>Ids/names whose removal would break the tooling itself or shared Windows frameworks.</summary>
    private static readonly string[] ProtectedWingetIdPrefixes =
    {
        "Microsoft.VCLibs.",
        "Microsoft.UI.Xaml.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.NETCore.",
    };

    private static bool IsProtectedWinget(string? id, string name, out string reason)
    {
        reason = "";
        if (id is not null)
        {
            if (id.Equals("Microsoft.AppInstaller", StringComparison.OrdinalIgnoreCase) ||
                id.Equals("Microsoft.DesktopAppInstaller", StringComparison.OrdinalIgnoreCase))
            {
                reason = "This is WinGet itself (App Installer).";
                return true;
            }
            foreach (var prefix in ProtectedWingetIdPrefixes)
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Shared Windows framework package other apps depend on.";
                    return true;
                }
            }
        }

        if (name.StartsWith("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Visual C++ Redistributable shared by many applications.";
            return true;
        }
        if (name.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Scoop", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Package manager — removing it through WinGet leaves its packages orphaned.";
            return true;
        }
        if (name.Contains("Terminal App Remover", StringComparison.OrdinalIgnoreCase))
        {
            reason = "This application itself.";
            return true;
        }
        return false;
    }

    public static WingetParseResult ParseWinget(IReadOnlyList<string> lines)
    {
        var headerIndex = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
                line.Contains("Version", StringComparison.OrdinalIgnoreCase))
            {
                headerIndex = i;
                break;
            }
        }

        if (headerIndex < 0)
        {
            return new WingetParseResult(
                ParseWingetFallback(lines),
                "WinGet: output header not recognized (non-English Windows?) — used the fallback parser; results may be incomplete.");
        }

        var header = lines[headerIndex];
        var nameStart = header.IndexOf("Name", StringComparison.OrdinalIgnoreCase);
        var idStart = nameStart >= 0 ? header.IndexOf("Id", nameStart + 4, StringComparison.OrdinalIgnoreCase) : -1;
        if (nameStart < 0 || idStart < 0) return new WingetParseResult(ParseWingetFallback(lines), "WinGet: malformed header — used the fallback parser.");

        var versionStart = header.IndexOf("Version", idStart + 2, StringComparison.OrdinalIgnoreCase);
        if (versionStart < 0) return new WingetParseResult(ParseWingetFallback(lines), "WinGet: malformed header — used the fallback parser.");

        var availableStart = header.IndexOf("Available", versionStart + 7, StringComparison.OrdinalIgnoreCase);
        var sourceSearchFrom = availableStart >= 0 ? availableStart + 9 : versionStart + 7;
        var sourceStart = sourceSearchFrom < header.Length
            ? header.IndexOf("Source", sourceSearchFrom, StringComparison.OrdinalIgnoreCase)
            : -1;

        string? parseWarning = sourceStart < 0
            ? "WinGet: Source column not found (non-English Windows?) — package origins and protections may be incomplete."
            : null;

        var result = new List<PackageEntry>();
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (WingetSeparator.IsMatch(line)) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var nameEnd = idStart > nameStart ? idStart : line.Length;
            var idEnd = versionStart > idStart ? versionStart : line.Length;
            var versionEnd = availableStart > versionStart ? availableStart : (sourceStart > versionStart ? sourceStart : line.Length);
            var source = sourceStart > versionStart ? Slice(line, sourceStart, line.Length).Trim() : null;

            var name = Slice(line, nameStart, nameEnd).Trim();
            var id = Slice(line, idStart, idEnd).Trim();
            var version = Slice(line, versionStart, versionEnd).Trim();

            if (string.IsNullOrWhiteSpace(name)) continue;
            if (name.StartsWith("No package found", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.Equals("Id", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.Contains("---", StringComparison.Ordinal)) continue;

            result.Add(BuildWingetEntry(name, string.IsNullOrWhiteSpace(id) ? null : id,
                string.IsNullOrWhiteSpace(version) ? null : version, source));
        }

        return new WingetParseResult(result, parseWarning);
    }

    private static PackageEntry BuildWingetEntry(string name, string? id, string? version, string? source)
    {
        var truncated = (id?.Contains('\u2026') ?? false) || (id?.EndsWith("...", StringComparison.Ordinal) ?? false)
                        || name.Contains('\u2026') || name.EndsWith("...", StringComparison.Ordinal);

        string? warning = null;
        UninstallSpec spec;
        if (!string.IsNullOrWhiteSpace(id) && !truncated)
        {
            spec = new UninstallSpec("winget", new[] { "uninstall", "--id", id, "--exact" });
        }
        else
        {
            spec = new UninstallSpec("winget", new[] { "uninstall", "--name", name, "--exact" });
            if (truncated)
                warning = "WinGet truncated this name/id in its table output; the uninstall may not match. If it fails, uninstall from a terminal with the full id.";
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            !source.Equals("winget", StringComparison.OrdinalIgnoreCase))
        {
            var sourceWarning = $"Not installed by WinGet (source: {source}). It is shown because 'winget list' reports every installed app; uninstalling removes the underlying application.";
            warning = warning is null ? sourceWarning : warning + " " + sourceWarning;
        }

        var isProtected = IsProtectedWinget(id, name, out var reason);
        if (isProtected)
            warning = $"Protected: {reason}";

        return new PackageEntry
        {
            Name = name,
            Id = id,
            Version = version,
            Manager = "WinGet",
            Source = string.IsNullOrWhiteSpace(source) ? null : source,
            IsProtected = isProtected,
            Warning = warning,
            Uninstall = spec
        };
    }

    private static IReadOnlyList<PackageEntry> ParseWingetFallback(IEnumerable<string> lines)
    {
        var list = new List<PackageEntry>();
        foreach (var line in lines)
        {
            if (WingetSeparator.IsMatch(line)) continue;
            var parts = WhitespaceColumns.Split(line.Trim());
            if (parts.Length < 3) continue;
            if (parts[0].Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;

            list.Add(BuildWingetEntry(parts[0],
                string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1],
                parts[2], source: null));
        }
        return list;
    }

    // ------------------------------------------------------------------ Chocolatey

    public static IReadOnlyList<PackageEntry> ParseChocolatey(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim().Split('|', 2))
            .Where(parts => parts.Length == 2 && IsPackageName(parts[0]) && IsPackageVersion(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = "Chocolatey",
                Uninstall = new UninstallSpec("choco", new[] { "uninstall", parts[0], "-y" }, Elevate: true)
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ Scoop

    public static IReadOnlyList<PackageEntry> ParseScoop(IEnumerable<string> lines) =>
        lines
            // NOTE: the old code dropped any line containing the substring "name",
            // which silently removed real packages such as "rename-tool". The header
            // is filtered precisely by IsPackageName("Name") below.
            // Scoop names/versions never contain spaces, so split on ANY whitespace:
            // the old 2+-space column split fused "name 1.2.3" into one token whenever
            // the padding between the name and version columns was a single space.
            .Where(line => !line.StartsWith("----", StringComparison.Ordinal))
            .Where(line => !line.TrimStart().StartsWith("Installed apps", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && IsPackageName(parts[0]) && IsVersionToken(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = "Scoop",
                Uninstall = CmdSpec("scoop", "uninstall", parts[0])
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ .NET tools

    public static IReadOnlyList<PackageEntry> ParseDotnetTools(IEnumerable<string> lines) =>
        lines
            .Where(line => !line.StartsWith("----", StringComparison.Ordinal))
            // Same single-space fusion hazard as Scoop; ids/versions have no spaces.
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2 && IsPackageName(parts[0]) && IsVersionToken(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = ".NET Tool",
                Uninstall = new UninstallSpec("dotnet", new[] { "tool", "uninstall", "--global", parts[0] })
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    // ------------------------------------------------------------------ Cargo

    public static IReadOnlyList<PackageEntry> ParseCargoList(IEnumerable<string> lines)
    {
        var list = new List<PackageEntry>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(' ') || line.StartsWith('\t')) continue;
            if (!line.EndsWith(':')) continue;

            // The old code took IndexOf(':'), which for git/path installs
            // ("foo v0.1.0 (https://github.com/...):") cut the version at "https".
            // The crate header ends with the LAST ':' on the line.
            var header = line[..^1].Trim();
            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;

            string? version = null;
            if (parts.Length > 1 && !parts[1].StartsWith('('))
                version = parts[1].TrimStart('v');

            list.Add(new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = version,
                Manager = "Cargo",
                Uninstall = new UninstallSpec("cargo", new[] { "uninstall", parts[0] })
            });
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ pip helpers

    /// <summary>PEP 503 normalized name, as used for dist-info directory matching (zope.interface -> zope_interface).</summary>
    public static string NormalizePipName(string name) =>
        PipNameSeparators.Replace(name.Trim().ToLowerInvariant(), "_");

    /// <summary>Parse one CSV line (PEP 376 RECORD is real CSV: quoted fields may contain commas and doubled quotes).</summary>
    public static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < line.Length)
                {
                    if (line[i] == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i += 2; }
                        else { i++; break; }
                    }
                    else sb.Append(line[i++]);
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') { i++; continue; }
                break;
            }

            var comma = line.IndexOf(',', i);
            if (comma < 0) { fields.Add(line[i..]); break; }
            fields.Add(line[i..comma]);
            i = comma + 1;
            if (i == line.Length) { fields.Add(""); break; }
        }
        return fields.ToArray();
    }

    /// <summary>Sum the size column (field 3) of PEP 376 RECORD lines.</summary>
    public static long SumRecordSizes(IEnumerable<string> lines)
    {
        long total = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = ParseCsvLine(line);
            if (fields.Length >= 3 && long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                total += size;
        }
        return total;
    }

    // ------------------------------------------------------------------ version / registry matching

    /// <summary>
    /// Boundary-aware version compatibility: equal, or one is a prefix of the other
    /// AND the next character is a non-alphanumeric separator. So 1.1 is NOT
    /// compatible with 1.10, and 1.0 is NOT compatible with 11.0 (the old plain
    /// StartsWith check claimed both).
    /// </summary>
    public static bool VersionCompatible(string? packageVersion, string? registryVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion) || string.IsNullOrWhiteSpace(registryVersion))
            return true;

        var v1 = packageVersion.Trim();
        var v2 = registryVersion.Trim();

        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsBoundaryPrefix(v1, v2) || IsBoundaryPrefix(v2, v1);
    }

    private static bool IsBoundaryPrefix(string prefix, string full) =>
        full.Length > prefix.Length &&
        full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        !char.IsLetterOrDigit(full[prefix.Length]);

    /// <summary>
    /// Score how well a registry Uninstall entry matches a package (0 = no match).
    /// Callers keep the BEST-scoring entry instead of the first one a hive yields.
    /// </summary>
    public static int RegistryMatchScore(PackageEntry package, string displayName, string? displayVersion)
    {
        var pkgName = package.Name.Trim();
        var dispName = displayName.Trim();
        if (pkgName.Length == 0 || dispName.Length == 0) return 0;

        var versionScore = 0;
        if (!string.IsNullOrWhiteSpace(package.Version) && !string.IsNullOrWhiteSpace(displayVersion))
        {
            if (string.Equals(package.Version.Trim(), displayVersion.Trim(), StringComparison.OrdinalIgnoreCase))
                versionScore = 20;
            else if (VersionCompatible(package.Version, displayVersion))
                versionScore = 10;
            else
                return 0; // same name but a different version: a different install owns this entry
        }

        // 1. Exact display-name match
        if (string.Equals(pkgName, dispName, StringComparison.OrdinalIgnoreCase))
            return 80 + versionScore;

        // 2. Truncated WinGet name ("Git…", "Git...") recovering into the full display name
        var cleanPkgName = pkgName.TrimEnd('.', '\u2026').Trim();
        if (cleanPkgName.Length >= 4 && cleanPkgName.Length < pkgName.Length &&
            dispName.StartsWith(cleanPkgName, StringComparison.OrdinalIgnoreCase))
            return 50 + versionScore;

        // 2b. Name is a whole-word prefix of the display name ("PowerShell" -> "PowerShell 7-x64")
        if (pkgName.Length >= 4 && IsBoundaryPrefix(pkgName, dispName))
            return 45 + versionScore;

        // 3. Package ID matches
        if (!string.IsNullOrWhiteSpace(package.Id))
        {
            var id = package.Id.Trim();
            if (string.Equals(id, dispName, StringComparison.OrdinalIgnoreCase))
                return 70 + versionScore;

            // WinGet IDs follow Publisher.Package format (e.g. VideoLAN.VLC -> VLC).
            // An EXACT app-part match is safe even at 3 chars ("VLC") — the >= 4
            // floor from the original code silently disabled exactly this,
            // its own documented example. The floor stays for prefix matches.
            var dotIndex = id.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < id.Length - 1)
            {
                var appPart = id[(dotIndex + 1)..].Trim();
                if (appPart.Length >= 3 && dispName.Equals(appPart, StringComparison.OrdinalIgnoreCase))
                    return 40 + versionScore;
                if (appPart.Length >= 4 && IsBoundaryPrefix(appPart, dispName))
                    return 25 + versionScore;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Build a spec for tools that only exist as cmd shims on Windows (scoop, npm, pnpm).</summary>
    public static UninstallSpec CmdSpec(string fileName, params string[] arguments) =>
        new("cmd.exe", new[] { "/c", CommandLine.BuildCmdInner(fileName, arguments) });

    private static string Slice(string value, int start, int endExclusive)
    {
        if (start < 0 || start >= value.Length) return string.Empty;
        var end = endExclusive <= start ? value.Length : Math.Min(endExclusive, value.Length);
        return value[start..end];
    }
}
