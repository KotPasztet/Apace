<p align="center">
  <img src="images/Apace_Branding.png" alt="Apace" width="80%">
</p>

<p align="center">
  <a href="./LICENSE"><img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License"></a>
  <img src="https://img.shields.io/github/stars/ApaceProject/Apace?style=flat-square" alt="GitHub Stars">
  <img src="https://img.shields.io/github/downloads/ApaceProject/Apace/total?style=flat-square" alt="GitHub Downloads">
  <a href="https://discord.gg/5K8HJukMSX"><img src="https://img.shields.io/badge/Discord-join_us-5865F2?logo=discord&logoColor=white&style=flat-square" alt="Discord"></a>
</p>

Really fast replacement server for Minecraft Earth™, based on [Solace](https://github.com/Earth-Restored/Solace) with additional features and fixes.

> [!NOTE]
> **Actively developed.** The server is functional — maps, buildplates, **shared buildplate multiplayer**, challenges, daily rewards, adventures, crafting, the dressing room with marketplace pieces, and more are working.

> [!TIP]
> ### ✨ Unreleased on `dev`: Shared Buildplate Multiplayer
>
> - **Join a friend's buildplate** — every buildplate session gets a join code; guests scan the host's QR code and build together on the shared `19132/udp` bridge (ported from the Apace-Rework multiplayer update).
> - **See other players in AR** — remote players appear as floating nametags at eye height with their held item below (no full skins; the Genoa client crashes on player entities).
> - **Clean exits** — closing a buildplate returns guests to the map with "The buildplate has been closed" instead of stranding them.
> - **Offline QR scanning** — the patched client decodes QR codes with embedded ZXing, no Google services needed.
> - Also from the port: child JVMs can no longer outlive the launcher on Windows, and patcher jobs work from non-ASCII install paths.
>
> See the *Unreleased* section in [CHANGELOG.md](CHANGELOG.md) for details and known limitations.

> [!TIP]
> ### ✨ New on `dev`: iOS sign-in & marketplace management
>
> - **iOS sign-in is served** — the XAL sisu flow (device/title/user/XSTS auth, sisu authenticate/authorize/client/logout) is implemented server-side. The built-in patcher writes the IPA intact (fixes the corrupt-archive bug), redirects the iOS PlayFab title API and embeds the XAL keychain entitlements — the output IPA still needs signing.
> - **Marketplace management** — the panel picks which bundles players see (panel defaults + per-player grants). Nothing selected = **zero** accessories beyond default skins, capes and custom cosmetics — enforced on the catalog, published items, saved profiles and the piece binaries.
> - **The frozen 2021 staticdata catalog no longer leaks** when the live index is missing, and probe-dead marketplace pieces leave the searches immediately instead of rendering as broken tiles.
> - **Per-search diagnostics** — one Debug line per catalog search shows the allowed set and served/dropped counts (`Live offer: caller=… defaults=N grants=M passed=… dropped=…`).
>
> Full list: [CHANGELOG.md](CHANGELOG.md) · dressing room details: [docs/live-persona-catalog.md](docs/live-persona-catalog.md) · panel guide: [docs/marketplace-management.md](docs/marketplace-management.md).

## Disclaimer

**Apace** is an independent, community-driven project and is **not affiliated with, authorized, maintained, endorsed, or sponsored** by Microsoft Corporation, Mojang Studios, or any of their affiliates or subsidiaries.

* *Minecraft Earth™* is a trademark of Microsoft Corporation. All trademarks and registered trademarks are the property of their respective owners.
* This project does not distribute, host, or provide access to original game assets, proprietary binaries, or resource packs. Users are responsible for providing their own legally obtained assets.
* The panel syncs the still-live Minecraft marketplace (character creator pieces, dressing room thumbnails) into the persistent data volume at runtime — see [docs/live-persona-catalog.md](docs/live-persona-catalog.md). Legacy asset binaries are not shipped or gated on; the repo only carries a manifest of facts (source URLs, sha256), and startup never blocks on missing assets.
* This software is provided solely for educational, research, and archival purposes to restore functionality to a discontinued service.
* This project is provided "as-is" without any warranty of any kind, express or implied. In no event shall the authors be held liable for any claim, damages, or other liability.

## Features

| Feature        | Status             | Notes                                                                                     |
|----------------|--------------------|-------------------------------------------------------------------------------------------|
| Map            | :white_check_mark: |                                                                                           |
| Profile        | :construction:     | Activity log/settings load; statistics not implemented                                    |
| Dressing Room  | :white_check_mark: | Pack-gated marketplace pieces, skin packs, custom cosmetics — [docs/marketplace-management.md](docs/marketplace-management.md) |
| Journal        | :white_check_mark: |                                                                                           |
| Activity Log   | :white_check_mark: |                                                                                           |
| Inventory      | :white_check_mark: |                                                                                           |
| Crafting       | :white_check_mark: |                                                                                           |
| Smelting       | :white_check_mark: |                                                                                           |
| Boosts         | :white_check_mark: |                                                                                           |
| Boost Minis    | :white_check_mark: | NFC minifig activation with Mattel tag decoding                                           |
| Tappables      | :white_check_mark: |                                                                                           |
| Buildplates    | :white_check_mark: |                                                                                           |
| Multiplayer    | :white_check_mark: | Shared buildplates via QR join codes (dev); AR nametags — see CHANGELOG known limitations |
| Store          | :white_check_mark: | Tab titles do not load                                                                    |
| iOS Sign-in    | :construction:     | XAL sisu flow served, patcher fixes pushed — awaiting on-device confirmation              |
| Challenges     | :construction:     | Daily challenge system (3 per player, deterministic rotation, progress tracking)          |
| Seasons        | :white_check_mark: | Seasonal content support                                                                  |
| Adventures     | :white_check_mark: | Join responses, port reuse, instance lifecycle                                            |
| Daily Rewards  | :white_check_mark: | Daily login rewards with streak tracking                                                  |
| Tokens         | :white_check_mark: | Token claim/redeem system                                                                 |
| Tutorial       | :x:                |                                                                                           |

:white_check_mark: - Complete
:construction: - Under Development
:x: - Not Working

Remaining work lives in [TODO.md](TODO.md).

## Apace v0.1 vs Solace

The v0.1 **persistent-server architecture**: one Fabric server hosts every buildplate as an on-demand dimension on a single shared Bedrock port, with the client patcher built into the panel.

|                                        | Solace / previous architecture                     | Apace v0.1                                                       |
|----------------------------------------|----------------------------------------------------|------------------------------------------------------------------|
| Buildplate instance creation           | Boots a whole new server (JVM pair) per buildplate | Dynamically registered dimension, created in **~1 s**            |
| Buildplate load                        | Wait for a full server boot                        | **3471% faster** — wait time reduced by **97.2%**                |
| RAM                                    | **1.6 GB per buildplate** (a full JVM each)        | **~1.5 GB total + ~30 MB** per additional concurrent buildplate  |
| 15 concurrent buildplates              | **24 GB**                                          | **~1.9 GB** (≈92% less)                                          |
| Boot worldgen ("Preparing spawn area") | Minutes                                            | **Seconds** (air overworld, no Nether/End)                       |

*Measured against v0.0.3 (the previous, Solace-derived architecture) — see [CHANGELOG.md](CHANGELOG.md).*

## Quick Start

**Linux/macOS:**
```bash
curl -sSL https://raw.githubusercontent.com/ApaceProject/Apace/main/install.sh | bash
```

**Windows (PowerShell as Administrator):**
```powershell
iwr https://raw.githubusercontent.com/ApaceProject/Apace/main/install.ps1 | iex
```

Then:

1. Open the panel at http://localhost:5000 and create an account.
2. **Server Options** → set your IP address.
3. **Server Status** → click **Start All**.
4. Accept the Minecraft EULA when prompted.

To start the server again later, run `run.sh` (Linux/macOS) or `run.ps1` (Windows) from the `~/apace` directory.

## Guides

* **Updating** — one command keeps an existing install current; your data and `config.json` are preserved and backed up (newest 3 kept), and `--rollback` reverts if an update misbehaves:
Linux (Bash):
  ```bash
  curl -sSL https://raw.githubusercontent.com/ApaceProject/Apace/main/scripts/update.sh | bash -s -- --yes
  ```

  Windows (PowerShell):
  ```powershell
  irm https://raw.githubusercontent.com/ApaceProject/Apace/main/scripts/update.ps1 | iex
  ```

  Flags, backup rotation and rollback details: [QUICKINSTALL.md → Updating](QUICKINSTALL.md#updating).
* **Migrating from Solace** — one command detects your old Solace install, installs Apace if it is missing, stops both servers and carries your accounts (username + password), player progress, inventory and buildplates over; only active login sessions are lost. A dry-run plan is always shown first, and the Solace directory is never modified:
Linux (Bash):
  ```bash
  curl -sSL https://raw.githubusercontent.com/ApaceProject/Apace/main/scripts/migrate-from-solace.sh | bash
  ```

  Windows (PowerShell):
  ```powershell
  iwr https://raw.githubusercontent.com/ApaceProject/Apace/main/scripts/migrate-from-solace.ps1 | iex
  ```
  Flags and the manual converter: [docs/solace-migration.md](docs/solace-migration.md).
* **Termux (Android)** — host the server on an Android phone straight from [Termux](https://f-droid.org/packages/com.termux/); no Docker needed, the installer sets up a minimal Ubuntu via proot-distro:

  ```bash
  curl -sSL https://raw.githubusercontent.com/ApaceProject/Apace/main/install-termux.sh | bash
  ```

  Requirements and first run: [QUICKINSTALL-TERMUX.md](QUICKINSTALL-TERMUX.md).
* **No Docker / building from source** — full manual instructions: [Installation.md](Installation.md).
* **Play away from home** — no router ports needed, via Tailscale: [docs/tailscale.md](docs/tailscale.md).

## Community

Questions, setup help, and development news — join the Discord: **https://discord.gg/5K8HJukMSX**
