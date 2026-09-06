using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace MindVideoAutoSign;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = new MainWindow();
                WriteStartupNote($"MainWindow created: {desktop.MainWindow.Title}");
            }
            catch (Exception ex)
            {
                WriteStartupFailure(ex);
                desktop.MainWindow = CreateErrorWindow(ex);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window CreateErrorWindow(Exception ex)
    {
        var details = ex.ToString();
        var box = new TextBox
        {
            Text = details,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Height = 360
        };
        var openLog = new Button { Content = "開啟錯誤日誌", Margin = new Avalonia.Thickness(0, 8, 0, 0) };
        openLog.Click += (_, _) =>
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MindVideo Auto Sign",
                    "logs",
                    "startup-crash.log");
                if (File.Exists(path))
                    Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        };

        return new Window
        {
            Title = "MindVideo Flow — 啟動失敗",
            Width = 720,
            Height = 520,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(16),
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "程式啟動時發生錯誤（已寫入日誌，視窗不會再無聲閃退）：",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    box,
                    openLog
                }
            }
        };
    }

    private static void WriteStartupNote(string message)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MindVideo Auto Sign",
                "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "startup-crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteStartupFailure(Exception ex)
    {
        WriteStartupNote($"MainWindow ctor{Environment.NewLine}{ex}{Environment.NewLine}");
    }
}
