using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace TerminalAppRemover.Bootstrapper;

/// <summary>
/// Single-file launcher for Terminal App Remover.
///
/// Why this exists: a WinUI 3 (unpackaged) app cannot be published as one
/// standalone exe — its XAML resources (.pri/.xbf) and native DLLs must sit
/// next to the exe, so the "single exe" attached to the old v1.2.0 release
/// could not run on its own. This bootstrapper is a small NativeAOT WinExe
/// with the whole self-contained app ZIP embedded as a resource:
///
///   first run  → extracts to %LOCALAPPDATA%\TerminalAppRemover\app-&lt;payloadhash&gt;\
///   every run  → launches the extracted TerminalAppRemover.exe and exits
///
/// The version folder is keyed by the payload's SHA-256, so a newer
/// single-file download extracts side-by-side and stale versions are
/// cleaned up afterwards (best effort).
/// </summary>
internal static class Program
{
    private const string PayloadResourceName = "payload.zip";
    private const string AppExeName = "TerminalAppRemover.exe";

    private static int Main(string[] args)
    {
        try
        {
            var appDir = Payload.EnsureExtracted(OpenPayload, Payload.DefaultBaseDir());
            Payload.CleanupStaleVersions(Payload.DefaultBaseDir(), appDir);

            var exePath = Path.Combine(appDir, AppExeName);
            if (!OperatingSystem.IsWindows())
            {
                // Developer smoke-test path (the shipped binary is win-x64 only).
                Console.WriteLine(exePath);
                return 0;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = appDir,
                UseShellExecute = true,
                Arguments = string.Join(" ", args.Select(QuoteArg))
            };
            Process.Start(startInfo);
            return 0;
        }
        catch (Exception ex)
        {
            ShowError($"Terminal App Remover could not start.\n\n{ex.Message}");
            return 1;
        }
    }

    private static Stream OpenPayload() =>
        typeof(Program).Assembly.GetManifestResourceStream(PayloadResourceName)
        ?? throw new InvalidOperationException(
            "This single-file build has no embedded app payload. Download TerminalAppRemover-SingleFile.exe from the official release.");

    private static string QuoteArg(string value)
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

    private static void ShowError(string text)
    {
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, text, "Terminal App Remover", 0x10 /* MB_ICONERROR */);
        else
            Console.Error.WriteLine(text);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

internal static class Payload
{
    public static string DefaultBaseDir()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // GetFolderPath can return empty on exotic hosts; never extract relative
        // to the (unpredictable) current directory in that case.
        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, "TerminalAppRemover");
    }

    /// <summary>Extract the payload (once per payload hash) and return the app directory.</summary>
    public static string EnsureExtracted(Func<Stream> openPayload, string baseDir)
    {
        string hash;
        using (var stream = openPayload())
            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        var appDir = Path.Combine(baseDir, "app-" + hash[..12]);
        var marker = Path.Combine(appDir, MarkerName);
        if (File.Exists(marker) && File.Exists(Path.Combine(appDir, "TerminalAppRemover.exe")))
            return appDir;

        // Extract into a sibling temp dir, then move into place, so a crashed or
        // interrupted first run never leaves a half-extracted app folder behind.
        var tempDir = Path.Combine(baseDir, "extracting-" + Environment.ProcessId);
        try
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            Directory.CreateDirectory(tempDir);

            using (var stream = openPayload())
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                var root = Path.GetFullPath(tempDir + Path.DirectorySeparatorChar);
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
                    var dest = Path.GetFullPath(Path.Combine(tempDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Unsafe entry in payload: {entry.FullName}");
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            File.WriteAllText(Path.Combine(tempDir, MarkerName), hash);

            if (Directory.Exists(appDir)) Directory.Delete(appDir, recursive: true);
            Directory.Move(tempDir, appDir);
            return appDir;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Remove app-&lt;hash&gt; folders belonging to other (older) payloads.</summary>
    public static void CleanupStaleVersions(string baseDir, string currentAppDir)
    {
        try
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(baseDir, "app-*"))
            {
                if (string.Equals(dir, currentAppDir, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* in use or locked: leave it */ }
            }
        }
        catch { /* best effort */ }
    }

    private const string MarkerName = ".payload-ok";
}
