[CmdletBinding()]
param(
    [string]$Distro = 'Ubuntu-24.04'
)

$ErrorActionPreference = 'Stop'

$proxyScript = Join-Path $PSScriptRoot 'dify-local-proxy.ps1'
$statePath = Join-Path ([IO.Path]::GetTempPath()) 'market-intelligence-agent-dify-local.json'

if (Test-Path -LiteralPath $statePath) {
    try {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($state.ProxyPid) {
            $proxyInfo = Get-CimInstance Win32_Process -Filter "ProcessId = $([int]$state.ProxyPid)" -ErrorAction SilentlyContinue
            if ($proxyInfo -and $proxyInfo.CommandLine -like "*$proxyScript*") {
                Stop-Process -Id ([int]$state.ProxyPid) -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
    }

    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
}

wsl.exe --terminate $Distro 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Stopped temporary Dify WSL service: $Distro"
}
else {
    Write-Host "Dify WSL service was already stopped: $Distro"
}
