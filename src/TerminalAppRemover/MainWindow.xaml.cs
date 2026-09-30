using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;

namespace TerminalAppRemover;

public sealed partial class MainWindow : Window
{
    private readonly PackageManagerService _service = new();
    private readonly InstalledSizeService _sizeService = new();
    private readonly ObservableCollection<PackageEntry> _allPackages = new();
    private bool _isBusy;
    private bool _isDialogOpen;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 680));
        PackageList.ItemsSource = _allPackages;
        _ = ScanAsync();
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e) => await ScanAsync();

    private async Task ScanAsync()
    {
        if (_isBusy) return;

        SetBusy(true, "Scanning package managers...");
        try
        {
            var packages = await _service.ScanAsync();
            var sizeIndex = await Task.Run(() => _sizeService.BuildSizeIndex(packages));

            var entries = new List<PackageEntry>(packages.Count);
            foreach (var package in packages)
            {
                var size = sizeIndex.TryGetValue(InstalledSizeService.PackageKey(package), out var bytes)
                    ? bytes
                    : (long?)null;

                entries.Add(new PackageEntry
                {
                    Name = package.Name,
                    Id = package.Id,
                    Version = package.Version,
                    Manager = package.Manager,
                    UninstallCommand = package.UninstallCommand,
                    SizeBytes = size
                });
            }

            _allPackages.Clear();
            foreach (var entry in entries)
            {
                _allPackages.Add(entry);
            }

            ApplyFilter();
            UpdateStatus();
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

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        PackageList.ItemsSource = string.IsNullOrWhiteSpace(query)
            ? _allPackages
            : _allPackages.Where(x =>
                    x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    x.Manager.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (x.Id?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
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

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isDialogOpen) return;
        if (sender is not Button { Tag: PackageEntry package }) return;

        _isDialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Uninstall package?",
                Content = $"{package.Name}\n\nCommand:\n{package.UninstallCommand}",
                PrimaryButtonText = "Uninstall",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = RootGrid.XamlRoot
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        finally
        {
            _isDialogOpen = false;
        }

        SetBusy(true, $"Uninstalling {package.Name}...");
        try
        {
            var exitCode = await RunUninstallAsync(package);
            if (exitCode == 0 || exitCode == 3010)
            {
                StatusText.Text = exitCode == 3010
                    ? $"Uninstalled: {package.Name} (Reboot required)"
                    : $"Uninstalled: {package.Name}";
                _allPackages.Remove(package);
                ApplyFilter();
                UpdateStatus();
            }
            else
            {
                StatusText.Text = $"Uninstall returned exit code {exitCode}.";
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            StatusText.Text = "Uninstall cancelled by the user (UAC).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Uninstall failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static async Task<int> RunUninstallAsync(PackageEntry package)
    {
        var parts = SplitCommand(package.UninstallCommand);
        if (string.IsNullOrWhiteSpace(parts.file))
            throw new InvalidOperationException("Uninstall command is empty.");

        var fileName = parts.file;
        var arguments = parts.arguments;

        // Scoop, npm, pnpm, pip, Cargo, and Go delete commands must be launched via cmd.exe
        if (package.Manager.Equals("Scoop", StringComparison.OrdinalIgnoreCase) ||
            package.Manager.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
            package.Manager.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
            package.Manager.Equals("Go", StringComparison.OrdinalIgnoreCase) ||
            package.Manager.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
            package.Manager.Equals("Cargo", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("scoop", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("npm", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("pnpm", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("pip", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("cargo", StringComparison.OrdinalIgnoreCase) ||
            parts.file.Equals("python", StringComparison.OrdinalIgnoreCase))
        {
            fileName = "cmd.exe";
            arguments = $"/c {package.UninstallCommand}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            CreateNoWindow = false
        };

        // Chocolatey packages require elevation.
        // Scoop and .NET tools are strictly per-user and must not run with runas.
        // WinGet self-elevates when required.
        if (package.Manager.Equals("Chocolatey", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.Verb = "runas";
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            return -1;
        }

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static (string file, string arguments) SplitCommand(string command)
    {
        command = command.Trim();
        if (string.IsNullOrEmpty(command)) return (string.Empty, string.Empty);

        if (command.StartsWith('"'))
        {
            var nextQuote = command.IndexOf('"', 1);
            if (nextQuote > 0)
            {
                var file = command[1..nextQuote];
                var args = command[(nextQuote + 1)..].TrimStart();
                return (file, args);
            }
        }

        var firstSpace = command.IndexOf(' ');
        return firstSpace < 0
            ? (command, string.Empty)
            : (command[..firstSpace], command[(firstSpace + 1)..].TrimStart());
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _isBusy = busy;
        if (status is not null) StatusText.Text = status;
        LoadingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.IsEnabled = !busy;
        PackageList.IsEnabled = !busy;
        ScanButton.IsEnabled = !busy;
    }
}
