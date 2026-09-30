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
        CalculateGoSizes(candidates, index);
        CalculateCargoSizes(candidates, index);

        return index;
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
