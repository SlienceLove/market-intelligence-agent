# Temporary Local Dify Access

Dify runs in Ubuntu 24.04 under WSL2. WSL uses a changing NAT address, and the
Windows `localhost` forwarding path is not reliable on this workstation. The
repository provides an on-demand local launcher that keeps the WSL session
alive and exposes Dify through a user-level loopback proxy.

## Start

From the repository root, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-dify-local.ps1 -OpenBrowser
```

Open `http://127.0.0.1:18080/` if the browser does not open automatically.
The launcher starts the existing Compose stack and does not recreate the
database or delete any Dify data.

## Stop

When the temporary session is no longer needed, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\stop-dify-local.ps1
```

This stops the WSL distribution and the temporary proxy. It does not configure
Windows startup, a scheduled task, a permanent port proxy, or a ComfyUI
service.

## Troubleshooting

If the proxy port is busy, start with another local port:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\start-dify-local.ps1 -ListenPort 18081 -OpenBrowser
```

The direct WSL URL can be checked with:

```powershell
$wslIp = (wsl.exe -d Ubuntu-24.04 -- bash -lc "hostname -I").Trim().Split(' ')[0]
Start-Process "http://$wslIp/"
```
