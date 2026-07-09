namespace AlwaysOnTopWidget;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("Unhandled AppDomain exception", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        Application.ThreadException += (_, e) =>
            Logger.Error("Unhandled UI thread exception", e.Exception);

        Logger.Info("=== Claude Usage Widget starting ===");

        using var mutex = new Mutex(initiallyOwned: true, "Global\\ClaudeUsageWidgetOverlay", out var createdNew);
        if (!createdNew)
        {
            Logger.Info("Another instance is already running; exiting.");
            return; // already running — exit silently rather than stacking a second overlay
        }

        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new OverlayForm());
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal exception from Application.Run", ex);
            throw;
        }

        Logger.Info("=== Claude Usage Widget exiting ===");
    }
}
