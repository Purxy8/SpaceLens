namespace DesktopOrganizer;

internal static class CrashLog
{
    private static int handling;
    internal static void Initialize()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, eventArgs) => Handle(eventArgs.Exception, true);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => { if (eventArgs.ExceptionObject is Exception exception) Handle(exception, false); };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { Write(eventArgs.Exception); eventArgs.SetObserved(); };
    }
    private static void Handle(Exception exception, bool showMessage)
    {
        if (Interlocked.Exchange(ref handling, 1) != 0) return; Write(exception);
        if (showMessage) try { MessageBox.Show($"SpaceLens encountered an unexpected error and must close.\n\nA diagnostic log was saved at:\n{LogPath}", "SpaceLens error", MessageBoxButtons.OK, MessageBoxIcon.Error); } catch { }
        Environment.ExitCode = 1; Application.Exit();
    }
    private static void Write(Exception exception)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!); File.AppendAllText(LogPath, $"[{DateTimeOffset.UtcNow:O}] SpaceLens {UpdateService.CurrentVersionText}\r\n{exception}\r\n\r\n"); } catch { }
    }
    private static string LogPath => Environment.GetEnvironmentVariable("SPACELENS_ERROR_LOG") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "crash.log");
}
