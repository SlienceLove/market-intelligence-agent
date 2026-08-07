[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu-24.04',

    [string]$ComposeDir = '~/services/dify/docker',

    [int]$ListenPort = 18080,

    [switch]$OpenBrowser
)

$ErrorActionPreference = 'Stop'

$proxyScript = Join-Path $PSScriptRoot 'dify-local-proxy.ps1'
$tempRoot = [IO.Path]::GetTempPath()
$statePath = Join-Path $tempRoot 'market-intelligence-agent-dify-local.json'
$proxyLogPath = Join-Path $tempRoot 'market-intelligence-agent-dify-proxy.log'
$proxyErrorPath = Join-Path $tempRoot 'market-intelligence-agent-dify-proxy.error.log'
$keepAlive = $null
$proxy = $null

function Invoke-WslBash {
    param([Parameter(Mandatory = $true)][string]$Command)

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $result = & wsl.exe -d $Distro -- bash -lc $Command 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "WSL command failed: $Command`n$($result -join [Environment]::NewLine)"
    }

    return $result
}

function Wait-WslDocker {
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & wsl.exe -d $Distro -- bash -lc 'docker info >/dev/null 2>&1'
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Seconds 2
    }

    throw 'Docker Engine did not become ready inside WSL within 120 seconds.'
}

function Wait-DifyHttp {
    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & wsl.exe -d $Distro -- bash -lc 'curl -fsS --max-time 3 http://127.0.0.1/ >/dev/null'
        if ($LASTEXITCODE -eq 0) {
            return
        }

        Start-Sleep -Seconds 2
    }

    throw 'Dify Nginx did not become ready inside WSL within 120 seconds.'
}

function Get-WslAddress {
    $raw = ((& wsl.exe -d $Distro -- bash -lc 'hostname -I' 2>&1) -join ' ')
    $match = [regex]::Match($raw, '(?<!\d)(?:\d{1,3}\.){3}\d{1,3}(?!\d)')
    if (-not $match.Success) {
        throw "Could not determine the WSL IPv4 address. Output: $raw"
    }

    return $match.Value
}

function Test-ManagedProxyProcess {
    param([int]$ProcessId)

    $info = Get-CimInstance Win32_Process -Filter "ProcessId = $ProcessId" -ErrorAction SilentlyContinue
    if (-not $info) {
        return $false
    }

    return $info.CommandLine -like "*$proxyScript*"
}

try {
    if (-not (Test-Path -LiteralPath $proxyScript)) {
        throw "Proxy script not found: $proxyScript"
    }

    if (Test-Path -LiteralPath $statePath) {
        try {
            $previous = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
            if ($previous.ProxyPid -and (Test-ManagedProxyProcess -ProcessId ([int]$previous.ProxyPid))) {
                Stop-Process -Id ([int]$previous.ProxyPid) -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 300
            }
        }
        catch {
        }
    }

    $existing = Get-NetTCPConnection -State Listen -LocalAddress '127.0.0.1' -LocalPort $ListenPort -ErrorAction SilentlyContinue
    if ($existing) {
        throw "Local port $ListenPort is already in use by PID $($existing.OwningProcess)."
    }

    $keepAlive = Start-Process -FilePath 'wsl.exe' -WindowStyle Hidden -ArgumentList @(
        '-d', $Distro,
        '--',
        'sleep',
        'infinity'
    ) -PassThru

    Wait-WslDocker
    Invoke-WslBash "cd $ComposeDir && docker compose up -d"
    Invoke-WslBash "cd $ComposeDir && docker compose restart nginx"
    Wait-DifyHttp

    $wslAddress = Get-WslAddress
    $proxy = Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList @(
        '-NoLogo',
        '-NoProfile',
        '-ExecutionPolicy',
        'Bypass',
        '-File',
        $proxyScript,
        '-TargetAddress',
        $wslAddress,
        '-TargetPort',
        '80',
        '-ListenAddress',
        '127.0.0.1',
        '-ListenPort',
        $ListenPort.ToString()
    ) -RedirectStandardOutput $proxyLogPath -RedirectStandardError $proxyErrorPath -PassThru

    $localUrl = "http://127.0.0.1:$ListenPort/"
    $ready = $false
    for ($attempt = 1; $attempt -le 30; $attempt++) {
        if ($proxy.HasExited) {
            $proxyError = if (Test-Path -LiteralPath $proxyErrorPath) { Get-Content -LiteralPath $proxyErrorPath -Raw } else { '' }
            throw "Local proxy exited before becoming ready. $proxyError"
        }

        try {
            $response = Invoke-WebRequest -Uri $localUrl -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
                $ready = $true
                break
            }
        }
        catch {
        }

        Start-Sleep -Seconds 1
    }

    if (-not $ready) {
        throw "Local Dify proxy did not become ready at $localUrl."
    }

    [pscustomobject]@{
        Distro = $Distro
        WslAddress = $wslAddress
        ComposeDir = $ComposeDir
        ListenAddress = '127.0.0.1'
        ListenPort = $ListenPort
        WslKeepAlivePid = $keepAlive.Id
        ProxyPid = $proxy.Id
        StartedAt = (Get-Date).ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding ascii

    Write-Host "Dify is ready: $localUrl"
    Write-Host "WSL target: http://$wslAddress/"
    Write-Host "Stop with: .\scripts\stop-dify-local.ps1"

    if ($OpenBrowser) {
        Start-Process $localUrl | Out-Null
    }
}
catch {
    if ($proxy -and -not $proxy.HasExited) {
        Stop-Process -Id $proxy.Id -Force -ErrorAction SilentlyContinue
    }

    if ($keepAlive -and -not $keepAlive.HasExited) {
        Stop-Process -Id $keepAlive.Id -Force -ErrorAction SilentlyContinue
    }

    throw
}
