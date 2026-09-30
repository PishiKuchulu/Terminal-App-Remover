using Microsoft.Win32;

namespace TerminalAppRemover;

public sealed class InstalledSizeService
{
    private const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyDictionary<string, long> BuildSizeIndex(IEnumerable<PackageEntry> packages)
    {
        var candidates = packages.ToList();
        var index = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ReadHive(RegistryHive.LocalMachine, view, candidates, index);
            ReadHive(RegistryHive.CurrentUser, view, candidates, index);
        }

        CalculateNpmSizes(candidates, index);
        CalculatePnpmSizes(candidates, index);
        CalculatePipSizes(candidates, index);
        CalculateGoSizes(candidates, index);
        CalculateCargoSizes(candidates, index);

        return index;
    }

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
            catch { }
        }

        foreach (var modulesDir in moduleDirs)
        {
            foreach (var package in pnpmPackages)
            {
                var key = PackageKey(package);
                if (index.ContainsKey(key)) continue;

                var packagePath = Path.Combine(modulesDir, package.Name.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(packagePath)) continue;

                try
                {
                    long totalBytes = 0;
                    foreach (var file in new DirectoryInfo(packagePath).EnumerateFiles("*", SearchOption.AllDirectories))
                    {
                        totalBytes += file.Length;
                    }
                    if (totalBytes > 0) index[key] = totalBytes;
                }
                catch { }
            }
        }
    }

    private static void CalculatePipSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var pipPackages = packages.Where(p => p.Manager.Equals("pip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (pipPackages.Count == 0) return;

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
            catch { }
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
            catch { }
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
            catch { }
        }

        var venv = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        if (!string.IsNullOrWhiteSpace(venv))
        {
            var sp = Path.Combine(venv, "Lib", "site-packages");
            if (Directory.Exists(sp)) sitePackagesDirs.Add(sp);
        }

        foreach (var sp in sitePackagesDirs)
        {
            foreach (var package in pipPackages)
            {
                var key = PackageKey(package);
                if (index.ContainsKey(key)) continue;

                var nameUnderscore = package.Name.Replace('-', '_');
                var nameHyphen = package.Name.Replace('_', '-');

                string? distInfoDir = null;
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(sp, "*.dist-info", SearchOption.TopDirectoryOnly))
                    {
                        var dirName = Path.GetFileName(d);
                        if (dirName.StartsWith($"{nameUnderscore}-", StringComparison.OrdinalIgnoreCase) ||
                            dirName.StartsWith($"{nameHyphen}-", StringComparison.OrdinalIgnoreCase) ||
                            dirName.StartsWith($"{package.Name}-", StringComparison.OrdinalIgnoreCase))
                        {
                            distInfoDir = d;
                            break;
                        }
                    }
                }
                catch { }

                if (distInfoDir is not null)
                {
                    var recordFile = Path.Combine(distInfoDir, "RECORD");
                    if (File.Exists(recordFile))
                    {
                        try
                        {
                            long totalBytes = new FileInfo(recordFile).Length;
                            foreach (var line in File.ReadLines(recordFile))
                            {
                                var parts = line.Split(',');
                                if (parts.Length >= 3 && long.TryParse(parts[2], out var size))
                                {
                                    totalBytes += size;
                                }
                            }
                            if (totalBytes > 0)
                            {
                                index[key] = totalBytes;
                                continue;
                            }
                        }
                        catch { }
                    }
                }

                var pkgDir = Path.Combine(sp, nameUnderscore);
                if (!Directory.Exists(pkgDir)) pkgDir = Path.Combine(sp, nameHyphen);
                if (!Directory.Exists(pkgDir)) pkgDir = Path.Combine(sp, package.Name);

                if (Directory.Exists(pkgDir))
                {
                    try
                    {
                        long totalBytes = 0;
                        foreach (var f in new DirectoryInfo(pkgDir).EnumerateFiles("*", SearchOption.AllDirectories))
                            totalBytes += f.Length;
                        if (distInfoDir is not null && Directory.Exists(distInfoDir))
                        {
                            foreach (var f in new DirectoryInfo(distInfoDir).EnumerateFiles("*", SearchOption.AllDirectories))
                                totalBytes += f.Length;
                        }
                        if (totalBytes > 0) index[key] = totalBytes;
                    }
                    catch { }
                }
                else
                {
                    var pyFile = Path.Combine(sp, $"{package.Name}.py");
                    if (!File.Exists(pyFile)) pyFile = Path.Combine(sp, $"{nameUnderscore}.py");
                    if (File.Exists(pyFile))
                    {
                        try
                        {
                            long totalBytes = new FileInfo(pyFile).Length;
                            if (distInfoDir is not null && Directory.Exists(distInfoDir))
                            {
                                foreach (var f in new DirectoryInfo(distInfoDir).EnumerateFiles("*", SearchOption.AllDirectories))
                                    totalBytes += f.Length;
                            }
                            index[key] = totalBytes;
                        }
                        catch { }
                    }
                }
            }
        }
    }

    private static void CalculateGoSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var goPath = Environment.GetEnvironmentVariable("GOPATH");
        var goBin = !string.IsNullOrWhiteSpace(goPath)
            ? Path.Combine(goPath, "bin")
            : Path.Combine(userProfile, "go", "bin");
        if (!Directory.Exists(goBin)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Go", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            var exe = Path.Combine(goBin, $"{package.Name}.exe");
            if (File.Exists(exe))
            {
                try { index[key] = new FileInfo(exe).Length; } catch { }
            }
        }
    }

    private static void CalculateCargoSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cargoBin = Path.Combine(userProfile, ".cargo", "bin");
        if (!Directory.Exists(cargoBin)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("Cargo", StringComparison.OrdinalIgnoreCase)) continue;
            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            var exe = Path.Combine(cargoBin, $"{package.Name}.exe");
            if (File.Exists(exe))
            {
                try { index[key] = new FileInfo(exe).Length; } catch { }
            }
        }
    }

    private static void CalculateNpmSizes(IReadOnlyList<PackageEntry> packages, IDictionary<string, long> index)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmModules = Path.Combine(appData, "npm", "node_modules");
        if (!Directory.Exists(npmModules)) return;

        foreach (var package in packages)
        {
            if (!package.Manager.Equals("npm", StringComparison.OrdinalIgnoreCase)) continue;

            var key = PackageKey(package);
            if (index.ContainsKey(key)) continue;

            var packagePath = Path.Combine(npmModules, package.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(packagePath)) continue;

            try
            {
                long totalBytes = 0;
                var dirInfo = new DirectoryInfo(packagePath);
                foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    totalBytes += file.Length;
                }
                index[key] = totalBytes;
            }
            catch
            {
                // Best effort
            }
        }
    }

    private static void ReadHive(
        RegistryHive hive,
        RegistryView view,
        IReadOnlyList<PackageEntry> packages,
        IDictionary<string, long> index)
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
                    var publisher = entryKey.GetValue("Publisher") as string;

                    long sizeBytes;
                    try { sizeBytes = checked(estimatedKb * 1024L); }
                    catch (OverflowException) { continue; }

                    foreach (var package in packages)
                    {
                        if (!Matches(package, displayName, displayVersion, publisher)) continue;

                        var key = PackageKey(package);
                        if (!index.ContainsKey(key))
                            index[key] = sizeBytes;
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

    private static bool Matches(PackageEntry package, string displayName, string? displayVersion, string? publisher)
    {
        var pkgName = package.Name.Trim();
        var dispName = displayName.Trim();

        // 1. Direct name match
        if (string.Equals(pkgName, dispName, StringComparison.OrdinalIgnoreCase))
            return VersionCompatible(package.Version, displayVersion);

        // 2. WinGet output may truncate long names with ellipses
        var cleanPkgName = pkgName.TrimEnd('.', '\u2026').Trim();
        if (cleanPkgName.Length >= 4 && dispName.StartsWith(cleanPkgName, StringComparison.OrdinalIgnoreCase))
            return VersionCompatible(package.Version, displayVersion);

        // 3. Match against package ID
        if (!string.IsNullOrWhiteSpace(package.Id))
        {
            var id = package.Id.Trim();
            if (string.Equals(id, dispName, StringComparison.OrdinalIgnoreCase))
                return VersionCompatible(package.Version, displayVersion);

            // WinGet IDs follow Publisher.Package format (e.g. VideoLAN.VLC -> VLC)
            var dotIndex = id.LastIndexOf('.');
            if (dotIndex >= 0 && dotIndex < id.Length - 1)
            {
                var appPart = id[(dotIndex + 1)..].Trim();
                if (appPart.Length >= 4 && (dispName.Equals(appPart, StringComparison.OrdinalIgnoreCase) ||
                                           dispName.StartsWith(appPart, StringComparison.OrdinalIgnoreCase)))
                {
                    return VersionCompatible(package.Version, displayVersion);
                }
            }
        }

        return false;
    }

    private static bool VersionCompatible(string? packageVersion, string? registryVersion)
    {
        if (string.IsNullOrWhiteSpace(packageVersion) || string.IsNullOrWhiteSpace(registryVersion))
            return true;

        var v1 = packageVersion.Trim();
        var v2 = registryVersion.Trim();

        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
            return true;

        return v1.StartsWith(v2, StringComparison.OrdinalIgnoreCase) ||
               v2.StartsWith(v1, StringComparison.OrdinalIgnoreCase);
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
