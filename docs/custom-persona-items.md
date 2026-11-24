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

**The nested `ppack0.zip` structure is mandatory, and `contents.json` must be a
real encrypted container.** Every real Mojang piece is an outer zip containing
exactly one `ppack0.zip` whose `contents.json` is a 256-byte ContentIdentity
header followed by an AES-encrypted body. The client reads the header to decide
how to load the pack (reverse-engineered from `libgenoa.so`, arm64):

- header layout (identical in all 231 real pieces): `u32@0x00 == 0`,
  magic `u32@0x04 == 0x9BCFB9FC` (LE), `byte@0x10 == 36`,
  pack uuid as NUL-terminated ASCII at `0x11`;
- if the header parses, the client selects the DERIVED access strategy
  (DirectoryPackWithEncryption), resolves the pack key via its key provider for
  that ContentIdentity, and DECRYPTS the rest of contents.json with
  AES-256-CBC (key = keyString[0:32], IV = keyString[0:16], PKCS7);
- the decrypted body is JSON `{"content":[{"path":"<file>","key":"<key>"},...]}`;
  a file whose per-file key is empty is served raw, so the piece art itself can
  stay plaintext;
- if the header does not parse, the client falls back to the plain strategy and
  reads contents.json as plaintext JSON.

History: the first build (a0b4d51) shipped a flat zip — nothing loaded. The
second build (a04a762) shipped the nested shape with a synthesized 133-byte
plaintext contents.json: its header PARSED (it copied the real layout), so the
client went down the encrypted path and computed `bodyLen - 256` on a 133-byte
file — size_t underflow, `malloc(18446744073709551504)`, uncaught
`std::bad_alloc` ~100ms after download, hard crash. That crash is the reason
the current build exists.

The pieces are rebuilt by `scripts/build_apace_piece.py` as a REAL encrypted
container: valid 256-byte header (pack uuid = manifest uuid) + AES-256-CBC
body listing every file with an EMPTY per-file key, so piece files stay
plaintext and openable. There is no `signatures.json` (a foreign one fails the
hash check; absence is fine). The key is a fixed constant (`APACE_CONTENT_KEY`
in the build script, mirrored in `InventoryController.cs`), delivered to the
client through the content-key receipt channel:

- the client registers pack keys from its PlayFab entitlement receipts. The
  receipt rides on the `/inventory/GetInventoryItems` response (which Apace
  serves itself): a JWS `Receipt` string (RS256-signed) whose payload validates
  with `iss == "https://playfab.com"` and `aud == "https://playfab.com/B63A0803D3653643/"`
  (the MCE title id hash) and carries `Receipt.Entitlements[]` entries of
  `{FriendlyId: <pack uuid>, ContentKey: <key>, CreatorId, Quantity, InstanceId}`;
- the same response pattern is cached in `premium_cache/*.ent` on the device;
- receipts are processed asynchronously: payload validated, then each
  entitlement's FriendlyId→ContentKey lands in the client's ContentKeyMap,
  which the pack access strategies consult when loading contents.json;
- the client fetches the receipt issuer certificate while validating the
  receipt (`GET /inventory/GetReceiptIssuerCertificateAsPem` — the real service
  answers `{"code":200,"status":"OK","data":{"PemCert":"-----BEGIN
  CERTIFICATE-----…"}}`, an RSA certificate for
  `mcreceiptissuer.inventory.prod.economy.playfab.com`). Apace serves its own
  self-signed twin, generated once and persisted in the data volume
  (`<data>/persona-catalog/receipt-issuer.pem` + `receipt-issuer.key.pem`,
  see `Services/ReceiptIssuerCertificate.cs`), and signs the receipts with
  that key — so a client that verifies the receipt signature against the
  served certificate succeeds. No PlayFab secret is needed.

Before 2026-11-25 the receipts were unsigned (alg=none) and the certificate
endpoint 404'd, leaving the client's receipt validation unable to complete —
receipt-keyed pieces (the Apace capes, panel custom cosmetics) were the
suspected cannot-equip set of that date. If a future device test still shows
the key never registering (the client decrypts with whatever fallback it
uses, decryption yields garbage, the contents parse fails and the pack is
rejected GRACEFULLY — no crash, the body is always ≥ 16 bytes, so the
underflow can never happen again), the next suspects are a pinned issuer
chain (a client that validates the certificate against the Microsoft TLS CA
chain rejects any self-signed certificate we can produce) or an ES256
signature requirement; the fallback plan is swapping the item's `PieceId` to
a real piece.

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

Rebuild after changing art: edit the flat piece sources (kept in git history at
`a04a762^`; `scripts/build_apace_piece.py` restores them automatically, builds
and removes them again), run it, then run `scripts/update_apace_items.py` if
item JSONs need refreshing, and restart the Api server. The built manifest
version is `2.0.0` — bump `BUILD_VERSION` in the build script if the client may
have cached the old piece, and replace the thumbnail PNG in
`src/Apace.ApiServer/wwwroot/playfab/images/`. Keep `APACE_CONTENT_KEY`
in sync between `scripts/build_apace_piece.py` and
`src/Apace.ApiServer/Controllers/PlayfabApi/InventoryController.cs` — the
client will only accept the pack if the receipt key matches the one used to
encrypt contents.json.

Status: the catalog side is verified end-to-end (search returns the items, the
binaries download with the nested structure) and the encrypted container is
verified byte-level (header layout, AES roundtrip, exact file list). The open
device test is whether the receipt-delivered key is accepted: equip each cape
in the dressing room. If the capes equip — done, and the 256/512 variants
additionally answer whether textures above 128px are accepted. If they do NOT
equip but the game no longer crashes — the receipt channel is not being
honored (see the fallback note above); swap the `PieceId` to a real piece.

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
the still-live xforge CDN (same bytes the real service served); a truly custom
piece (the Apace Cape) rides on the same download path in a self-encrypted
container whose key Apace delivers itself through the inventory receipt — see
"The Apace Cape" section above.

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
