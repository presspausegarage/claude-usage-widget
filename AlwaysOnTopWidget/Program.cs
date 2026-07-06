namespace AlwaysOnTopWidget;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Global\\ClaudeUsageWidgetOverlay", out var createdNew);
        if (!createdNew) return; // already running — exit silently rather than stacking a second overlay

        ApplicationConfiguration.Initialize();
        Application.Run(new OverlayForm());
    }
}
