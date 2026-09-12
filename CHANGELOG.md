# Changelog

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
