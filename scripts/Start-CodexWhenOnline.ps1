[CmdletBinding()]
param(
    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds = 180,

    [switch]$DiagnoseOnly,

    [switch]$KeepRunningInstance
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

Add-Type -AssemblyName System.Net.Http

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDirectory = Join-Path $scriptDirectory 'logs'
$logPath = Join-Path $logDirectory ("Codex-Network-{0}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null

function Write-Status {
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [ConsoleColor]$Color = [ConsoleColor]::Gray
    )

    $line = '[{0}] {1}' -f (Get-Date -Format 'HH:mm:ss'), $Message
    Write-Host $line -ForegroundColor $Color
    Add-Content -LiteralPath $logPath -Value $line -Encoding utf8
}

function Get-ProxySummary {
    try {
        $internetSettings = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings' -ErrorAction Stop

        if ($internetSettings.ProxyEnable -eq 1 -and $internetSettings.ProxyServer) {
            return "Windows proxy: $($internetSettings.ProxyServer)"
        }

        if ($internetSettings.AutoConfigURL) {
            return "Automatic proxy script: $($internetSettings.AutoConfigURL)"
        }
    }
    catch {
        return 'Windows proxy: unable to read settings'
    }

    return 'Windows proxy: disabled. Turn on your VPN/proxy if direct access is unavailable.'
}

function Test-CodexEndpoint {
    param(
        [Parameter(Mandatory)]
        [string]$Uri
    )

    $handler = [System.Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $true
    $handler.Proxy = [System.Net.WebRequest]::DefaultWebProxy
    if ($null -ne $handler.Proxy) {
        $handler.Proxy.Credentials = [System.Net.CredentialCache]::DefaultCredentials
    }

    $client = [System.Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(6)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('WinTools-Codex-Network-Check/1.0')

    try {
        # Any HTTP response means that DNS, TLS, and the remote route are working.
        $response = $client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        $statusCode = [int]$response.StatusCode
        $response.Dispose()
        return [pscustomobject]@{ Success = $true; Detail = "HTTP $statusCode" }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Detail = $_.Exception.GetBaseException().Message }
    }
    finally {
        $client.Dispose()
        $handler.Dispose()
    }
}

function Test-CodexNetwork {
    $results = @(
        [pscustomobject]@{ Name = 'ChatGPT'; Uri = 'https://chatgpt.com/' }
        [pscustomobject]@{ Name = 'OpenAI API'; Uri = 'https://api.openai.com/v1/models' }
    )

    $chatGptReachable = $false
    foreach ($endpoint in $results) {
        $probe = Test-CodexEndpoint -Uri $endpoint.Uri
        Write-Status "$($endpoint.Name): $($probe.Detail)"
        if ($endpoint.Name -eq 'ChatGPT') {
            $chatGptReachable = $probe.Success
        }
    }

    # The desktop app signs in through ChatGPT, so API-only access is not enough.
    return $chatGptReachable
}

function Get-CodexProcesses {
    # Codex currently uses ChatGPT.exe internally. Restrict the match to the
    # Codex package so a separately installed ChatGPT app is not closed.
    return @(Get-Process -Name 'ChatGPT' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -match '\\OpenAI\.Codex_' } catch { $false }
    })
}

function Stop-CodexDesktop {
    if ($KeepRunningInstance) {
        return
    }

    $running = Get-CodexProcesses
    if ($running.Count -eq 0) {
        return
    }

    Write-Status 'Closing the Codex instance stuck on Reconnecting...' Yellow
    $running | Stop-Process -Force

    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and (Get-CodexProcesses).Count -gt 0) {
        Start-Sleep -Milliseconds 250
    }
}

function Reset-CodexNetworkState {
    if ($KeepRunningInstance) {
        Write-Status 'Network-state repair was skipped because KeepRunningInstance was selected.' DarkYellow
        return
    }

    $package = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter 'OpenAI.Codex_*' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $package) {
        Write-Status 'Codex profile was not found; network-state repair was skipped.' DarkYellow
        return
    }

    $networkDirectory = Join-Path $package.FullName 'LocalCache\Roaming\Codex\web\Codex\Default\Network'
    if (-not (Test-Path -LiteralPath $networkDirectory)) {
        Write-Status 'Codex network-state folder does not exist yet; no cleanup is needed.' DarkYellow
        return
    }

    $backupDirectory = Join-Path $scriptDirectory ("backups\Codex-Network-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null

    # Cookies, tokens, and Device Bound Sessions are deliberately preserved.
    $stateFiles = @(
        'Network Persistent State',
        'Reporting and NEL',
        'Reporting and NEL-journal',
        'SCT Auditing Pending Reports',
        'TransportSecurity'
    )

    $moved = 0
    foreach ($name in $stateFiles) {
        $source = Join-Path $networkDirectory $name
        if (Test-Path -LiteralPath $source) {
            Move-Item -LiteralPath $source -Destination (Join-Path $backupDirectory $name) -Force
            $moved++
        }
    }

    if ($moved -gt 0) {
        Write-Status "Rebuilt $moved cached Codex network-state files. Backup: $backupDirectory" Green
    }
    else {
        Write-Status 'No stale Codex network-state files were found.' DarkGreen
    }
}

function Start-CodexDesktop {
    $launcher = Get-Command 'chatgpt-classic.exe' -ErrorAction SilentlyContinue
    if ($null -eq $launcher) {
        $launcher = Get-Command 'ChatGPT.exe' -ErrorAction SilentlyContinue
    }

    if ($null -eq $launcher) {
        throw 'The Codex launcher was not found. Start Codex once from the Start menu or reinstall it.'
    }

    # The local Codex logs contain repeated HTTP2 ping failures and closed
    # connections. Force the more compatible HTTP/1.1-over-TCP path.
    Start-Process -FilePath $launcher.Source -ArgumentList @('--disable-quic', '--disable-http2')
    Write-Status 'Codex has started in connection-stability mode.' Green
}

Write-Status 'Checking the Codex network connection...' Cyan
Write-Status (Get-ProxySummary)

if ($DiagnoseOnly) {
    if (Test-CodexNetwork) {
        Write-Status 'Check passed: Codex is reachable.' Green
        exit 0
    }

    Write-Status "Check failed. Log: $logPath" Red
    exit 1
}

Stop-CodexDesktop
Reset-CodexNetworkState

try {
    Clear-DnsClientCache
    Write-Status 'The DNS cache was cleared.' Yellow
}
catch {
    Write-Status "Could not clear the DNS cache; repair will continue: $($_.Exception.Message)" DarkYellow
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$consecutiveSuccesses = 0

while ((Get-Date) -lt $deadline) {
    if (Test-CodexNetwork) {
        $consecutiveSuccesses++
        if ($consecutiveSuccesses -ge 2) {
            Write-Status 'The connection is stable. Starting Codex...' Green
            Start-CodexDesktop
            exit 0
        }

        Write-Status 'First check passed. Confirming connection stability...' DarkGreen
        Start-Sleep -Seconds 2
        continue
    }

    $consecutiveSuccesses = 0

    $remaining = [Math]::Max(0, [int]($deadline - (Get-Date)).TotalSeconds)
    Write-Status "Not connected. Turn on your VPN/proxy; waiting for up to $remaining more seconds..." Yellow
    Start-Sleep -Seconds 5
}

Write-Status 'Timed out. Turn on your VPN/proxy, then run this launcher again.' Red
Write-Status "Diagnostic log: $logPath" Red
exit 2
