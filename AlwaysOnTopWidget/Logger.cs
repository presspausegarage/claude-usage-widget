namespace AlwaysOnTopWidget;

// File-based logging for the WinForms shell. There was previously zero
// logging anywhere in this app, which meant every bug (missing companion
// files, off-screen windows, orphaned server processes) had to be chased
// live with Andy in the loop instead of just reading a log after the fact.
//
// Writes to %LOCALAPPDATA%\ClaudeUsageWidget\logs\overlay.log - deliberately
// NOT next to the exe, since that directory can be read-only (e.g. under
// Program Files) and is wherever the release zip happened to be extracted.
// The PowerShell server (usage-widget.ps1) logs to the same directory
// (server.log) so both halves of the app land in one place.
static class Logger
{
    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeUsageWidget", "logs");
    static readonly string LogPath = Path.Combine(LogDir, "overlay.log");
    const long MaxBytes = 2 * 1024 * 1024; // 2 MB, then rotate to a single .old backup
    static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {ex}");

    static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDir);
                RotateIfNeeded();
                File.AppendAllText(LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never be the reason the app crashes.
        }
    }

    static void RotateIfNeeded()
    {
        var info = new FileInfo(LogPath);
        if (!info.Exists || info.Length < MaxBytes) return;
        try
        {
            var rotated = Path.Combine(LogDir, "overlay.log.old");
            File.Delete(rotated);
            File.Move(LogPath, rotated);
        }
        catch
        {
            // Best-effort; worst case the log just keeps growing past the cap.
        }
    }
}
