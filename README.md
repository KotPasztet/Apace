<p align="center">
  <img src="images/Apace_Branding.png" alt="Apace" width="80%">
</p>

<p align="center">
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/github/stars/KotPasztet/Apace?style=flat-square" alt="GitHub Stars">
</p>

Really fast replacement server for Minecraft Earth™, based on [Solace](https://github.com/Earth-Restored/Solace) with additional features and fixes.

> [!NOTE]
> **Active development.** Server is functional — maps, buildplates, challenges, daily rewards, adventures, crafting, and more are working. Performance improvements planned.

## Disclaimer

**Apace** is an independent, community-driven project and is **not affiliated with, authorized, maintained, endorsed, or sponsored** by Microsoft Corporation, Mojang Studios, or any of their affiliates or subsidiaries.

* *Minecraft Earth™* is a trademark of Microsoft Corporation. All trademarks and registered trademarks are the property of their respective owners.
* This project does not distribute, host, or provide access to original game assets, proprietary binaries, or resource packs. Users are responsible for providing their own legally obtained assets.
* This software is provided solely for educational, research, and archival purposes to restore functionality to a discontinued service.
* This project is provided "as-is" without any warranty of any kind, express or implied. In no event shall the authors be held liable for any claim, damages, or other liability.

## Features

| Feature       | Status             | Notes                                                                                    |
|---------------|--------------------|------------------------------------------------------------------------------------------|
| Map           | :white_check_mark: |                                                                                          |
| Profile       | :construction:     | Loads, can view activity log/settings, cannot change skin, statistics not implemented    |
| Journal       | :white_check_mark: |                                                                                          |
| Activity Log  | :white_check_mark: |                                                                                          |
| Inventory     | :white_check_mark: |                                                                                          |
| Crafting      | :white_check_mark: |                                                                                          |
| Smelting      | :white_check_mark: |                                                                                          |
| Boosts        | :white_check_mark: |                                                                                          |
| Boost Minis   | :white_check_mark: | NFC minifig activation with Mattel tag decoding                                          |
| Tappables     | :white_check_mark: |                                                                                          |
| Buildplates   | :white_check_mark: |                                                                                          |
| Store         | :white_check_mark: | Tab titles do not load                                                                   |
| Challenges    | :construction: | Daily challenge system (3 per player, deterministic rotation, progress tracking)         |
| Seasons       | :white_check_mark: | Seasonal content support                                                                 |
| Adventures    | :white_check_mark: | Join responses, port reuse, instance lifecycle                                           |
| Daily Rewards | :white_check_mark: | Daily login rewards with streak tracking                                                 |
| Tokens        | :white_check_mark: | Token claim/redeem system                                                                |
| Tutorial      | :x:                |                                                                                          |

:white_check_mark: - Complete
:construction: - Under Development
:x: - Not Working


## Quick Start

### Docker (recommended)

One command, fully automatic. The script detects your CPU architecture, installs Docker if needed, pulls the correct image, and starts Apace.

```bash
# Linux/macOS
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash

# Windows (PowerShell as Administrator)
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex
```

**What the installer does:**
1. Detects your CPU architecture (`x86_64` → `linux/amd64`, `aarch64` → `linux/arm64`)
2. Auto-starts Docker if it's not running (Docker Desktop on Windows, `systemctl` on Linux)
3. Downloads `docker-compose.yml` from this repo and injects `platform:` for native execution (no emulation)
4. Pulls the multi-arch Docker image (`ghcr.io/kotpasztet/apace:main`)
5. Starts the container — admin panel is available at **http://localhost:5000**

After install: open http://localhost:5000, create an account, set your IP in Server Options, click Start.

### Subsequent runs (quick-launch)

After the initial install, use the quick-launch script to start Apace without re-downloading everything:

```bash
# Linux/macOS
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/run.sh | bash

# Windows (PowerShell)
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/run.ps1 | iex
```

The quick-launch script: detects architecture, downloads `docker-compose.yml` only if missing, pulls the latest image, and starts containers.

### Binary download (no Docker)

Pre-built binaries for each platform from GitHub Releases. Requires [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and Java 17.

```bash
# Linux/macOS — auto-downloads the right binary for your OS and CPU
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash -s -- --no-docker

# Windows (PowerShell) — auto-downloads the right binary for your platform
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex -args '--no-docker'
```

The `--no-docker` installer calls the GitHub API to find the latest release, detects your OS and CPU architecture, downloads the correct zip automatically.

Full instructions: [Installation.md](Installation.md)
