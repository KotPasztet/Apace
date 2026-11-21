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
- `Contents` — the item's `personabinary` download. Real persona items carry an
  explicit entry: `{ "Id": <guid>, "Url": "/playfab/persona/<itemId>.zip",
  "MaxClientVersion": "65535.65535.65535", "MinClientVersion": "1.13.0",
  "Tags": [], "Type": "personabinary" }`, pointing at a file named after the
  ITEM id in `staticdata/playfab/persona/`. Use this for pieces the client does
  not have built in. `[]` for built-in pieces.
- `ItemReferences` — real persona items carry exactly one dangling reference
  (`{ "Id": <guid>, "Amount": 1 }` whose target does not exist in the catalog);
  replicate it so the item is structurally indistinguishable from real ones.
- `ThumbnailImageId` — optional thumbnail GUID; PNG must exist at
  `wwwroot/playfab/images/<id>.png` in the Api server.

## The Apace Cape

The server ships an **Apace Cape** — a free `persona_back` (cape) item available to
every player by default, and it is a **real custom piece**: art by **KotPasztet**.
Its catalog item is
`staticdata-extras/playfab/items/6e8671bd-7d35-442c-8e7c-39b6562490ca.json`
(`PieceId 3199377b-130a-4189-9b90-484ae751c54f`, creator KotPasztet, thumbnail
`10413b0a-1dfd-48d1-9215-22c0d41624ca.png`). Two more items (Apace Cape 256 /
512) ship the same art at higher square resolutions as a device test.

How it reaches the client:

1. the dressing room shows the item in the capes category; claiming it is free,
2. the item's `Contents` entry advertises the piece binary at
   `/playfab/persona/6e8671bd-7d35-442c-8e7c-39b6562490ca.zip` (served from
   `staticdata/playfab/persona/`, file named after the item id like the real
   pieces),
3. the client downloads that zip and opens the nested `ppack0.zip` inside it —
   the exact structure of the 231 real piece binaries — reads its
   `manifest.json` (`persona_piece` module), the `apace_cape.meta.json` piece
   definition and applies the piece with the matching `piece_id` UUID.

**The nested `ppack0.zip` structure is mandatory.** The first build (a0b4d51)
shipped a flat zip (manifest + piece files at the zip root) and none of the
Apace capes could be equipped — every real Mojang piece is an outer zip
containing exactly one `ppack0.zip`, plus `signatures.json` (80 bytes) and a
`contents.json` whose header embeds the manifest pack uuid. Real pieces carry
Microsoft-encrypted content (meta/geometry/textures) and a real signature; we
cannot produce either. The pieces are therefore rebuilt by
`scripts/build_apace_piece.py` in the nested shape with PLAINTEXT content, in
three test variants (this is the open device test):

- `6e8671bd-...zip` (Apace Cape, 128px) — synthesized `contents.json`, no
  `signatures.json` → probes whether `contents.json` presence is required,
- `a5d108fa-...zip` (Apace Cape 256) — bare inner pack → probes whether
  plaintext/unsigned content loads at all,
- `1981967a-...zip` (Apace Cape 512) — synthesized `contents.json` PLUS
  `signatures.json` copied verbatim from a real piece → probes whether
  signature presence (not validity) is enough.

Reading the results: if 256 works, the client ignores both files (bare wins);
if only 128 works, `contents.json` must exist but is not validated; if only
512 works, `signatures.json` must exist but is not validated; if none work
while real pieces still do, the client validates the crypto and a custom piece
is impossible without Microsoft's keys — fall back to real pieces.

The piece inside the nested `ppack0.zip` (plaintext, you can open it):

- `manifest.json` — `format_version 1`, one module of type `persona_piece`
- `apace_cape.meta.json` — `piece_id`/`piece_name`/`piece_type` plus
  `geometry_sources` for every body size (`tall`, `medium`, `small`, `smaller`)
- `apace_cape.geometry.json` — legacy `1.8.0` geometry, one
  `geometry.apace_cape.<body_size>` per body size; a single `cape` cube in the
  vanilla cape position (`[-5, 8, -3]`, size `[10, 16, 1]`, box UV `[0, 0]`)
  parented under `body`/`waist`/`root` so it follows the player torso
- `apace_cape.png` — the cape texture, 128x128 (the ct_cape layout, design
  mirrored onto both faces; the sheet must be a **square power of two** — that
  is the Marketplace persona rule; 256/512 variants probe whether the client
  accepts more than 128)
- `texts/en_US.lang` — `persona.apace_cape.title=Apace Cape`

Rebuild after changing art or variants: edit
`scripts/build_apace_piece.py` (sources are the flat piece zips kept under
version control history / the art PNG in the repo), run it, then run
`scripts/update_apace_items.py` if item JSONs need refreshing, and restart the
Api server. Bump the module/piece versions if the client may have cached the
old piece, and replace the thumbnail PNG in
`src/Apace.ApiServer/wwwroot/playfab/images/`.

Status: the catalog side is verified end-to-end (search returns the items, the
binaries download with the nested structure). Whether the client ACCEPTS
plaintext unsigned pieces is a device test — equip each cape variant in the
dressing room and read the matrix above.

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

For pieces the client does not have, build a persona piece (manifest with the
piece uuid, meta with the `piece_id`, geometry, square power-of-two texture),
wrap it in the nested `ppack0.zip` shape the real pieces use (see
`scripts/build_apace_piece.py` and the Apace Cape section above), name the outer
zip after the catalog ITEM id, drop it into `staticdata/playfab/persona/` and
add the explicit `Contents` entry to the item JSON.

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

## Classic skins (Steve / Alex skin packs)

The dressing room's **Classic Skins** section (header `dr.header.skins`) shows Bedrock-style
*skin packs*, not persona pieces. The packs themselves are **built into the client**
(`data/skin_packs/vanilla` in the app bundle: the "Standard" pack `c18e65aa-7b21-4637-9b63-8ad63622ef01`
with Steve/Alex/Dummy, plus the empty "Persona" pack `5eb65f73-af11-448e-82aa-1b7b165316ad`) —
the server only supplies the catalog scaffolding around them:

- `staticdata-extras/playfab/items/a2e12d4d-…json` — **`PersonaQueryManifest_V1.0`** ("Home L1"):
  the dressing room layout (categories/subcategories per piece type, featured rows and
  `skinPackFeaturedQuery` — the Classic Skins row, listing skin pack offer ids). Served
  with the layout JSON from the real catalog (`displayProperties` passed through verbatim);
  featured product ids are trimmed to the offers Apace can serve.
- `staticdata-extras/playfab/items/b7c61d34-…json` — **`StoreConfig_V1.1`**: store/dressing
  room configuration from the real catalog (`dressingRoomFilters`, `dressingRoomSearch`,
  `dressingRoomClassicSkinsSearch`, …), verbatim.
- `staticdata-extras/playfab/items/c8a4571e-…json` — **`MarketplaceDurableCatalog_V1.2`**,
  free "Standard" pack offer: `displayProperties.packIdentity[0].uuid` is the client's own
  vanilla pack uuid, so the client resolves it locally ("Skin pack is in repo") and offers
  Steve/Alex for picking. `skinPack.count: 3`.
- `staticdata-extras/playfab/items/b3099ceb-…json` — real 1P "City Folk Skin Pack" offer
  (the client hardcodes a search for tag `acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806`, which is
  that pack's uuid; the item comes from the live catalog unchanged). Its pack binary is not
  servable (the real download went through the marketplace DLC pipeline and no copy of the
  bytes is reachable — the live offer, its referenced product stub and the xforge CDN all
  404), so picking it shows the offer but the pack cannot actually download.

### Downloadable skin packs (skinbinary)

Skin packs the client does not have built in are downloaded the same way persona pieces
are: the client resolves the offer (from the Classic Skins row's `productIds`, via
`Catalog/GetPublishedItem`), sees the pack uuid in `displayProperties.packIdentity` is
missing from its skin pack repo ("Need to download skin pack"), fetches the offer's
`Contents` entry of type **`skinbinary`** and imports the zip into its premium cache
("Loading skin pack"), then matches the zip's `manifest.json` header uuid against the
promised `packIdentity` uuid ("Skin pack is in repo"). A mismatch shows
"Skin pack was not recognized" — so **the pack manifest header uuid must equal
`packIdentity[0].uuid`**. The pack zip itself is a plain Bedrock skin pack: `manifest.json`
(`format_version 1`, one `skin_pack` module), `skins.json` (skins with
`localization_name`/`geometry`/`texture`/`type`, NO cape field) and `texts/en_US.lang`
(`skinpack.<serialize_name>=…` plus `skin.<serialize_name>.<skin>=…` lines) — same shape
as the client's built-in `assets/skin_packs/vanilla`.

- `staticdata-extras/playfab/items/42a5f927-…json` — **"Apace Skins"** (by KotPasztet),
  free offer whose `packIdentity` uuid is the pack's own manifest header uuid; `Contents`
  carries the `skinbinary` download at `/playfab/skinpacks/42a5f927-….zip`.
- `staticdata-extras/playfab/skinpacks/` — pack binaries, served by
  `SkinPackCdnController` (`/playfab/skinpacks/{fileName}`, mirrors
  `PersonaCdnController`). The skin texture is a **placeholder** generated in the Apace
  Cape palette — the real skin should be KotPasztet's own 64x64 drawing (replace
  `apace.png` inside the zip; the cape banner art cannot be re-cut into a skin layout).
- Discovery: the committed layout manifest (`a2e12d4d`) only lists the two original offers
  in `skinPackFeaturedQuery.productIds`. Instead of editing it, `CatalogController` appends
  the ids of **every `MarketplaceDurableCatalog_V1.2` item tagged `has_skinpack`** to the
  served featured query at response time — a new skin pack is just an item JSON with that
  tag, the manifest file never changes.

Adding another downloadable pack: build the Bedrock skin pack zip, drop it into
`staticdata-extras/playfab/skinpacks/<offerId>.zip`, add an offer item JSON
(`MarketplaceDurableCatalog_V1.2`, `packIdentity` = manifest header uuid, `Contents` entry
`Type: "skinbinary"` pointing at `/playfab/skinpacks/<offerId>.zip`, tags incl.
`skinpack` + `has_skinpack`), thumbnail PNG in `src/Apace.ApiServer/wwwroot/playfab/images/`.

These use the `Data.Type = "ClientConfig"` item shape: `ContentType` (the exact string the
client filters on), `ResponseType` (`catalogItem` or `bundle`), `MinClientVersion` /
`MaxClientVersion`, `IsStackable`, `Price` (ruby price for the response price block or
`null` for none) and `DisplayProperties` — a raw JSON object served to the client unchanged
(`displayProperties` key in catalog responses). All of them are free-form client-parsed
entries: the server never interprets their displayProperties.

Classic skin selection itself (`skin: true` entries in the serialized persona, skin ids
shaped `<packUuid>_<skinName>` like `c18e65aa-…_Steve`) is entirely client-side.

## Diagnosing what the client asks for

The Api server logs every catalog search filter at `Debug` level, and every search
that **matches nothing** at `Information` level:

```
Catalog search matched no items: filter=... top=...
```

Walk through the empty dressing room categories once and check the Api server log —
the filters show exactly which content types/tags/piece types the client requests per
category, which is the fastest way to see why a category is empty.
