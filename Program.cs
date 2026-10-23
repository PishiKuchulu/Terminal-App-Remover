using TerminalAppRemover;

// Minimal zero-dependency test harness (no NuGet in this repo's test path):
// each test throws on failure; exit code is the number of failures (capped).

var tests = new List<(string Name, Action Body)>();
void Test(string name, Action body) => tests.Add((name, body));

void Eq<T>(T expected, T actual, string what)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{what}: expected '{expected}', got '{actual}'");
}
void True(bool condition, string what)
{
    if (!condition) throw new InvalidOperationException($"expected true: {what}");
}
void False(bool condition, string what) => True(!condition, what);

// ---------------------------------------------------------------- VersionCompatible (2.3)

Test("VersionCompatible: 1.1 is NOT compatible with 1.10", () =>
    False(Parsers.VersionCompatible("1.1", "1.10"), "1.1 vs 1.10"));
Test("VersionCompatible: 1.0 is NOT compatible with 11.0", () =>
    False(Parsers.VersionCompatible("1.0", "11.0"), "1.0 vs 11.0"));
Test("VersionCompatible: 1.0 compatible with 1.0.1", () =>
    True(Parsers.VersionCompatible("1.0", "1.0.1"), "1.0 vs 1.0.1"));
Test("VersionCompatible: equal versions", () =>
    True(Parsers.VersionCompatible("2.45.1", "2.45.1"), "equal"));
Test("VersionCompatible: unknown version is compatible", () =>
    True(Parsers.VersionCompatible(null, "1.0"), "null version"));
Test("VersionCompatible: 1.0 compatible with 1.0-rc1 (dash boundary)", () =>
    True(Parsers.VersionCompatible("1.0", "1.0-rc1"), "dash boundary"));

// ---------------------------------------------------------------- RegistryMatchScore (2.2)

PackageEntry Pkg(string name, string? id = null, string? version = null) => new()
{
    Name = name,
    Id = id,
    Version = version,
    Manager = "WinGet",
    Uninstall = new UninstallSpec("winget", new[] { "uninstall" })
};

Test("RegistryScore: exact name + version beats everything", () =>
    Eq(100, Parsers.RegistryMatchScore(Pkg("Git", version: "2.45.1"), "Git", "2.45.1"), "exact score"));
Test("RegistryScore: same name, different version = no match", () =>
    Eq(0, Parsers.RegistryMatchScore(Pkg("Git", version: "2.0"), "Git", "9.9"), "version guard"));
Test("RegistryScore: 'Python' must not claim 'Pythonic' (no boundary)", () =>
    Eq(0, Parsers.RegistryMatchScore(Pkg("Python"), "Pythonic", null), "no boundary no match"));
Test("RegistryScore: 'Python' may match 'Python Launcher' but below exact", () =>
{
    var score = Parsers.RegistryMatchScore(Pkg("Python"), "Python Launcher", null);
    True(score > 0 && score < 80, $"prefix score in range, got {score}");
});
Test("RegistryScore: id app-part matches display name (VideoLAN.VLC -> VLC, 3 chars)", () =>
    Eq(40, Parsers.RegistryMatchScore(Pkg("VLC media player", id: "VideoLAN.VLC"), "VLC", null), "app part score"));

// ---------------------------------------------------------------- Scoop parser (2.1 regression)

Test("Scoop: packages containing 'name' are NOT dropped", () =>
{
    var lines = new[]
    {
        "Name        Version      Source Updated             Info",
        "----        -------      ------ -------             ----",
        "rename-tool 1.2.3        main   2024-01-01 10:00:00",
        "namecheap   0.9.0        main   2024-01-01 10:00:00",
        "git         2.45.1       main   2024-01-01 10:00:00"
    };
    var entries = Parsers.ParseScoop(lines);
    Eq(3, entries.Count, "scoop count");
    True(entries.Any(e => e.Name == "rename-tool"), "rename-tool present");
    Eq("1.2.3", entries.Single(e => e.Name == "rename-tool").Version, "rename-tool version (single-space column gap)");
});

// ---------------------------------------------------------------- Chocolatey / dotnet parsers

Test("Chocolatey: pipe format parsed, junk filtered", () =>
{
    var entries = Parsers.ParseChocolatey(new[] { "git|2.45.1", "badline", "curl|8.9.0" });
    Eq(2, entries.Count, "choco count");
    var git = entries.Single(e => e.Name == "git");
    Eq("2.45.1", git.Version, "git version");
    True(entries.All(e => e.Uninstall.Elevate), "choco elevates");
});

Test("DotnetTools: header filtered", () =>
{
    var entries = Parsers.ParseDotnetTools(new[]
    {
        "Package Id      Version      Commands",
        "-------------------------------------",
        "dotnet-ef       8.0.0        dotnet-ef"
    });
    Eq(1, entries.Count, "dotnet count");
    Eq("dotnet-ef", entries[0].Name, "dotnet name");
});

// ---------------------------------------------------------------- Cargo parser (3.1 regression)

Test("Cargo: plain crate line", () =>
{
    var entries = Parsers.ParseCargoList(new[] { "ripgrep v14.1.0:", "    rg.exe" });
    Eq(1, entries.Count, "cargo count");
    Eq("14.1.0", entries[0].Version, "cargo version");
});

Test("Cargo: git install line keeps the real version (https colon trap)", () =>
{
    var entries = Parsers.ParseCargoList(new[] { "foo v0.1.0 (https://github.com/x/y):", "    foo.exe" });
    Eq(1, entries.Count, "cargo git count");
    Eq("0.1.0", entries[0].Version, "cargo git version");
});

// ---------------------------------------------------------------- pip helpers (2.7)

Test("PipName: PEP 503 normalization", () =>
{
    Eq("zope_interface", Parsers.NormalizePipName("zope.interface"), "dots");
    Eq("flask_sqlalchemy", Parsers.NormalizePipName("Flask-SQLAlchemy"), "case+dash");
});

Test("CsvLine: quoted field containing a comma", () =>
{
    var fields = Parsers.ParseCsvLine("\"some,path/file.py\",sha256=abc,1234");
    Eq(3, fields.Length, "field count");
    Eq("some,path/file.py", fields[0], "quoted field");
    Eq("1234", fields[2], "size field");
});

Test("RecordSizes: sums size column incl. quoted paths", () =>
{
    var total = Parsers.SumRecordSizes(new[]
    {
        "pkg/__init__.py,sha256=x,100",
        "\"weird,path.py\",sha256=y,250",
        "no-size-line,,"
    });
    Eq(350L, total, "record sum");
});

// ---------------------------------------------------------------- Winget parser (1.1 / 2.4)

string WingetRow(string name, string id, string version, string available, string source) =>
    name.PadRight(46) + id.PadRight(26) + version.PadRight(10) + available.PadRight(11) + source;

Test("Winget: source parsed, winget entry clean, msix entry warned", () =>
{
    var lines = new[]
    {
        WingetRow("Name", "Id", "Version", "Available", "Source"),
        WingetRow("Git", "Git.Git", "2.45.1", "2.46.0", "winget"),
        WingetRow("Store App", "9NBLGGH4NNS1", "1.0", "", "msix"),
    };
    var result = Parsers.ParseWinget(lines);
    Eq(2, result.Entries.Count, "winget count");
    var git = result.Entries.Single(e => e.Name == "Git");
    Eq("winget", git.Source, "git source");
    Eq(null, git.Warning, "git has no warning");
    var store = result.Entries.Single(e => e.Name == "Store App");
    True(store.Warning?.Contains("Not installed by WinGet") ?? false, "msix warning");
});

Test("Winget: App Installer is protected", () =>
{
    var lines = new[]
    {
        WingetRow("Name", "Id", "Version", "Available", "Source"),
        WingetRow("App Installer", "Microsoft.AppInstaller", "1.0", "", "winget"),
        WingetRow("Microsoft Visual C++ 2015-2022", "Microsoft.VCRedist", "14.0", "", "winget"),
    };
    var result = Parsers.ParseWinget(lines);
    Eq(2, result.Entries.Count, "protected count");
    True(result.Entries.All(e => e.IsProtected && !e.CanUninstall), "all protected");
});

Test("Winget: truncated id falls back to --name with a warning", () =>
{
    var lines = new[]
    {
        WingetRow("Name", "Id", "Version", "Available", "Source"),
        WingetRow("Some Long App", "Some.Publisher.VeryLon\u2026", "1.0", "", "winget"),
    };
    var result = Parsers.ParseWinget(lines);
    var entry = result.Entries.Single();
    Eq("--name", entry.Uninstall.Arguments[1], "uses --name");
    True(entry.Warning?.Contains("truncated") ?? false, "truncation warning");
});

// ---------------------------------------------------------------- ToolPaths (2.6)

Test("ToolPaths: GOBIN wins over GOPATH", () =>
{
    Func<string, string?> env = n => n switch { "GOBIN" => @"D:\gobin", "GOPATH" => @"C:\go", _ => null };
    Eq(@"D:\gobin", ToolPaths.GoBinDir(env, @"C:\Users\x"), "gobin");
});

Test("ToolPaths: multi-entry GOPATH uses first entry's bin", () =>
{
    Func<string, string?> env = n => n == "GOPATH" ? @"C:\a;D:\b" : null;
    Eq(Path.Combine(@"C:\a", "bin"), ToolPaths.GoBinDir(env, @"C:\Users\x"), "gopath first");
});

Test("ToolPaths: CARGO_HOME respected, default ~/.cargo", () =>
{
    Func<string, string?> withHome = n => n == "CARGO_HOME" ? @"E:\cargo" : null;
    Func<string, string?> noHome = _ => null;
    Eq(@"E:\cargo", ToolPaths.CargoHome(withHome, @"C:\Users\x"), "cargo home env");
    Eq(Path.Combine(@"C:\Users\x", ".cargo"), ToolPaths.CargoHome(noHome, @"C:\Users\x"), "cargo home default");
});

// ---------------------------------------------------------------- cmd quoting (2.10)

Test("CmdInner: percent signs are doubled against cmd expansion", () =>
    True(CommandLine.BuildCmdInner("scoop", new[] { "uninstall", "100% legit" }).Contains("100%% legit"), "percent doubled"));

Test("QuoteArg: spaces get CommandLineToArgvW quotes", () =>
    Eq("\"a b\"", CommandLine.QuoteArg("a b"), "quoted"));

// ---------------------------------------------------------------- run

var failures = 0;
foreach (var (name, body) in tests)
{
    try
    {
        body();
        Console.WriteLine($"PASS  {name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL  {name}: {ex.Message}");
    }
}

Console.WriteLine($"\n{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;
