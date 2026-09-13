<p align="center">
  <img src="images/Apace_Branding.png" alt="Apace" width="80%">
</p>

<p align="center">
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/github/stars/KotPasztet/Apace?style=flat-square" alt="GitHub Stars">
  <img src="https://img.shields.io/github/downloads/KotPasztet/Apace/total?style=flat-square" alt="GitHub Downloads">
  <a href="https://discord.gg/5K8HJukMSX"><img src="https://img.shields.io/badge/Discord-join_us-5865F2?logo=discord&logoColor=white&style=flat-square" alt="Discord"></a>
</p>

Really fast replacement server for Minecraft Earth™, based on [Solace](https://github.com/Earth-Restored/Solace) with additional features and fixes.

> [!NOTE]
> **Actively developed.** The server is functional — maps, buildplates, challenges, daily rewards, adventures, crafting, and more are working.

> [!TIP]
> ### ✨ What's new in v0.1.3
>
> - **Apace** — the project is now called Apace everywhere (namespaces, panel, branding), with upstream [Solace](https://github.com/Earth-Restored/Solace) attribution preserved.
> - **No more forced logouts** — expired login tokens are handled with a proper reauthenticate flow, login secrets are randomized per install, and `api_config.json` survives container updates.
> - **One-click self-update** from the panel (Docker and bare metal), with **versioned release images** (`ghcr.io/kotpasztet/apace:vX.Y.Z`) and a self-updating `update.sh` with backup and rollback.
> - **File browser in the panel** — browse the Java server, server data and panel files, and safely edit `config.json`, `server.properties` and more, with automatic `.bak` backups.
> - **Steadier buildplates** — ObjectStore concurrency raised to 256, cached map tiles with `304` responses, user `server.properties` preserved, and persistent Fabric data kept on the volume.
> - **Runs on your phone** — Termux quick install without Docker, plus a **one-command migration from Solace** that carries accounts, progress and buildplates over.
> - **Solace v0.0.7 features ported** — daily sign-in challenges, buildplate export, linked accounts, log level filters, configurable bridge port and local-login-only.
>
> Full list: [CHANGELOG.md](CHANGELOG.md). The v0.1 **persistent-server architecture** (one Fabric server hosting every buildplate as an on-demand dimension, single shared Bedrock port `19132/udp`, client patcher built into the panel) is unchanged — see the table below and the v0.1.0 notes in [CHANGELOG.md](CHANGELOG.md).

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

## Apace v0.1 vs Solace

|                                       | Solace / previous architecture                  | Apace v0.1                                                          |
|---------------------------------------|-------------------------------------------------|---------------------------------------------------------------------|
| Buildplate instance creation          | Boots a whole new server (JVM pair) per buildplate | Dynamically registered dimension, created in **~1 s**               |
| Buildplate load                       | Wait for a full server boot                     | **3471% faster** — wait time reduced by **97.2%**                    |
| RAM                                   | **1.6 GB per buildplate** (a full JVM each)     | **~1.5 GB total + ~30 MB** per additional concurrent buildplate      |
| 15 concurrent buildplates             | **24 GB**                                       | **~1.9 GB** (≈92% less)                                              |
| Boot worldgen ("Preparing spawn area")| Minutes                                         | **Seconds** (air overworld, no Nether/End)                           |

*Measured against v0.0.3 (the previous, Solace-derived architecture) — see [CHANGELOG.md](CHANGELOG.md).*

## Quick Start

**Linux/macOS:**
```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash
```

**Windows (PowerShell as Administrator):**
```powershell
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex
```

Then:

1. Open the panel at http://localhost:5000 and create an account.
2. **Server Options** → set your IP address.
3. **Server Status** → click **Start All**.
4. Accept the Minecraft EULA when prompted.

To start the server again later, run `run.sh` (Linux/macOS) or `run.ps1` (Windows) from the `~/apace` directory.

## Guides

* **Updating** — one command keeps an existing install current; your data and `config.json` are preserved and backed up (newest 3 kept), and `--rollback` reverts if an update misbehaves:

  ```bash
  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/update.sh | bash -s -- --yes
  ```

  Windows (PowerShell): `irm https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/update.ps1 | iex`.

  Flags, backup rotation and rollback details: [QUICKINSTALL.md → Updating](QUICKINSTALL.md#updating).
* **Migrating from Solace** — one command detects your old Solace install, installs Apace if it is missing, stops both servers and carries your accounts (username + password), player progress, inventory and buildplates over; only active login sessions are lost. A dry-run plan is always shown first, and the Solace directory is never modified:

  ```bash
  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.sh | bash
  ```

  Windows (PowerShell): `iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.ps1 | iex`. Flags and the manual converter: [docs/solace-migration.md](docs/solace-migration.md).
* **Termux (Android)** — host the server on an Android phone straight from [Termux](https://f-droid.org/packages/com.termux/); no Docker needed, the installer sets up a minimal Ubuntu via proot-distro:

  ```bash
  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install-termux.sh | bash
  ```

  Requirements and first run: [QUICKINSTALL-TERMUX.md](QUICKINSTALL-TERMUX.md).
* **No Docker / building from source** — full manual instructions: [Installation.md](Installation.md).
* **Play away from home** — no router ports needed, via Tailscale: [docs/tailscale.md](docs/tailscale.md).

## Community

Questions, setup help, and development news — join the Discord: **https://discord.gg/5K8HJukMSX**
