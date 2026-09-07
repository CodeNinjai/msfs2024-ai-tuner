using System.IO;
using System.Windows;
using AiTuner.Core.Apply;

namespace AiTuner.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Kein stiller Tod mehr: Unbehandelte Fehler loggen und anzeigen statt Prozess-Exit.
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            Controls.AppDialog.Warn("Unerwarteter Fehler",
                $"{args.Exception.GetType().Name}: {args.Exception.Message}\n\nDie App läuft weiter; Details stehen im Log ({CrashLogPath}).");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);

        // Elevated relaunch for HKLM writes (HAGS/MPO): perform the command and exit.
        if (ElevatedCommands.TryHandle(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }

        new MainWindow().Show();
    }

    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Msfs2024AiTuner", "crash.log");

    private static void LogCrash(string source, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}\n\n");
        }
        catch { }
    }
}
