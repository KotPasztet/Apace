# Live persona catalog

The dressing room is backed by the **still-live Minecraft marketplace** (PlayFab
title `20ca2` — the same catalog the MCE client's dressing room searched in
2019–2021, still answering anonymous device logins today: ~39.8k `PersonaDurable`
entries, ~12.4k unique character-creator pieces incl. ~2.2k emotes after
deduplication). Apace syncs that catalog **at runtime** and serves every item
**unlocked** — no locks, no achievements, no prices.

The legal frame is unchanged:

- **Metadata = facts.** Item metadata (titles, piece ids, CDN URLs) is cached as
  JSON in the persistent data volume and never committed to the repo. The repo
  ships code only.
- **Binaries = content.** Persona piece binaries and thumbnails are downloaded
  **lazily, on the first client request** and stored in the volume. Nothing is
  redistributed ahead of a player actually asking for it, and nothing lands in
  the installation directory or the repo.
- There is no asset gate: startup never blocks on the legacy persona assets
  (their download sources are dead and the live catalog replaces them). The one
  exception is the nine default skin textures for the built-in "Minecraft Default
  Skins" pack, which `MojangSkinTextureFetcher` fetches best-effort at panel start
  (never blocking) so `MojangSkinPackBuilder` can rebuild the pack zip — see
  `docs/custom-persona-items.md`.

## Architecture

Two processes share the data volume (`<data>/mojang-assets/persona-catalog/`)
(panel syncs, ApiServer serves):

```
panel (Apace.LauncherUI)                          ApiServer (Apace.ApiServer)
Services/LivePersonaCatalogSync.cs                Services/LivePersonaCatalog.cs
  anonymous AndroidDeviceID login (title 20ca2)     reads index.json (hot-reload on re-sync)
  Catalog/Search pagination via skip/top           builds the client offer (Catalog/Search +
  (the live API does not honour                    GetPublishedItem) with the unlock-all strip
  ContinuationToken — verified 22.11),             lazy probe + proxy:
  ~1.4 req/s                                        /playfab/persona/<itemId>.zip —
  writes raw facts into the volume:                 resolve URL (Catalog/GetItem), probe
    index.json  (deduped per-piece summary)         (404 -> new-CDN shapes -> dead), download
    items/<id>.json (raw PlayFab metadata)          into binaries/<itemId>.zip, serve
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
→ else the item is marked **dead** and drops out of the offer and the catalog
searches immediately until it revives
(a dead verdict older than 24 h past the last sync re-probes on the next
request). Everything is client-driven; there are **no bulk probe loops** — all
remote calls funnel through a shared ≤2 req/s pacer with 3-attempt
retry/backoff, and probe-state writes are debounced.

Liveness evidence (22.11 probes): the bulk of the catalog is the MCE-era
2019–2021 publication and its binaries are dark on every CDN shape, but the
marketplace is still receiving content — the 100 newest `PersonaDurable`
entries carry `creationDate` within the last weeks, and 8/8 of the newest items
probed **live** with their `Catalog/GetItem` URL already on
`content*.prod.catalog.playfab.com`. So the live offer grows from the recent
publications, dead legacy entries drop out lazily as clients touch them, and a
future Mojang re-host of the old GUIDs on the content CDN would revive them
without any code change (the alternate-URL shape is already probed).

Binaries are cached in the volume after the first download; thumbnails resolve
the same way (first the thumbnail URL recorded at sync time, then the
Bedrock-Persona mirror
`raw.githubusercontent.com/Daniel-Ric/Bedrock-Persona/main/assets/images/persona_piece/<thumbnailId>.png`
— flat path keyed by thumbnail id, verified live 23.11; an earlier
`{pieceType}/{id}` spelling 404'd on every lookup and broke the mirror
fallback — a runtime URL fact only, nothing from it is stored in the repo).
The panel's `api/persona-images` endpoint runs the same resolve itself, so
skins-tab thumbnails no longer wait for a game client to warm the cache.

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

## Pack gating (theme tags, not pack bundles)

The panel's Skin Packs page gates the live catalog by **thematic packs**. There
are no accessory pack entities in the marketplace to gate by — verified
23.11 with `scripts/probe_persona_tags.py` (1200 PersonaDurable entries across
both ends of the creationDate range + the full 1600-entry bundle catalog):

- every persona piece carries **exactly one `packIdentity`** — the
  `persona_piece` itself; no piece references a bundle;
- the bundles' `packIdentity` types are only `skinpack` (1151, the Classic
  Skins row), `worldtemplate` (684) and `resourcepack` (54);
- the committed dressing-room layout (`PersonaQueryManifest_V1.0`) groups
  accessories purely by piece type (`pieceTypeFilters`), with no themed
  collections.

The marketplace's own thematic grouping of accessories is its **theme-tag
metadata** (`tag.*` — Cool, Dark, Cute, Halloween, Fantasy, …; ~284 distinct
values, ~55% of pieces carry one). So:

- one pack = one theme tag; enabling it serves every **accessory** carrying the
  tag, across all piece categories (a pack spans head + tops + footwear + …);
- **accessories serve only from the selected packs**: every live accessory
  (any creator, not just Minecraft-creator pieces) needs the allowed set
  non-empty AND one of its theme tags in it — accessories **without** a theme
  tag never serve;
- **skins are exempt from pack selection**: `persona_skin` pieces and the
  classic-skin packs (MarketplaceDurableCatalog bundles) serve regardless;
- with **nothing configured** (defaults ∪ per-player grants empty) **0
  accessories serve** — skins still do. What always shows is the
  non-PersonaDurable staticdata (the built-in "Minecraft Default Skins" pack,
  the client's vanilla Standard pack scaffolding, layout/StoreConfig) plus
  Bold Rabbit Feet and the custom cosmetics (non-Minecraft creators — the
  Apace capes — are exempt from the gate); the legacy Minecraft-creator
  staticdata accessories now drop at every serve path, live index or not
  (staticdata carries no pack tags, so the gate can never allow them);
- type-ish tags (the piece's own category restated as a tag, e.g. `tag.hair` on
  hair pieces) never form packs — excluded by the same piece-type-suffix rule
  on both sides (panel `PersonaPackIndex.ReadThemePacks`, ApiServer
  `LivePersonaCatalog.GetOffer` — separate processes share the volume, not the
  code);
- enforcement points: the search offer, `Catalog/GetPublishedItem` at equip
  resolve (a non-selected accessory 404s like an unknown id), the saved persona
  profiles (`ObjectController.SanitizePersonaObjects`) and the binary endpoint —
  `LivePersonaCatalog.TryEnsureBinaryAsync` refuses to download a non-selected
  accessory's zip (pre-existing mirror files and customs always serve;
  staticdata-resolving Minecraft-creator accessory zips refuse in
  `PersonaCdnController` before the mirror/staticdata dirs are consulted;
  refusals log once per piece per run);
- the per-category limit caps whatever serves — default **45** per
  dressing-room category group, `packs.json` `categoryLimit` overrides;
- gating config (`packs.json`, defaults ∪ per-player grants, ~30 s grant
  cache) and the custom-cosmetics exemption are unchanged.

## Rate limits (polite by design)

| Operation | Limit |
| --- | --- |
| Panel sync pagination | ~1.4 req/s (700 ms between `Catalog/Search` pages), full catalog ≈ 400 pages, one pass per interval (default 24 h) |
| ApiServer lazy probe / GetItem / binary+thumbnail downloads | shared ≤2 req/s pacer, 3-attempt retry with backoff |
| Bulk probing | none — no bulk loops exist in the codebase; probing is strictly per-item on client request |

## E2E test steps (on a machine with a client)

1. Push the branch, let CI build the image (`dotnet build` is already verified
   here: 0 errors).
2. Start the panel, keep **Live persona marketplace** ON, press
   **Sync now** in System Configuration →
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
   (user decision 22.11); the manifest's `fallbackUrls` records remain as
   facts, and nothing gates on them.
