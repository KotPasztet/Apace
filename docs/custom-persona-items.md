# Custom persona items (dressing room / skins)

Apace can serve character creator items (`PersonaDurable` catalog entries, like the
built-in Bold Rabbit Feet) from simple JSON files in `staticdata/playfab/items/`.
Neither Solace nor Vienna ever had this — items are added by dropping a file, no
rebuild needed (restart the Api server to pick them up).

## Item file format

One JSON file per item, file name irrelevant (use the item id), format identical to
the other shop items plus a `Data` block of type `Persona`:

```json
{
  "Purchasable": true,
  "Data": {
    "Type": "Persona",
    "PieceType": "persona_feet",
    "PieceId": "4f7cdadd-a33c-489d-8969-752ca689f567",
    "OfferId": "53bee6fe-c9d9-43c9-b3af-4c5438fba4b7",
    "Version": "1.1.0",
    "Rarity": "rare",
    "Price": 0,
    "CreatorName": "Minecraft",
    "AssetFileName": null
  },
  "Title": "My Skin Piece",
  "Description": "Shown under the item name.",
  "ThumbnailImageId": null,
  "CreationDate": "2020-12-07T22:46:33.066Z",
  "LastModifiedDate": "2023-08-10T14:11:19.081Z",
  "StartDate": "2019-10-14T17:00:00Z",
  "Id": "11111111-2222-3333-4444-555555555555",
  "FriendlyId": "53bee6fe-c9d9-43c9-b3af-4c5438fba4b7",
  "SourceEntityId": "B63A0803D3653643",
  "CreatorEntityId": "3B1B443CE3FD8EBA",
  "Tags": ["1P"],
  "Keywords": {},
  "Contents": [],
  "ItemReferences": [],
  "TitleTranslations": {},
  "DescriptionTranslations": {}
}
```

Fields:

- `Id` — any unique GUID (the catalog item id). Must not collide with existing items.
- `FriendlyId` — set it to `OfferId`, like the built-in persona item does.
- `PieceType` — the dressing room category the item appears in. The values the
  Minecraft Earth client asks for are the Bedrock character creator piece types:
  `persona_skin` (base skin textures), `persona_hair`, `persona_eyes`,
  `persona_mouth`, `persona_face_accessory` (glasses/masks), `persona_head`
  (hats), `persona_hood` (hoods/cloaks), `persona_top` (shirts/suits),
  `persona_bottom` (trousers/skirts), `persona_outerwear` (jackets/costumes),
  `persona_hand` (gloves), `persona_arms`, `persona_legs`, `persona_feet`
  (boots/shoes), `persona_back` (wings/backpacks), `persona_emote`.
  (The old hardcoded Bold Rabbit Feet item proves `persona_feet`; the Earth Hero
  set — an item made *for* MCE — uses `persona_top`/`persona_head`/`persona_hood`/
  `persona_hand`/`persona_feet`, so these are the client's own category names.)
- `PieceId` — **the UUID of the persona piece the client applies when the item is
  equipped**. This must be a piece the client actually knows: either one built into
  its resource pack, or one it downloads from the `personabinary` URL. See below.
- `OfferId` — offer id, shown in the item details.
- `Rarity` — lowercase: `common`, `uncommon`, `rare`, `epic`, `legendary`.
- `Price` — ruby price, `0` for free.
- `AssetFileName` — optional; a file in `staticdata/playfab/persona/` served at
  `/playfab/persona/<file>` as the item's `personabinary` download (use this for
  pieces the client does not have built in). `null` for built-in pieces.
- `ThumbnailImageId` — optional thumbnail GUID; PNG must exist at
  `wwwroot/playfab/images/<id>.png` in the Api server.

## The Apace Cape

The server ships an **Apace Cape** — a free `persona_hood` (cloak) item available to
every player by default, and it is a **real custom piece**: art by **KotPasztet**,
shipped as a proper `.mcpersona` binary. Its catalog item is
`staticdata-extras/playfab/items/6e8671bd-7d35-442c-8e7c-39b6562490ca.json`
(`PieceId 3199377b-130a-4189-9b90-484ae751c54f`, creator KotPasztet, thumbnail
`10413b0a-1dfd-48d1-9215-22c0d41624ca.png`).

How it reaches the client:

1. the dressing room shows the item in the cloak category; claiming it is free,
2. `AssetFileName: apace_cape.mcpersona` makes the Api server advertise the
   piece binary as the item's `personabinary` content (served from
   `staticdata/playfab/persona/` at `/playfab/persona/apace_cape.mcpersona`),
3. the client downloads that zip, reads its `manifest.json` (`persona_piece`
   module), the `apace_cape.meta.json` piece definition and applies the piece
   with the matching `piece_id` UUID.

The piece itself (see `staticdata-extras/playfab/persona/apace_cape.mcpersona`,
a plain zip you can open):

- `manifest.json` — `format_version 1`, one module of type `persona_piece`
- `apace_cape.meta.json` — `piece_id`/`piece_name`/`piece_type` plus
  `geometry_sources` for every body size (`tall`, `medium`, `small`, `smaller`)
- `apace_cape.geometry.json` — legacy `1.8.0` geometry, one
  `geometry.apace_cape.<body_size>` per body size; a single `cape` cube in the
  vanilla cape position (`[-5, 8, -3]`, size `[10, 16, 1]`, box UV `[0, 0]`)
  parented under `body`/`waist`/`root` so it follows the player torso
- `apace_cape.png` — the cape texture, 64x64 (the classic 64x32 cape UV layout
  in the top half; the sheet must be a **square power of two** — that is the
  Marketplace persona rule, which is why the original 1024x512 art is downscaled
  and padded)
- `texts/en_US.lang` — `persona.apace_cape.title=Apace Cape`

The file passes the Marketplace persona validator (Spright's `spyglass`,
`spyglass check persona --layout standard`) with zero findings.

Swapping in different art later: replace `apace_cape.png` inside the zip — keep
the classic cape UV layout on a square power-of-two sheet (64x64, 128x128, ...) —
bump the module/piece versions if the client has cached the old one, and replace
the thumbnail PNG in `src/Apace.ApiServer/wwwroot/playfab/images/`. The original
1024x512 art is the author's; the design lives in the `(12,1)-(22,17)` UV region
of that sheet and is mirrored into `(1,1)-(11,17)` (the face the vanilla cape
orientation shows) during the build.

Caveat: the `personabinary` download path follows the same wire format as the
real service (`Contents` entry of type `personabinary`), but has not been
verified against a real client yet — test on a patched client before promising
it to players.

## Where to get valid PieceIds

**Apace already ships 231 dressing room items** — see "Built-in dressing room items"
below. They cover every category, so most servers do not need to hunt for PieceIds
at all.

The MCE client ships its persona pieces in its resource pack
(`staticdata/resourcepacks/vanilla.zip` -> `genoa.mcpack`, extracted to
`staticdata/resourcepacks/genoa_cache` while the panel runs). Run:

```
python3 scripts/extract-persona-pieces.py staticdata/resourcepacks/genoa_cache
```

It lists every persona piece definition it can find (uuid, type, parent pack) in the
client's own data. Pieces reported there can be used as `PieceId` with
`AssetFileName: null` — the client already has them.

Another source of valid (real) pieces is the still-live PlayFab catalog of the
Minecraft title: log in anonymously (`Client/LoginWithAndroidDeviceID`, TitleId
`20ca2`), exchange the entity token for a `master_player_account` token
(`Authentication/GetEntityToken`) and `Catalog/Search` with
`(contentType eq 'PersonaDurable' and displayProperties/pieceType eq '<type>')` —
that is where the built-in items below come from. Prefer free 1P items
(`creatorName` `Minecraft`) created before 2021: the MCE client froze in January
2021 and does not know newer pieces.

For pieces the client does not have, put a persona piece zip (`.mcpersona`-style:
manifest with the piece uuid, geometry, textures) into `staticdata/playfab/persona/`
and reference it by file name in `AssetFileName`.

## Built-in dressing room items

`staticdata-extras/playfab/items/` contains 231 free character creator items
(fetched from the live catalog with `scripts/generate_persona_items.py`), overlaid
onto the staticdata submodule at publish time by `publish.ps1`:

- 41 hair, 31 eyes, 18 mouth, 22 top, 15 bottom, 20 outerwear, 13 feet, 34 head,
  5 hand (incl. Hero's Gloves), 3 hood (incl. Blue Hoodie), 3 arms, 5 legs,
  5 back, 10 face accessory, 7 base skins
- includes the **Earth Hero set** (Hero's Top/Cowl/Cloak/Gloves/Boots) — the outfit
  items made for MCE — and the whole 2019 launch set of default pieces
  (Buzzcut, Blue Jeans, Work Boots, ...) that the real dressing room showed
- every item is free (`price: 0`), 1P (`Minecraft`) and created before 2021 —
  pieces the MCE client knows; `AssetFileName: null` for all of them
- thumbnails ship in `src/Apace.ApiServer/wwwroot/playfab/images/` and are served
  at `/playfab/images/<guid>.png`

**Every built-in item also ships its real piece binary** (`Contents` entry of type
`personabinary` pointing at `/playfab/persona/<item id>.zip`, file in
`staticdata-extras/playfab/persona/`). This matters: the MCE client has **no**
persona pieces compiled in — not even the Earth Hero set or the 2019 defaults —
it downloads every piece it applies from the item's `personabinary` content
(xforge `primary.zip` → `ppack0.zip`, signed and encrypted by Microsoft). Without
the Contents entry the client waits for the piece download forever, which is why
items in the dressing room used to spin on click. The binaries were fetched from
the still-live xforge CDN (same bytes the real service served); a custom piece
built from scratch cannot be encrypted by us, so a truly custom piece
(the Apace Cape) rides on the same download path but in the plain Marketplace
submission format — if the client refuses it, swap its `PieceId` to a real piece.

Removing or editing a built-in item: they are plain JSON files in
`staticdata/playfab/items/` **inside the published output** (baked into the Docker
image from `staticdata-extras/`). Edit or delete the file there and restart the Api
server. Do not add a second file with the same item `Id` anywhere — duplicate ids
crash the staticdata loader.

## Diagnosing what the client asks for

The Api server logs every catalog search filter at `Debug` level, and every search
that **matches nothing** at `Information` level:

```
Catalog search matched no items: filter=... top=...
```

Walk through the empty dressing room categories once and check the Api server log —
the filters show exactly which content types/tags/piece types the client requests per
category, which is the fastest way to see why a category is empty.
