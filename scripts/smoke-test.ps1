#Requires -Version 7
<#
Clean-environment smoke test for the packaged release.

CI (build.yml) only ever confirms `dotnet build` succeeds. That already let a
release-breaking packaging bug through TWICE (v0.1.0/v0.1.1 shipped without
their required usage-widget.ps1/widget.html companions, because a release
asset list of just the .exe silently drops Content files) - `dotnet build`
has no way to catch "the zip a user downloads doesn't actually work."

This script extracts the built release zip into a fresh, empty temp folder -
no dev tooling, no leftover settings.json, nothing a real dev machine would
have lying around - and confirms:
  1. The exe and both its companion files actually exist in the zip.
  2. The exe launches and stays running (doesn't crash on startup).
  3. It spawns its data server and the server binds on port 8484.
  4. The server serves both `/` (widget.html) and `/data` (JSON) correctly.
  5. Killing the widget actually tears down the spawned server child
     (regression check for the orphaned-process fix - this uses a hard
     Stop-Process -Force, which bypasses FormClosing entirely, so a pass
     here specifically proves the kill-on-close Job Object is working).

  pwsh -File scripts/smoke-test.ps1                  build fresh, then test it
  pwsh -File scripts/smoke-test.ps1 -ZipPath x.zip    test an already-built zip
#>
param(
    [string]$ZipPath,
    [int]$Port = 8484,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$CsprojPath = Join-Path $RepoRoot 'AlwaysOnTopWidget\AlwaysOnTopWidget.csproj'

function Test-PortOpen([int]$TestPort) {
    try {
        $client = [System.Net.Sockets.TcpClient]::new()
        $iar = $client.BeginConnect('127.0.0.1', $TestPort, $null, $null)
        $ok = $iar.AsyncWaitHandle.WaitOne(300) -and $client.Connected
        $client.Close()
        return $ok
    } catch {
        return $false
    }
}

function New-ReleaseZip {
    $publishDir = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-usage-widget-publish-" + [guid]::NewGuid())
    Write-Host "Publishing a fresh Release build to $publishDir ..."
    # Same flags as .github/workflows/release.yml - this must exercise the
    # actual release artifact shape, not just a plain `dotnet build` output.
    # Piped through Out-Host (not just printed) so dotnet's own stdout can't
    # leak into this function's return value - PowerShell functions return
    # everything written to the output stream, and an unredirected native
    # command's output goes straight into it.
    dotnet publish $CsprojPath -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

    $zip = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-usage-widget-smoketest-" + [guid]::NewGuid() + ".zip")
    Compress-Archive -Path (Join-Path $publishDir 'AlwaysOnTopWidget.exe'), `
                            (Join-Path $publishDir 'usage-widget.ps1'), `
                            (Join-Path $publishDir 'widget.html') `
                      -DestinationPath $zip
    Remove-Item $publishDir -Recurse -Force
    return $zip
}

$ownsZip = $false
if (-not $ZipPath) {
    $ZipPath = New-ReleaseZip
    $ownsZip = $true
}
if (-not (Test-Path $ZipPath)) { throw "Zip not found: $ZipPath" }
Write-Host "Using release zip: $ZipPath"

if (Test-PortOpen $Port) {
    throw "Port $Port is already in use (a widget instance may already be running). " +
          "Close it first - otherwise the exe under test would just detect and reuse that " +
          "existing server instead of proving THIS build's server starts cleanly."
}

$extractDir = $null
$proc = $null
$exitCode = 1

try {
    $extractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("claude-usage-widget-smoketest-" + [guid]::NewGuid())
    New-Item -ItemType Directory -Path $extractDir | Out-Null
    Expand-Archive -Path $ZipPath -DestinationPath $extractDir -Force
    Write-Host "Extracted to empty folder: $extractDir"

    $exePath   = Join-Path $extractDir 'AlwaysOnTopWidget.exe'
    $scriptCompanion = Join-Path $extractDir 'usage-widget.ps1'
    $htmlCompanion   = Join-Path $extractDir 'widget.html'
    foreach ($required in @($exePath, $scriptCompanion, $htmlCompanion)) {
        if (-not (Test-Path $required)) {
            throw "FAIL: missing required file in extracted release: $required " +
                  "(this is exactly the class of bug this smoke test exists to catch)."
        }
    }
    Write-Host "PASS: exe + usage-widget.ps1 + widget.html all present in the zip."

    Write-Host "Launching $exePath ..."
    $proc = Start-Process -FilePath $exePath -PassThru
    Start-Sleep -Milliseconds 800
    if ($proc.HasExited) {
        throw "FAIL: process exited immediately (exit code $($proc.ExitCode)). " +
              "Check %LOCALAPPDATA%\ClaudeUsageWidget\logs\overlay.log."
    }

    Write-Host "Waiting up to ${TimeoutSeconds}s for the data server to come up on port $Port ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $dataResp = $null
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            throw "FAIL: process exited while waiting for the server (exit code $($proc.ExitCode)). " +
                  "Check %LOCALAPPDATA%\ClaudeUsageWidget\logs\overlay.log and server.log."
        }
        try {
            $resp = Invoke-WebRequest "http://localhost:$Port/data" -TimeoutSec 3 -ErrorAction Stop
            if ($resp.Headers['X-Claude-Usage-Widget']) { $dataResp = $resp; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $dataResp) {
        throw "FAIL: data server never responded on port $Port within ${TimeoutSeconds}s."
    }
    Write-Host "PASS: data server is up and responding on port $Port."

    $root = Invoke-WebRequest "http://localhost:$Port/" -TimeoutSec 5
    if ($root.StatusCode -ne 200 -or $root.Content -notmatch 'Claude usage') {
        throw "FAIL: root page didn't return the expected widget HTML."
    }
    Write-Host "PASS: root page serves widget.html correctly."

    $data = $dataResp.Content | ConvertFrom-Json
    if ($data.ok) {
        Write-Host "PASS: server returned real usage data (fetched_at=$($data.fetched_at))."
    } else {
        Write-Host "NOTE: server responded but ok=false (error: '$($data.error)'). " +
                    "Expected on a machine without a logged-in Claude Code CLI (e.g. CI) - " +
                    "the exe -> server -> HTTP pipeline itself is confirmed working either way."
    }

    Write-Host "Killing widget (hard Stop-Process, to exercise the crash-path Job Object cleanup, not just graceful FormClosing) ..."
    Stop-Process -Id $proc.Id -Force
    $proc.WaitForExit(5000) | Out-Null
    Start-Sleep -Seconds 2
    if (Test-PortOpen $Port) {
        Write-Warning "Server child is STILL listening on port $Port after the widget was killed - the orphaned-process fix regressed."
        $exitCode = 1
    } else {
        Write-Host "PASS: server child was torn down when the widget closed."
        $exitCode = 0
    }

    $overlayLog = Join-Path $env:LOCALAPPDATA 'ClaudeUsageWidget\logs\overlay.log'
    $serverLog  = Join-Path $env:LOCALAPPDATA 'ClaudeUsageWidget\logs\server.log'
    if (Test-Path $overlayLog) { Write-Host "overlay.log present ($((Get-Item $overlayLog).Length) bytes)." }
    else { Write-Warning "overlay.log was not created - logging may not be wired correctly." }
    if (Test-Path $serverLog) { Write-Host "server.log present ($((Get-Item $serverLog).Length) bytes)." }
    else { Write-Warning "server.log was not created - logging may not be wired correctly." }
}
finally {
    if ($proc -and -not $proc.HasExited) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    if ($extractDir -and (Test-Path $extractDir)) {
        Remove-Item $extractDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($ownsZip -and (Test-Path $ZipPath)) {
        Remove-Item $ZipPath -Force -ErrorAction SilentlyContinue
    }
}

if ($exitCode -eq 0) {
    Write-Host "`nSMOKE TEST PASSED"
} else {
    Write-Host "`nSMOKE TEST FAILED"
}
exit $exitCode
