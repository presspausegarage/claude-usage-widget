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
    const int Margin = 16;
    static readonly int[] PollChoices = { 30, 60, 120, 300 };

    static readonly Color GripIdle = Color.FromArgb(58, 55, 51);   // matches widget.html's --line
    static readonly Color GripHover = Color.FromArgb(80, 76, 70);

    readonly Settings settings = Settings.Load();
    readonly WebView2 webView = new() { Dock = DockStyle.Fill };
    readonly Panel grip = new() { Dock = DockStyle.Top, Height = 10, BackColor = GripIdle, Cursor = Cursors.SizeAll };
    ToolStripMenuItem alwaysOnTopItem = null!;
    readonly List<ToolStripMenuItem> pollItems = new();
    ContextMenuStrip menu = null!;

    public OverlayForm()
    {
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
    }

    static string FormatInterval(int seconds) => seconds < 60 ? $"{seconds}s" : $"{seconds / 60} min";

    Point DefaultLocation()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        return new Point(area.Right - Width - Margin, area.Bottom - Height - Margin);
    }

    Point RestoredOrDefaultLocation()
    {
        if (settings.X == int.MinValue || settings.Y == int.MinValue) return DefaultLocation();
        var candidate = new Point(settings.X, settings.Y);
        var bounds = new Rectangle(candidate, Size);
        // Fall back to the default corner if the saved position is off any
        // currently-connected monitor (e.g. a monitor got unplugged).
        return Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(bounds)) ? candidate : DefaultLocation();
    }

    void ToggleAlwaysOnTop(object? sender, EventArgs e)
    {
        settings.TopMost = !settings.TopMost;
        TopMost = settings.TopMost;
        alwaysOnTopItem.Checked = settings.TopMost;
        settings.Save();
    }

    void SetPollSeconds(int seconds)
    {
        settings.PollSeconds = seconds;
        foreach (var item in pollItems) item.Checked = item.Text == FormatInterval(seconds);
        settings.Save();
        Navigate();
    }

    void ResetPosition()
    {
        Location = DefaultLocation();
        settings.X = Location.X;
        settings.Y = Location.Y;
        settings.Save();
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
        if (!await EnsureServerRunning()) return; // pwsh missing — friendly message already shown, just don't navigate
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

    void Navigate() => webView.CoreWebView2?.Navigate($"{WidgetUrl}?interval={settings.PollSeconds}");

    static async Task<bool> EnsureServerRunning()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var resp = await http.GetAsync(WidgetUrl + "data");
            if (resp.Headers.Contains("X-Claude-Usage-Widget")) return true; // already running
        }
        catch { /* not running yet — start it below */ }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoProfile -File \"{ScriptPath}\" -NoBrowser",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Win32Exception)
        {
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
            settings.X = Location.X;
            settings.Y = Location.Y;
            settings.Save();
        }
    }
}
