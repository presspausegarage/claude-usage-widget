using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.WinForms;

namespace AlwaysOnTopWidget;

// Closes the shared ContextMenuStrip on any mouse-down outside its bounds.
// WebView2 hosts its content in its own process/surface, so a click on it
// doesn't reliably send the deactivation signal ContextMenuStrip normally
// relies on to auto-close - a plain right-click-invoked menu happened to work
// because focus visibly left and returned to the WebView2, but the
// hamburger-button case (menu opened via postMessage from *inside* the
// WebView2, which never lost focus) doesn't get that signal at all. Watching
// every mouse-down at the message-pump level sidesteps the focus quirk
// entirely.
class MenuCloseFilter(ContextMenuStrip menu) : IMessageFilter
{
    const int WM_LBUTTONDOWN = 0x0201;
    const int WM_RBUTTONDOWN = 0x0204;

    public bool PreFilterMessage(ref Message m)
    {
        if (menu.Visible && (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN) && !menu.Bounds.Contains(Control.MousePosition))
        {
            menu.Close();
        }
        return false; // never swallow the click - just observe it
    }
}

public class OverlayForm : Form
{
    const string WidgetUrl = "http://localhost:8484/";
    static readonly string ScriptPath = Path.Combine(AppContext.BaseDirectory, "usage-widget.ps1");
    const int CornerMargin = 16; // avoid shadowing Control.Margin
    static readonly int[] PollChoices = { 30, 60, 120, 300 };

    static readonly Color GripIdle = Color.FromArgb(58, 55, 51);   // matches widget.html's --line
    static readonly Color GripHover = Color.FromArgb(80, 76, 70);

    readonly Settings settings = Settings.Load();
    readonly WebView2 webView = new() { Dock = DockStyle.Fill };
    readonly Panel grip = new() { Dock = DockStyle.Top, Height = 10, BackColor = GripIdle, Cursor = Cursors.SizeAll };
    ToolStripMenuItem alwaysOnTopItem = null!;
    readonly List<ToolStripMenuItem> pollItems = new();
    ContextMenuStrip menu = null!;

    // The pwsh server child this instance spawned (if any) - tracked so it
    // can be torn down on FormClosing instead of orphaning past app close.
    Process? serverProcess;
    JobObject? serverJob;

    public OverlayForm()
    {
        Logger.Info($"OverlayForm: initializing (TopMost={settings.TopMost}, PollSeconds={settings.PollSeconds}, saved position=({settings.X},{settings.Y}))");
        FormBorderStyle = FormBorderStyle.None;
        TopMost = settings.TopMost;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 180);
        BackColor = Color.FromArgb(31, 30, 27);

        Location = RestoredOrDefaultLocation();

        menu = new ContextMenuStrip();
        menu.Items.Add("Reload", null, (_, _) => Navigate());

        alwaysOnTopItem = new ToolStripMenuItem("Always on Top", null, ToggleAlwaysOnTop) { CheckOnClick = false, Checked = settings.TopMost };
        menu.Items.Add(alwaysOnTopItem);

        var pollMenu = new ToolStripMenuItem("Polling interval");
        foreach (var seconds in PollChoices)
        {
            var item = new ToolStripMenuItem(FormatInterval(seconds)) { Checked = settings.PollSeconds == seconds };
            item.Click += (_, _) => SetPollSeconds(seconds);
            pollItems.Add(item);
            pollMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(pollMenu);

        menu.Items.Add("Reset Position", null, (_, _) => ResetPosition());
        menu.Items.Add("Exit", null, (_, _) => Close());

        grip.ContextMenuStrip = menu;
        grip.MouseDown += Grip_MouseDown;
        grip.MouseEnter += (_, _) => grip.BackColor = GripHover;
        grip.MouseLeave += (_, _) => grip.BackColor = GripIdle;

        Application.AddMessageFilter(new MenuCloseFilter(menu));

        Controls.Add(webView);
        Controls.Add(grip);

        Load += OverlayForm_Load;
        FormClosing += OverlayForm_FormClosing;
    }

    static string FormatInterval(int seconds) => seconds < 60 ? $"{seconds}s" : $"{seconds / 60} min";

    Point DefaultLocation()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        return new Point(area.Right - Width - CornerMargin, area.Bottom - Height - CornerMargin);
    }

    Point RestoredOrDefaultLocation()
    {
        if (settings.X == int.MinValue || settings.Y == int.MinValue) return DefaultLocation();
        var candidate = new Point(settings.X, settings.Y);
        // Fall back to the default corner if the saved position is off any
        // currently-connected monitor (e.g. a monitor got unplugged or the
        // layout was reconfigured since the position was saved).
        if (IsOnScreen(candidate))
        {
            return candidate;
        }
        Logger.Warn($"RestoredOrDefaultLocation: saved position ({candidate.X},{candidate.Y}) is off every current monitor; falling back to default corner.");
        return DefaultLocation();
    }

    // Real bounds check against the monitor layout as it exists right now
    // (Screen.AllScreens), used both when restoring a saved position and -
    // critically - before persisting one. A previous release only checked
    // this on load, so a bad value (e.g. X:2153 on a since-reconfigured
    // layout that topped out at 2048) could still be written to
    // settings.json in the first place and strand the window off-screen on
    // the next launch. Checking here too closes that gap.
    bool IsOnScreen(Point location) =>
        Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new Rectangle(location, Size)));

    void ToggleAlwaysOnTop(object? sender, EventArgs e)
    {
        settings.TopMost = !settings.TopMost;
        TopMost = settings.TopMost;
        alwaysOnTopItem.Checked = settings.TopMost;
        settings.Save();
        Logger.Info($"ToggleAlwaysOnTop: now {settings.TopMost}");
    }

    void SetPollSeconds(int seconds)
    {
        settings.PollSeconds = seconds;
        foreach (var item in pollItems) item.Checked = item.Text == FormatInterval(seconds);
        settings.Save();
        Logger.Info($"SetPollSeconds: now {seconds}s");
        Navigate();
    }

    void ResetPosition()
    {
        Location = DefaultLocation();
        SavePosition("reset to default corner");
    }

    // Only ever persists a position that actually intersects a currently
    // connected monitor - see IsOnScreen. A rejected save just keeps
    // whatever was last known-good in settings.json rather than writing a
    // value that could strand the window off-screen on the next launch.
    void SavePosition(string context)
    {
        if (!IsOnScreen(Location))
        {
            Logger.Warn($"SavePosition: refusing to persist off-screen position ({Location.X},{Location.Y}) [{context}]; keeping last saved ({settings.X},{settings.Y}).");
            return;
        }
        settings.X = Location.X;
        settings.Y = Location.Y;
        settings.Save();
        Logger.Info($"SavePosition: saved ({settings.X},{settings.Y}) [{context}]");
    }

    // WebView2 renders through its own composited surface, which a manual
    // Form.Region clip (GraphicsPath + SetWindowRgn) doesn't reach — only
    // plain GDI-painted controls like the grip strip respect it, which is why
    // that rounded fine while the WebView2 area stayed square. DWM's own
    // per-window corner rounding (Windows 11 22000+) rounds the whole
    // composited frame, WebView2 included.
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWCP_ROUND = 2;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    async void OverlayForm_Load(object? sender, EventArgs e)
    {
        Logger.Info("OverlayForm_Load: starting");
        if (!await EnsureServerRunning())
        {
            Logger.Error("OverlayForm_Load: EnsureServerRunning failed; not navigating.");
            return; // pwsh missing — friendly message already shown, just don't navigate
        }
        await webView.EnsureCoreWebView2Async();
        // widget.html posts 'menu' from the hamburger button (which can't call
        // into this native ContextMenuStrip directly) and 'click' on every
        // click anywhere in the page (our own MenuCloseFilter can't see clicks
        // that land on WebView2's rendered content - only on real controls
        // like the grip strip - so the page has to report them itself).
        webView.CoreWebView2.WebMessageReceived += (_, args) =>
        {
            switch (args.TryGetWebMessageAsString())
            {
                case "menu": menu.Show(Cursor.Position); break;
                case "click": if (menu.Visible) menu.Close(); break;
            }
        };
        Navigate();
    }

    void Navigate()
    {
        Logger.Info($"Navigate: interval={settings.PollSeconds}");
        webView.CoreWebView2?.Navigate($"{WidgetUrl}?interval={settings.PollSeconds}");
    }

    async Task<bool> EnsureServerRunning()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var resp = await http.GetAsync(WidgetUrl + "data");
            if (resp.Headers.Contains("X-Claude-Usage-Widget"))
            {
                Logger.Info("EnsureServerRunning: server already responding on " + WidgetUrl + " (not started by this instance, so not tracked for shutdown).");
                return true; // already running
            }
        }
        catch { /* not running yet — start it below */ }

        try
        {
            serverProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -File \"{ScriptPath}\" -NoBrowser",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            });

            if (serverProcess != null)
            {
                Logger.Info($"EnsureServerRunning: spawned pwsh server, PID {serverProcess.Id}");
                // Belt-and-suspenders: assigning to a kill-on-close Job Object
                // means the child dies with us even on a crash/taskkill, not
                // just the clean-exit path handled in FormClosing below.
                serverJob = new JobObject();
                serverJob.AddProcess(serverProcess.Handle);
            }
            else
            {
                Logger.Warn("EnsureServerRunning: Process.Start returned null for pwsh.");
            }
        }
        catch (Win32Exception ex)
        {
            Logger.Error($"EnsureServerRunning: pwsh not found on PATH ({ex.Message})");
            MessageBox.Show(
                "Couldn't find PowerShell 7 (pwsh) on PATH, which this app needs to run its data server.\n\n" +
                "Install it from https://aka.ms/powershell, then relaunch.",
                "Claude Usage — PowerShell 7 required",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // Give the HttpListener a moment to bind before the WebView2 navigates.
        await Task.Delay(1500);
        return true;
    }

    // Closing the widget used to leave its spawned server running forever,
    // still listening on port 8484, because nothing ever tore it down. Kill
    // it explicitly here (covers the normal clean-exit case); the Job
    // Object set up in EnsureServerRunning covers the crash/kill case where
    // this handler never runs at all.
    void OverlayForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        Logger.Info("OverlayForm_FormClosing: shutting down");
        TryKillServerProcess();
    }

    void TryKillServerProcess()
    {
        if (serverProcess == null) return;
        try
        {
            if (!serverProcess.HasExited)
            {
                serverProcess.Kill(entireProcessTree: true);
                Logger.Info($"TryKillServerProcess: killed server PID {serverProcess.Id}");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"TryKillServerProcess: failed to kill server PID {serverProcess.Id}: {ex.Message}");
        }
        finally
        {
            serverProcess.Dispose();
            serverProcess = null;
            serverJob?.Dispose();
            serverJob = null;
        }
    }

    // Drag the borderless window by its top grip strip.
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 0x2;
    const int WM_EXITSIZEMOVE = 0x232;

    void Grip_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_EXITSIZEMOVE)
        {
            SavePosition("drag end");
        }
    }
}
