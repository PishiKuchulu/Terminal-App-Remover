using Microsoft.Win32;

namespace TerminalAppRemover;

public sealed class InstalledSizeService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyDictionary<string, long> BuildSizeIndex(IEnumerable<PackageEntry> packages)
    {
        var candidates = packages.ToList();
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // 1) Precise folder/binary measurements FIRST. The old order ran the loose
        //    registry name-matching first for EVERY manager, and the folder passes
        //    then skipped anything already present — so a chance registry name
        //    collision overwrote the exact size of npm/pip/Cargo packages.
        CalculateNpmSizes(candidates, index);
        CalculatePnpmSizes(candidates, index);
        CalculatePipSizes(candidates, index);
        CalculateScoopSizes(candidates, index);
        CalculateChocolateySizes(candidates, index);
        CalculateDotnetToolSizes(candidates, index);
        CalculateGoSizes(candidates, index);
        CalculateCargoSizes(candidates, index);

        // 2) Registry EstimatedSize LAST, and only for the managers that actually
        //    represent ARP-registered applications (WinGet, Chocolatey).
        var registryCandidates = candidates
            .Where(p => p.Manager.Equals("WinGet", StringComparison.OrdinalIgnoreCase) ||
                        p.Manager.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (registryCandidates.Count > 0)
        {
            // Best-scoring match across ALL hives/views wins (scores compared
            // across passes, merged once at the end).
            var registryBest = new Dictionary<string, (long Size, int Score)>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadHive(RegistryHive.LocalMachine, view, registryCandidates, registryBest);
                ReadHive(RegistryHive.CurrentUser, view, registryCandidates, registryBest);
            }
            foreach (var (key, value) in registryBest)
            {
                if (!index.ContainsKey(key))
                    index[key] = value.Size;
            }
        }

        return index;
    }

    // ------------------------------------------------------------------ npm

    private static void CalculateNpmSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var npmPackages = packages.Where(p => p.Manager.Equals("npm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (npmPackages.Count == 0) return;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var modulesDir in ToolPaths.NpmNodeModulesCandidates(ToolPaths.GetEnv, appData, localAppData, userProfile))
        {
            if (!Directory.Exists(modulesDir)) continue;
            foreach (var package in npmPackages)
            {
                var key = PackageKey(package);
                if (index.ContainsKey(key)) continue;

                var packagePath = Path.Combine(modulesDir, package.Name.Replace('/', Path.DirectorySeparatorChar));
                if (TryGetDirectorySize(packagePath, out var size))
                    index[key] = size;
            }
        }
    }

    // ------------------------------------------------------------------ pnpm

    private static void CalculatePnpmSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var pnpmPackages = packages.Where(p => p.Manager.Equals("pnpm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pnpmPackages.Count == 0) return;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var pnpmHome = Environment.GetEnvironmentVariable("PNPM_HOME");

        var candidatesDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(pnpmHome)) candidatesDirs.Add(pnpmHome);
        candidatesDirs.Add(Path.Combine(localAppData, "pnpm"));
        candidatesDirs.Add(Path.Combine(appData, "pnpm"));

        var moduleDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseDir in candidatesDirs)
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                var directModules = Path.Combine(baseDir, "node_modules");
                if (Directory.Exists(directModules)) moduleDirs.Add(directModules);

                var globalDir = Path.Combine(baseDir, "global");
                if (Directory.Exists(globalDir))
                {
                    foreach (var sub in Directory.EnumerateDirectories(globalDir))
                    {
                        var subModules = Path.Combine(sub, "node_modules");
                        if (Directory.Exists(subModules)) moduleDirs.Add(subModules);
                    }
                }
            }
            catch { /* best effort */ }
        }

        foreach (var modulesDir in moduleDirs)
        {
            foreach (var package in pnpmPackages)
            {
                var key = PackageKey(package);
                if (index.ContainsKey(key)) continue;

                var packagePath = Path.Combine(modulesDir, package.Name.Replace('/', Path.DirectorySeparatorChar));
                // NOTE: pnpm links files from its content-addressable store, so this is the
                // package directory's logical size — files shared via hardlinks are counted
                // per package. Documented as an approximation in FEATURES.md.
                if (TryGetDirectorySize(packagePath, out var size))
                    index[key] = size;
            }
        }
    }

    // ------------------------------------------------------------------ pip

    private static void CalculatePipSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var pipPackages = packages.Where(p => p.Manager.Equals("pip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pipPackages.Count == 0) return;

        foreach (var sp in EnumerateSitePackagesDirs())
        {
            // Index this site-packages directory's dist-info folders ONCE (the old
            // code re-enumerated *.dist-info for every package: O(N²)), keyed by
            // PEP 503 normalized name, preferring an entry whose version matches.
            Dictionary<string, List<(string Dir, string Version)>> distInfos;
            try
            {
                distInfos = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in Directory.EnumerateDirectories(sp, "*.dist-info", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(d)[..^".dist-info".Length];
                    var dash = dirName.LastIndexOf('-');
                    if (dash <= 0) continue;
                    var normName = Parsers.NormalizePipName(dirName[..dash]);
                    var version = dirName[(dash + 1)..];
                    if (!distInfos.TryGetValue(normName, out var list))
                        distInfos[normName] = list = new List<(string, string)>();
                    list.Add((d, version));
                }
            }
            catch { continue; }

            foreach (var package in pipPackages)
            {
                var key = PackageKey(package);
                if (index.ContainsKey(key)) continue;

                string? distInfoDir = null;
                if (distInfos.TryGetValue(Parsers.NormalizePipName(package.Name), out var matches))
                {
                    // Prefer the dist-info whose version equals the version pip reported:
                    // with several Pythons installed, a same-name dist-info in another
                    // interpreter's site-packages must not win (old first-match bug).
                    var chosen = matches.FirstOrDefault(m =>
                        !string.IsNullOrWhiteSpace(package.Version) &&
                        m.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase));
                    distInfoDir = chosen.Dir ?? matches[0].Dir;
                }

                if (distInfoDir is not null)
                {
                    var recordFile = Path.Combine(distInfoDir, "RECORD");
                    if (File.Exists(recordFile))
                    {
                        try
                        {
                            // PEP 376 RECORD is real CSV — ParseCsvLine handles quoted
                            // paths containing commas (the old Split(',') did not).
                            long totalBytes = new FileInfo(recordFile).Length
                                              + Parsers.SumRecordSizes(File.ReadLines(recordFile));
                            if (totalBytes > 0)
                            {
                                index[key] = totalBytes;
                                continue;
                            }
                        }
                        catch { /* fall through to folder sizing */ }
                    }
                }

                var nameUnderscore = Parsers.NormalizePipName(package.Name);
                var pkgDir = Path.Combine(sp, nameUnderscore);
                if (!Directory.Exists(pkgDir)) pkgDir = Path.Combine(sp, package.Name.Replace('_', '-'));
                if (!Directory.Exists(pkgDir)) pkgDir = Path.Combine(sp, package.Name);

                if (TryGetDirectorySize(pkgDir, out var dirSize))
                {
                    long total = dirSize;
                    if (distInfoDir is not null && TryGetDirectorySize(distInfoDir, out var diSize)) total += diSize;
                    if (total > 0) { index[key] = total; continue; }
                }

                var pyFile = Path.Combine(sp, $"{nameUnderscore}.py");
                if (!File.Exists(pyFile)) pyFile = Path.Combine(sp, $"{package.Name}.py");
                if (File.Exists(pyFile))
                {
                    try
                    {
                        long total = new FileInfo(pyFile).Length;
                        if (distInfoDir is not null && TryGetDirectorySize(distInfoDir, out var diSize)) total += diSize;
                        index[key] = total;
                    }
                    catch { /* best effort */ }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSitePackagesDirs()
    {
        var sitePackagesDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localPythonDir = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(localPythonDir))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(localPythonDir, "Python*"))
                {
                    var sp = Path.Combine(dir, "Lib", "site-packages");
                    if (Directory.Exists(sp)) sitePackagesDirs.Add(sp);
                }
            }
            catch { /* best effort */ }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roamingPythonDir = Path.Combine(appData, "Python");
        if (Directory.Exists(roamingPythonDir))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(roamingPythonDir, "Python*"))
                {
                    var sp = Path.Combine(dir, "site-packages");
                    if (Directory.Exists(sp)) sitePackagesDirs.Add(sp);
                }
            }
            catch { /* best effort */ }
        }

        foreach (var root in new[] { @"C:\Program Files", @"C:\Program Files (x86)", @"C:\" })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "Python*"))
                {
                    var sp = Path.Combine(dir, "Lib", "site-packages");
                    if (Directory.Exists(sp)) sitePackagesDirs.Add(sp);
                }
            }
            catch { /* best effort */ }
        }

        var venv = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        if (!string.IsNullOrWhiteSpace(venv))
        {
            var sp = Path.Combine(venv, "Lib", "site-packages");
            if (Directory.Exists(sp)) sitePackagesDirs.Add(sp);
        }

        return sitePackagesDirs;
    }

    // ------------------------------------------------------------------ Scoop / Chocolatey / .NET (new: previously never sized)

    private static void CalculateScoopSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appsDir = Path.Combine(ToolPaths.ScoopRoot(ToolPaths.GetEnv, userProfile), "apps");
        if (!Directory.Exists(appsDir)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Scoop", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            // scoop keeps the live version under <app>\current
            var current = Path.Combine(appsDir, package.Name, "current");
            if (TryGetDirectorySize(current, out var size) || TryGetDirectorySize(Path.Combine(appsDir, package.Name), out size))
                index[key] = size;
        }
    }

    private static void CalculateChocolateySizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var libDir = Path.Combine(ToolPaths.ChocolateyRoot(ToolPaths.GetEnv), "lib");
        if (!Directory.Exists(libDir)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            if (TryGetDirectorySize(Path.Combine(libDir, package.Name), out var size))
                index[key] = size;
        }
    }

    private static void CalculateDotnetToolSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dotnetHome = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
        var toolsRoot = !string.IsNullOrWhiteSpace(dotnetHome)
            ? Path.Combine(dotnetHome, ".dotnet", "tools")
            : Path.Combine(userProfile, ".dotnet", "tools");
        var storeDir = Path.Combine(toolsRoot, ".store");
        if (!Directory.Exists(storeDir)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals(".NET Tool", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            // Store folders are named by the lower-cased package id
            var idDir = Path.Combine(storeDir, (package.Id ?? package.Name).ToLowerInvariant());
            if (TryGetDirectorySize(idDir, out var size))
                index[key] = size;
        }
    }

    // ------------------------------------------------------------------ Go / Cargo binaries

    private static void CalculateGoSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var goBin = ToolPaths.GoBinDir(ToolPaths.GetEnv, userProfile);
        if (!Directory.Exists(goBin)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Go", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            var exe = Path.Combine(goBin, $"{package.Name}.exe");
            if (File.Exists(exe))
            {
                try { index[key] = new FileInfo(exe).Length; } catch { /* best effort */ }
            }
        }
    }

    private static void CalculateCargoSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoBin = Path.Combine(ToolPaths.CargoHome(ToolPaths.GetEnv, userProfile), "bin");
        if (!Directory.Exists(cargoBin)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Cargo", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            var exe = Path.Combine(cargoBin, $"{package.Name}.exe");
            if (File.Exists(exe))
            {
                try { index[key] = new FileInfo(exe).Length; } catch { /* best effort */ }
            }
        }
    }

    // ------------------------------------------------------------------ registry (best-scoring match wins)

    private static void ReadHive(
        RegistryHive hive,
        RegistryView view,
        IReadOnlyList<PackageEntry> packages,
        IDictionary<string, (long Size, int Score)> best)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallPath, writable: false);
            if (uninstallKey is null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var entryKey = uninstallKey.OpenSubKey(subKeyName, writable: false);
                    if (entryKey is null) continue;

                    var estimatedKb = ReadLong(entryKey.GetValue("EstimatedSize"));
                    if (estimatedKb <= 0) continue;

                    var displayName = entryKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName)) continue;

                    var displayVersion = entryKey.GetValue("DisplayVersion") as string;

                    long sizeBytes;
                    try { sizeBytes = checked(estimatedKb * 1024L); }
                    catch (OverflowException) { continue; }

                    foreach (var package in packages)
                    {
                        var key = PackageKey(package);
                        var score = Parsers.RegistryMatchScore(package, displayName, displayVersion);
                        if (score <= 0) continue;

                        if (!best.TryGetValue(key, out var current) || score > current.Score)
                            best[key] = (sizeBytes, score);
                    }
                }
                catch
                {
                    // Ignore corrupted or unreadable subkey entries
                }
            }
        }
        catch
        {
            // Registry access is best-effort
        }
    }

    private static bool TryGetDirectorySize(string path, out long size)
    {
        size = 0;
        if (!Directory.Exists(path)) return false;
        try
        {
            long total = 0;
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories))
                total += file.Length;
            if (total <= 0) return false;
            size = total;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long ReadLong(object? value)
    {
        return value switch
        {
            int intValue => intValue,
            long longValue => longValue,
            uint uintValue => uintValue,
            ulong ulongValue when ulongValue <= long.MaxValue => (long)ulongValue,
            _ when long.TryParse(value?.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }

    public static string PackageKey(PackageEntry package) =>
        $"{package.Manager}:{package.Id ?? package.Name}";
}
