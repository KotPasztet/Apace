# Custom persona items (dressing room / skins)

Apace can serve character creator items (`PersonaDurable` catalog entries, like the
built-in Bold Rabbit Feet) from simple JSON files in `staticdata/playfab/items/`.
No other revival server has this — items are added by dropping a file, no
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

## The Apace Cape (removed)

The Apace Cape items (`Apace Cape`, `Apace Cape 256`, `Apace Cape 512` — free
`persona_back` pieces, art by KotPasztet) were **removed on 2026-11-25** (user
decision): they never equipped properly on a real device and fixing the
receipt-keyed custom-piece path was not worth the effort. Gone are the item
JSONs, the piece zips in `staticdata-extras/playfab/persona/`, the thumbnails
in `wwwroot/playfab/images/` and the hardcoded `ApaceEntitlements` in
`InventoryController` — players who still carry a cached cape entitlement get
the deterministic placeholder via the usual stub path.

The knowledge the cape effort produced stays relevant for ANY custom piece, so
it is kept here in short form:

- **Every real Mojang piece is an outer zip containing exactly one nested
  `ppack0.zip`** whose `contents.json` is a 256-byte ContentIdentity header
  (`u32@0x00 == 0`, magic `u32@0x04 == 0x9BCFB9FC` LE, `byte@0x10 == 36`, pack
  uuid NUL-terminated ASCII at `0x11`) followed by an AES-256-CBC encrypted
  body (key = keyString[0:32], IV = keyString[0:16], PKCS7) shaped
  `{"content":[{"path":"<file>","key":"<key>"},...]}`; a file with an empty
  per-file key is served raw. A header that does not parse makes the client
  fall back to the plain strategy — but a header that DOES parse on a
  too-short body caused a `size_t` underflow crash (the reason the current
  build exists). `scripts/build_apace_piece.py` builds such containers.
- The client decrypts PERSONA packs with its own built-in key (AES-256-CFB8,
  the silica.codes `SKIN_PERSONA_KEY` disclosure; `ApaceContentKey` in
  `InventoryController.cs` mirrors it), and Apace delivers that key to every
  pack uuid through the RS256-signed receipt on `/inventory/GetInventoryItems`
  (issuer certificate self-signed, see `Services/ReceiptIssuerCertificate.cs`).
  The receipt channel is verified shape-wise but was never confirmed working
  on a device end-to-end — that uncertainty is why custom key-delivered pieces
  were dropped.

## Where to get valid PieceIds

The dressing room's pieces come from the **live marketplace sync** (see
`docs/live-persona-catalog.md`) — the catalog covers every category, so most
servers do not need to hunt for PieceIds at all.

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
that is where the sync's index comes from. Prefer free 1P items
(`creatorName` `Minecraft`) created before 2021: the MCE client froze in January
2021 and does not know newer pieces.

For pieces the client does not have, build a persona piece (manifest with the
piece uuid, meta with the `piece_id`, geometry, square power-of-two texture),
wrap it in the nested `ppack0.zip` shape the real pieces use (see
`scripts/build_apace_piece.py` and the Apace Cape section above), name the outer
zip after the catalog ITEM id, drop it into `staticdata/playfab/persona/` and
add the explicit `Contents` entry to the item JSON.

## Built-in staticdata items

`staticdata-extras/playfab/items/` carries only what the live catalog cannot
replace (the 231 Microsoft persona piece JSONs were removed — user decision
25.11; their URLs are dead and the binaries are Microsoft-copyrighted content;
the Apace capes were removed the same day — see "The Apace Cape"):

- the **skin pack twins** (`Standard`, `Minecraft Default Skins`, `Apace Skins`)
  and the infrastructure entries (`PersonaQueryManifest_V1.0` layout,
  `StoreConfig_V1.1`), mirrored by code-defined fallback offers in
  `CatalogController` when the deployed staticdata copy is missing or
  defective;
- the hardcoded **Bold Rabbit Feet** offer lives in `CatalogController`
  (shop-required, not a JSON file).

Everything else in the dressing room — pieces, thumbnails, skin pack zips —
serves from the live marketplace pipeline (sync → warm-up → mirror-first
offer).

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
- `staticdata-extras/playfab/items/90860f69-…json` — **"Minecraft Default Skins"**, free
  1P offer that replaces the retired City Folk pack below: it inherits the same
  `packIdentity` uuid `acf11539-f3ca-4ac6-b3a9-fdd1c2cf3806` (the tag the client hardcodes
  its Classic Skins search on), but its `Contents` carries a servable `skinbinary` at
  `/playfab/skinpacks/90860f69-….zip`. That zip is **not in the repo** — it is produced
  automatically at every panel start, no operator action required:
  `MojangSkinTextureFetcher` (a `MojangAssetPostProcessor` step registered before the
  builder) downloads the nine default character textures (`skins/Steve.png`,
  `skins/Alex.png`, `skins/Sunny.png`, `skins/Kai.png`, `skins/Makena.png`, `skins/Zuri.png`,
  `skins/Efe.png`, `skins/Ari.png`, `skins/Noor.png`) from the sources recorded in
  `staticdata-extras/playfab/mojang-assets.manifest.json` (kind `skin-texture`; every
  download is sha256/size-verified and files already present with matching size/hash are
  skipped) into `<data>/mojang-assets/skins/`, and `MojangSkinPackBuilder` then builds the
  pack zip from them into `<data>/mojang-assets/playfab/skinpacks/` (idempotently — rebuilt
  only when the zip is missing, older than a re-fetched texture or carries an older pack
  version), where `SkinPackCdnController` serves it. If the fetch cannot reach the sources
  (or the textures were wiped), the offer still shows but its binary 404s and the pack's
  rows render empty; the operator workaround is to drop the nine PNGs into
  `<data>/mojang-assets/skins/` — or a prebuilt zip directly into
  `<data>/mojang-assets/playfab/skinpacks/90860f69-bcc5-46c4-9e76-b56b9db5d729.zip` — and
  restart the panel (a missing zip is logged loudly by `SkinPackCdnController`, naming the
  offer id and the expected path).
- ~~`staticdata-extras/playfab/items/b3099ceb-…json` — real 1P "City Folk Skin Pack"
  offer~~ — **removed** (Mojang content cleanup): the pack's binary was never servable
  (the real download went through the marketplace DLC pipeline and no copy of the bytes
  is reachable — the live offer, its referenced product stub and the xforge CDN all
  404), so picking it showed the offer but the pack could not download. The
  "Minecraft Default Skins" offer above now occupies its Classic Skins slot, reusing
  the `acf11539-…` packIdentity uuid so the client's hardcoded search keeps matching.

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
- Discovery: the committed layout manifest (`a2e12d4d`) lists the Standard and
  Minecraft Default Skins offers in `skinPackFeaturedQuery.productIds` (row order:
  Standard → Minecraft Default Skins → Apace Skins). Instead of further editing it,
  `CatalogController` appends the ids of **every `MarketplaceDurableCatalog_V1.2` item
  tagged `has_skinpack`** that is not already listed to the served featured query at
  response time — a new skin pack is just an item JSON with that tag, the manifest file
  never changes.

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
