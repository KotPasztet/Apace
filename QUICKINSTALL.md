# Quick Install

Apace is a self-hosted Minecraft Earth replacement server. Fastest way to run it: Docker.

## Requirements

- **Linux / macOS:** [Docker](https://docs.docker.com/desktop/setup/install/mac-install/) (Docker Desktop or the engine); **Windows:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)
- ~4 GB free RAM, and a phone on the **same network** as the server

## Install

**Linux / macOS:**
```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash
```

**Windows (PowerShell as Administrator):**
```powershell
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex
```

The installer installs Docker if it's missing, downloads `docker-compose.yml` into `~/apace`,
creates the persistent volumes under `/opt/apace-persistent` (`C:\apace-persistent` on Windows:
worlds, accounts, logs), writes a default `config.json` with the API port already set to 1808,
and starts the container.

## First run

1. Open the panel: **http://localhost:5000**
2. Create an account — the first one becomes the admin.
3. **Server Options** → set your PC's IP (`hostname -I` on Linux/macOS, `ipconfig` on Windows).
4. **Server Status** → click **Start All**.
5. Accept the Minecraft EULA when the yellow banner appears on that page.

To start Apace again later, run `run.sh` / `run.ps1` from `~/apace`.

## Ports (Docker)

| Port | Protocol | Purpose |
|---|---|---|
| 5000 | TCP | Web panel |
| 1808 | TCP | Game API (the port patched phone clients call) |
| 5532 | TCP | Event bus (internal) |
| 19132 | UDP | Bedrock — the single port every player connects to |

Allow 5000/TCP, 1808/TCP and 19132/UDP through your firewall. The game API port is the
**API Port** in Server Options — it is already set to 1808 on fresh installs (the port
Docker publishes); if you change it there, update the Docker port mapping too.

## Patch the phone client

The patcher is built into the panel — no separate patcher download needed.

1. Open the **Patcher** page and pick **Android (APK)** or **iOS (IPA)**.
2. Pick a mode: **Auto** (everything from your Apace config), **Simple** (override addresses),
   or **Advanced** (every option).
3. Drop in your **original** Minecraft Earth APK/IPA and start the patch.
4. Install the patched file on the phone, open it, and sign in with your panel account.

No-Docker install, details and troubleshooting: [Installation.md](Installation.md).
