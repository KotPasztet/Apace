# Changelog

## Unreleased (dev) — Apace Cape crash fix: real encrypted container + server-delivered content key

> Equipping any Apace Cape hard-crashed the MCE client (`std::bad_alloc`, `malloc(18446744073709551504)`) ~100ms after the piece download. Root cause (reverse-engineered from `libgenoa.so`): the previous synthesized `contents.json` copied the real 256-byte ContentIdentity header, so the client picked the DERIVED (encrypted-pack) strategy and computed `bodyLen − 256` on a 133-byte file — a size_t underflow into `operator new`.

### 🧍 Persona items (dressing room / skins)

- **contents.json is now a real encrypted container** (`scripts/build_apace_piece.py`): valid 256-byte header (`u32@0x00 = 0`, magic `0x9BCFB9FC` LE @0x04, uuid length byte @0x10, pack uuid ASCII @0x11 — exact real-piece layout) followed by AES-256-CBC PKCS7 ciphertext of the `{"content":[{"path":..,"key":""},..]}` file map. Per-file keys are empty so the piece art stays plaintext; `signatures.json` is gone (a foreign one fails the hash check). The body is always ≥ 16 bytes, so the underflow crash is structurally impossible — worst case is a graceful pack rejection.
- **The content key is delivered through the inventory receipt channel**: the client registers pack keys from PlayFab entitlement receipts riding on `/inventory/GetInventoryItems` — a JWT-shaped `Receipt` string whose payload must carry `iss == "https://playfab.com"`, `aud == "https://playfab.com/B63A0803D3653643/"` (the MCE title hash) and `Receipt.Entitlements[]` with `{FriendlyId: <pack uuid>, ContentKey: <key>}`. The Apace server now serves exactly that receipt for all three cape identities with a fixed key (`ApaceContentKey` in `InventoryController.cs`, mirrored as `APACE_CONTENT_KEY` in the build script).
- **Manifest header/module versions bumped to 2.0.0** (client caches imported pieces by version), pack identities unchanged (uuids from the manifest headers). Flat `.mcpersona` sources still live only in git history (`a04a762^`) — the build script restores them, builds and removes them again.
- **Byte-level verification built in**: the build self-checks the header layout, decrypts contents.json back with the receipt key, and asserts the file list matches the inner zip exactly (one `ppack0.zip` in the outer zip).
- If the device does not honor the receipt (e.g. a receipt-signature check we cannot see), the client decrypts with its own fallback key, gets garbage and rejects the pack gracefully — the cape then still won't equip (fallback plan: swap `PieceId` to a real piece), but the crash is gone either way.

## Unreleased (dev) — Apace Cape equip fix: nested ppack format + device-test matrix

> The Apace Cape items appeared in the dressing room but none of them could be equipped, while every real cosmetic could. Root cause: the pieces were shipped as a flat zip, but the MCE client only accepts the nested `ppack0.zip` structure every real Mojang piece binary uses.

### 🧍 Persona items (dressing room / skins)

- **Root cause found by comparing formats**: all 231 working piece binaries are an outer zip containing exactly one `ppack0.zip`, which holds `manifest.json` (plaintext), `texts/`, the piece files and two extra binary files — `signatures.json` (80 bytes, different per piece) and `contents.json` (header embeds the manifest pack uuid, body encrypted). The Apace pieces were flat zips with plaintext files only — the client downloaded nothing and equip silently did nothing.
- **Pieces rebuilt in the nested shape** (`scripts/build_apace_piece.py`, replaces the lost v2 script): outer zip named after the catalog item id (`/playfab/persona/<itemId>.zip` — how real pieces are addressed), inner `ppack0.zip` with the plaintext piece content. Because Microsoft's content encryption and signatures cannot be reproduced, the three capes ship as a **device-test matrix**: Apace Cape (128px) with a synthesized `contents.json` (probes contents-presence), Apace Cape 256 bare (probes plaintext acceptance at all), Apace Cape 512 with `contents.json` plus a `signatures.json` copied verbatim from a real piece (probes signature-presence). If none equip while real pieces do, the client validates the crypto and custom art is impossible without Microsoft's keys — the doc records the full decision matrix (`docs/custom-persona-items.md`).
- **Catalog items made structurally identical to real ones** (`scripts/update_apace_items.py`): explicit `Contents` entry of type `personabinary` pointing at `/playfab/persona/<itemId>.zip` with `MinClientVersion 1.13.0` (real-item values; the synthesized `AssetFileName` URL that pointed at `apace_cape.mcpersona` is gone), and the one dangling `ItemReferences` entry every real persona item carries (all 231 have it, the Apace items were the only ones without — harmless, but one more difference eliminated). `LastModifiedDate` bumped so clients refresh cached catalog entries.
- **Verified end-to-end locally** (ApiServer + EventBus + ObjectStore on a machine): the `persona_back` dressing-room query returns all three Apace capes alongside the real capes/wings at price 0 and purchasable, each binary downloads with the exact nested structure of a real piece, and the other piece categories are unaffected. Whether the client accepts plaintext unsigned content is the remaining device test.


## Unreleased (dev) — every catalog cosmetic is free (no achievement gates)

> The dressing room showed 82 cosmetics as locked behind Minecraft/Earth achievements, and 29 more reward-only items could not be acquired at all. Everything is now a normal free offer.

### 🧍 Persona items (dressing room / skins)

- **All cosmetics are now free and ungated**: stripped the `is_achievement` / `earth_achievement` / `vanilla_achievement` tags from all 82 achievement-gated catalog items (the MCE client shows those as locked behind achievements and refuses to acquire them) and flipped `Purchasable` to true on the 29 reward-only items (the "Hero's" set, event eyes/mouths etc.). Prices were already 0 everywhere, so every offer is now a plain free item like the Apace Cape. `hidden_offer` items stay hidden (they never appeared in the dressing room). The built-in Bold Rabbit Feet shop item had the same achievement tags hardcoded in `CatalogController` — removed there as well.

## Unreleased (dev) — login secrets survive redeploys, Data Management tab fixed

> Two deployment-level bugs: every redeploy locked all clients out in a 401 loop on user/authenticate (login secrets rotated on every boot), and the Data Management tab of System Configuration rendered completely empty. A second redeploy killer — the Genoa session key ring living in the container layer — is fixed here as well.

### 🔐 Account login

- **401 loop on user/authenticate after every redeploy**: the ApiServer's login secrets (api_config.json) were kept in its working directory, which in docker was a single-FILE bind-mount — when the host file was missing, Docker created a DIRECTORY at the mount path, the server could neither read nor write it ("Access to the path '/app/components/api_config.json' is denied" in the logs) and regenerated random secrets on every boot. Clients cache their tickets for up to 7 days and never re-request them while unexpired, so every redeploy locked everyone into a 401 loop.
- The config now lives in the **data directory** (`../data/api_config.json`): the persistent `/app/data` volume in docker, the installation's data folder outside it. The legacy working-directory file is still read (and its contents migrated to the data directory, so a compose refresh cannot strand it). If neither location is writable the server now logs an explicit error instead of silently rotating.
- docker-compose.yml no longer bind-mounts api_config.json at all (the data volume covers persistence); install.ps1/update.sh migrate any existing api-config/api_config.json into the data volume, remove the bogus directory Docker may have created, and refresh composes that still carry the old file mount.
- **Genoa session tokens also died on every redeploy** (the second half of the "can't log in after redeploy" bug): the DataProtection key ring protecting the 12h Earth session tokens was stored at `<cwd>/data/dataprotection-keys` — `/app/components/data/...`, i.e. inside the container's **image layer**, not the `/app/data` volume — so every redeploy destroyed it and all issued session tokens with it (`GenoaAuth was forbidden`, 401 on every Earth API call). The key ring now lives in the data directory (`../data/dataprotection-keys`) next to api_config.json, with a fixed application name so it stays valid across image rebuilds; keys found at the legacy location (non-docker installs) are migrated so their sessions survive the move.
- Startup logging now states plainly whether the login secrets were **reused** ("future redeploys keep all client sessions valid") or **generated fresh** ("clients holding tickets from before this boot must sign in once through the login page"). The misleading boot warning "Local account only login disabled, account credentials cannot be verified" (which fired on every default install and read like an error) is now an informational line about the login mode. user/authenticate logs a once-per-restart warning when it rejects a ticket signed with unknown secrets — the exact signature of a client that cached its ticket before the secrets were persisted and needs one full re-login.

### 🖥️ Launcher UI

- **The Data Management tab of System Configuration was completely empty** (only the Save Settings / Reset to Defaults buttons below). `<DataManagementPanel />` did not resolve to the component — ServerOptions.razor was missing `@using Apace.LauncherUI.Components.Admin.Shared`, and Razor silently degrades an unresolvable tag to a plain HTML element, so the page emitted an empty `<datamanagementpanel>` element with no build or runtime error. Added the import plus a folder-level `Components/Admin/_Imports.razor` so every Admin page sees Shared components.

## Unreleased (dev) — dressing room equippable: real piece binaries

> Clicking a dressing room item used to spin forever and nothing could be equipped. Root cause: the MCE client has no persona pieces compiled in — it downloads every piece from the item's `personabinary` content — and our items shipped none.

### 🧍 Persona items (dressing room / skins)

- **Ship the real piece binaries for all 231 built-in items** (`staticdata-extras/playfab/persona/<item id>.zip`, fetched from the still-live xforge CDN — the exact bytes the real service served). Every built-in item JSON now carries its `Contents` personabinary entry pointing at the local file, so the client can download and apply the piece instead of waiting forever.
- `CatalogController.CIItemToItem` now converts Contents that come from item JSON files (raw `JsonElement`s, which previously passed through untouched and left relative URLs the client cannot fetch) into the typed response model; `ToResponse` then prefixes the host, exactly like it already did for `AssetFileName` pieces.
- Inventory endpoints (`inventory/redeem`, `GetVirtualCurrencies`, `GetInventoryItems`) now log their request bodies at Information level — the persona claim/ownership wire format is still being mapped and this is how the next missing piece gets identified.

## Unreleased (dev) — Apace Cape

> The server now has its own free cape in the dressing room — a real custom persona piece with art by KotPasztet, shipped as a `.mcpersona` binary and served to clients.

### 🧍 Persona items (dressing room / skins)

- **New free "Apace Cape" cloak item** (`staticdata-extras/playfab/items/6e8671bd-….json`): every player can claim it from the dressing room's cloak category. Unlike the earlier placeholder it is a **real custom piece**: `staticdata-extras/playfab/persona/apace_cape.mcpersona` (art by **KotPasztet**) is a Marketplace-format persona piece — `persona_piece` manifest, meta + geometry for all four body sizes, 64×64 classic cape layout texture, own thumbnail — advertised to clients as the item's `personabinary` download and served from `/playfab/persona/apace_cape.mcpersona`. The piece passes the Marketplace persona validator (`spyglass check persona --layout standard`, zero findings). The build is reproducible with `scripts/build_apace_cape.py` (not in the repo tree — see docs for the manual art-swap workflow).

## Unreleased (dev) — Log-in fix, inventory fix, filled dressing room

> Fixes the two bugs from the 2026-11-21 play session (login 401 loop, panel-added items breaking the client inventory) and fills the dressing room with 231 real character creator items — something neither Solace nor Vienna ever had.

### 🔐 Account login

- Clients holding login tickets cached before the switch to random per-installation secrets (or from a re-created `api_config.json`) were locked out in a 401 loop on `user/authenticate` — the client never re-requests an unexpired ticket. Tokens are now verified against the legacy public Solace secrets as a fallback (never signed with them), so cached tickets keep working after an update. Disable with `"AcceptLegacyTokenSecrets": false` in `api_config.json`.

### 🎒 Inventory

- Items added through the panel's inventory editor had no journal entry; the game client expects one for every item, so `GET /inventory/survival` crashed with a NullReferenceException and the client showed no inventory at all — "adding items does not work". The endpoint now backfills missing journal entries in the same request, and the panel editor writes them for new additions. `SetHotbar` no longer crashes on a hotbar instance missing from the inventory either.

### 📦 Player buildplates

- Each player buildplate card now has an Export button that downloads the player's **edited** world from the object store (the Buildplates page keeps exporting templates).

### 🧍 Persona items (dressing room / skins)

- **The dressing room is no longer empty: 231 real character creator items ship with the server** (in `staticdata-extras/`, overlaid onto the staticdata submodule at publish time). Every category has content again: 41 hair, 31 eyes, 18 mouth, 22 tops, 15 bottoms, 20 outerwear, 13 footwear, 34 hats, 5 gloves, 3 hoods, 5 legs, 3 arms, 5 back items, 10 face accessories and 7 base skins — including the Earth Hero set made for MCE and the whole 2019 launch set (Buzzcut, Blue Jeans, Work Boots, ...) that the real dressing room showed. All items are free 1P pieces the MCE client knows; thumbnails are served locally. Fetched from the still-live PlayFab catalog with `scripts/fetch_persona_catalog.py` + `scripts/generate_persona_items.py` (reproducible).
- Custom character creator items can be added as JSON files in `staticdata/playfab/items/` with a new `Data` type `Persona` (piece type, piece id, offer id, rarity, price, optional local personabinary asset) — served as `PersonaDurable` catalog entries exactly like the built-in Bold Rabbit Feet. See `docs/custom-persona-items.md`.
- New `/playfab/persona/<file>` endpoint serves persona piece binaries from `staticdata/playfab/persona/`.
- `scripts/extract-persona-pieces.py` scans the client resource pack (`genoa.mcpack`) for persona piece definitions and their UUIDs.
- Catalog searches log their filters (Debug) and every search that matches nothing is logged at Information — walking the empty dressing room categories now shows exactly what the client asks for per category.

### 🗂️ Panel

- Data Management moved into System Configuration as a tab (next to Network/Map/Databases/Gameplay/Data Handling); the page is reachable with either server-options or data permissions and shows only the tabs the user may use. The old `/Admin/Data` page is gone.

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
