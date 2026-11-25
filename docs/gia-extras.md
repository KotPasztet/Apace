# The `gia-extras` branch

This branch ports the extra gameplay features from **Gia's fork** (her base was
Apace `0d3e62c`, v0.1.3) on top of current `dev`. Research inventory: her
`EXTRAS_PLAN.md` / `CHANGE_MANIFEST.md` (kept outside the repo).

## What is here

- **Super Season (Season 17) done right** — a 14-day rolling season per player
  (`ChallengeProgressVersion.SeasonStartedAt`/`EnsureSeason`, `SeasonLength`
  in `ChallengesController`), season objective counters that survive the daily
  reset, the season unlock-graph solver (`ComputeSeasonGraph` — prerequisites
  point sideways/backwards, locked challenges show no progress, the final
  challenge auto-completes), claim hardening in `ChallengeActionsController`,
  per-player season start/end in `SeasonsController`, and ruby rewards
  (`rubies: 250` on the final challenge, `rubies: 20` on "defeat 3 spiders",
  persona-item rewards replaced by rubies).
- **In-game challenge credit** — new `src/Apace.ApiServer/Utils/ChallengeEvents.cs`
  maps `[apace-event] <name>` console markers from the Fabric server to season
  challenges; the launcher parses the markers (`PersistentProcessManager`) and
  publishes them on the event bus; `BuildplateInstancesManager` gates them by
  instance type (own buildplate vs adventure vs guest); `TappablesController`
  credits loot-based challenges (mobs/rabbits/terracotta colors/dark oak slabs)
  and auto-redeems completed continuous challenges.
- **Adventures & trading posts** — deterministic per-tile trading posts with a
  6-hour stock rotation (`EncounterGenerator.GenerateTradingPost`; random
  encounters are switched off), adventure-crystal replacement stops the
  superseded adventure instances (already on dev from the multiplayer port),
  `TappablesManager.GetPlayerAdventureIds`.
- **Datapack** — GIA's `apace_adventures` datapack (adventure chests,
  reward/loot tables, event markers) grafted into dev's
  `mods/fountain-0.0.1.jar` (and its identical
  `fountain-0.0.1-SNAPSHOT-jar-with-dependencies.jar` copy). Fabric
  auto-loads it in every world (persistent world + instance worlds).
- **Data assets** — `staticdata-extras/` now also carries
  `adventures/` (spawn config + common/uncommon/trading_posts pools),
  `buildplates/shop/` (Gia's 13 October-2026 buildplate zips),
  `tappables/chest_tappable_map.json` (gold nugget in every chest rarity's
  dropSets) and `server_template_dir/datapacks/raw_ore_sucks.zip`. At publish
  time `publish.ps1` copies `staticdata-extras/*` into the staticdata tree
  (extras win); in dev checkouts the loaders probe the sibling overlay via
  `StaticDataExtras` (`Apace.StaticData`). The persistent server seeds the
  template's `datapacks/` into a fresh world (`PersistentProcessManager`).
- **Docs** — `docs/adventures.md` (Gia's adventure authoring guide).

## What is deliberately NOT here

- No build output, binaries or her `bin/`/`obj/` trees; no `.bak` files; none
  of her launcher component binaries or connector-plugin jar changes.
- Her **multiplayer** guest/join-code work is not ported — dev already ships a
  better implementation (CSPRNG join codes, rate limiting, guest routing);
  only her session fixes (`routeLogin`, `stopInstance`, trading-post encounter
  ownership, adventure early-stop) were already taken onto dev earlier.
- `BuildplatesController`'s change of the `PLAYER_ADVENTURE` gameplay mode to
  `BUILDPLATE_PLAY` was **not** ported: dev deliberately keeps reporting
  `PLAYER_ADVENTURE` for adventure instances.
- Her iOS-login/patcher/infra changes belong to the manifest effort, not this
  branch.

## The fountain jar graft

`mods/fountain-0.0.1.jar` = dev's jar + Gia's `data/` tree (108 files:
`data/apace_adventures/**` + `data/minecraft/**` overrides). Dev's own entries
(nested fountain-core/content/generator jars, `fabric.mod.json`, manifest) are
untouched. To rebuild it:

```sh
mkdir tmp && cd tmp
unzip /path/to/dev/mods/fountain-0.0.1.jar -d jar
# extract Gia's datapack from her jar and copy its data/ tree in:
unzip gia-mods/fountain-0.0.1.jar 'data/*' -d gia-jar
cp -r gia-jar/data jar/
cd jar && zip -r ../fountain-0.0.1.jar data && cd ..
# install over mods/fountain-0.0.1.jar AND mods/fountain-0.0.1-SNAPSHOT-jar-with-dependencies.jar
# (dev keeps both copies byte-identical; publish.ps1 ships both into server_template_dir/mods)
unzip -t fountain-0.0.1.jar   # must report no errors
```

Note: her connector-plugin jar change (new `InventoryTypeHelper.class`) was
NOT grafted — the datapack does not need it, and dev's multiplayer port did
not take her connector binaries.

## Known limitations

- Season 17 challenge/item ids are hard-coded tables
  (`ChallengeEvents.cs`, `TappablesController.cs`); a future season means
  editing those tables.
- Some season challenges are still unreliable (her own assessment: "some work,
  some don't") — the tappable-loot and datapack-event paths work; verify each
  challenge on a live instance.
- `POST_TILE_ODDS = 1` places a trading post on every tile (her shipped
  behavior); `DEBUG_POST_LOCATION` exists in `EncounterGenerator.cs` but is
  set to `null` (disabled).
