using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TerminalAppRemover;

public sealed class PackageManagerService
{
    private static readonly Regex WingetSeparator = new(@"^\s*-{3,}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceColumns = new(@"\s{2,}", RegexOptions.Compiled);

    public async Task<IReadOnlyList<PackageEntry>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var tasks = new Task<IReadOnlyList<PackageEntry>>[]
        {
            ScanWingetAsync(cancellationToken),
            ScanChocolateyAsync(cancellationToken),
            ScanScoopAsync(cancellationToken),
            ScanDotnetToolsAsync(cancellationToken)
        };

        var all = await Task.WhenAll(tasks);
        return all.SelectMany(x => x)
            .GroupBy(x => $"{x.Manager}:{x.Id ?? x.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Manager, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<bool> IsCommandAvailableAsync(string command)
    {
        var result = await RunAsync("where.exe", command, CancellationToken.None);
        return result.ExitCode == 0;
    }

    private async Task<IReadOnlyList<PackageEntry>> ScanWingetAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("winget")) return [];

        var r = await RunAsync("winget", "list --accept-source-agreements --disable-interactivity", ct);
        if (r.ExitCode != 0) return [];

        var lines = r.StdOut.SplitLines();
        var headerIndex = Array.FindIndex(lines, line =>
            line.Contains("Name", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Id", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("Version", StringComparison.OrdinalIgnoreCase));

        if (headerIndex < 0) return ParseWingetFallback(lines);

        var header = lines[headerIndex];
        var nameStart = header.IndexOf("Name", StringComparison.OrdinalIgnoreCase);
        var idStart = header.IndexOf("Id", nameStart + 4, StringComparison.OrdinalIgnoreCase);
        if (nameStart < 0 || idStart < 0) return ParseWingetFallback(lines);

        var versionStart = header.IndexOf("Version", idStart + 2, StringComparison.OrdinalIgnoreCase);
        if (versionStart < 0) return ParseWingetFallback(lines);

        var availableStart = header.IndexOf("Available", versionStart + 7, StringComparison.OrdinalIgnoreCase);
        var sourceStart = header.IndexOf("Source", Math.Max(availableStart >= 0 ? availableStart + 1 : 0, versionStart + 7), StringComparison.OrdinalIgnoreCase);

        var result = new List<PackageEntry>();
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (WingetSeparator.IsMatch(line)) continue;
            if (string.IsNullOrWhiteSpace(line)) continue;

            var nameEnd = idStart > nameStart ? idStart : line.Length;
            var idEnd = versionStart > idStart ? versionStart : line.Length;
            var versionEnd = availableStart > versionStart ? availableStart : (sourceStart > versionStart ? sourceStart : line.Length);

            var name = Slice(line, nameStart, nameEnd).Trim();
            var id = Slice(line, idStart, idEnd).Trim();
            var version = Slice(line, versionStart, versionEnd).Trim();

            if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "No package found", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(name, "Name", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.Equals("Id", StringComparison.OrdinalIgnoreCase)) continue;
            if (id.Contains("---", StringComparison.Ordinal)) continue;

            var idToUse = string.IsNullOrWhiteSpace(id) ? null : id;
            var uninstallCmd = !string.IsNullOrWhiteSpace(idToUse)
                ? $"winget uninstall --id {Quote(idToUse)} --exact"
                : $"winget uninstall --name {Quote(name)} --exact";

            result.Add(new PackageEntry
            {
                Name = name,
                Id = idToUse,
                Version = string.IsNullOrWhiteSpace(version) ? null : version,
                Manager = "WinGet",
                UninstallCommand = uninstallCmd
            });
        }

        return result;
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

            var idToUse = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1];
            var uninstallCmd = !string.IsNullOrWhiteSpace(idToUse)
                ? $"winget uninstall --id {Quote(idToUse)} --exact"
                : $"winget uninstall --name {Quote(parts[0])} --exact";

            list.Add(new PackageEntry
            {
                Name = parts[0],
                Id = idToUse,
                Version = parts[2],
                Manager = "WinGet",
                UninstallCommand = uninstallCmd
            });
        }
        return list;
    }

    private async Task<IReadOnlyList<PackageEntry>> ScanChocolateyAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("choco")) return [];
        var r = await RunAsync("choco", "list --limit-output", ct);
        if (r.ExitCode != 0) return [];

        return r.StdOut.SplitLines()
            .Select(line => line.Trim().Split('|', 2))
            .Where(parts => parts.Length == 2 && IsPackageName(parts[0]) && IsPackageVersion(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = "Chocolatey",
                UninstallCommand = $"choco uninstall {Quote(parts[0])} -y"
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<PackageEntry>> ScanScoopAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("scoop")) return [];
        var r = await RunAsync("cmd.exe", "/c scoop list", ct);
        if (r.ExitCode != 0) return [];

        return r.StdOut.SplitLines()
            .Where(line => !line.StartsWith("----", StringComparison.Ordinal) && !line.Contains("Name", StringComparison.OrdinalIgnoreCase))
            .Select(line => WhitespaceColumns.Split(line.Trim()))
            .Where(parts => parts.Length >= 2 && IsPackageName(parts[0]) && IsPackageVersion(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = "Scoop",
                UninstallCommand = $"scoop uninstall {Quote(parts[0])}"
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<PackageEntry>> ScanDotnetToolsAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("dotnet")) return [];
        var r = await RunAsync("dotnet", "tool list --global", ct);
        if (r.ExitCode != 0) return [];

        return r.StdOut.SplitLines()
            .Where(line => !line.StartsWith("----", StringComparison.Ordinal) && !line.Contains("Package Id", StringComparison.OrdinalIgnoreCase))
            .Select(line => WhitespaceColumns.Split(line.Trim()))
            .Where(parts => parts.Length >= 2 && IsPackageName(parts[0]) && IsPackageVersion(parts[1]))
            .Select(parts => new PackageEntry
            {
                Name = parts[0],
                Id = parts[0],
                Version = parts[1],
                Manager = ".NET Tool",
                UninstallCommand = $"dotnet tool uninstall --global {Quote(parts[0])}"
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPackageName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Equals("Name", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("Package Id", StringComparison.OrdinalIgnoreCase);

    private static bool IsPackageVersion(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.StartsWith("-", StringComparison.Ordinal) &&
        !value.Equals("Version", StringComparison.OrdinalIgnoreCase);

    private static string Slice(string value, int start, int endExclusive)
    {
        if (start < 0 || start >= value.Length) return string.Empty;
        var end = endExclusive <= start ? value.Length : Math.Min(endExclusive, value.Length);
        return value[start..end];
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

    private static async Task<ProcessResult> RunAsync(string fileName, string arguments, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout.HasValue)
        {
            cts.CancelAfter(timeout.Value);
        }
        else
        {
            // Default 30-second timeout to prevent indefinite hangs if package managers stall
            cts.CancelAfter(TimeSpan.FromSeconds(30));
        }

        Process? process = null;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            };

            if (!process.Start())
            {
                process.Dispose();
                return new ProcessResult(-1, string.Empty, string.Empty);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var result = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
            process.Dispose();
            return result;
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, string.Empty, "Timed out");
        }
        catch
        {
            process?.Dispose();
            return new ProcessResult(-1, string.Empty, string.Empty);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
}

internal static class StringExtensions
{
    public static string[] SplitLines(this string value) =>
        value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
