#Requires -Version 7
<#
Claude usage widget - local dashboard for the numbers on claude.ai > Settings > Usage.

Reads Claude Code's OAuth token from ~/.claude/.credentials.json (locally, at
request time - the token never leaves this machine), polls the usage endpoint
with a 60s cache, and serves a small dashboard bound to localhost only.

  pwsh -File usage-widget.ps1              start server + open widget window
  pwsh -File usage-widget.ps1 -Once        print a one-shot snapshot to console
  pwsh -File usage-widget.ps1 -Mock        serve fake data (UI check, no token)
  pwsh -File usage-widget.ps1 -NoBrowser   server only, don't open a window
#>
param(
    [int]$Port = 8484,
    [switch]$Once,
    [switch]$Mock,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$CredPath     = Join-Path $env:USERPROFILE '.claude\.credentials.json'
$ApiUrl       = 'https://api.anthropic.com/api/oauth/usage'
$CacheSeconds = 60

# Without a claude-code/<ver> User-Agent this endpoint lands in an aggressively
# rate-limited bucket and 429s persistently. Best effort: use the real installed
# version, fall back to a plausible one.
$script:UserAgent = 'claude-code/2.0.0'
try {
    $v = & claude --version 2>$null | Select-Object -First 1
    if ($v -match '(\d+\.\d+\.\d+)') { $script:UserAgent = "claude-code/$($Matches[1])" }
} catch { }

function Get-AccessToken {
    if (-not (Test-Path $CredPath)) {
        throw "Credentials file not found at $CredPath - log in to Claude Code once, then retry."
    }
    $tok = (Get-Content $CredPath -Raw | ConvertFrom-Json).claudeAiOauth.accessToken
    if (-not $tok) { throw "No accessToken found in $CredPath." }
    $tok
}

function Get-Usage {
    if ($Mock) {
        $now = [datetimeoffset]::UtcNow
        return [pscustomobject]@{
            five_hour      = [pscustomobject]@{ utilization = 42.0; resets_at = $now.AddHours(2.4).ToString('o') }
            seven_day      = [pscustomobject]@{ utilization = 63.0; resets_at = $now.AddDays(2.2).ToString('o') }
            seven_day_opus = [pscustomobject]@{ utilization = 88.0; resets_at = $now.AddDays(2.2).ToString('o') }
            extra_usage    = [pscustomobject]@{ is_enabled = $false; monthly_limit = $null; used_credits = $null; utilization = $null }
        }
    }
    $headers = @{
        Authorization    = "Bearer $(Get-AccessToken)"
        'anthropic-beta' = 'oauth-2025-04-20'
    }
    Invoke-RestMethod -Uri $ApiUrl -Headers $headers -UserAgent $script:UserAgent -TimeoutSec 15
}

function Get-FriendlyError($err) {
    $status = $null
    if ($err.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
        $status = [int]$err.Exception.Response.StatusCode
    }
    switch ($status) {
        401 { 'Token expired or invalid (401). Run anything in Claude Code - it refreshes the token file - and this recovers on its own.' }
        403 { 'Forbidden (403) - this token cannot read the usage endpoint.' }
        429 { 'Rate-limited by Anthropic (429). Showing last known data; retries every minute.' }
        default { $err.Exception.Message }
    }
}

# ---- window formatting (shared by -Once console output) --------------------

$WindowLabels = [ordered]@{
    five_hour           = 'Session (5h)'
    seven_day           = 'Week - all models'
    seven_day_opus      = 'Week - Opus'
    seven_day_sonnet    = 'Week - Sonnet'
    seven_day_oauth_apps = 'Week - connected apps'
}

function Format-Reset([string]$iso) {
    if (-not $iso) { return '' }
    $t = [datetimeoffset]::Parse($iso, [cultureinfo]::InvariantCulture).ToLocalTime()
    $delta = $t - [datetimeoffset]::Now
    $rel = if ($delta.TotalMinutes -le 0) { 'now' }
           elseif ($delta.TotalHours -ge 24) { 'in {0}d {1}h' -f [int][math]::Floor($delta.TotalDays), $delta.Hours }
           elseif ($delta.TotalHours -ge 1)  { 'in {0}h {1}m' -f [int][math]::Floor($delta.TotalHours), $delta.Minutes }
           else { 'in {0}m' -f [int][math]::Ceiling($delta.TotalMinutes) }
    $when = if ($delta.TotalHours -lt 24) { $t.ToString('h:mm tt') } else { $t.ToString('ddd h:mm tt') }
    "resets $when ($rel)"
}

function Write-Snapshot($data) {
    Write-Host ("Claude usage - {0}" -f (Get-Date).ToString('ddd MMM d, h:mm tt'))
    foreach ($p in $data.PSObject.Properties) {
        $v = $p.Value
        if ($null -eq $v -or $v -isnot [pscustomobject]) { continue }
        if ($p.Name -eq 'extra_usage') {
            if ($v.is_enabled) {
                $limitText = if ($v.monthly_limit) { "of $($v.monthly_limit) credits" } else { "credits" }
                Write-Host ("  {0,-24} {1} {2}" -f 'Extra usage', $v.used_credits, $limitText)
            }
            continue
        }
        if ($null -eq $v.PSObject.Properties['utilization']) { continue }
        $label = if ($WindowLabels.Contains($p.Name)) { $WindowLabels[$p.Name] } else { $p.Name }
        Write-Host ("  {0,-24} {1,4:N0}%   {2}" -f $label, $v.utilization, (Format-Reset $v.resets_at))
    }
}

if ($Once) {
    Write-Snapshot (Get-Usage)
    return
}

# ---- server -----------------------------------------------------------------

$script:cache          = $null                # last good API payload
$script:cacheOkAt      = [datetime]::MinValue # when it was fetched
$script:triedAt        = [datetime]::MinValue # last attempt (success or not)
$script:lastError      = $null
$script:lastHealAttempt = [datetime]::MinValue
$HealCooldownSeconds   = 120   # don't hammer `claude -p` if something's persistently broken

function Invoke-TokenHeal {
    <#
    On a 401, `claude --version` and `claude auth status` both leave an
    expired token untouched - only a real authenticated call refreshes it
    (confirmed by testing). Returns a specific error message if the account
    isn't logged in at all (nothing to heal), or $null if a refresh attempt
    was made - or skipped via cooldown - and the caller should just retry.
    #>
    $now = [datetime]::UtcNow
    if (($now - $script:lastHealAttempt).TotalSeconds -lt $HealCooldownSeconds) { return $null }
    $script:lastHealAttempt = $now

    $auth = $null
    try { $auth = (& claude auth status --json 2>$null) | ConvertFrom-Json } catch { }
    if (-not $auth -or -not $auth.loggedIn) {
        return 'Not logged in to Claude Code. Run "claude login" in a terminal, then this recovers on its own.'
    }

    # Trivial prompt, just to force the refresh side effect - spends a sliver
    # of the same session-usage quota this widget displays, not a separate
    # metered API cost (for subscription auth; API-key users would see a
    # tiny real charge here).
    & claude -p "hi" *> $null
    return $null
}

function Update-UsageCache {
    try {
        $script:cache     = Get-Usage
        $script:cacheOkAt = [datetime]::UtcNow
        $script:lastError = $null
        return
    } catch {
        $firstErr = $_
        $status = $null
        if ($firstErr.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException]) {
            $status = [int]$firstErr.Exception.Response.StatusCode
        }

        if ($status -eq 401) {
            $override = Invoke-TokenHeal
            if ($override) {
                $script:lastError = $override
                return
            }
            try {
                $script:cache     = Get-Usage
                $script:cacheOkAt = [datetime]::UtcNow
                $script:lastError = $null
            } catch {
                $script:lastError = Get-FriendlyError $_
            }
            return
        }

        $script:lastError = Get-FriendlyError $firstErr
    }
}

function Get-DataJson([int]$MaxAge = $CacheSeconds) {
    # Client-adjustable polling rate (widget.html's interval setting), but never
    # below 15s - the upstream endpoint is rate-limited server-side (see
    # Get-FriendlyError's 429 handling) and a too-eager client shouldn't be able
    # to trip that.
    $effectiveMaxAge = [math]::Max(15, $MaxAge)
    $now = [datetime]::UtcNow
    if (($now - $script:triedAt).TotalSeconds -ge $effectiveMaxAge) {
        $script:triedAt = $now
        Update-UsageCache
    }
    [ordered]@{
        ok         = ($null -ne $script:cache)
        fetched_at = if ($script:cacheOkAt -ne [datetime]::MinValue) { $script:cacheOkAt.ToString('o') } else { $null }
        error      = $script:lastError
        data       = $script:cache
    } | ConvertTo-Json -Depth 8
}

function Open-Widget {
    $url = "http://localhost:$Port/"
    $chrome = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe"
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
        "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($chrome) {
        Start-Process $chrome -ArgumentList "--app=$url", '--window-size=390,600'
    } else {
        Start-Process $url   # default browser, plain tab
    }
}

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://localhost:$Port/")
try {
    $listener.Start()
} catch {
    # Port busy - if it's a previous instance of this widget, just open the window.
    $probe = $null
    try { $probe = Invoke-WebRequest "http://localhost:$Port/data" -TimeoutSec 3 } catch { }
    if ($probe -and $probe.Headers['X-Claude-Usage-Widget']) {
        Write-Host "Widget server already running on port $Port."
        if (-not $NoBrowser) { Open-Widget }
        return
    }
    throw "Port $Port is in use by something else - rerun with -Port <n>."
}

$html = Get-Content (Join-Path $PSScriptRoot 'widget.html') -Raw

Write-Host "Claude usage widget -> http://localhost:$Port/   (Ctrl+C to stop)"
if ($Mock) { Write-Host 'MOCK MODE - serving fake data, credentials untouched.' }
if (-not $NoBrowser) { Open-Widget }

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $res = $ctx.Response
        try {
            $res.Headers['X-Claude-Usage-Widget'] = '1'
            $res.Headers['Cache-Control'] = 'no-store'
            $origin = $ctx.Request.Headers['Origin']
            if ($origin -and $origin -ne "http://localhost:$Port") {
                $res.StatusCode = 403
                $body = ''
            } else {
                switch ($ctx.Request.Url.AbsolutePath) {
                    '/' {
                        $body = $html
                        $res.ContentType = 'text/html; charset=utf-8'
                    }
                    '/data' {
                        $maxAge = $CacheSeconds
                        $requested = $ctx.Request.QueryString['maxAge']
                        $parsed = 0
                        if ($requested -and [int]::TryParse($requested, [ref]$parsed)) { $maxAge = $parsed }
                        $body = Get-DataJson -MaxAge $maxAge
                        $res.ContentType = 'application/json; charset=utf-8'
                    }
                    default {
                        $res.StatusCode = 404
                        $body = ''
                    }
                }
            }
            $buf = [System.Text.Encoding]::UTF8.GetBytes($body)
            $res.ContentLength64 = $buf.Length
            $res.OutputStream.Write($buf, 0, $buf.Length)
        } catch {
            try { $res.StatusCode = 500 } catch { }
        } finally {
            try { $res.Close() } catch { }
        }
    }
} finally {
    $listener.Stop()
    $listener.Close()
}
