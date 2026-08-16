using System.IO;
using System.Windows;

namespace GltfBakeTool;

public partial class App : Application
{
    public static string LogPath { get; } = Path.Combine(Path.GetTempPath(), "GltfBakeTool.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("Dispatcher", args.Exception);
            MessageBox.Show(args.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => LogException("Task", args.Exception);
    }

    public static void LogException(string source, Exception? ex)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { }
    }
}
