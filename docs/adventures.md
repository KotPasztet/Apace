# Adventure creation

Authored by Gia — instructions for building adventure templates that the Apace
server turns into playable adventures (crystal tap → instance → chest → loot).
Lightly formatted for the repo; the original text is hers.

## What your template needs

### 1. Place the mobs, then tag them

Spawn-egg mobs have no tag, so the datapack can't see them until you do. Run
these with only your mobs nearby, since the first command tags everything
except players and armor stands:

```
/tag @e[type=!minecraft:player,type=!minecraft:armor_stand,distance=..60] add advmob
```

or for larger adventures:

```
/tag @e[type=!minecraft:player,type=!minecraft:armor_stand] add advmob
```

and then either way, make sure to do:

```
/execute as @e[tag=advmob] run data merge entity @s {PersistenceRequired:1b}
```

### 2. Summon the marker (this is where the chest will spawn)

```
/summon minecraft:armor_stand ~ ~ ~ {Marker:1b,Invisible:1b,NoGravity:1b,Tags:["advmarker"]}
```

### 3. Tag the marker with its rarity

```
/tag @e[type=minecraft:armor_stand,limit=1,distance=..5] add rarity_epic
/tag @e[type=minecraft:armor_stand,limit=1,distance=..5] add rarity_uncommon
/tag @e[type=minecraft:armor_stand,limit=1,distance=..5] add rarity_common
```

With no tag, you get common.

How the chest appears in game (the datapack swaps the marker for a stand-in
block that the bridge shows the Earth client as the adventure chest):

| Rarity tag | Stand-in block |
|---|---|
| `rarity_common` (or none) | moss block |
| `rarity_uncommon` | sculk |
| `rarity_rare` | mud |
| `rarity_epic` | ochre froglight |
| `rarity_legendary` | verdant froglight |

When all `advmob` mobs within 48 blocks of the marker are gone and a player
comes within 48 blocks, the chest spawns. When the chest block is broken,
everyone within 48 blocks gets the rarity's loot table
(`data/apace_adventures/loot_tables/` inside the fountain mod jar) and the
launcher reads the `[apace-adventure-complete]` console lines to count the
adventure as played (for the "play 2 adventures" season challenge).

## Package it

Save and quit to the title screen so the entity files are written.

Make a new zip whose top level contains only `region\`, `entities\` and
`buildplate_metadata.json`. There should be no parent folder, no `level.dat`
and no playerdata. Zip the contents, not the folder.

Write the metadata file as `{"version":1,"size":16,"offset":34,"night":false}`,
unless you're modding an existing buildplate or adventure — in that case use
its original metadata.

Run `[guid]::NewGuid()` in PowerShell and name the zip `<that-guid>.zip`, if
you need a new GUID.

## Register it

Put the zip in `staticdata/buildplates/shop`. The shop folder is imported at
startup, and since the ID isn't in `shop.json` it shouldn't appear in the
store.

In `<your-chosen-rarity>-buildplates.json`, add an entry to the list with the
adventure's UUID:

```json
{ "templateId": "<that-guid>", "weight": 10 }
```

The manifests live in `staticdata/adventures/<rarity>/<rarity>-buildplates.json`
(see `adventures/README.md` for the spawn config fields). On this branch the
Apace-shipped copies are in `staticdata-extras/adventures/` and win over the
upstream submodule's files.

## Trading posts and gold nuggets

Trading posts are the server-generated "gambling" encounters: the tappables
generator places one per map tile (`EncounterGenerator.GenerateTradingPost`),
their stock (which buildplate you get) rotates every 6 hours and is picked
weighted from `staticdata/adventures/trading_posts/trading_posts-buildplates.json`.
Tapping one starts a fresh player-owned adventure instance.

Gold nuggets (item `6194f06e-2462-25e0-0710-f1f39d7e1680`, already in the
catalog with a 9-nugget recipe) are appended to every chest rarity's dropSets
in `staticdata/tappables/chest_tappable_map.json` — they are the currency the
trading-post contraptions inside the buildplate worlds gamble with. The
`raw_ore_sucks` datapack (seeded from
`staticdata-extras/server_template_dir/datapacks/` into the persistent world)
makes gold ore drop nugget-style loot to support the economy.

## Season challenges driven by the datapack

The datapack prints `[apace-event] <name>` console lines for in-world actions;
the launcher forwards them and `ChallengeEvents.cs` maps them onto the Super
Season challenges (spiders with sword/TNT/gold pickaxe, rare chest, saplings,
leaves, oak logs, placed rare/uncommon/squid/pale pig/parrot/salmon, collect
salmon, creeper + flint, gold ore, terracotta per color, dark oak slab, spider
kills). To support another challenge, add a row to the rule table in
`src/Apace.ApiServer/Utils/ChallengeEvents.cs` and print the matching line
from the datapack.
