using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalAppRemover;

public sealed partial class MainWindow : Window
{
    /// <summary>Uninstalls get far longer than scans (real installers run for minutes),
    /// but they are no longer unbounded: timeout + a working Cancel button.</summary>
    private static readonly TimeSpan UninstallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>0 = success, 3010 = success/reboot required, 1641 = success/reboot initiated.</summary>
    private static readonly HashSet<int> SuccessExitCodes = new() { 0, 3010, 1641 };

    private readonly PackageManagerService _service = new();
    private readonly InstalledSizeService _sizeService = new();
    private readonly ObservableCollection<PackageEntry> _allPackages = new();
    private bool _isBusy;
    private bool _isDialogOpen;
    private bool _initialSized;
    private string _sortField = "Name";
    private bool _sortAscending = true;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _uninstallCts;

    public MainWindow()
    {
        InitializeComponent();

        // FEATURES.md always claimed Mica; now it is actually applied.
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        PackageList.ItemsSource = _allPackages;
        RootGrid.Loaded += RootGrid_Loaded;
        Closed += (_, _) =>
        {
            _scanCts?.Cancel();
            _uninstallCts?.Cancel();
        };

        _ = ScanAsync();
    }

    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialSized) return;
        _initialSized = true;

        // AppWindow.Resize takes PHYSICAL pixels; the old fixed (1000, 680) made
        // the window half the intended size at 200% DPI. Scale by the XAML root's
        // rasterization scale, and enforce a minimum track size in physical px.
        var scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Round(1000 * scale),
            (int)Math.Round(680 * scale)));

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            MinSizeHook.Attach(hwnd, scale, minWidthDip: 720, minHeightDip: 480);
        }
        catch
        {
            // Minimum-size enforcement is cosmetic; never block startup on it.
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isBusy) return;

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        SetBusy(true, "Scanning package managers...");
        WarningsText.Visibility = Visibility.Collapsed;
        WarningsText.Text = string.Empty;
        try
        {
            var outcome = await _service.ScanAsync(ct);
            var sizeIndex = await Task.Run(() => _sizeService.BuildSizeIndex(outcome.Packages), ct);

            _allPackages.Clear();
            foreach (var package in outcome.Packages)
            {
                // SizeBytes is settable now: no field-by-field copy of the entry
                // (the old copy silently dropped any newly added model field).
                package.SizeBytes = sizeIndex.TryGetValue(InstalledSizeService.PackageKey(package), out var bytes)
                    ? bytes
                    : null;
                _allPackages.Add(package);
            }

            ApplyFilter();
            UpdateStatus();

            if (outcome.Warnings.Count > 0)
            {
                WarningsText.Text = string.Join("\n", outcome.Warnings.Select(w => $"⚠ {w}"));
                WarningsText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        UpdateStatus();
    }

    private void SortButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string field }) return;

        if (string.Equals(_sortField, field, StringComparison.Ordinal))
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortField = field;
            // Largest-first is the useful first click for Size; A→Z for the rest.
            _sortAscending = !field.Equals("Size", StringComparison.Ordinal);
        }

        ApplyFilter();
        UpdateStatus();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var isDefaultView = string.IsNullOrWhiteSpace(query)
                             && _sortField.Equals("Name", StringComparison.Ordinal)
                             && _sortAscending;

        if (isDefaultView)
        {
            // Binding the live collection keeps removals in place without
            // resetting the ListView's scroll position.
            if (!ReferenceEquals(PackageList.ItemsSource, _allPackages))
                PackageList.ItemsSource = _allPackages;
            return;
        }

        IEnumerable<PackageEntry> items = _allPackages;
        if (!string.IsNullOrWhiteSpace(query))
        {
            items = items.Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Manager.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (x.Id?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.Version?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        items = (_sortField, _sortAscending) switch
        {
            ("Version", true) => items.OrderBy(x => x.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            ("Version", false) => items.OrderByDescending(x => x.Version ?? string.Empty, StringComparer.OrdinalIgnoreCase),
            ("Size", true) => items.OrderBy(x => x.SizeBytes ?? -1),
            ("Size", false) => items.OrderByDescending(x => x.SizeBytes ?? -1),
            ("Source", true) => items.OrderBy(x => x.Manager, StringComparer.OrdinalIgnoreCase),
            ("Source", false) => items.OrderByDescending(x => x.Manager, StringComparer.OrdinalIgnoreCase),
            (_, true) => items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            (_, false) => items.OrderByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
        };

        PackageList.ItemsSource = items.ToList();
    }

    private void UpdateStatus()
    {
        var visible = PackageList.ItemsSource is IEnumerable<PackageEntry> items ? items.ToList() : _allPackages.ToList();
        var knownTotal = visible.Where(x => x.SizeBytes.HasValue).Sum(x => x.SizeBytes!.Value);
        var knownCount = visible.Count(x => x.SizeBytes.HasValue);

        StatusText.Text = knownCount == 0
            ? $"{visible.Count} package(s) found \u2022 Total size: Unknown"
            : $"{visible.Count} package(s) found \u2022 Total size: {SizeFormatter.Format(knownTotal)}";
    }

    private void PackageList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        RefreshSelectionButton();

    private void RefreshSelectionButton()
    {
        var count = PackageList.SelectedItems.Count;
        UninstallSelectedButton.Content = count > 0 ? $"Uninstall selected ({count})" : "Uninstall selected";
        UninstallSelectedButton.IsEnabled = count > 0 && !_isBusy;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _uninstallCts?.Cancel();
    }

    // ------------------------------------------------------------------ uninstall

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isDialogOpen) return;
        if (sender is not Button { Tag: PackageEntry package }) return;
        if (!package.CanUninstall) return;

        if (!await ConfirmAsync(
                "Uninstall package?",
                BuildConfirmText(package),
                "Uninstall"))
            return;

        _uninstallCts?.Dispose();
        _uninstallCts = new CancellationTokenSource();

        SetBusy(true, $"Uninstalling {package.Name}...");
        try
        {
            var result = await RunUninstallAsync(package, _uninstallCts.Token);
            if (result.Success)
            {
                _allPackages.Remove(package);
                ApplyFilter();
                UpdateStatus();
                StatusText.Text = result.Message;
            }
            else
            {
                StatusText.Text = result.Message;
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isDialogOpen) return;

        var selected = PackageList.SelectedItems
            .OfType<PackageEntry>()
            .Where(p => p.CanUninstall)
            .ToList();
        if (selected.Count == 0) return;

        var names = string.Join("\n", selected.Take(12).Select(p => $"• {p.Name} ({p.Manager})"));
        if (selected.Count > 12) names += $"\n• … and {selected.Count - 12} more";
        var warnings = selected.Where(p => p.Warning is not null).Take(3).Select(p => p.Warning).ToList();

        var content = $"{selected.Count} package(s):\n\n{names}" +
                      (warnings.Count > 0 ? $"\n\n⚠ {string.Join("\n⚠ ", warnings)}" : "");

        if (!await ConfirmAsync("Uninstall selected packages?", content, "Uninstall all"))
            return;

        _uninstallCts?.Dispose();
        _uninstallCts = new CancellationTokenSource();
        var ct = _uninstallCts.Token;

        SetBusy(true, "Uninstalling selected packages...");
        var succeeded = new List<string>();
        var failed = new List<string>();
        try
        {
            for (var i = 0; i < selected.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var package = selected[i];
                StatusText.Text = $"Uninstalling {package.Name} ({i + 1}/{selected.Count})...";

                var result = await RunUninstallAsync(package, ct);
                if (result.Success)
                {
                    succeeded.Add(package.Name);
                    _allPackages.Remove(package);
                }
                else
                {
                    failed.Add($"{package.Name}: {result.Message}");
                }
            }
        }
        finally
        {
            SetBusy(false);
        }

        ApplyFilter();
        UpdateStatus();
        StatusText.Text = failed.Count == 0
            ? $"Uninstalled {succeeded.Count} package(s)."
            : $"Uninstalled {succeeded.Count}, failed {failed.Count}.";

        if (failed.Count > 0)
        {
            WarningsText.Text = string.Join("\n", failed.Select(f => $"⚠ {f}"));
            WarningsText.Visibility = Visibility.Visible;
        }
    }

    private static string BuildConfirmText(PackageEntry package)
    {
        var text = $"{package.Name}\n\nCommand:\n{package.UninstallCommand}";
        if (package.Warning is not null)
            text += $"\n\n⚠ {package.Warning}";
        return text;
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primaryText)
    {
        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private sealed record UninstallResult(bool Success, string Message);

    private async Task<UninstallResult> RunUninstallAsync(PackageEntry package, CancellationToken userCt)
    {
        // userCt is the current operation's token (owned by _uninstallCts, which
        // the Cancel button cancels); combine it with the hard timeout.
        using var timeoutCts = new CancellationTokenSource(UninstallTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(userCt, timeoutCts.Token);
        var ct = linkedCts.Token;

        var spec = package.Uninstall;

        // --- In-process file delete (Go): verified afterwards, unlike `cmd /c del`
        // which returns errorlevel 0 for many failures and used to fake success.
        if (spec.FileToDelete is not null)
        {
            var path = spec.FileToDelete;
            try
            {
                var deleted = await Task.Run(() =>
                {
                    if (!File.Exists(path)) return true; // already gone = goal reached
                    File.Delete(path);
                    return !File.Exists(path);
                }, ct);

                return deleted
                    ? new UninstallResult(true, $"Uninstalled: {package.Name}")
                    : new UninstallResult(false, $"Uninstall failed: {package.Name} — the file still exists after delete: {path}");
            }
            catch (OperationCanceledException)
            {
                return new UninstallResult(false, $"Uninstall cancelled: {package.Name}");
            }
            catch (Exception ex)
            {
                return new UninstallResult(false, $"Uninstall failed: {package.Name} — {ex.Message}");
            }
        }

        // --- External uninstaller
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
        };

        if (spec.Elevate)
        {
            // Chocolatey: elevation requires ShellExecute (UAC). Output cannot be
            // redirected on this path — a known Windows limitation, now explicit.
            // ShellExecute takes a single Arguments string, quoted per
            // CommandLineToArgvW rules; ArgumentList is not used here.
            startInfo.UseShellExecute = true;
            startInfo.Verb = "runas";
            startInfo.Arguments = string.Join(" ", spec.Arguments.Select(CommandLine.QuoteArg));
        }
        else
        {
            // Direct launch with a real argument list — no string is ever built
            // and re-parsed (the old SplitCommand approach), so package names
            // containing spaces or shell metacharacters cannot break out.
            foreach (var arg in spec.Arguments) startInfo.ArgumentList.Add(arg);
            startInfo.UseShellExecute = false;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.CreateNoWindow = true;
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }

        Process? process = null;
        try
        {
            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new UninstallResult(false, $"Uninstall failed: {package.Name} — process did not start.");

            Task<string>? stdoutTask = null;
            Task<string>? stderrTask = null;
            if (!spec.Elevate)
            {
                stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
                stderrTask = process.StandardError.ReadToEndAsync(ct);
            }

            await process.WaitForExitAsync(ct);
            var exitCode = process.ExitCode;

            if (SuccessExitCodes.Contains(exitCode))
            {
                return new UninstallResult(true, exitCode == 0
                    ? $"Uninstalled: {package.Name}"
                    : $"Uninstalled: {package.Name} (Reboot required)");
            }

            var detail = "";
            if (stderrTask is not null)
            {
                try
                {
                    var stderr = await stderrTask;
                    var lastLine = stderr.SplitLines().LastOrDefault();
                    if (!string.IsNullOrWhiteSpace(lastLine)) detail = $" — {lastLine}";
                }
                catch { /* output unavailable; the exit code still stands */ }
            }

            var codeText = exitCode < 0 ? $"{exitCode} (0x{exitCode:X8})" : exitCode.ToString();
            return new UninstallResult(false, $"Uninstall failed: {package.Name} — exit code {codeText}{detail}");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new UninstallResult(false, "Uninstall cancelled by the user (UAC).");
        }
        catch (OperationCanceledException)
        {
            if (process is not null)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* elevated or already gone */ }
            }
            var why = timeoutCts.IsCancellationRequested && !userCt.IsCancellationRequested && !(_uninstallCts?.IsCancellationRequested ?? false)
                ? $"timed out after {UninstallTimeout.TotalMinutes:0} minutes and was stopped"
                : "cancelled";
            return new UninstallResult(false, $"Uninstall {why}: {package.Name}");
        }
        catch (Exception ex)
        {
            return new UninstallResult(false, $"Uninstall failed: {package.Name} — {ex.Message}");
        }
        finally
        {
            process?.Dispose();
        }
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        if (status is not null) StatusText.Text = status;
        LoadingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = !busy;
        PackageList.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
        RefreshSelectionButton();
    }

    /// <summary>Enforces a minimum window track size via a Win32 window subclass
    /// (OverlappedPresenter has no stable minimum-size API in this SDK).</summary>
    private static class MinSizeHook
    {
        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

        private const uint WmGetMinMaxInfo = 0x0024;
        private static SubclassProc? _proc; // kept alive for the app's lifetime
        private static int _minWidthPx;
        private static int _minHeightPx;

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MinMaxInfo
        {
            public Point PtReserved;
            public Point PtMaxSize;
            public Point PtMaxPosition;
            public Point PtMinTrackSize;
            public Point PtMaxTrackSize;
        }

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        public static void Attach(IntPtr hWnd, double scale, int minWidthDip, int minHeightDip)
        {
            _minWidthPx = (int)Math.Round(minWidthDip * scale);
            _minHeightPx = (int)Math.Round(minHeightDip * scale);
            _proc = Proc;
            SetWindowSubclass(hWnd, _proc, (UIntPtr)1, UIntPtr.Zero);
        }

        private static IntPtr Proc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
        {
            if (uMsg == WmGetMinMaxInfo && lParam != IntPtr.Zero)
            {
                var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                mmi.PtMinTrackSize = new Point { X = _minWidthPx, Y = _minHeightPx };
                Marshal.StructureToPtr(mmi, lParam, fDeleteOld: false);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }
    }
}
