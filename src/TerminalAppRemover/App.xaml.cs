using Microsoft.UI.Xaml;

namespace TerminalAppRemover;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        // Previously an unexpected exception closed the app without a trace.
        // Log it (best effort) and keep the app alive for recoverable XAML errors.
        UnhandledException += OnUnhandledException;
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        try
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerminalAppRemover");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "error.log"),
                $"[{DateTimeOffset.Now:O}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never throw from inside an exception handler.
        }

        // Mark recoverable UI exceptions as handled so one bad dialog/parse
        // cannot take the whole app down silently.
        e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
