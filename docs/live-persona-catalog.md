# Live persona catalog

The dressing room is backed by the **still-live Minecraft marketplace** (PlayFab
title `20ca2` — the same catalog the MCE client's dressing room searched in
2019–2021, still answering anonymous device logins today: ~39.8k `PersonaDurable`
entries, ~12.4k unique character-creator pieces incl. ~2.2k emotes after
deduplication). Apace syncs that catalog **at runtime** and serves every item
**unlocked** — no locks, no achievements, no prices.

The legal frame is the same one `docs/mojang-asset-gate.md` established, kept
intact here:

- **Metadata = facts.** Item metadata (titles, piece ids, CDN URLs) is cached as
  JSON in the persistent data volume and never committed to the repo. The repo
  ships code only.
- **Binaries = content.** Persona piece binaries and thumbnails are downloaded
  **lazily, on the first client request, behind the Mojang-assets consent** and
  stored in the volume. Nothing is redistributed ahead of a player actually
  asking for it, and nothing lands in the installation directory or the repo.
- The `RequireMojangAssets` gate policy (default ON) and the consent flow are
  unchanged — the live catalog only runs when the asset consent has been
  accepted, and the legacy manifest/fallbackUrls machinery is untouched.

## Architecture

Two processes share the data volume (`<data>/mojang-assets/persona-catalog/`),
mirroring the asset-gate split (panel downloads, ApiServer serves):

```
panel (Apace.LauncherUI)                          ApiServer (Apace.ApiServer)
Services/LivePersonaCatalogSync.cs                Services/LivePersonaCatalog.cs
  anonymous AndroidDeviceID login (title 20ca2)     reads index.json (hot-reload on re-sync)
  Catalog/Search pagination via                    builds the client offer (Catalog/Search +
  ContinuationToken, ~1.4 req/s                    GetPublishedItem) with the unlock-all strip
  writes raw facts into the volume:                lazy probe + proxy:
    index.json  (deduped per-piece summary)          /playfab/persona/<itemId>.zip —
    items/<id>.json (raw PlayFab metadata)           resolve URL (Catalog/GetItem), probe
                                                     (404 -> new-CDN shapes -> dead), download
                                                     into binaries/<itemId>.zip, serve
```

Settings (panel, System Configuration → Data Handling): **Live persona
marketplace** toggle (default ON, but inert until the Mojang-assets consent is
accepted), **sync interval in hours** (default 24), **Sync now** button and
counters (pieces cached, emotes, probed live/dead, binaries/thumbnails cached).
The toggle rides to the ApiServer as `--live-persona-catalog`.

### The volume cache (facts only — never in the repo)

| Path | Content |
| --- | --- |
| `persona-catalog/index.json` | one record per unique piece (deduped by piece UUID, newest re-publication wins), with the fields the offer needs — original tags/prices are kept verbatim; the strip happens on the offer output |
| `persona-catalog/items/<itemId>.json` | raw PlayFab item metadata, exactly as `Catalog/Search` returned it |
| `persona-catalog/probe-state.json` | lazy liveness cache: `live` / `dead` per item + resolved URL + timestamp |
| `persona-catalog/binaries/<itemId>.zip` | piece binaries downloaded on demand |
| `persona-catalog/thumbnails/<imageId>.png` | thumbnails downloaded on demand |

### The unlock-all strip (offer output only)

The MCE client recognizes locks in catalog metadata; the same fields
`scripts/update_apace_items.py` neutralized for the legacy items are neutralized
when the offer is built from the live metadata (the raw cache is never edited):

| Lock field | Client behaviour | Strip |
| --- | --- | --- |
| tags `is_achievement` / `earth_achievement` / `vanilla_achievement` | item shown locked behind achievements, acquisition refused | tag removed |
| `displayProperties.price` > 0 / response `Price` blocks | ruby price badge, locked until purchase | `Data.Price` → 0 (drives all three client-visible surfaces: `displayProperties.price`, `Price`/`PriceOptions`) |
| `Purchasable: false` (reward-only items) | item cannot be acquired at all | → `true` |
| tag `hidden_offer` | item never surfaced (StoreConfig `globalNotTags`) | entry excluded from the offer |

Kept on purpose: the dangling `ItemReferences` entry (every real free item
carries one — structural, not a lock) and the piece `rarity`/creator metadata
(cosmetic).

`Minecraft_Earth_Patcher` has no catalog-metadata parsing at all (it is a pure
APK patcher — URL redirects, telemetry, signature checks), so the lock-field
list above comes from the documented client behaviour that already let us
ungate the 82 achievement-gated legacy items.

Verified against a real sample: the harness linking the actual
`LivePersonaCatalog.cs` over a live-fetched 110-entry sample (55 unique pieces,
49 offer items, 3 of them achievement-tagged, 6 emotes) produced **49 offer
items with zero lock fields, zero live-CDN URL leaks and zero emotes**
(`scripts/verify_unlock_all_sample.py` + a /tmp C# harness; rerunnable).

### Lazy probe (no mass probing)

The offer advertises `/playfab/persona/<itemId>.zip` — **the client never sees a
live CDN URL**. `Catalog/Search` never returns `Contents` (verified on the live
API), so the real binary URL is discovered per item on the first client request:
`Catalog/GetItem` → HEAD (fallback 1-byte GET) on the returned URL → on 404 the
same GUID re-hosted on the new PlayFab content CDN
(`content1/content2.prod.catalog.playfab.com/pf-namespace-b63a0803d3653643/<guid>/primary.zip`)
→ else the item is marked **dead** and drops out of the offer until it revives
(a dead verdict older than 24 h past the last sync re-probes on the next
request). Everything is client-driven; there are **no bulk probe loops** — all
remote calls funnel through a shared ≤2 req/s pacer with 3-attempt
retry/backoff, and probe-state writes are debounced.

Binaries are cached in the volume after the first download; thumbnails resolve
the same way (first the thumbnail URL recorded at sync time, then the
Bedrock-Persona mirror `raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona`
— a runtime URL fact only, nothing from it is stored in the repo).

### Emotes

**Skipped in the offer** (synced and cached, so a later decision needs no
re-sync). Evidence from this repo:

- `Minecraft_Earth_Patcher` contains zero emote references — no patches, no
  catalog deserialization;
- the committed dressing-room layout (`PersonaQueryManifest_V1.0`,
  `a2e12d4d-…json`, fetched from the real catalog for the MCE-era client) has
  **no emote category** — `dressingRoomLayout.categoryObjects` never queries
  `persona_emote`;
- the StoreConfig (`b7c61d34-…json`) carries a vestigial `dressingroomemotes`
  filter toggle from the shared Bedrock config, but the layout the client
  renders is the only path that issues searches, and it has none.

So the frozen MCE client has no UI path that would browse or play emote pieces;
offering them would be dead weight in the dressing-room queries.

## Rate limits (polite by design)

| Operation | Limit |
| --- | --- |
| Panel sync pagination | ~1.4 req/s (700 ms between `Catalog/Search` pages), full catalog ≈ 400 pages, one pass per interval (default 24 h) |
| ApiServer lazy probe / GetItem / binary+thumbnail downloads | shared ≤2 req/s pacer, 3-attempt retry with backoff |
| Bulk probing | none — no bulk loops exist in the codebase; probing is strictly per-item on client request |

## E2E test steps (on a machine with a client)

1. Push the branch, let CI build the image (`dotnet build` is already verified
   here: 0 errors).
2. Start the panel, accept the Mojang-assets consent (asset gate), keep
   **Live persona marketplace** ON, press **Sync now** in System Configuration →
   Data Handling — the counters should fill: ~39.8k raw entries → ~12.4k unique
   pieces (~2.2k emotes counted but not offered).
3. Start the server components; confirm the ApiServer log line
   "Live persona offer rebuilt: N item(s)" and that a dressing-room search on a
   device returns the live items at price 0 with no lock badges.
4. Click a piece → the client downloads `/playfab/persona/<itemId>.zip`
   (watch the ApiServer log for "Live persona piece … downloaded from
   content*.prod.catalog.playfab.com into the volume cache") and equips it.
5. Re-check the panel counters (probed live/dead, binaries cached) and verify a
   dead item (probe 404 on all shapes) disappears from the dressing room.
6. Release `mojang-persona-mirror` stays parked — deliberately not created
   (user decision 22.11); the `fallbackUrls` machinery in the manifest remains
   useful for the legacy asset gate and is untouched.
