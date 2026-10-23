using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TerminalAppRemover;

public sealed class PackageManagerService
{
    // Scan timeouts per manager. The old single 30s timeout silently dropped slow
    // managers (first-run winget source updates, large npm trees) with no trace.
    private static readonly TimeSpan WingetTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan DefaultScanTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async Task<ScanOutcome> ScanAsync(CancellationToken cancellationToken = default)
    {
        var scanners = new Func<CancellationToken, Task<ManagerScan>>[]
        {
            ScanWingetAsync,
            ScanChocolateyAsync,
            ScanScoopAsync,
            ScanDotnetToolsAsync,
            ScanNpmAsync,
            ScanPnpmAsync,
            ScanPipAsync,
            ScanCargoAsync,
            ScanGoAsync
        };

        var results = await Task.WhenAll(scanners.Select(s => s(cancellationToken)));

        var packages = results.SelectMany(x => x.Entries)
            .GroupBy(x => $"{x.Manager}:{x.Id ?? x.Name}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Manager, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var warnings = results.Select(x => x.Warning).OfType<string>().ToList();
        return new ScanOutcome(packages, warnings);
    }

    public static async Task<bool> IsCommandAvailableAsync(string command)
    {
        var result = await RunAsync("where.exe", new[] { command }, CancellationToken.None, ProbeTimeout);
        return result.ExitCode == 0;
    }

    /// <summary>
    /// True only when the command actually runs. `where.exe` also "finds" the
    /// Microsoft Store python alias (%LOCALAPPDATA%\Microsoft\WindowsApps\python.exe),
    /// which exits 9009 for every real invocation — probing avoids that trap.
    /// </summary>
    private static async Task<bool> ProbeRunsAsync(string fileName, string[] arguments, CancellationToken ct)
    {
        var r = await RunAsync(fileName, arguments, ct, ProbeTimeout);
        return r.ExitCode == 0;
    }

    private async Task<ManagerScan> ScanWingetAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("winget")) return ManagerScan.Empty;

        var r = await RunAsync("winget",
            new[] { "list", "--accept-source-agreements", "--disable-interactivity" }, ct, WingetTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"WinGet: timed out after {WingetTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (r.ExitCode != 0) return ManagerScan.Warn($"WinGet: 'winget list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        var parsed = Parsers.ParseWinget(r.StdOut.SplitLines());
        return new ManagerScan(parsed.Entries, parsed.Warning);
    }

    private async Task<ManagerScan> ScanChocolateyAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("choco")) return ManagerScan.Empty;

        // --local-only is required: on Chocolatey 1.x, plain `choco list` queried
        // the remote community feed and returned thousands of unrelated packages.
        var r = await RunAsync("choco", new[] { "list", "--local-only", "--limit-output" }, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"Chocolatey: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (r.ExitCode != 0) return ManagerScan.Warn($"Chocolatey: 'choco list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        return new ManagerScan(Parsers.ParseChocolatey(r.StdOut.SplitLines()), null);
    }

    private async Task<ManagerScan> ScanScoopAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("scoop")) return ManagerScan.Empty;

        var r = await RunCmdAsync("scoop", new[] { "list" }, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"Scoop: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (r.ExitCode != 0) return ManagerScan.Warn($"Scoop: 'scoop list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        return new ManagerScan(Parsers.ParseScoop(r.StdOut.SplitLines()), null);
    }

    private async Task<ManagerScan> ScanDotnetToolsAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("dotnet")) return ManagerScan.Empty;

        var r = await RunAsync("dotnet", new[] { "tool", "list", "--global" }, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($".NET Tools: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (r.ExitCode != 0) return ManagerScan.Warn($".NET Tools: 'dotnet tool list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        return new ManagerScan(Parsers.ParseDotnetTools(r.StdOut.SplitLines()), null);
    }

    private async Task<ManagerScan> ScanNpmAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("npm")) return ManagerScan.Empty;

        var r = await RunCmdAsync("npm", new[] { "list", "-g", "--depth=0", "--json" }, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"npm: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (string.IsNullOrWhiteSpace(r.StdOut)) return ManagerScan.Warn($"npm: 'npm list -g' produced no output (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        // npm exits 1 for extraneous/invalid/peer-dependency problems while still
        // printing a perfectly valid dependency JSON — the old code discarded the
        // whole list in that case. Parse whatever JSON we got; warn, don't vanish.
        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            if (!doc.RootElement.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                return ManagerScan.Warn("npm: unexpected output format — its packages were not listed.");

            var list = new List<PackageEntry>();
            foreach (var prop in deps.EnumerateObject())
            {
                var name = prop.Name;
                if (string.Equals(name, "npm", StringComparison.OrdinalIgnoreCase)) continue;

                string? version = null;
                if (prop.Value.TryGetProperty("version", out var vProp))
                    version = vProp.GetString();

                list.Add(new PackageEntry
                {
                    Name = name,
                    Id = name,
                    Version = version,
                    Manager = "npm",
                    Uninstall = Parsers.CmdSpec("npm", "uninstall", "-g", name)
                });
            }

            string? warning = r.ExitCode != 0
                ? $"npm: 'npm list -g' reported problems (exit {r.ExitCode}); the list shown may be incomplete."
                : null;
            return new ManagerScan(list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), warning);
        }
        catch (JsonException)
        {
            return ManagerScan.Warn("npm: could not parse 'npm list -g --json' output — its packages were not listed.");
        }
    }

    private async Task<ManagerScan> ScanPnpmAsync(CancellationToken ct)
    {
        if (!await IsCommandAvailableAsync("pnpm")) return ManagerScan.Empty;

        var r = await RunCmdAsync("pnpm", new[] { "list", "-g", "--depth=0", "--json" }, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"pnpm: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (string.IsNullOrWhiteSpace(r.StdOut)) return ManagerScan.Warn($"pnpm: 'pnpm list -g' produced no output (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            var list = new List<PackageEntry>();

            // pnpm prints an array of workspace roots on most versions, a single
            // object on others — accept both (the old code only handled the array).
            var roots = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : doc.RootElement.ValueKind == JsonValueKind.Object
                    ? new List<JsonElement> { doc.RootElement }
                    : new List<JsonElement>();

            foreach (var elem in roots)
            {
                if (!elem.TryGetProperty("dependencies", out var deps) || deps.ValueKind != JsonValueKind.Object)
                    continue;

                foreach (var prop in deps.EnumerateObject())
                {
                    var name = prop.Name;
                    string? version = null;
                    if (prop.Value.TryGetProperty("version", out var vProp))
                        version = vProp.GetString();

                    list.Add(new PackageEntry
                    {
                        Name = name,
                        Id = name,
                        Version = version,
                        Manager = "pnpm",
                        Uninstall = Parsers.CmdSpec("pnpm", "remove", "-g", name)
                    });
                }
            }

            string? warning = r.ExitCode != 0 && list.Count == 0
                ? $"pnpm: 'pnpm list -g' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}"
                : null;
            return new ManagerScan(list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), warning);
        }
        catch (JsonException)
        {
            return ManagerScan.Warn("pnpm: could not parse 'pnpm list -g --json' output — its packages were not listed.");
        }
    }

    private async Task<ManagerScan> ScanPipAsync(CancellationToken ct)
    {
        // Candidate invocation chains, best first. Each candidate is PROBED by
        // actually running `pip --version`, because `where python` can resolve to
        // the Microsoft Store alias stub which fails every real invocation.
        var candidates = new (string File, string[] Prefix, string Label)[]
        {
            ("pip", Array.Empty<string>(), "pip"),
            ("python", new[] { "-m", "pip" }, "python -m pip"),
            ("py", new[] { "-m", "pip" }, "py -m pip"),
        };

        string? file = null;
        string[] prefix = Array.Empty<string>();
        foreach (var (candidateFile, candidatePrefix, _) in candidates)
        {
            var probeArgs = candidatePrefix.Concat(new[] { "--version" }).ToArray();
            if (await ProbeRunsAsync(candidateFile, probeArgs, ct))
            {
                file = candidateFile;
                prefix = candidatePrefix;
                break;
            }
        }
        if (file is null) return ManagerScan.Empty;

        var listArgs = prefix.Concat(new[] { "list", "--format=json" }).ToArray();
        var r = await RunAsync(file, listArgs, ct, DefaultScanTimeout);
        if (r.TimedOut) return ManagerScan.Warn($"pip: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
        if (r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.StdOut))
            return ManagerScan.Warn($"pip: 'pip list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");

        try
        {
            using var doc = JsonDocument.Parse(r.StdOut);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ManagerScan.Warn("pip: unexpected output format — its packages were not listed.");

            var list = new List<PackageEntry>();
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pip", "setuptools", "wheel" };
            var uninstallPrefix = prefix.Concat(new[] { "uninstall", "-y" }).ToArray();

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var n) || !item.TryGetProperty("version", out var v))
                    continue;

                var name = n.GetString();
                if (string.IsNullOrWhiteSpace(name) || ignored.Contains(name)) continue;

                list.Add(new PackageEntry
                {
                    Name = name,
                    Id = name,
                    Version = v.GetString(),
                    Manager = "pip",
                    Warning = "Python library package — removing it can break other Python tools that depend on it.",
                    Uninstall = new UninstallSpec(file, uninstallPrefix.Concat(new[] { name }).ToArray())
                });
            }

            return new ManagerScan(list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (JsonException)
        {
            return ManagerScan.Warn("pip: could not parse 'pip list --format=json' output — its packages were not listed.");
        }
    }

    private async Task<ManagerScan> ScanCargoAsync(CancellationToken ct)
    {
        var list = new List<PackageEntry>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoHome = ToolPaths.CargoHome(ToolPaths.GetEnv, userProfile);
        var cratesFile = Path.Combine(cargoHome, ".crates2.json");

        if (File.Exists(cratesFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(cratesFile, ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("installs", out var installs) && installs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in installs.EnumerateObject())
                    {
                        // Key format: "name version (source)"
                        var parts = prop.Name.Split(' ', 3);
                        if (parts.Length < 2) continue;
                        list.Add(new PackageEntry
                        {
                            Name = parts[0],
                            Id = parts[0],
                            Version = parts[1],
                            Manager = "Cargo",
                            Uninstall = new UninstallSpec("cargo", new[] { "uninstall", parts[0] })
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ManagerScan.Warn($"Cargo: could not read {cratesFile} ({ex.GetType().Name}) — its packages were not listed.");
            }
        }

        if (list.Count > 0) return new ManagerScan(list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);

        if (await IsCommandAvailableAsync("cargo"))
        {
            var r = await RunAsync("cargo", new[] { "install", "--list" }, ct, DefaultScanTimeout);
            if (r.TimedOut) return ManagerScan.Warn($"Cargo: timed out after {DefaultScanTimeout.TotalSeconds:0}s — its packages were not listed.");
            if (r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.StdOut))
                return new ManagerScan(Parsers.ParseCargoList(r.StdOut.SplitLines()), null);
            if (r.ExitCode != 0)
                return ManagerScan.Warn($"Cargo: 'cargo install --list' failed (exit {r.ExitCode}){FirstLineSuffix(r.StdErr)}");
        }

        return ManagerScan.Empty;
    }

    private Task<ManagerScan> ScanGoAsync(CancellationToken ct)
    {
        var list = new List<PackageEntry>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var goBin = ToolPaths.GoBinDir(ToolPaths.GetEnv, userProfile);

        if (Directory.Exists(goBin))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(goBin, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileNameWithoutExtension(file);
                    list.Add(new PackageEntry
                    {
                        Name = name,
                        Id = name,
                        Version = null,
                        Manager = "Go",
                        Warning = "Listed because an .exe exists in the Go bin directory; it may have been placed there manually. Uninstall deletes only this file (module caches are not touched).",
                        // In-process delete + post-delete existence check (see UninstallSpec.FileToDelete).
                        // The old `cmd.exe /c del` could report success while the file survived.
                        Uninstall = new UninstallSpec("go", Array.Empty<string>(), FileToDelete: file)
                    });
                }
            }
            catch (OperationCanceledException)
            {
                throw; // the old catch{} swallowed cancellation from inside the try block
            }
            catch (Exception ex)
            {
                return Task.FromResult(ManagerScan.Warn($"Go: could not read {goBin} ({ex.GetType().Name}) — its packages were not listed."));
            }
        }

        return Task.FromResult(new ManagerScan(
            list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList(), null));
    }

    // ------------------------------------------------------------------ process plumbing

    private sealed record ManagerScan(IReadOnlyList<PackageEntry> Entries, string? Warning)
    {
        public static readonly ManagerScan Empty = new(Array.Empty<PackageEntry>(), null);
        public static ManagerScan Warn(string warning) => new(Array.Empty<PackageEntry>(), warning);
    }

    private static string FirstLineSuffix(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";
        var first = stderr.SplitLines().FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return "";
        if (first.Length > 120) first = first[..120] + "…";
        return $": {first}";
    }

    /// <summary>Run a cmd-shim tool (scoop/npm/pnpm) through cmd.exe /c with a cmd-safe inner string.</summary>
    private static Task<ProcessResult> RunCmdAsync(string fileName, string[] arguments, CancellationToken ct, TimeSpan timeout) =>
        RunAsync("cmd.exe", new[] { "/c", CommandLine.BuildCmdInner(fileName, arguments) }, ct, timeout);

    private static async Task<ProcessResult> RunAsync(string fileName, string[] arguments, CancellationToken ct, TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            foreach (var arg in arguments) startInfo.ArgumentList.Add(arg);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                return new ProcessResult(-1, string.Empty, string.Empty, false);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);
            var result = new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask, false);
            process.Dispose();
            return result;
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
                process.Dispose();
            }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, string.Empty, "Timed out", TimedOut: true);
        }
        catch (Exception ex)
        {
            process?.Dispose();
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }
    }

    private readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);
}

internal static class StringExtensions
{
    public static string[] SplitLines(this string value) =>
        value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
