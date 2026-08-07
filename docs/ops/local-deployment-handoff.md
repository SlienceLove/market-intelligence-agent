# Local Deployment Handoff

This file is the durable handoff for the local Dify deployment. Read it before
continuing setup after a Windows restart or in a new agent session.

## Recorded State

- Date: 2026-08-03
- Repository: `D:\Project\Github\Agent\market-intelligence-agent`
- Host: Windows 10/11 64-bit, build 26200, about 16 GB RAM
- Virtualization: detected as available
- Disk: D: has ample free space for Docker images and Dify data
- Docker: not installed or not available on PATH; `docker version`,
  `docker info`, and `docker compose version` all failed
- WSL: the Windows features `Microsoft-Windows-Subsystem-Linux` and
  `VirtualMachinePlatform` were enabled successfully from an Administrator
  PowerShell
- WSL kernel MSI: `wsl_update_x64.msi` was downloaded from Microsoft's official
  CDN, verified as 17,104,896 bytes, and installed with `msiexec`
- WSL MSI SHA-256: `4D09C776C8D45F70A202281D18E19BE1118F53159B0C217A5274A31CE18525FE`
- Reboot: required before WSL registration can be checked; pre-reboot
  `wsl --status`, `wsl --version`, and `wsl --list --verbose` still report that
  WSL is not installed
- Dify: not started yet
- Linux distribution: not installed; do not assume Ubuntu installation
  completed
- Previous attempt: WSL installation stopped at about 17% and Windows
  reported that `C:\Program Files\Bonjour\mdnsNSP.dll` was blocked from
  loading into Local Security Authority. The DLL is an old Bonjour component
  dated 2011; this is not a WSL file and security protection must not be
  disabled to work around it.

## Already Completed in the Repository

- `docs/ops/dify-deployment.md`: redacted Dify deployment metadata template
- `docs/ops/ima-to-dify-sync.md`: IMA-to-Dify manual sync SOP
- `docs/PROGRESS.md`: Phase 1 marked in progress and execution recorded
- Existing .NET foundation test passed: 1 test passed
- No Dify repository, containers, secrets, or `.env` files were created during
  this prerequisite setup.

## Continue After Restart

After Windows restarts, run these checks from an Administrator PowerShell:

```powershell
wsl --status
wsl --version
wsl --list --verbose
wsl --set-default-version 2
```

If WSL is registered and no distribution is listed, install Ubuntu without
using the Microsoft Store:

```powershell
wsl --install --web-download -d Ubuntu-24.04
```

Do not repeat the MSI or feature installation before checking the post-restart
status. If `--web-download` is not supported, use `wsl --list --online` and
then install the available Ubuntu 24.04 entry.

The selected local route is Docker Engine inside Ubuntu WSL, using the verified
Tsinghua Docker CE repository:
`https://mirrors.tuna.tsinghua.edu.cn/docker-ce/linux/ubuntu/`
Docker Desktop is optional for this route and has no verified Tsinghua
installer mirror. Run Docker and Dify Compose inside the Ubuntu terminal, not
from Windows PowerShell.

After Ubuntu initializes, configure the Docker CE apt source from the Tsinghua
help page, install `docker-ce`, `docker-buildx-plugin`, and
`docker-compose-plugin`, then confirm inside Ubuntu:

```bash
docker --version
docker compose version
docker info
```

If the Bonjour compatibility warning appears again, update Bonjour or remove
the standalone Bonjour component from Windows Installed apps if it is not
needed for Apple device discovery or network printer workflows. Restart after
that change. Only if WSL is still unavailable after the restart and the package
installation is known to be incomplete, use the official WSL package with:

```powershell
winget install --id Microsoft.WSL -e --source winget `
  --accept-source-agreements --accept-package-agreements
```

The Microsoft WSL GitHub Release API was reachable, but its binary asset timed
out in this network. Tsinghua GitHub Release candidates returned 404 for this
WSL asset, so do not use an untrusted GitHub proxy or third-party installer.
The Microsoft CDN kernel MSI used above is the verified fallback.

After Docker is ready, the next repository task is to add a local Compose
profile for Dify and the .NET API/Worker. Do not download the Dify repository or
start containers until the Docker engine check succeeds.

## New Session Prompt

Use this short prompt in a new agent session:

> Continue the local deployment. Read `docs/ops/local-deployment-handoff.md`,
> `docs/PROGRESS.md`, and `docs/ops/dify-deployment.md` first. Check WSL and
> Docker status before changing anything. Current checkpoint: Windows WSL
> features and the verified WSL kernel MSI are installed, but a restart is
> pending. Preserve existing worktree changes.

## Safety Rules

- Never commit Dify `.env` files, passwords, API keys, real server addresses,
  exported IMA notes, or scraped content.
- Do not expose the local Dify port to the public Internet.
- Do not mark Phase 1 deployment or retrieval acceptance complete until the
  corresponding external checks have actually passed.
