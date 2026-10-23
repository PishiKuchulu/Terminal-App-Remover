namespace TerminalAppRemover;

/// <summary>
/// Single source of truth for locating tool directories (previously the Go/Cargo
/// path logic was copy-pasted between the scanner and the size service, and
/// GOBIN / CARGO_HOME / multi-entry GOPATH were ignored).
/// Environment access goes through a delegate so the logic is unit-testable.
/// </summary>
internal static class ToolPaths
{
    public static string? GetEnv(string name) => Environment.GetEnvironmentVariable(name);

    /// <summary>Go binary directory: $GOBIN, else first entry of $GOPATH + \bin, else ~/go/bin.</summary>
    public static string GoBinDir(Func<string, string?> env, string userProfile)
    {
        var gobin = env("GOBIN");
        if (!string.IsNullOrWhiteSpace(gobin)) return gobin.Trim();

        var gopath = env("GOPATH");
        if (!string.IsNullOrWhiteSpace(gopath))
        {
            // GOPATH is a ';'-separated list on Windows; binaries land in the first entry's bin.
            var first = gopath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first)) return Path.Combine(first, "bin");
        }

        return Path.Combine(userProfile, "go", "bin");
    }

    /// <summary>Cargo home: $CARGO_HOME, else ~/.cargo.</summary>
    public static string CargoHome(Func<string, string?> env, string userProfile)
    {
        var cargoHome = env("CARGO_HOME");
        return !string.IsNullOrWhiteSpace(cargoHome)
            ? cargoHome.Trim()
            : Path.Combine(userProfile, ".cargo");
    }

    /// <summary>Scoop root: $SCOOP, else ~/scoop.</summary>
    public static string ScoopRoot(Func<string, string?> env, string userProfile)
    {
        var scoop = env("SCOOP");
        return !string.IsNullOrWhiteSpace(scoop)
            ? scoop.Trim()
            : Path.Combine(userProfile, "scoop");
    }

    /// <summary>Chocolatey install root: $ChocolateyInstall, else %ProgramData%\chocolatey.</summary>
    public static string ChocolateyRoot(Func<string, string?> env)
    {
        var choco = env("ChocolateyInstall");
        if (!string.IsNullOrWhiteSpace(choco)) return choco.Trim();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(programData, "chocolatey");
    }

    /// <summary>
    /// Candidate global node_modules roots for npm. The fixed %APPDATA%\npm path is
    /// wrong whenever the prefix was changed or nvm-windows / Volta / fnm is used,
    /// so every known layout is probed; callers keep the ones that exist.
    /// </summary>
    public static IReadOnlyList<string> NpmNodeModulesCandidates(Func<string, string?> env, string appData, string localAppData, string userProfile)
    {
        var dirs = new List<string>();

        var prefix = env("npm_config_prefix");
        if (!string.IsNullOrWhiteSpace(prefix))
            dirs.Add(Path.Combine(prefix.Trim(), "node_modules"));

        dirs.Add(Path.Combine(appData, "npm", "node_modules"));

        // nvm-windows: $NVM_HOME\<version>\node_modules (and the symlink $NVM_SYMLINK)
        var nvmHome = env("NVM_HOME");
        if (!string.IsNullOrWhiteSpace(nvmHome) && Directory.Exists(nvmHome))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(nvmHome, "v*"))
                    dirs.Add(Path.Combine(dir, "node_modules"));
            }
            catch { /* best effort */ }
        }
        var nvmSymlink = env("NVM_SYMLINK");
        if (!string.IsNullOrWhiteSpace(nvmSymlink))
            dirs.Add(Path.Combine(nvmSymlink.Trim(), "node_modules"));

        // Volta: $VOLTA_HOME\tools\image\node\<version>\node_modules
        var voltaHome = env("VOLTA_HOME") ?? Path.Combine(localAppData, "Volta");
        var voltaNode = Path.Combine(voltaHome, "tools", "image", "node");
        if (Directory.Exists(voltaNode))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(voltaNode))
                    dirs.Add(Path.Combine(dir, "node_modules"));
            }
            catch { /* best effort */ }
        }

        // fnm: $FNM_DIR\node-versions\<version>\installation\node_modules
        var fnmDir = env("FNM_DIR");
        if (!string.IsNullOrWhiteSpace(fnmDir))
        {
            var versions = Path.Combine(fnmDir.Trim(), "node-versions");
            if (Directory.Exists(versions))
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(versions, "v*"))
                        dirs.Add(Path.Combine(dir, "installation", "node_modules"));
                }
                catch { /* best effort */ }
            }
        }

        return dirs;
    }
}
