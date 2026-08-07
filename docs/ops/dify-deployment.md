# Dify Deployment Notes

This file records non-sensitive deployment metadata only. The real server
address, passwords, API keys, and recovery information belong in the team
password manager and must not be committed here.

## Current Status

- Status: Compose stack running; browser initialization completed
- Host label: `DIFY_HOST`
- Access: Internal network only; do not expose the HTTP port to the Internet
- Workspace: `market-intelligence`
- Stack version: Dify source commit `5456d4d56e5701999bc8da2a2c97f5ae9b3b78d3`; container images use Dify `1.16.1`
- Deployed: 2026-08-04 (Ubuntu 24.04 in WSL2)
- Admin account: Created during browser initialization; username and password intentionally not recorded

## Deployment Checklist

Run these steps in the target Linux environment. The verified local target is
Ubuntu 24.04 inside WSL2; this repository remains separate from the Dify runtime.

1. Install Docker Engine and the Docker Compose plugin. Confirm with
   `docker --version` and `docker compose version`.
2. Prepare the official Dify source at a reviewed commit or tag. The current
   verified source is commit `5456d4d56e5701999bc8da2a2c97f5ae9b3b78d3`; WSL
   direct GitHub access timed out, so the official GitHub codeload archive was
   downloaded from Windows and extracted into `~/services/dify`.
3. Enter `dify/docker`, copy `.env.example` to `.env`, and set `SECRET_KEY`
   and `INIT_PASSWORD` using the team's secret-management process.
4. Start the stack with `docker compose up -d`.
5. Confirm every required service is running with `docker compose ps`, and
   inspect the API logs for a successful application start.
6. From a second machine on the internal network, open `http://DIFY_HOST` and
   confirm that the login page loads.
7. Complete the initial admin setup at `http://DIFY_HOST/install`, sign in,
   create the `market-intelligence` workspace, and invite only required users.
8. Record the reviewed Dify commit or tag and the admin username in this file.

## Verification Record

Complete this section only after the external deployment has been verified.

- Verified on: 2026-08-04 (infrastructure and HTTP health check)
- Verified by: Codex execution in the local WSL environment
- Dify commit or tag: `5456d4d56e5701999bc8da2a2c97f5ae9b3b78d3`
- `docker compose ps` result: All required services running; API, PostgreSQL, Redis, sandbox, and local sandbox healthy
- HTTP result: `http://localhost/` returned `307` (expected redirect); `http://localhost/install` returned `200`
- Internal login result: Completed by user; unauthenticated root request redirects to `/auth/refresh`, indicating the auth flow is active
- Workspace result: `market-intelligence` created during browser initialization, confirmed by user

## Security Notes

- Keep Dify reachable only from the internal network during Phase 1.
- Never add `.env`, passwords, API keys, real IP addresses, exported notes, or
  scraped content to this repository.
- Back up the Dify database and secret material according to the server team's
  operational policy before production use.
