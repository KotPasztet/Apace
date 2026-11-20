# Changelog

## Unreleased (dev) — Catching up with Solace: hearts, skins, stability

> Closes the remaining feature gaps found in the "fall behind Solace" audit, and adds two things Solace does not have: persistent skins and hearts in full-size play sessions.

### ❤️ AR like the original game: hearts in play sessions

- Full-size play sessions (PLAY and SHARED_PLAY) now use health: the client shows the hearts HUD and damage applies, like in encounters. BUILD and SHARED_BUILD stay creative.
- Health persists between sessions: the connector plugin now reports the player's health in the `playerDisconnected` message and the API server stores it in the player profile.
- Dying (health 0) restores full health for the next session, like respawning in the original game.
- The connector plugin jar is now built from reconstructed sources (the shipped binary contained instance lifecycle and control channel additions that are not in upstream Project-Genoa/Vienna, so its sources were recovered by decompiling it). Future plugin changes no longer need binary-only hacks.

### 🧍 Skins

- Skin selections persist per player: `SetObjects` previously discarded the client's persona profile changes and `GetObjects` returned the same hardcoded profile (default wide-arm skin) to everyone.
- Profiles are stored in the earth database; players who never changed their skin keep the built-in defaults. Writes are rate limited (30/min).

### 📦 Resource pack downloads

- The pack is streamed from disk instead of being loaded into memory in full; interrupted downloads can resume (HTTP range requests, 206), unchanged packs answer 304 Not Modified via ETag/Last-Modified, and HEAD requests are served with a correct Content-Length.
- Downloading the pack from the panel no longer fails at random. The old implementation was killed mid-download by the HTTP client's default 100 s timeout (the pack is ~131 MB), tried every mirror exactly once, and hid the reason behind a generic "still missing" error. The Wayback Machine is now queried through the CDX API to find the actual 200-status snapshots of the pack (the `/web/2/` shortcut can redirect to a capture of an error page), each mirror is retried with backoff when archive.org rate limits (429) or errors out (5xx), partial downloads are resumed with range requests instead of restarting, and the result is validated (ZIP containing `genoa.mcpack`) before it is accepted. The panel now shows the real failure reason (rate limited, stalled connection, wrong file, ...) and a running download can be cancelled — or keeps running server side when the tab is closed.

### 🛡️ Multiplayer hardening

- Join attempts are rate limited per player (10/min sliding window); a throttled request is indistinguishable from an unknown join code (both 404), so brute forcing cannot tell throttling from wrong guesses.
- Instance status (which contains the join code and the server address) is now only visible to the instance's host and its guests. Fixes the previously documented limitation.

### ⚡ Performance (port from Solace)

- Catalog API responses (items, recipes, journal, products) are cached for 15 minutes instead of being rebuilt for every request — Solace's `CatalogResponseCacheService` idea, implemented without a DI container.
- The player inventory viewer and data management (export/import/delete) were ported from Solace in the previous update.

---

## Unreleased (dev) — Shared Buildplate Multiplayer

> Ported from the abandoned **Apace-Rework** multiplayer update (2026-11-14) onto `dev`, with an additional guest-routing fix on top. The Java-side changes ship as prebuilt jars from Apace-Rework — this repository contains no Java sources.

### 🤝 Shared buildplate multiplayer

- **Join codes** — every buildplate instance gets an 8-character join code (unambiguous alphabet, generated with a CSPRNG) returned in `gameplayMetadata.playerJoinCode`, replacing the hardcoded placeholder.
- **QR join flow** — new endpoint `POST /1/api/v1.1/multiplayer/join/instances` resolves the code scanned by the guest (`id` or `playerJoinCode` in the request body), waits for the instance to be ready, registers the guest and returns the host's instance details. The `encoded_join_enabled` / `qr_code_join_enabled` environment settings are now enabled, unlocking the join flow in the client.
- **Guest routing** — new `addGuest` / `removeGuest` event-bus messages: the launcher routes a guest's `playerLogin` to the host's instance on the shared `19132/udp` bridge without dropping the host session, and guests polling `multiplayer/partitions/...` see the host's buildplate.
- **AR remote players** (bridge jar) — remote players appear as floating nametags at eye height (+1.62) with their held item 0.5 blocks below, carried by invisible zero-size entities; `AddPlayerPacket` is never sent to the client, preventing Genoa crashes.
- **Graceful guest disconnect** (Fabric mod jar) — closing a buildplate disconnects everyone still inside with "The buildplate has been closed" instead of stranding guests in an unloaded dimension.
- **Fixed on top of the port** — guest routing is now forgotten when a player starts (or re-enters) their own instance, so joining someone's buildplate no longer hijacks subsequent logins into the host's instance.
- **Offline QR scanning** — new `fix-qr-scanner` client patch replaces Google Mobile Vision with embedded ZXing in `AndroidBarcodeReader`, so scanning join codes works without GMS.

### 🔧 Other fixes from the port

- **Windows job objects** — all child processes (persistent Fabric server, Fountain bridge, java probes) are assigned to a job object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, so they can no longer outlive the launcher after a crash or kill and keep holding ports and world files.
- **Patcher job paths** — patch jobs fall back to `%TEMP%/ApacePatcher` and then `C:\ApacePatcher` when the install path contains non-ASCII characters (aapt2, run by apktool, cannot open such paths on Windows).

### ⚠️ Known limitations

- AR shared anchors (`GenoaShareAnchorPacket` / `GenoaNetworkTransformPacket`) are not emulated — remote-player nametags can be offset differently per device, and held items jitter due to client-side item physics.
- The `minecraftearth://` URI scheme registration claimed in Apace-Rework's README had no implementation and is **not** included here; QR join works through the in-app scanner only.
- The multiplayer changes inside the Fountain bridge and Fabric mod jars are **binary-only** (verified against the Apace-Rework builds by checksum).

---

## v0.1.3 — 2026-09-12

> Scope: everything between **v0.1.2** and **v0.1.3**
> (39 commits, base: `v0.1.2` @ `dafd0d8`, 2026-09-04).
> Headlines: **Solace is now Apace** (full rebrand), logins that survive deploys, a **one-click self-updating** install, and a much more capable panel.

---

## 🎨 Rebrand: Solace → Apace

- Projects, namespaces and assemblies renamed `Solace.*` → `Apace.*`; all user-facing strings and branding images follow.
- Upstream attribution to [Solace](https://github.com/Earth-Restored/Solace) is preserved; legacy upstream installers and unused Solace images were removed.

## 🔐 Login & sessions

- **Expired login tokens no longer trap players**: the RST2 endpoint returns a well-formed SOAP fault instead of a 500, and the client's **reauthenticate flow** issues fresh Live tokens (port of the upstream Solace fixes).
- **Per-install random secrets** are generated when `api_config.json` is missing or invalid — the public upstream secrets are no longer shipped.
- `api_config.json` **persists across Docker updates**, so the JWT login secrets (and with them every session) survive redeploys — **no more forced logouts on every release**.

## 🧱 Buildplates & server stability

- ObjectStore **command concurrency raised to 256**, queued commands are cancelled on timeout, command responses are flushed reliably, and buildplate loads **retry the first fetch once**.
- **Tile cache with ETag/`304` responses** for the map; the tile renderer **auto-restarts after a crash** and tolerates transient MapTiler outages.
- User `server.properties` are **preserved** (the `max-tick-time` watchdog is disabled on the wrapper instead), and the **persistent Fabric server keeps its data on the persistent volume** across container updates.

## 🖥️ Panel

- **File browser** with tabs **Java Server / Server Data / Panel**: browse, download/upload, and safely edit an allow-list of text configs (`config.json`, `api_config.json`, `server.properties`, `eula.txt`) with a rotating `.bak` backup before every save; new `files.view` permission.
- **One-click self-update** from the panel — docker via the socket or bare metal via the installers — backed by an **update-available indicator** (channel, running/latest version, Check now, copyable update commands).
- **Log level filters** in live logs (alongside the 🐛 debug toggle), **linked accounts** folded into **Manage Users**, and the **Java-server status indicator** now reflects real readiness.
- **Resource-pack download prompt** on a fresh start, a **MapTiler link** above the API-key field, and the **default API port is now `1808`**.
- Startup is more resilient: pending EF model changes no longer crash boot, the Java-server wait no longer depends on the evictable log buffer, and Fabric log paths follow the moved persistent directory.

## 📦 Installs, updates & CI

- **Termux (Android)**: no-Docker quick install via proot-distro (`install-termux.sh`) and a dedicated `Apace-termux-arm64.zip` release asset.
- **One-command Solace migration** (`scripts/migrate-from-solace.sh` / `.ps1`): auto-detects the old Solace install, installs Apace if it is missing, and carries accounts, player progress, inventory and buildplates over (dry-run plan first; the Solace directory is never modified).
- **Backwards-compatible self-updating `update.sh`** with backup rotation and `--rollback`, and **versioned release images** (`ghcr.io/kotpasztet/apace:vX.Y.Z`) alongside the rolling `:main` tag.
- **CI**: the release workflow syntax-checks (`bash -n`) and shellchecks install scripts and compile-checks the migration script; new Tailscale and integration-port docs.

## ✨ Features ported from Solace v0.0.7

- **Local-login-only option** for the Earth API sign-in.
- **Buildplate export** from the panel.
- **Log level filtering** in live logs.
- **Linked accounts** (in-game profile ↔ admin panel account).
- **Configurable bridge (public) port** for buildplates.
- **Daily sign-in challenges**, and the **daily-login streak now advances on claim and resets after a missed day**.

---

## v0.1.0 — 2026-09-02

> Scope: everything between **v0.0.3** and **v0.1.0**
> (95 commits, base: `v0.0.3` @ `7d745f8`, 2026-08-07).
> Released in **v0.1.0** — this is the first release of the v2 persistent-server architecture.

---

## 🏗️ v2 persistent-server architecture (the headline change)

Instead of booting a whole new Minecraft server (JVM pair) **per buildplate**, v0.1.0 runs **one persistent Fabric server** that hosts every buildplate as an on-demand **dimension**.

- One **persistent Fabric server** + one **persistent bridge** serve all instances concurrently (`5132c73`, `32263fe`).
- A buildplate instance is a **dynamically registered dimension** — created in **~1 s** via `createInstance` on the control channel, world data imported/exported on the fly.
- **Single shared Bedrock port `19132/udp`** for all players and instances — no per-instance port offsets (v1 used `19132 + offset` per server). Player routing is explicit: `bindPlayerToInstance` → `dimensionId` is placed in the Java login packet.
- All dimensions live in the persistent volume (`/opt/apace-persistent/fabric-data`), so buildplates survive container restarts.
- Fountain-fabric mod shipped in the Docker image and auto-synced into the volume on every start.

**Measured impact (see README):**
- Buildplates load **3471% faster** than v0.0.3 — wait time reduced by **97.2%**.
- RAM: v0.0.3 needs a full JVM (**1.6 GB per server**) per buildplate; v0.1.0 serves everything from one server — **~1.5 GB total + ~30 MB per additional concurrent buildplate**.
- 15 concurrent buildplates: **24 GB → ~1.9 GB (≈92% less RAM)**.

## ⚡ Startup & world generation performance

- Default overworld of the persistent Fabric server is generated with `fountain:empty` (**100% air**) — `Preparing spawn area` finishes in **seconds instead of minutes** (no noise-terrain generation at boot).
- **Nether and End are not created at all** (removed from `WorldGenSettings` dimensions).
- Stale overworld chunk data from previous starts is deleted at startup (instance dimensions and player data are preserved).
- Buildplate instances use `fountain:empty` as the wrapper's inner generator — chunks touching the plate are pure air and are immediately overwritten by the plate import (no wasted noise terrain).

## 📱 Minecraft Earth client patcher (new)

- Client patcher integrated into the panel: **Auto / Simple / Advanced** modes, **APK & IPA**.
- Default **Apace branding** (`com.kotpasztet.apace`, Apace output name and app icon).
- `Minecraft_Earth_Patcher` **vendored into the repo** (no submodule).
- **Native `aapt2`/`zipalign` including ARM64**; responsive host during patching.
- APK/IPA uploads go over **plain HTTP instead of the SignalR circuit** — **resumable, chunked**, with a **manifest-based icon patcher** (fixed upload append bug).
- Panel locks itself to the patcher page while a job is running; Generate reloads straight into the job view.
- `aapt2` wrapper compatibility fixes for Debian (`-P` fallback, unknown-flag dropping, android-29 framework).

## 🖥️ Panel / admin UI

- **Fabric Server log viewer** (admin tab) with live tailing, refresh and copy — timestamps converted to the **user's timezone**.
- **Java Server** tab in live logs + status card on Server Status (status stays `Starting` until Fabric prints its "Done" banner — no more Online flicker).
- **All panel timestamps are in the browser's timezone**: "Last updated" on System Status (was raw server/UTC time), Fabric log lines, and copied log text.
- Live logs: 🐛 **debug toggle**, per-chunk bedrock block mapping warnings demoted to debug (much quieter logs).
- Role/permission claims are **refreshed on every request** — role changes apply without re-login.
- `PermissionClaimsTransformation` + control channel configuration for the persistent Fabric server.

## 🐛 Fixes

- Imported buildplate world data is **actually unpacked** into the dimension (was a no-op in the Fabric mod).
- Player **login is routed to the player's newest instance** (previously could land in a stale one).
- Players bound by **offline-mode UUIDs**; undashed UUID handling.
- **Invalid player movement kicks** from the Bedrock bridge tolerated (no more ghost kicks).
- Object store: requests run on **per-command connections**, GET/DEL **flushed** properly, request **timeouts instead of hanging**.
- Earth database: **WAL + busy_timeout** enabled (fewer `database is locked` errors).
- Event bus requests also **time out instead of hanging**.
- Bridge/mod rebuild fixes: `com.nukkitx:natives` for packet compression, full fastutil, runtime-applied access widener, `RegistryOps` generator-settings parsing, `RequestWithInstanceId` forwarding, `destroyInstance` fix.
- Runtime directory cleanup tolerates double-delete and missing directories; shutdown duration logged.

---

### Port map after v2 (shared services, not per-instance)

| Port | Purpose |
|------|---------|
| `5000` | launcher UI / dashboard (panel) |
| `1808` | public HTTP API (configurable; default `1808`, also on fresh installs) |
| `5532` | event bus (TCP) — orchestrator ↔ connector plugin |
| `19132/udp` | **single shared** bridge Bedrock port (all instances) |
