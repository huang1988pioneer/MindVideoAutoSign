using System.Diagnostics;
using Avalonia;

namespace MindVideoAutoSign;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Avalonia telemetry can crash the process if Roaming folders are incomplete.
        Environment.SetEnvironmentVariable("AVALONIA_TELEMETRY_OPTOUT", "1");
        try
        {
            var telemetryDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                ".avalonia-build-tasks");
            Directory.CreateDirectory(telemetryDir);
        }
        catch
        {
            // ignore
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject?.ToString());

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception?.ToString());
            e.SetObserved();
        };

        try
        {
            WriteCrashLog("Main", "starting lifetime");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            WriteCrashLog("Main", "lifetime returned");
        }
        catch (Exception ex)
        {
            WriteCrashLog("Main", ex.ToString());
            try
            {
                // Last-resort visible error for double-click launches (no console).
                Process.Start(new ProcessStartInfo
                {
                    FileName = "notepad.exe",
                    Arguments = $"\"{CrashLogPath()}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignore
            }
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();

    private static string CrashLogPath()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MindVideo Auto Sign",
            "logs");
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "startup-crash.log");
    }

    private static void WriteCrashLog(string source, string? details)
    {
        try
        {
            var path = CrashLogPath();
            var text =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
                $"{details}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, text);

            // Also write next to the executable when possible.
            var local = Path.Combine(AppContext.BaseDirectory, "startup-crash.log");
            File.AppendAllText(local, text);
        }
        catch
        {
            // ignore
        }
    }
}
