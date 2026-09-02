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
> ### ✨ What's new in v0.1.0
>
> The **persistent-server architecture** is now released. Instead of booting a whole new Minecraft server for every buildplate, **one persistent Fabric server** hosts all of them as **on-demand dimensions** — created in ~1 s, with world data imported and exported on the fly. Every player connects through a **single shared Bedrock port** (`19132/udp`), and the **client patcher** (APK & IPA) is built straight into the panel.
>
> Buildplates load **3471% faster**, and 15 concurrent buildplates now need **~1.9 GB of RAM instead of 24 GB**. Details in the table below.

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

*Measured as dev-vs-main — see [CHANGELOG.md](CHANGELOG.md).*

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

No Docker, or building from source? Full instructions: [Installation.md](Installation.md).

## Community

Questions, setup help, and development news — join the Discord: **https://discord.gg/5K8HJukMSX**
