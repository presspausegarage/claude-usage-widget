# Claude Usage Widget

A small always-on-top card that shows your Claude session usage (5-hour window),
without opening claude.ai. Sits in the bottom-right corner of your screen, dragged
into place once and left there.

> **Unofficial, unaffiliated with Anthropic.** This reads usage data from an
> **undocumented** endpoint (the same one Claude Code's own `/usage` command uses
> internally) — it could change or break without notice. If it stops working,
> that's why.

## What it does

- Reads your Claude Code OAuth token from `~/.claude/.credentials.json`
  **locally, at request time** — the token is only ever sent to
  `api.anthropic.com`, same as Claude Code itself, and never leaves your machine
  otherwise.
- Polls the usage endpoint (cached, rate-limit-aware — see [Caveats](#caveats))
  and shows session % used, a countdown to reset, and when it was last
  refreshed.
- Runs as a borderless, always-on-top WinForms window hosting a small
  HTML/JS card via WebView2. Drag it by the top strip, or use the in-card
  hamburger menu for Reload / Always-on-Top toggle / polling interval /
  Reset Position / Exit.

## Requirements

- Windows 10 or 11
- [PowerShell 7+](https://aka.ms/powershell) (`pwsh` on PATH) — the data server
  runs as a PowerShell script
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) —
  present by default on Windows 11; Windows 10 may need the Evergreen bootstrapper
- A working [Claude Code](https://claude.com/claude-code) CLI login, so
  `~/.claude/.credentials.json` exists

## Install

**Download** the latest release from the [Releases page](../../releases) — a
self-contained `.exe`, no separate .NET install needed.

**Or build from source**: open `AlwaysOnTopWidget/AlwaysOnTopWidget.csproj` in
Visual Studio 2022+ with the **.NET desktop development** workload, then Build
and Run.

On first run, Windows SmartScreen will likely warn that this is an unrecognized
app — that's expected for an unsigned personal-scale tool, not a sign anything's
wrong. Click "More info" → "Run anyway".

## Usage

- Drag the card by the thin strip at the very top (WebView2 captures mouse
  input over the rest of the card, so that strip is the deliberate handle).
- Click the hamburger icon (☰) in the card, or right-click the top strip, for:
  - **Reload**
  - **Always on Top** (toggle)
  - **Polling interval** (30s / 60s / 2 min / 5 min)
  - **Reset Position** (back to the default bottom-right corner)
  - **Exit**
- Settings (position, always-on-top, interval) persist across restarts in a
  `settings.json` next to the exe.

## Caveats

- The usage endpoint is undocumented and rate-limited server-side. Polling
  intervals below 15 seconds are clamped up to 15s regardless of what you pick,
  to avoid tripping that.
- **401** in the card = your OAuth token expired. Run anything in Claude Code
  (it refreshes the token file) and the widget recovers on its own within a
  minute.
- **429** = Anthropic rate-limited the poll; the widget keeps showing the last
  known data and retries on the next interval.

## License

MIT — see [LICENSE](LICENSE).
