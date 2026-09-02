# Changelog

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
| `1808` | public HTTP API (configurable, default on fresh installs `1808`; code default `8080`) |
| `5532` | event bus (TCP) — orchestrator ↔ connector plugin |
| `19132/udp` | **single shared** bridge Bedrock port (all instances) |
