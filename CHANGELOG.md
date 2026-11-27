# Changelog

## v0.2.4 — 2026-11-27

> Scope: release-packaging fix — **release ZIPs no longer ship the developer's local database**.
> Headlines: the csproj copied `Data/app.db` into the publish output, so the **v0.2.0–v0.2.3** release ZIPs bundled a DB with a pre-existing owner account — on a fresh install the first registered account could never get admin (Android main channel affected, dev/OCI unaffected). Already-poisoned installs self-heal on the first panel start: the shipped owner account is recognized by its constant SecurityStamp/Id and removed.

---

## 🗄️ fix(panel): the developer's local database is out of the release ZIPs

- **The bug**: `Apace.LauncherUI.csproj` carried `<None Update="Data\app.db" CopyToOutputDirectory="PreserveNewest" ExcludeFromSingleFile="true" />`, so `dotnet publish` copied the developer's local `Data/app.db` into the publish output — every release ZIP from **v0.2.0 through v0.2.3** contains `launcher/Data/app.db` with an existing **owner** account (`testupload@apace.local`). On a fresh install (Apace-Launcher on Android, **main** channel) the panel starts on that bundled DB, so no new account is ever granted the owner role — the first registered account on a fresh install can never become admin.
- **The fix**: `Data/app.db` is excluded from every publish (the csproj copy line is gone) and `publish.ps1` strips `launcher/Data` from the publish output as a guard — release artifacts never carry a developer/runtime database, and a fresh install creates its own DB on first boot.
- **Existing poisoned installs heal automatically**: the panel's startup now removes the shipped user on first start after the update — the bundled DB was byte-identical in every zip, so the artifact account is recognized by its constant SecurityStamp + Id (`LLH23F64OI4BZIRADJPFI3ZJER6MLJNA` / `f179a98f-bd61-4ab1-b654-30d4867e3a5c`, a pair nobody legitimately produces) and deleted with a clear warning; the install then behaves as a fresh one and the existing bootstrap paths grant Owner to the real first account. A user who merely NAMED their account `testupload@apace.local` is left untouched (stamp/Id mismatch, logged at Debug).
- **Affected**: release ZIPs **v0.2.0–v0.2.3** (Android main channel, where the panel runs on the bundled DB). Unaffected: dev builds and OCI/Docker installs.

## Unreleased (dev) — the AR-scan native crash root-caused in the disassembly and hex-patched (fix-arcore-session-race), multiplayer join answers 200 again, marketplace logs go quiet

> The field report "the second QR button still crashes after the permission fix" traced to a completely different animal: a NATIVE SIGSEGV that Breakpad intercepts before Android's tombstone (empty crash buffer, `Breakpad DumpCallback` in the log). Disassembly of `libgenoa.so` found the exact defect: `AndroidAR::stop()` destroys the AR session ASYNCHRONOUSLY (the only async call-site — `_uninitialize` uses the synchronous variant of the same call) and immediately constructs the replacement session, whose `_tryInitialize` never takes the session's recursive_mutex — so `ArSession_create` enters an in-flight `ArSession_destroy` inside ARCore 1.50 (Dynamite) and dies on the delete thread (3/3 crashes: signal 11, crash tid == delete tid, create 2–46 ms after the delete log). The fix is four bytes. Along the way the same session produced two server-side fixes: the multiplayer join popup ("Couldn't connect to server" right after a QR scan) was OUR `encounters/state` answering 400 to EVERY valid request, and the marketplace had been drowning the ApiServer log with per-item dead-probe lines.

### 🕹️ fix-arcore-session-race (patcher, Android, default on)

- Hex-patch of `lib/arm64-v8a/libgenoa.so` @ `0x10158E8`: `E1 03 1F 2A` (mov w1, wzr → `destroy(false)`, async) → `21 00 80 52` (mov w1, #1 → `destroy(true)`, synchronous) in `genoa::AndroidAR::stop()`. The teardown now runs inline under the session's recursive_mutex before `stop()` constructs the replacement — the create task (queued later) can no longer race the in-flight destroy. Matches the already-synchronous `_uninitialize` path; ~2–4 ms of blocking on the caller thread that already does a synchronous `ArSession_pause` + `TaskGroup::flush` there.
- Applied as a hexdump unified diff (the engine's existing native-patch format, same as `disable-sunset-time-check.patch`) with a 3-byte context that is UNIQUE in the lib (the bare 4-byte pattern occurs 9022×). Engine-verified end-to-end: `MCEPatcher.Core.Patcher.Patch(["fix-arcore-session-race"])` → PATCH_OK, exactly 4 bytes changed, size unchanged.
- Field APK for the standalone flow: `Minecraft_Earth_patched_QRfix3.apk` (the standalone patcher gained P6: null-context guards in the `DeviceOrientation`/`AndroidNFC` constructors genoa builds via JNI without ExceptionCheck — NFC degrades to "unavailable" instead of aborting — and P7, the hex-patch above). Signature unchanged (uber built-in debug keystore) → installs as an update; versions QRfix3 > QRfix2 > QRfix.

### 🛰️ multiplayer `encounters/state` answered 400 to every valid request (api)

- The client posts `{"<encounter id>": "<tile id>", …}` and expects the same keys mapped to their state. The body was bound as `Dictionary<string, object>` — System.Text.Json boxes values as `JsonElement`, so the old `entry.Value is not string` check fired for EVERY well-formed request and the endpoint always answered 400 — which is exactly what the client surfaces as "Couldn't connect to server. Try again later." right after a multiplayer QR scan (the rest of the join flow — join/instances by QR code, partitions polling — was already 200).
- Now binds `Dictionary<string, string>` (a non-string value throws → same 400 branch, logged at Information) and reports each requested encounter as `Pristine` — the correct empty state for a private server — wrapped in the standard `EarthJson` envelope. Route aliases: `multiplayer/adventures/state`, `multiplayer/player/adventures/state`. Honest limitations (unchanged): no persistent per-encounter dirty state, AR shared anchors not emulated, and the post-HTTP leg of a join still rides the Bedrock bridge.

### 🔇 marketplace log noise (api)

- Per-item dead verdicts (pieces AND the "Live skin pack … probed dead" spam, ~20–30 lines/min on the dying CDN) → Debug, folded into new shape tallies (`skinpack-no-contents`/`skinpack-probe-404` join the existing shapes); one aggregate Information line per warm-up pass and per 60 s idle tick ("Live persona dead probes since the last report: N (by shape: …)"). The pass-end report now splits skin packs (mirrored/dead).
- Benign empty searches — the client's normal probes for `ResourcePackTreatment_V1.2`/`StoreConfig_V1.1` that never match anything — demote their "search failed/matched no items" lines to Debug; PersonaDurable and every other type keep Warning/Information (an empty match there means real pieces are missing).
- The framework's response-caching middleware logs two Information lines per uncached request ("No cached response available…" / "The response could not be cached…") — the normal outcome for this server; silenced with a `Microsoft.AspNetCore.ResponseCaching` namespace override (Warning+ stays visible). "warm-up skipped: no stamp change" (60 s tick) → Debug; the histograms and pass reports keep their Information level.

## Unreleased (dev) — the QR-scanner permission crash joins the Apace patcher (fix-qr-permission-context, Android, default on)

> The multiplayer QR scanner crashed the game (SIGABRT) on the field Android install: genoa's JNI permission check dereferences the static `MainActivity.mInstance` with NO null guard — the field is set only at the end of `onCreate` and nulled in `onDestroy`, so opening the scan while the activity instance is gone throws an NPE on top of a pending exception and ART aborts the process. The exact fix shipped first as a standalone smali patch and was installed on a real Android 16 phone — the scanner works (user-confirmed 26.11). It now ships in the Apace patcher as a selectable Android patch, CHECKED BY DEFAULT (Auto and Simple modes always apply it; Advanced exposes the checkbox).

### 📱 fix-qr-permission-context (patcher, Android, default on)

- `Permission.smali#checkPermission()Z`: null-guard on `MainActivity.mInstance` with a fallback to `ActivityThread.currentApplication()` (light-greylist, callable at targetSdk 29) and a deny-return `false` when even that is null — the QR camera flow KEEPS working, because the real context resolves the already-granted permissions instead of a blind denial.
- `Permission.smali#requestPermission()V`: silent `return-void` when the instance is gone (the system dialog needs an Activity; substituting the Application would ClassCastException — and OOBE grants the permissions anyway).
- `AndroidPermissions.smali#checkLocationEnabled()Z`: the same application-context fallback, `false` when null — "location off" instead of a crash.
- Engine-verified: MCEPatcher.Core's own `Patcher.Patch` over a decoded tree produces smali BYTE-IDENTICAL to the device-verified patched files. The patch file matches the `fix-qr-scanner.patch` conventions (3-line contexts, blank context lines as a single space — the DiffParser skips fully-empty lines). Both patchers decode with the same apktool 3.0.3, so line numbers line up, and the APK hash check gates any drift.

## Unreleased (dev) — the face-cosmetics recovery: the git-history mirror restore becomes a script, binary landing hardens against frozen caches, per-piece-type offer diagnostics

> The "0 facial cosmetics besides the basic ones" field report, traced end to end. The dressing-room face tabs (eyes/mouth/face_accessory/hair) can only fill from binaries on disk (mirror-first), and every upstream copy of the 2019 character-creator binaries is dead — the ONLY surviving source is this repository's own git history (234 zips before commit 8e0ec99). The rescue existed only as a hand-typed `git show` loop in `docs/persona-mirror.md`, and hand-landing files was exactly the write path the offer cache could not see (the frozen-cache bug from the previous section lives on for any path that does not call `MarkBinaryLanded`). This push makes the rescue a first-class script, hardens the offer cache against ANY file landing route, and adds the per-category observability that was missing ("is this tab empty because no binary, or hidden by a gate?").

### 🛠️ scripts/restore_persona_mirror.py — the git-history rescue, scripted

- Stdlib-only script reading the 234 original `personabinary` zips straight from git objects (default ref `8e0ec99^`, override `--ref`): `--list` (234 zips, 24.2 MiB), `--target DIR` idempotent extract (atomic `.part` + `os.replace`, size-match skip), `--tarball OUT` deterministic flat tar.gz for moving to a phone volume (adb push + untar inside the container), `--verify DIR` coverage count. Refuses any `--target`/`--tarball` path inside the worktree — the assets must never return to the public tree (that is exactly what 8e0ec99 removed).
- `docs/persona-mirror.md` rewritten around the script (list/extract/tarball/verify, the three delivery routes to a volume, restart advice after manual drops) and gains a measured-coverage table: hair 41/139, eyes 31/57, mouth 18/35, skin 7/37, facial_hair 0/39 (facial hair cannot be filled without new source material); `face_accessory` coverage measured against the volume after the restore.

### 🧊 The offer cache now sees binary landings from ANY process (the restore path is finally visible)

- The binary snapshot keyed the two mirror dirs on their DIRECTORY mtime — which never moves on the field's Docker volume, and out-of-process landings (panel file management, `restore_persona_mirror.py`, a hand scp) cannot call `MarkBinaryLanded` at all: the documented git-history rescue could land all 234 zips and the offer would still hide them. Dir stamps are now CONTENT stamps (`DirectoryContentStamp`: zip count + total size + newest file mtime, per-dir 30 s TTL cache) — a landing moves them no matter which process dropped the file. On top, a 60 s `OfferRefreshBucket` is folded into the snapshot stamps: the binary-id snapshot and the hidden-id set rebuild UNCONDITIONALLY once per window, so any future landing signal that fails to move delays a landed binary's offer entry by at most one window.
- New packs.json knob `deadRetryHours` (default 24 = exactly the pre-knob behavior; hand-edit only like `maxGeometryFormatVersion`): how long a dead CDN verdict stays trusted before the next request re-probes it — a CDN revival (or a mirror landing flipping the dead→alive bridge) is picked up without an edit; `0` re-probes every dead piece on its next request.
- The warm-up loop now says WHY it idles ("no usable index" vs "no stamp change (index/gate unchanged)" vs the pass's own "nothing to do — all N indexed piece(s) already mirrored or fresh"), and every pass report carries the dead-verdict tally by probe shape ("dead by shape: no-contents: 12, warmup-404: 38") — "why is this tab empty" answers itself from one log line.

### 📊 Per-piece-type offer diagnostics (log histograms + the panel "Cosmetics coverage" card)

- `CatalogController` logs `Live persona offer per piece type: persona_skin=37, persona_eyes=31, …` after every offer rebuild and the same histogram for the blank-slot filler pool — the per-category picture (an empty face tab is a MISSING `persona_eyes=` line, not a low total) used to require log archaeology; now it is one deterministic Information line per stamp. Never computed per request.
- The Marketplace Management page gains a **Cosmetics coverage** card: per dressing-room tab (layout order, unknown types → "Other"), the funnel Index / On disk / In offer — red badge = offered 0 with a non-zero index (the empty-face-tabs signature), amber = partial gap, footer counts emotes (no tab). Strictly on-demand (the Refresh button is the only reader; the megabyte-scale index parse never sits on the page-load path; stamp-cached between clicks). The panel computes its volume-side snapshot directly (`PersonaCosmeticsCoverage` — LauncherUI is a separate process reading the same volume, the established pattern); the in-process-accurate variant lives as `CatalogController.GetLivePersonaCoverage()` for operational use. "In offer" is the saved-default-packs approximation and says so on the card (it does not model per-player grants, the grouped limit, the filler or the flatOnly custom-skeleton hide).

## Unreleased (dev) — iOS xbox login fixed on sideloads (PR #4), "Cosmetics sources": Enable legacy cosmetics + Enable marketplace sync (both default on)

> The iOS UH OH that survived every dev build had a third root cause: Xal creates its device key in `<team>.com.microsoft.xal`, but iloader/SideStore/AltStore sign with a profile that only has `<team>.*` — SecKeyGeneratePair returns -34018 and sign-in dies before anything shows. PR #4 (JensFoghable, squash-merged as ea76170) patches Xal's PopulateKeysOnKeychain with three instructions (skip the kSecAttrAccessGroup attribute pair, count 8→7), so the key lands in the app's default group; tested by the author on iOS 27.3 with iloader, login works and survives a reboot. The panel's Marketplace Management page gains a **Cosmetics sources** card with two separate checkboxes, both default ON: **Enable legacy cosmetics** (packs.json `enableLegacyCosmetics`, hot-reloaded) gates the volume mirror (`<mojang-assets>/playfab/persona/<itemId>.zip` — the dead→alive bridge for the recovered original binaries) across the offer, the warm-up scope, the lazy pipeline and binary serving; **Enable marketplace sync** (config.json `marketplaceSync`) gates the marketplace crawl alone — the scheduled start and both "Sync now" buttons — while the cached catalog keeps serving. The existing "Live persona marketplace" master switch stays untouched; both new toggles layer on top of it.

### 🍎 iOS: xbox login on sideloaded installs (PR #4)

- `IpaProcessor.PatchXalKeychainAccessGroup` (under `AddXalEntitlements`): `PopulateKeysOnKeychain` builds the SecKeyGeneratePair attribute dict with `dictionaryWithObjects:forKeys:count:8` and pair 0 is `kSecAttrAccessGroup` — both arrays start one slot later and the count drops to 7, so the device key goes into the app's default keychain group instead of `<team>.com.microsoft.xal` (which sideload entitlements `<team>.*` cannot access → -34018 → sign-in dies before anything shows — the persistent UH OH). Three 4-byte patches with strict original-bytes verification (`0x884a8/0x884ac/0x884b0`); load/remove already query without a group, so nothing else changes. Author-tested on iPhone iOS 27.3 (iloader, no re-sign, `743288A4SK.*` only): login works, survives reboot.
- PR #5 (oobe adventure crystal duration) reviewed, intentionally NOT merged — adventure-related PRs wait per the release checklist.

### 🎛️ Cosmetics sources (panel, Marketplace Management)

- **Enable legacy cosmetics** (default on): a new packs.json knob `enableLegacyCosmetics` hot-reloaded with the rest of the config. OFF makes `GetLocalPersonaBinaries` return empty — the offer's binary snapshot, the warm-up scope, the dead→alive bridge, the probe summary and `PersonaCdnController`'s disk-first mirror hit all behave as if the mirror dir were empty (pieces without a live-synced binary vanish from the dressing room; custom cosmetics and built-in defaults never affected). The knob state and `PersonaPackGate.ConfigStamp` joined the binary-snapshot change signal, so a panel Save refreshes the offer on the next request — no restart.
- **Enable marketplace sync** (default on): a new config.json field `marketplaceSync`. OFF skips the scheduled sync start (`EnsureScheduledSyncStarted`) and makes `SyncNowAsync` refuse with a clear `LastError` (both "Sync now" buttons show why) — the crawl stops, the cached catalog and mirrored binaries keep serving. Independent of `LivePersonaCatalog` (the master serving switch, which must stay on for the sync to run at all).
- The card's Save writes packs.json as a read-modify-write (never clobbers the Availability card's unsaved edits or hand-edited keys like `maxGeometryFormatVersion`) and mirrors both values into config.json. The Availability card's own Save now carries `enableLegacyCosmetics` too (the PacksConfig record gained the field).
- Docs refreshed: `docs/marketplace-management.md` (the new card), `KONTEKST.md` (sections 3/5/9).

## Unreleased (dev) — the frozen offer cache found (the field "filler doesn't work / no face cosmetics" root cause), flatOnly now flattens instead of hiding, every category fills to the limit, the baseline defaults are locked in

> The follow-up to the streaming warm-up: the field logs showed the filler appending 223 candidates and the warm-up landing 759 binaries — yet the offer stayed at 257 pieces, the filler pool at 255 candidates for minutes, and the client's mouth/eyes tabs got "matched no items". **Root cause: the offer's hidden-id cache keyed on the binaries DIRECTORY mtime, and on the field install's Docker volume that mtime NEVER MOVED while files landed** — the whole mirror grew invisibly behind a frozen cache. The cache now keys on an explicit landing counter (`MarkBinaryLanded()`, called by every path that writes a piece binary — warm-up, lazy fetch, mirror import), so the offer, the filler pool and every category tab refresh the moment a binary lands. **Second root cause, the empty face tabs: Flat-only mode UNCONDITIONALLY hid every custom-bone piece — and nearly every head/eyes/mouth/face-accessory piece carries custom bones**, so those categories could never list anything. A stripped piece IS flat: with the strip active the gate now treats custom-bone pieces as qualifying (they serve rewritten, rigid, rest pose preserved), and only pieces the rewriter cannot fix still hide. The grouped limit (default 45) then fills EVERY category — staticdata/customs first, pack-selected pieces, then the deterministic filler pool (every mirrored qualifying piece, binary-on-disk enforced) — exactly the requested "all categories filled to the limit with qualifying cosmetics". **Baseline defaults locked**: category limit 45, onlySupported ON, fill blank slots ON, auto-repair OFF, customSkeletonMode = FlatOnly (the packs.json key default; an explicit "off" still disables it).

### 🧊 The frozen offer cache (the actual "filler doesn't work" bug)

- `LivePersonaCatalog.BinaryIdsSnapshot` keyed the servable-binary set on `Directory.GetLastWriteTimeUtc(BinariesDir)` — on the field install that timestamp never changed while the warm-up downloaded hundreds of files (Docker volume), so `GetHiddenItemIds` returned the pre-warm-up set forever: offer=257, filler pool=255, face tabs empty, everything the pass landed invisible until restart. A new `_binaryLandedVersion` counter (bumped by `MarkBinaryLanded()` from the warm-up save, the lazy download saves and the mirror import) now participates in the cache stamp; the dir mtime stays as a secondary signal for manual file drops.
- Field symptom to watch for: "Blank-slot filler pool rebuilt: N candidate piece(s)" with an IDENTICAL stamp across many minutes while the warm-up logs progress — means the cache is not seeing landings (fixed by the counter).

### 😀 Flat-only flattens instead of hiding (face tabs live)

- `PersonaPieceTypes.EvaluateCosmetic`: the `hideCustomSkeleton` rule now yields to the serve-time strip — with the transform active, custom-bone pieces of EVERY type return `RepairedRig` (serve rewritten rigid) instead of hiding; the hide remains only when the transform is off (defensive; FlatOnly implies the transform). Face tabs (head/eyes/mouth/face_accessory) fill from the mirror because their pieces' custom bones stop being a listing block.
- Unchanged: particles, render controllers, missing texture binding, format cap, oversized head bounds and face-bone face accessories still hide — the rewriter cannot fix those.

### 🔢 Baseline defaults

- `PersonaPackGate.ParseCustomSkeletonMode` defaults to FlatOnly (packs.json missing the key; explicit `"off"` still means off), matching the requested baseline: limit 45, onlySupported ON, fill ON, auto-repair OFF, skeleton handling Flat only.
- `docs/marketplace-management.md` and `KONTEKST.md` refreshed: the knobs' current semantics, the limiter/filler mechanics, the frozen-cache lesson and its log symptom.

## Unreleased (dev) — the CDN truth measured (48% alive, only on the new content hosts), the warm-up streams (binaries land in minutes, not hours), the filler works again, the rig rewriter strips animations and covers every piece type, the Apace Cape is removed, KONTEKST.md maps the whole repo

> The "why are there so few binaries when thumbnails work?" investigation. (1) **Measured live (300-item sample spread over the whole catalog): 48.3% of the resolved pieces download — but ONLY via `content1/content2.prod.catalog.playfab.com` (the new PlayFab content CDN, newer republications). Every xforge-hosted item (155/300) is dead on the primary AND on all alternate URL shapes (0 revived) — the 2021 content is really gone; thumbnails survive because they ride a separate lazy lane (live URL → the Bedrock-Persona GitHub mirror → placeholder).** The previous warm-up resolved ALL URLs first (paced API calls, with the all-or-nothing GetItems bisecting stale ids into paced single calls) and only THEN started downloading — the first binary landed after the whole resolve phase, so the dressing room grew from the old local mirrors alone (~30 pieces in the field logs) while the pass was "still working". The pass is STREAMING now: one 20-id chunk resolves, its downloads dispatch immediately, the next chunk resolves while those download — binaries land from the first minute and the offer grows continuously. The download path also walks the alternate CDN shapes on 404 like the lazy client path always did, and a definitive all-shapes 404 no longer retries 3×. (2) **"Fill blank slots with random cosmetics" works again**: the previous turn's selected-bundles restriction made the pool a subset of the pack-filtered offer (30 candidates in the field logs) — it could never add anything. The pool is every mirrored accessory again (still binary-on-disk and onlySupported gated), and the warm-up's download scope follows the same switch: OFF → selected bundles + skins only, ON → everything (so the filler actually has random pieces to fill with). (3) **The rig rewriter (r3)**: animations are now REMOVED — geometry `animations` blocks deleted and `*.animation.json` payloads blanked (`{"format_version":"1.8.0","animations":{}}`, the entry stays so manifests keep resolving) — and pieces flagged risky ONLY for animations serve static instead of hiding. Non-head pieces with custom bones (which used to serve RAW and glitch) become repair candidates too: rigid strip into the nearest standard ancestor. Head-family re-parenting now refuses posed subtrees: a bindable bone's rotation starts APPLYING around its pivot once bound (before the repair the client ignored it), so a rotated custom subtree takes the rigid strip path instead — the "całkowicie psuje geometrię" class. Marker r2→r3 forces clients to re-fetch rewritten pieces. (4) **The Apace Cape is removed** (user decision: it never equipped on a device and the receipt-keyed custom-piece path is not worth chasing): item JSONs, piece zips, thumbnails and the hardcoded `ApaceEntitlements` receipt grants are gone; the container/receipt knowledge stays in the docs. (5) **`KONTEKST.md`** — a Polish end-to-end guide to the whole repo: architecture, the persona pipeline with the measured CDN facts, pack gating + filler, the r3 rewriter, receipts, panel, the mobile launcher, CI, decisions and open threads.

### 📡 The measured CDN truth

- Live probe (25.11): 300 PersonaDurable items sampled across skip=0..9000 of the catalog → 300 resolved URLs, **145 alive (48.3%)**, ALL on `content1/content2.prod.catalog.playfab.com`; 155 xforge items dead on primary + alternates; 0 with missing Contents. The dressing room's ceiling is therefore ~48% of the catalog unless a source revives — and the warm-up now gets every piece of that 48% within minutes instead of stalling in its resolve phase.
- `LivePersonaCatalog.WarmUpDownloadAsync` walks `CandidateDownloadUrls` (resolved URL first, then the `AlternateCdnUrls` shapes — deduplicated) before recording `warmup-404`; a definitive all-shapes 404 does NOT retry (it did — up to 9 requests per dead piece).

### 🪞 Streaming warm-up + working filler

- `RunWarmUpPassAsync` resolves one 20-id `Catalog/GetItems` chunk and immediately dispatches its downloads (bounded 6 at a time, per-item locks, verdict recording) before the next chunk — progress logs every 50 pieces as before.
- `CatalogController.RefreshFiller` builds the pool from every mirrored non-skin persona piece (binary-on-disk + onlySupported as before, sorted by item id for stability) when `PersonaPackGate.FillBlankSlots` is on, and leaves it EMPTY when off; the field symptom (30 candidates from the selected bundles — a subset of the pack-filtered offer that could never fill anything) is gone.

### 🔧 Rewriter r3 (animations stripped, every piece type covered)

- `PersonaPieceTypes.EvaluateCosmetic` with the transform on: `hasAnimations` no longer hides — the verdict reports `RepairedRig` (the rewriter blanks them); custom bones report `RepairedRig` for NON-head piece types as well (they used to serve raw); hair keeps its ponytail exemption (bones untouched, animations still stripped); particles, render controllers, texture-binding issues, face-accessory face bones, oversized head bounds and the format cap still hide.
- `PersonaRigRepairer`: `RewriteLayer` blanks `*.animation.json` payloads (keyed entries re-encrypt with their original key so contents.json validates); `PatchGeometry`/`StripGeometry` remove root-level `animations` blocks; `PatchGeometry` runs `SubtreeHasRotations` first and takes the rigid strip path for posed subtrees (`HasRotationProperty` handles arrays `[x,y,z]`, scalars and molang strings); non-head pieces strip rigid via the existing `StripBoneList` machinery. Cache keys and ETags move to the `r3`/`r3-strip`/`r3-flat` markers.

### 🗑️ Apace Cape removed + KONTEKST.md

- `staticdata-extras/playfab/items/{1981967a,6e8671bd,a5d108fa}.json`, their three piece zips, the three thumbnails and the `ApaceEntitlements` receipt grants are gone; `docs/custom-persona-items.md` keeps the condensed container/receipt knowledge under "The Apace Cape (removed)".
- `KONTEKST.md` (repo root, Polish) — the full map: projects, the volume, the persona pipeline with the measured CDN facts, gating + filler, the rewriter, receipts, panel pages, the mobile launcher, CI, decisions and open threads.

## Unreleased (dev) — the offer lists ONLY pieces with a binary on disk, the warm-up mirrors the selected bundles in minutes, and the legacy 231 Microsoft pieces are gone from the repo

> The follow-up to the mirror-first session. (1) The warm-up hid probe-dead pieces, but the offer still listed every UNCHECKED piece — a client tapping one ran the whole lazy pipeline (resolve → probe → 404 → placeholder): seconds of "wczytywanie…" and a piece that renders nothing. `GetOffer`/`GetOfferItem`/`GetHiddenItemIds` now gate the offer AND every search on **binary-on-disk** (volume cache ∪ mojang-assets mirror): no file, no listing — the dressing room cannot offer anything the client could not equip. `GetHiddenItemIds` replaces `GetDeadItemIds` (cached per index + disk stamp, so the per-request cost is two stat calls, not a 12k-file scan). (2) The user's model is now literal: metadata for EVERYTHING from the live marketplace (the few-minutes crawl), binaries ONLY for the selected bundles — the warm-up resolves URLs in batches (`Catalog/GetItems`, 20 ids per paced API call, bisection when the all-or-nothing endpoint trips on a stale id) and downloads in parallel (6 CDN GETs at a time, no API pacer; the 404 on the download itself is the dead verdict), so a selected-bundle mirror takes minutes instead of hours. The blank-slot filler pool draws from the SELECTED bundles only (`PersonaPackGate.DefaultPackUuids`) — with nothing selected it is empty, because an unselected accessory has no binary and could not serve anyway. (3) The 231 legacy Microsoft persona item JSONs are deleted from `staticdata-extras/playfab/items/` (the Apace capes, the skin pack twins, the store/layout entries stay), the manifest's `persona` entries are stripped (the `mojang-persona-mirror` release never existed; the xforge URLs were all dead) and the 29 Microsoft-owned thumbnails the deleted items referenced are gone from `wwwroot/playfab/images/` — what remains serves the kept items. Everything in the dressing room now comes from the live marketplace, filtered by the selected bundles, plus the Apace-original pieces and customs.

### 🪞 Mirror-first offer (no listing without its binary)

- **`LivePersonaCatalog.GetHiddenItemIds`** — every indexed item without a non-empty binary on disk is offer-hidden, whatever its probe verdict says (dead, stale, unchecked — all identical to the client: a download that would hang). A locally mirrored file overrides any verdict, so a manual mirror still resurrects pieces instantly. The set is cached per `(IndexStamp, binaries-dir stamp, mirror-dir stamp)`; `GetOffer`, `GetOfferItem`, `LiveOfferItemsFor` and the filler all consult it.
- **`GetDeadItemIds` is gone** — callers hide by disk state, not by verdict freshness, so a piece whose binary lands (warm-up progress, a manual mirror) appears on the next search without waiting for a re-sync.

### ⚡ Warm-up at the user's pace ("pobieranie samych nazw i contents to max kilka minut")

- **`BatchResolveBinaryUrlsAsync`** — `Catalog/GetItems` with 20 ids per call (verified live: the batch form returns the same `Contents[0].Url` per item). The endpoint is all-or-nothing on unknown ids (verified: one stale republication id fails the whole batch), so a failing chunk bisects recursively and a single-id chunk falls back to the retrying per-item `GetItem` resolve — worst case the old pace, best case 20× fewer API calls.
- **Parallel downloads** — `WarmUpDownloadAsync` runs under the per-item lock (a client request still wins the race), retries 3×, records verdicts (`warmup` / `warmup-404`) and captures geometry compat like the lazy path; 6 downloads at a time and no API pacer (the content CDN is not the PlayFab API — the thumbnail path already worked this way).
- **Filler scope** — `RefreshFiller` now requires a theme tag inside `PersonaPackGate.DefaultPackUuids` (new sync accessor) besides the binary-on-disk rule: the filler can only ever draw from the selected bundles, matching the binary-download scope.

### 🗑️ The 231 originals are out of the repo

- **`staticdata-extras/playfab/items/`**: 231 Microsoft persona piece JSONs deleted; the 8 files that remain are the 3 Apace capes, the 3 skin pack twins (Standard / Default Skins / Apace Skins), the layout (`PersonaQueryManifest_V1.0`) and the store config — exactly the entries the live catalog cannot replace. Bold Rabbit Feet stays hardcoded in `CatalogController` (the shop needs it).
- **`mojang-assets.manifest.json`**: all 231 `persona` entries and the `personaMirror` source are stripped (the mirror release does not exist and nothing referenced it at runtime — only the `skin-texture` entries are consumed); the 9 vanilla skin textures for the Default Skins pack keep downloading from minecraft.wiki.
- **`wwwroot/playfab/images/`**: the 29 Microsoft-owned thumbnails the deleted items pointed at are removed; the 7 that serve the kept items (capes, skin packs, Bold Rabbit Feet) stay. Thumbnails for live pieces resolve lazily from the recorded URLs / the Bedrock-Persona mirror as before.

## Unreleased (dev) — every servable live-catalog piece is mirrored into the volume in the background (the upstream CDN is dying), and the patcher rejects non-original APK uploads instead of dying in apktool

> Two fixes from the 2026-11-25 session logs. (1) "Still can't equip the majority of things" — the cold fetch pipeline measured the truth: a random sample of the live PlayFab catalog probed **4 % alive / 96 % dead** (404 on the primary xforge URL AND on both `content1/2.prod.catalog.playfab.com` alternates, confirmed from a second network — the binaries are gone globally, not blocked per-IP; Microsoft is purging the shut-down 2021 title's content CDN). A lazy-only pipeline meets every never-before-fetched piece cold: seconds of "wczytywanie…" per tap and a placeholder that renders nothing — the reported "most things cannot be equipped". `LivePersonaCatalog.BinaryWarmUp` now mirrors every piece the binary path can serve (pack-selected accessories, filler pieces, skins, thematic skin packs) into the volume in the background: started from `Program.Main` + `ApplicationStarted` (idempotent, 60 s cadence, resumable via the persisted probe state — mirrored pieces and fresh verdicts are skipped, repeat passes are cheap), paced through the existing ≤2 req/s API pacer, re-running when the index stamp or the pack-gate config stamp changes. Live verdicts capture still-alive content permanently; dead verdicts drop pieces from the offer BEFORE the client ever sees them, so the dressing room lists only pieces that work. The header's old "deliberately NO bulk probing" rule is retired — it predates the CDN dying. (2) The patcher's input check (`ApkProcessor.VerifyApkHash`) only logged a `[WRN]` and continued — an already-patched APK (re-signed by any patcher) or a re-packed mirror decoded for minutes and died in apktool with a cryptic smali error (the field-reported `Interop.smali[788,4] missing EOF at '.param'`). The mismatch now fails the job IMMEDIATELY with the actual reason ("upload the original com.mojang.minecraftearth 0.33.0 APK"), on Android and iOS both; the ServerOptions toggle "Reject non-original APK/IPA uploads" (`PatcherRejectNonOriginalApk`, default ON) restores the old warning-only behavior for operators who knowingly patch a different build.

### 🪞 Background binary warm-up (mirror everything still alive)

- **`LivePersonaCatalog.EnsureBinaryWarmUpStarted`** — one loop per process (Interlocked-guarded), 30 s boot-settle delay, then a pass whenever `IndexStamp` or `PersonaPackGate.ConfigStamp` changed. Each pass enumerates the index (emotes skipped — never offered), skips pieces already served from a local mirror, already mirrored (`live` + file present) or freshly probed dead, and orders the rest by what the binary path can actually serve: skins (`persona_skin`), filler pieces (`FillerExemptItemIds`), accessories with a theme tag inside the allowed pack set — accessories outside the pack gate are refused by the pipeline anyway, so warming them is pointless. Thematic skin packs follow the pieces.
- **The per-item work reuses `TryEnsureBinaryAsync`/`TryEnsureSkinPackZipAsync`** — resolve → probe (primary + both alternate CDN hosts) → download into the volume cache → geometry compat capture, all under the per-item locks so a client request for the same piece always wins the race, all paced by the shared ≤2 req/s limiter. Progress logs every 50 pieces; probe state flushes as it goes and at pass end; a failed pass is logged and the loop keeps running.
- **Effect on the dressing room**: pieces the CDN still hosts become mirrored on first run (equips get instant, mirror-first), pieces it has lost leave the offer via the existing dead-verdict drop before any client sees them — "can't equip" stops being a loading stall and becomes an honest, working list.

### 🛡️ Patcher: non-original APK uploads are rejected up front

- **`PatcherService.RunAndroidAsync` / `RunIosAsync`** — `VerifyApkHash`/`VerifyHash` mismatches now `throw InvalidOperationException` with a plain-language message (upload the original 0.33.0 APK; already-patched APKs cannot be re-patched; the check can be disabled in settings) instead of warning and letting apktool fail 4 minutes later at smali build. The old `[WRN]` line remains for operators with the toggle off.
- **`Settings.PatcherRejectNonOriginalApk`** (default true, null-normalized in `LoadAsync`) + the ServerOptions Data tab checkbox "Reject non-original APK/IPA uploads" with an explanation of what a mismatch means.

## Unreleased (dev) — skin packs a mangled staticdata copy strands become equippable again, and a failing persona fetch never answers an error

> Two repairs for the "some cosmetics can't be equipped" family (the 2026-11-25 session log: the Classic Skins pack-tag search returning empty right after the pack zip downloaded, and a persona piece fetch failing with retries). (1) The built-in skin pack fallback (baabaea) only fired when the deployed staticdata did not carry the pack item id at all — a twin that EXISTS but is defective (an operator edit through the panel's file browser, a hand-copied older JSON: wrong content type, the packIdentity uuid tag stripped, displayProperties no longer advertising the packIdentity) was skipped by the id-presence guard while the client's hardcoded `MarketplaceDurableCatalog_V1.2` tag search (`acf11539-…` for Default Skins) matched nothing — the pack imports but its skins cannot be selected or equipped. Defective twins are now detected by a health check (content type + the pack uuid tag + the displayProperties packIdentity uuid), excluded from every serve path (catalog search source, GetPublishedItem, the featured-row merge) and replaced by the code-defined offer, which is logged once per pack. (2) The persona binary endpoint promised "a piece uuid never 404s and never errors" but the lazy live pipeline could still throw — `ResolveBinaryUrlAsync`/`DownloadBinaryAsync` rethrow after 3 failed attempts, the controller had a `try/finally` with no `catch`, and there is no exception middleware: the client got an HTTP 500, retried until its own timeout, and the verdict stayed "live" without a file so every retry re-ran the whole ~30 s pipeline. `TryEnsureBinaryAsync` now degrades to `false` on any non-cancellation failure (warning logged, probe verdict untouched so a transient CDN hiccup recovers), and the controller adds a belt-and-braces catch — every failure path ends in the deterministic placeholder zip, which imports instantly and unblocks the equip.

### 🎒 Built-in skin pack offers repair defective staticdata twins

- **Health check** (`CatalogController.IsHealthyBuiltinSkinPackTwin`): a staticdata twin of a `BuiltinSkinPackItems` offer is healthy only when it is a `ClientConfigData` with `ContentType "MarketplaceDurableCatalog_V1.2"`, its tags carry the packIdentity uuid tag (`Tags[1]` of the builtin — kept in lockstep by construction), and its displayProperties still contain a `packIdentity` entry with that same uuid (parse failures count as defective). Anything else matches no client search and strands the pack exactly like a missing item.
- **Fallback coverage** (`CatalogController.BuiltinSkinPackFallbackItems` + new `DefectiveBuiltinSkinPackIds`): the code-defined offer now fills in ids missing from staticdata AND ids carried in a defective shape, logged once per pack ("is missing/defective in the deployed staticdata — serving the code-defined fallback offer…"). Healthy staticdata twins keep serving verbatim — staticdata stays the source of truth.
- **Defective twins serve nowhere** (`BaseOfferItems`, `GetPublishedItem`, `SkinPackOfferIds`): the broken copy is excluded from the composed catalog search source, `GetPublishedItem` resolves the repaired code-defined offer instead of the mangled shape, and the featured-row merge lists only the fallback — no path can serve the twin the client cannot resolve.

### 🧱 Persona piece fetches degrade to the placeholder, never to an error

- **Pipeline contract** (`LivePersonaCatalog.TryEnsureBinaryAsync`): the gated body catches everything except caller cancellation and returns `false` with a warning ("Live persona fetch failed for {Item} after all retries — the controller serves the placeholder piece instead of an error"). The probe verdict is intentionally untouched: a "live" verdict without a file re-runs the pipeline on the next request, so a transient CDN hiccup still recovers, while the current request terminates with an importable zip instead of a 500 that the frozen client would retry until its own timeout.
- **Controller safety net** (`PersonaCdnController.Get`): the live-fetch block (now with the entitlement check inside the `try`) gains a `catch` that logs and falls through to the placeholder — plus the existing `OperationCanceledException` rethrow when the client aborted. Together with the pipeline catch this makes the standing "a piece uuid never 404s there" promise literally true: every failure path ends in the deterministic placeholder.

## Unreleased (dev) — the server waits for the resource pack, asks for a map key before starting, and auto-detects the LAN IP (127.0.0.1 inside the mobile app)

> Three start-flow fixes. (1) The server used to boot while the ~131 MB vanilla resource pack was still downloading — the EULA prompt could appear before the pack had even started: the pack is now a hard start prerequisite (Start All auto-downloads it and waits, even with "Skip file validation" on, and the per-component ▶ buttons apply the same gate). (2) Starting with tile rendering enabled but no MapTiler key made the Tile Renderer exit fatally right after start: every start entry point now shows a "No map provider key" popup first (Take me there / Start anyway / Never show again). (3) Fresh installs prefilled the fake `192.168.x.x` placeholder as the public address: the real LAN IPv4 is now auto-detected (UDP route probe + interface enumeration, docker/veth/VPN adapters skipped), and installs still carrying the literal placeholder get it upgraded once — any user-set address is never touched, including on redeploys. Inside the mobile app the default is `127.0.0.1` (localhost) instead of any LAN address: the patched client runs on the same phone as the server, so a Wi-Fi/cellular address is never right there (detection: the `APACE_MOBILE=1` env marker set by the launcher entrypoint, plus a non-zero-TracerPid PRoot-ptrace fallback for older launcher builds).

### 🚀 Server start (resource pack gate)

- **Awaitable in-flight download** (`ResourcePackDownloader.cs`): `DownloadAsync` publishes its session task while holding the existing semaphore (one download at a time is unchanged — concurrent `DownloadAsync` callers still get the friendly "already in progress" result); the new `WaitForCurrentDownloadAsync()` lets the start flow wait for a session started by the panel button or another tab, and `CurrentProgress` exposes the live 0..1 progress of the current/last session.
- **`FileChecker.EnsureResourcePackReadyAsync`** (`FileChecker.cs`): cheap no-op when the pack validates; awaits an in-flight download; otherwise starts the exact code path of the panel's Download button and waits. On failure it throws the short fatal "Resource pack download failed — check your connection and try again." `CheckAsync` now calls it where the pack used to be merely "optional, flagged, non-fatal" — so the first java boot (and with it the EULA prompt) can only happen after the pack is fully in place.
- **Gate outside `SkipFileChecks`** (`ServerManager.cs`): `StartInternal` calls the ensure before `FileChecker.CheckAsync`, so the gate also holds when file validation is skipped; a failure aborts the start with `Offline`. New `StartComponents` (the panel's ▶ buttons) applies the same gate in front of `EnsureComponentsOnline` — the page-init callers of `EnsureComponentsOnline` (Buildplates etc.) stay ungated so opening a page never triggers the 131 MB download.
- **Panel** (`Components/Pages/ServerStatus.razor`): the progress bar moved out of the "Resource pack missing" alert and now animates for ANY running download (panel button, start flow, another tab — polled from `FileChecker.ResourcePackDownloadProgress` every 2 s) with a "Waiting for the resource pack download… X%" status line and the existing Cancel link; the alert copy no longer claims the server can start without the pack.

### 🔁 Two-phase start: consent question first, "press Start again" after the download

- **Consent question restored on the start path** (`Components/Pages/ServerStatus.razor`): a new OUTER `RunWithResourcePackConsent` guard (wrapping the map-key guard) sits in front of EVERY start entry — Start All, Restart, the per-component ▶ and both no-packs-popup buttons. While the pack is missing, no download is running and the rights are not confirmed for this panel session, the start is parked and a modal ("Resource pack required", same overlay pattern as the map-key popup, works in the Android WebView) asks the EXISTING rights question ("I confirm I own a licensed copy of Minecraft: Earth / Bedrock and accept the applicable Terms"). "Confirm & download" only fetches the pack (drives the shared progress bar and error text — it never starts the server); "Cancel" just dismisses. The parked start action is stored but intentionally never invoked: starting always requires a fresh, deliberate Start click.
- **Two-phase start** (`ServerManager.cs`): both `EnsureResourcePackReadyAsync` call sites (`StartInternal`, `StartComponents`) capture whether the pack was missing before the gate and, after a successful ensure, STOP there instead of booting — `Offline` / `false` plus the INFO line "Resource pack downloaded — press Start again to boot the server." So the flow is: consent → download → the user presses Start again → only then file validation and the EULA prompt run. Holds for every start path and despite `SkipFileChecks`, since the gate sits in front of `CheckAsync`; a pack that was already valid skips the extra click entirely.
- **"Downloaded — press Start again" banner + failure surfacing** (`Components/Pages/ServerStatus.razor`): the 2 s poll detects the download's true→false transition — on success a green `alert-success` banner tells the user to press Start again (also set by the manual Download button's success branch; cleared by any real start attempt, the banner's "Not now" link, or a new download), on failure the resource pack alert shows "Resource pack download failed — see the Live Logs for details." unless a more specific reason is already displayed.
- **`FileChecker.ResourcePackValid`** (`FileChecker.cs`): new public accessor over the private stat-based `ResourcePackFileIsValid()`, so the page can gate starts cheaply (and the guard does not depend on the dismissible `ResourcePackPending` flag).

### 📱 Mobile home title

- **Home page hero** (`Components/Pages/Home.razor`): the home page `<h1>` reads "Apace Mobile Launcher" when the panel runs inside the Android launcher app (`LanIp.IsMobileLauncher` — the `APACE_MOBILE=1` marker or the TracerPid fallback), and stays "Apace Launcher" everywhere else. Both the signed-in and signed-out hero branches are covered; the browser tab title, the sidebar brand and every other page are untouched — a desktop user can never see the mobile title (`IsMobileLauncher` is computed once and false on any detection failure).

### 🗺️ Map provider key popup

- **Setting** (`Settings.cs`): new `MapKeyPopupSuppressed` (persisted in config.json, mirrors `PersonaNoPacksPopupSuppressed`).
- **Popup + guard** (`Components/Pages/ServerStatus.razor`): a modal copying the no-packs popup ("No map provider key", provider-specific body, Take me there → `/Admin/ServerOptions?tab=map`, Start anyway, Never show again), shown by one shared `RunWithMapKeyGuard` from every start entry point — Start All (including the no-packs popup's own start buttons), Restart, and the per-component ▶. Condition: tile rendering not explicitly disabled (`EnableTileRenderingLabel == false` skips — null means enabled, same as the ServerManager component filter) AND the selected source's credential missing (MapTiler → empty key, PostgreSQL → empty connection string) AND not suppressed.
- **Save no longer wipes unrelated settings** (`Components/Admin/Pages/ServerOptions.razor`): `HandleSave` mutates `Settings.Instance` in place (like PersonaPacks/ServerStatus) instead of building a fresh `Settings` — a fresh object silently nulled every field the page doesn't show, so each options save reset the persona pack selection and both popup suppressions.

### ⚙️ LAN IP default

- **`LanIp.cs` (new)**: `DetectLanIPv4()` — UDP connect to 8.8.8.8:80 (no packet sent, the OS picks the route), fallback to the first Up non-loopback IPv4 interface skipping docker/veth/br-/vEthernet/tun/tap, successful detections cached for the process lifetime; inside a docker container (`/.dockerenv`, the same marker SelfUpdateService uses) it returns null — a container-internal address is never reachable by clients and would be persisted forever by the config re-save. New `IsMobileLauncher` (computed once, cached): true when the panel runs inside the Android launcher app — the `APACE_MOBILE=1` env marker set by the launcher's run-inside.sh (v1.1.12+) or, for older builds, a non-zero `TracerPid` in /proc/self/status (PRoot is ptrace-based and traces every process it runs; unreadable/unparseable counts as not mobile). On mobile `DetectLanIPv4()` returns null — the patched client runs on the same phone, so a Wi-Fi/cellular address is never the right default.
- **Settings** (`Settings.cs`): `Default.IPv4` is `127.0.0.1` on mobile (the client runs on the same phone — localhost is the only stable address) and the detected LAN IP on desktop (fallback `127.0.0.1`); `LoadAsync` upgrades ONLY the literal legacy placeholder `"192.168.x.x"` — to the detected address on desktop, to `127.0.0.1` on mobile (persisted by the existing re-save) — any other value, including every user-set address, is preserved verbatim on every boot, redeploy and self-update (a mobile install that already picked up a LAN IP from the previous dev build keeps it; the user can set `127.0.0.1` manually or reinstall).
- **Cosmetics** (`ServerOptions.razor`, `Installation.md`): the IPv4 field placeholder reads "auto-detected LAN IP" and the install doc describes the auto-detection (and that docker installs must set it manually).

## Unreleased (dev) — the APK patcher survives CPUs the bundled aapt2 cannot run on: a pre-flight probe switches that build to the system aapt2

> The patcher job died at `I: Building resources with aapt2...` on the user's phone: apktool 3.0.3 extracts the aapt2 binary bundled in its own jar and executes it, and on CPUs older than that binary's baseline it dies with exit code 132 (SIGILL) mid-build (a Raspberry Pi runs the same binary fine). The build now probes the bundled binary with the `version` subcommand BEFORE the resource build and, on any non-zero exit, switches this build to the distro aapt2 (`--aapt /usr/bin/aapt2`, generic ARMv8-A baseline). apktool 3.0.3 has no aapt1 mode (`--use-aapt1` is rejected as an unknown option), so `--aapt` is the only aapt-selection mechanism it offers — hence aapt2, not `/usr/bin/aapt`. Healthy hosts pass the probe and keep the byte-identical bundled invocation; hosts with `AAPT2_PATH` set (the Docker image) never run the bundled binary and skip the probe entirely.

### 🛠️ Patcher (Android)

- **Pre-flight** (`Minecraft_Earth_Patcher/APK.cs`): `Encode` extracts the bundled aapt2 (`prebuilt/linux/aapt2` inside apktool.jar) to a temp file and runs it with `version` — the same probe apktool itself performs (aapt2 has no `--version` option); a non-zero exit or exec failure falls back, and exactly one INFO line states the decision (`using bundled aapt2` / `using system aapt (fallback)`).
- **Fallback binary present** (`Dockerfile`, `install-termux.sh`): the image already installs the `aapt` package (Ubuntu, amd64+arm64) which provides `/usr/bin/aapt2` — the build now hard-fails at image build time if it is missing; the Termux proot Ubuntu (where the phone SIGILL happens) installs `aapt` too.

## Unreleased (dev) — the pack gate covers every live accessory: partner-creator pieces no longer bypass it and probe-dead pieces leave the searches at once

> With NOTHING selected the dressing room still served ~30 live accessories per category: the pack gate applied only to pieces whose marketplace `creatorName` is "Minecraft", so partner-creator accessories (the sync stores the creator verbatim and the 20ca2 catalog carries partner items) bypassed it entirely. The gate now runs on all live accessories (any creator, skins exempt) at the offer, published-item and binary paths — customs keep serving untouched. Probe-dead pieces also vanish from the catalog searches immediately (they used to keep listing until the next index re-sync, so the client rendered broken "unsupported" tiles whose binaries 404).

### 🧍 Persona items (dressing room / skins)

- **Catalog offer** (`CatalogController.LiveOfferItemsFor`): the `CreatorName is "Minecraft"` condition is gone — every live accessory (any creator) needs the allowed set non-empty AND a matching theme tag (tagless accessories never serve; `persona_skin` exempt); pieces in `LivePersonaCatalog.GetDeadItemIds()` skip the offer per request.
- **Equip resolve** (`CatalogController.GetPublishedItem`): the same de-creator-fication on the live path — a gated accessory or a probe-dead piece resolves NotFound like an unknown id instead of advertising a broken tile.
- **Binary lazy path** (`LivePersonaCatalog.TryEnsureBinaryAsync`): the allowed-set check no longer requires `CreatorName is null or "Minecraft"` — partner- and creatorless accessories refuse at binary time too (skins exempt; customs and mirror files never gated).
- **Diagnostics**: per-search Debug line with the allowed-set composition (`PersonaPackGate.AllowedCompositionAsync` — defaults ids + grants, reusing the existing loads) plus offer/passed/dropped/dead/limit counts, and a verdict line on the published-item live path.
- **Panel**: the Marketplace Management and per-player packs pages gained a hint stating when changes reach players (server immediate vs. next dressing-room load — game restart).

## Unreleased (dev) — iOS patcher fixed: the IPA is written intact, the PlayFab URL builders are redirected and the title API routes exist

> iOS clients died at sign-in with ZERO requests reaching the server: the vendored patcher corrupted the output IPA (`ZipArchiveMode.Update` rewrote the entry headers in front of the first removed entry while the data stayed put — every entry after it was garbage) and three PlayFab URL-builder sites that build `"https://" + titleId + ".playfabapi.com"` were never redirected, so the iOS title API calls went to `:///<titleId>.playfabapi.com/`. Ported from the community PR Earth-Restored/Minecraft_Earth_Patcher#4.

### 🔐 Account login

- **Corrupt-IPA fix** (`Minecraft_Earth_Patcher/IpaProcessor.cs`): the output IPA is written as a FRESH archive (open the original with `ZipFile.OpenRead`, write a new `ZipArchiveMode.Create` stream) — every original entry is copied with its `ExternalAttributes`/`LastWriteTime`, directory entries and the `_CodeSignature`/`SC_Info`/`Zachary@Cracks` deletion prefixes are skipped, and the patched files (`Info.plist`, the `minecraftearthtf` exe, the `Xal` framework binary, plus the added `Entitlements.plist`) are substituted from the work dir.
- **PlayFab title-API redirects** (`IpaProcessor.cs` `EndpointSites`): the three URL-builder sites (0x6D66A8, 0xB668A8, 0x259E628 — they patch the `"https://"` prefix of `"https://" + titleId + ".playfabapi.com" + path`) join the playfab group; with an empty Tail the slot writes as `{scheme}://{hostname}/`, so iOS requests land on `<hostname>/<titleId>.playfabapi.com/<path>`.
- **Unsigned-IPA warning + README**: the patcher warns that the IPA is still unsigned until the user signs it (keychain groups `<TEAMID>.com.microsoft.xal` / `.com.microsoft.adalcache`; the existing `AddXalEntitlements` option already writes `Entitlements.plist` for the re-signing step), and the README gained an iOS section with the `codesign -d --entitlements - Payload/minecraftearthtf.app` check.
- **Server routes** (`Controllers/PlayfabApi/*`): every PlayfabApi controller gained a second literal route `1712756451.playfabapi.com/<Area>` (Authentication, Object, Client — on both LoginController and ClientController — Catalog, inventory, Event): the patched iOS build hits the real title id prefix instead of Android's `20CA2`; handlers unchanged (they are title-agnostic).

## Unreleased (dev) — staticdata accessories are gone for good: the frozen 2021 catalog no longer leaks when the live index is missing

> With no packs selected the client still got ~45 staticdata accessories per dressing-room category: the frozen 2021 staticdata catalog (`BaseOfferItems`) was only filtered when the live persona index exists, so on installs without `persona-catalog/index.json` every legacy accessory leaked. Staticdata persona items carry NO pack tags, so under the pack gate they can never be allowed — they now drop at every serve path, live index or not. The exemptions keep serving: Bold Rabbit Feet, the Apace capes (creator KotPasztet), the panel's custom cosmetics (never gated) and the Default Skins pack (non-PersonaDurable, untouched).

### 🧍 Persona items (dressing room / skins)

- **Catalog offer** (`CatalogController.BaseOfferItems`): the `LivePersonaCatalog.IsAvailable` branch is gone — the Minecraft-creator `PersonaDurable` drop is unconditional (Bold Rabbit Feet and non-Minecraft creators stay exempt).
- **Equip resolve** (`CatalogController.GetPublishedItem`): the staticdata path applies the same rule — a staticdata Minecraft-creator accessory 404s like a non-selected live one (capes survive via their creator, skins via their piece type).
- **Saved profiles** (`ObjectController.SanitizePersonaObjects` + `PersonaPieceTypes`): the pack rule no longer requires a live item — `StaticPieces` now records the `PersonaData` creator (default "Minecraft", matching `CIItemToItem`) and the resolver surfaces it on `ResolvedPiece`, so staticdata Minecraft accessories drop on save too (the staticdata persona_skins and the KotPasztet capes keep).
- **Binary endpoint** (`EarthApi/PersonaCdnController`): a zip whose id resolves to a staticdata Minecraft-creator accessory refuses before the mirror/staticdata dirs are consulted — gated against the unauthenticated-endpoint allowed set (`PersonaPackGate.AllAllowedPackUuidsAsync`, staticdata carries no tags so no selection covers it); live-mirrored zips stay governed by the lazy-path gate; refusals log once per file per run.

## Unreleased (dev) — iOS sign-in works: the XAL sisu flow is served

> iOS clients died at "something went wrong during sign in" right after device auth: XAL signs in through the `sisu.xboxlive.com` web-view flow (not the Android device/title/user/XSTS requests) and the server had no sisu endpoints at all. Ported from Solace PR #50 (commit 7c53f66, tested by its author to the map on a patched 0.33.0 iOS build), adapted to Apace's hex account ids and config-based secrets.

### 🔐 Account login

- **SisuController** (new, `Controllers/XboxLive/Auth/SisuController.cs`): `sisu.xboxlive.com/authenticate` + `/authorize` return the full device/title/user/XSTS token set, `/client` and logout hand XAL back to the app; sisu responses use relaxed JSON escaping (iOS XAL chokes on `\uXXXX`).
- **`oauth20_token.srf`** (`LoginController.cs`): auth-code and refresh-token exchange; `oauth20_logout.srf` redirect added.
- **login.html**: `completeSignIn` passes the Live user token back through the OAuth redirect.
- **XUID as uint64** (new `Utils/XuidUtils.cs`, wired into Profile/Presence/Privacy): iOS expects a numeric XUID — derived stably from the account id, both formats accepted everywhere.
- **Title id 1712756451** (`TitleMgtController.cs`): `current` and the iOS title id now serve like the Android endpoint (iOS reads it from xboxservices.config).
- **Device/Title auth loosened**: `RpsTicket`/`SiteName` are optional (iOS omits them); XSTS relying parties normalize server-prefixed values. Android behavior unchanged.
- **Patcher** (`Minecraft_Earth_Patcher/IpaProcessor.cs`): patched IPAs now carry `Entitlements.plist` with the `com.microsoft.xal` + `com.microsoft.adalcache` keychain access groups (signature stripping had destroyed them — XAL failed before sending anything). The ATS exceptions and `*.playfabapi.com` redirects the PR also needs were already in the patcher.

## Unreleased (dev) — Minecraft Default Skins restored: the skin textures fetch and the pack zip rebuilds automatically again

> Since the Mojang asset gate removal nothing downloaded the nine default skin textures — `MojangSkinPackBuilder` bailed on the empty `skins/` dir, the pack zip never existed and the dressing room's "Minecraft Default Skins" rows went empty (the offer served, its `skinbinary` 404ing). A new `MojangSkinTextureFetcher` post-processor step (registered before the builder) now fetches the verified `skin-texture` entries from `mojang-assets.manifest.json` (sha256/size-verified, atomic `.part` → move, files already present are skipped, failures logged and never fatal) into `<data>/mojang-assets/skins/` at every panel start, so the zip builds and serves again. The pack version bumps 1.0.1 → 1.0.2 (builder constant + the offer's `packIdentity[0].version`) because the client caches imports per (uuid, version) — installs that once imported the pack empty keep it cached forever otherwise. `SkinPackCdnController` now names a known staticdata skin-pack offer (offer id + expected path + the texture-fetch hint) in its 404 warning instead of a generic line.

### 🧍 Persona items (dressing room / skins)

- **Skin texture fetcher** (`MojangSkinTextureFetcher.cs`, panel): resolves the manifest the same way the old asset gate did (assets-dir override, deployment `staticdata/`, dev-checkout `staticdata-extras/` walk-ups, the configured staticdata dir — CWD and `AppContext.BaseDirectory` anchors), downloads the verified `skin-texture` entries whose target is one of the pack builder's skins (`MojangSkinPackBuilder.DefaultSkinNames`) with the gate's HTTP idioms (primary URL → fallback mirrors, 3 attempts, sha256 + size check, `.part` file moved into place), logs a fetched/skipped/failed summary and, on any failure, that the pack cannot be built until the fetch succeeds. Best-effort and idempotent per the `MojangAssetPostProcessor` contract.
- **Pack version 1.0.2** (`MojangSkinPackBuilder.PackVersion` + `staticdata-extras/playfab/items/90860f69-….json`): forces clients that cached the empty import to re-import; the builder's "pack not built" warning now says the dressing room rows stay empty until the texture fetch succeeds (naming the `skins/` dir).
- **Diagnostics** (`SkinPackCdnController.Get`): a missing zip that belongs to a staticdata skin-pack offer (the `CatalogController.SkinPackOfferIds` set) logs the offer id, the expected `<mojang-assets>/playfab/skinpacks/` path and the default-skins texture-fetch hint, so operators see exactly why the rows are empty.

## Unreleased (dev) — the pack picker lists accessory-bearing bundles only

> Selecting a skin-only bundle did nothing: since skins serve ungated, the picker's classic skin-pack rows were pure noise.

### 🧍 Persona items (dressing room / skins)

- **Skin-only bundles hidden** (`PersonaPackIndex.ReadThemePacks` + `PackPicker.razor`): the picker lists only theme-tag bundles carrying at least one ACCESSORY — tags whose pieces are all persona_skin/emotes get no row, and the classic skin-pack rows are gone entirely (with their `IsSkinPack` flag, "classic" badge and skin-count noun); the summary's N counts accessory-bearing bundles only.
- Saved defaults/grants referencing skin-pack uuids stay harmless leftovers (skins serve ungated): they render as warning chips under their raw id, as the picker already did for stale entries.

## Unreleased (dev) — strict accessory enforcement: the dressing room serves accessories ONLY from the selected packs; skins un-gated

> Pack selection was not actually enforced: every live piece without a theme tag slipped through the gate unchanged, so dressing-room categories filled with ~30 accessories from ALL packs instead of only the selected bundles, and the classic skin packs resolved only when a selection contained their exact pack uuid — effectively never ("skiny nie działają"). The gate is now strict for accessories (catalog offer, equip resolve, saved profiles AND the physical binary download) and gone for skins; the category limit defaults to 45 and the picker shows real accessory counts.

### 🧍 Persona items (dressing room / skins)

- **Strict accessory enforcement** (`CatalogController.LiveOfferItemsFor` + `GetPublishedItem`, `ObjectController.SanitizePersonaObjects`): a Minecraft-creator accessory serves only when the allowed set (defaults ∪ per-player grants) is non-empty AND one of its theme tags is in it — tagless accessories never serve, and with nothing selected exactly 0 accessories serve. `persona_skin` pieces are exempt at all three points.
- **Skins and skin packs un-gated** (`CatalogController.LiveSkinPackItemsFor`): the allowed-set filter is gone — every indexed live classic skin pack serves and resolves in `GetPublishedItem` regardless of the pack selection.
- **Physical binary gate** (`LivePersonaCatalog.TryEnsureBinaryAsync`): the persona CDN endpoint refuses to resolve/probe/download a Minecraft accessory outside the allowed set — pre-existing mirror/staticdata files and custom cosmetics are never affected. The endpoint is unauthenticated, so it gates against defaults ∪ ALL players' grants (`PersonaPackGate.AllAllowedPackUuidsAsync`: a ~30 s-cached SQLite scan of the personaPacks objects via the new `EarthDB.ReadObjectsByTypeAsync`); refusals log once per piece per run.
- **Category limit default 30 → 45** (`PersonaPackGate.DefaultCategoryLimit` + the panel's mirrored constant, form text and field init — packs.json `categoryLimit` still overrides at runtime).
- **Accessory counts in the pack picker** (`PersonaPackIndex.ReadThemePacks` + `PackPicker.razor`): thematic bundle rows count ACCESSORIES only (no more skin/mixed counts, rows read "+ add (N accessories)"); classic skin-pack rows keep their skin count.
- **Shorter panel text**: the player page's long Auto-gating paragraph is now one short line (the empty-selection variant stays one short line too).

## Unreleased (dev) — compat gate: the pieces the frozen client renders broken are actually refused now

> Some live marketplace accessories rendered visually broken on the frozen MCE client (floating/misaligned parts — "Ashen Mantle", "Anime Fighter Tank Top") while the compat gate let them through: the enforced band was `[1.12.0, 1.21.0)` although the repo's own research note says geometry `format_version >= 1.16.0` renders broken, the inspector recorded only the FIRST parseable geometry of the piece (a benign tall-size variant hid the broken small-size one), and the declared render-controller / face-bone signals were never actually scanned. The gate now refuses what the client cannot render, and says so once per piece in the log.

### 🧍 Persona items (dressing room / skins)

- **Supported band `[1.12.0, 1.16.0)`** (`PersonaPieceTypes.IsSupportedCosmetic` + the panel's `CosmeticCompatIndex`): the default max supported geometry `format_version` drops 1.21.0 → 1.16.0 (exclusive), finally matching the research note in `LivePersonaCatalog`; min stays 1.12.0.
- **`maxGeometryFormatVersion` knob functional** (`PersonaPackGate`): the packs.json key was parsed-and-logged but consumed nowhere — it now drives the actual gate math on both sides (the panel mirrors the same packs.json value with the same default; hot-reloaded). The dead `maxAnimationFormatVersion` knob is removed: animated pieces are risky regardless of their animation format_version, so an animation-version threshold had nothing to act on.
- **Worst-geometry-wins inspection** (`LivePersonaCatalog.InspectCompatEntries`): every `*.geometry.json` candidate is parsed (real pieces carry one geometry per body size — tall/medium/small/smaller) and the recorded facts are the worst case across all of them: highest + lowest parsed `format_version` and every effect signal OR-ed in — a single risky variant marks the whole piece.
- **Nested `ppack0.zip` content is actually read now** (`LivePersonaCatalog.CollectZipEntries`): the collector disposed every nested `ZipArchive` right after collecting its entries, so reading their content threw `ObjectDisposedException` and was silently swallowed — for real piece binaries (outer zip + nested container) the old inspector recorded no `format_version` at all and everything classified as unknown/served. The opened archives now stay alive until the inspection finished; this is what makes the whole compat pipeline fire on real pieces.
- **Render-controller / face-bone scans live**: `GeometryCompatInfo.HasRenderControllers` / `HasFaceBones` were declared but never set — the inspector now scans geometry/meta/manifest for render-controller references (`render_controllers` / `controller.render.`) and the geometry bone lists for face bones (`face`/`hat`/`head`, MCPE-165232); both are risky signals like the gate always intended.
- **Compat facts re-probed once**: `probe-state.json` gains a geometry schema stamp (`GeometrySchemaVersion`, current 2) — a file recorded by an older inspector is not loaded, so every piece re-inspects from its binary on first touch and the derived verdicts recompute.
- **Deduplicated diagnostics** (`PersonaPieceTypes.LogCompatVerdictOnce`): hiding a piece from the catalog offer, refusing it in `GetPublishedItem` or dropping it from an equipped profile in `ObjectController.SanitizePersonaObjects` logs one line per piece id per server run — the verdict reason (band / animation / particles / render controller / face bones / explicit override) and the knob in effect.

## Unreleased (dev) — pack gate: nothing selected now gates the whole live marketplace off; virtualized pack picker

> The "empty allowlist serves everything" fallback is gone: with no packs selected (defaults ∪ per-player grants empty) the ApiServer serves **0 live marketplace cosmetics** — themed pieces and tagless "base" pieces alike, live classic skin packs included. Players keep the always-served staticdata (the built-in "Minecraft Default Skins" pack, the client's vanilla Standard pack scaffolding, Bold Rabbit Feet, the Apace capes, layout/StoreConfig) and the custom cosmetics. With at least one pack configured the behavior is unchanged: themed pieces filtered by the allowed tags, tagless base pieces keep serving, non-empty live classic skin packs filtered by uuid. Per-player grants follow the same rule — a player with no defaults and no grants gets nothing live. The per-category limit no longer doubles as the nothing-selected fallback; it still caps whatever serves. The panel's PackPicker is now a virtualized scrollable list showing all ~8K bundles.

### 🧍 Persona items (dressing room / skins)

- **Empty allowed set = 0 live pieces** (`PersonaPackGate.AllowedPackUuidsAsync`): the null sentinel ("nothing configured → serve everything") is removed — the gate returns the possibly-empty allowed set and every enforcement point handles it: `CatalogController.LiveOfferItemsFor` drops every live Minecraft-creator piece (tagless included) from the search offer, `LiveSkinPackItemsFor` serves no live classic skin packs, `Catalog/GetPublishedItem` refuses themed and tagless live Minecraft pieces at equip resolve (404 like an unknown id; non-Minecraft creators still resolve), and `ObjectController.SanitizePersonaObjects` drops themed and tagless live pieces from saved persona profiles. Staticdata and the custom cosmetics are never gated.
- **PackPicker virtualized** (`PackPicker.razor`): the panel's pack pickers render as a virtualized scrollable list — all ~8K bundles stay browsable instead of rendering the whole list at once.

## Unreleased (dev) — live persona catalog: the dressing room backed by the still-live marketplace, everything unlocked

> The legacy 231 bundled persona pieces are all dead binaries (xforge CDN dark, mirror release parked by user decision 22.11). The panel now syncs the **still-live** PlayFab marketplace (title `20ca2` — ~39.8k PersonaDurable entries, ~12.4k unique pieces incl. ~2.2k emotes) into the data volume at runtime, and the ApiServer serves it **unlock-all**: no achievement locks, no prices, no lock badges. Metadata are cached facts in the volume (`persona-catalog/`), binaries download lazily on the first client request — zero catalog content in the repo.

### 🧍 Persona items (dressing room / skins)

- **Live sync** (`LivePersonaCatalogSync.cs`, panel): anonymous AndroidDeviceID login (same flow as `scripts/fetch_persona_catalog.py`), `Catalog/Search` pagination via `skip`/`top` (the live API does not honour `ContinuationToken` — the token comes back absent or repeats the same page; verified 22.11), ~1.4 req/s, full walk ≈ 400 pages. Writes `persona-catalog/index.json` (deduped per piece UUID, newest re-publication wins) and raw `persona-catalog/items/<id>.json` into the volume — never the repo. Triggers: every panel start when the volume index is missing or older than `PersonaCatalogSyncIntervalHours` (default 24), or the admin panel's **Sync now**. `Search` never returns `Contents` — the piece URLs are resolved per item via `Catalog/GetItem`.
- **Unlock-all offer** (`Services/LivePersonaCatalog.cs` in the ApiServer): the live catalog replaces the dead 1P legacy pieces in `Catalog/Search`/`GetPublishedItem` (Bold Rabbit Feet, the Apace capes and the client-config entries stay). On the offer output only: `is_achievement`/`earth_achievement`/`vanilla_achievement` tags stripped (the client shows those as achievement locks and refuses acquisition), `hidden_offer` entries excluded, `Purchasable` → true, price → 0 (`displayProperties.price` + both response `Price` blocks). Verified by a harness linking the real service over a live-fetched 110-entry sample: 49 offer items, zero lock fields, zero live-CDN URLs leaked.
- **Lazy probe + binary proxy**: the offer advertises `/playfab/persona/<itemId>.zip`; on the first client request `PersonaCdnController` resolves the real URL (`Catalog/GetItem`), probes it (HEAD → 1-byte GET → the same GUID on `content1/content2.prod.catalog.playfab.com` → dead), downloads the piece into `persona-catalog/binaries/` and serves it. Dead items leave the offer until they revive (stale dead verdicts re-probe). No bulk probing exists — one shared ≤2 req/s pacer with retry/backoff covers every remote call; probe verdicts persist in `persona-catalog/probe-state.json`.
- **Thumbnails** (`MojangImagesCdnController`): same lazy path with the runtime Bedrock-Persona mirror fallback (`raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona` — URL fact only, nothing committed).
- **Emotes skipped**: the MCE client has no emote path — zero emote references in `Minecraft_Earth_Patcher`, and the committed dressing-room layout (`PersonaQueryManifest_V1.0`) has no emote category (the StoreConfig `dressingroomemotes` filter is vestigial shared Bedrock config). Emote metadata still syncs so a later decision needs no re-sync. See `docs/live-persona-catalog.md`.
- **Panel UI** (System Configuration → Data Handling): "Live persona marketplace" toggle (default ON), sync-interval setting, "Sync now" button with live counters (pieces/emotes cached, probed live/dead, binaries/thumbnails cached). The toggle rides to the ApiServer as `--live-persona-catalog`; the Mojang asset gate is removed (see below), so no consent is required.
- **Marketplace Management tab copy trimmed** (`PersonaPacks.razor`): the long compat / pack-filtering / custom-cosmetics explanations moved to `docs/marketplace-management.md` and the panel keeps one-liners; the pack summary now reads "N accessory bundles found" (`PersonaPackIndex.ReadThemePacks`); the custom-cosmetics **Creator** is editable per item (defaults to `KotPasztet`, the ApiServer falls back to it when empty); the Per-player grants card moved above Custom cosmetics; the Server Options live-marketplace help text reads "Sync with live Bedrock marketplace."

## Unreleased (dev) — Mojang asset gate removed: startup never blocks on the legacy persona assets

> The gate's download sources are all dark (xforge CDN 0/231 alive; the `mojang-persona-mirror` release stayed parked by user decision 22.11) and the live marketplace sync replaces the legacy pieces, so the consent flow is gone. Startup proceeds with whatever assets the data volume carries — no consent prompt, no Server Status alert, no `--require-mojang-assets` fail-fast, no `RequireMojangAssets` setting.

### 🧍 Persona items (dressing room / skins)

- **Gate removed** (`MojangAssetGate.cs` deleted): neither the panel's startup paths (`FileChecker`, `ServerManager`) nor the ApiServer direct start check `consent.json` or the manifest before starting. A missing manifest is only a debug line; incomplete assets produce one informational ApiServer log line ("legacy pieces may 404; the live marketplace catalog replaces them when synced").
- **Consent requirement dropped from the live sync**: `LivePersonaCatalogSync.EnsureScheduledSyncStarted` starts whenever the toggle is on and the volume index is missing or stale — no `consent.json` needed.
- **Server Options** lost the "Require additional Mojang assets" toggle; existing `config.json` files carrying `RequireMojangAssets` simply drop the key on the next save (System.Text.Json ignores unmapped members when loading).
- **Minecraft Default Skins** (Steve, Alex, Sunny, Kai, Makena, Zuri, Efe, Ari, Noor): still built idempotently from the volume's `skins/*.png` on every panel start (`MojangAssetPostProcessor.RunAllSafe` — best-effort, failures logged, never blocking).
- **Facts kept**: `staticdata-extras/playfab/mojang-assets.manifest.json` stays as recorded facts (source URLs, sha256/size), and the manifest generator/probe scripts remain for re-verification.

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
- **Backwards-compatible self-updating `update.sh`** with backup rotation and `--rollback`, and **versioned release images** (`ghcr.io/apaceproject/apace:vX.Y.Z`) alongside the rolling `:main` tag.
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
