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
- `PieceType` — the dressing room category the item appears in: `persona_skins`
  (full skins), `persona_body`, `persona_eyes`, `persona_hair`, `persona_mouth`,
  `persona_feet`, `persona_hands`, `persona_outerwear`, `persona_innerwear`, ...
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

## Where to get valid PieceIds

The MCE client ships its persona pieces in its resource pack
(`staticdata/resourcepacks/vanilla.zip` -> `genoa.mcpack`, extracted to
`staticdata/resourcepacks/genoa_cache` while the panel runs). Run:

```
python3 scripts/extract-persona-pieces.py staticdata/resourcepacks/genoa_cache
```

It lists every persona piece definition it can find (uuid, type, parent pack) in the
client's own data. Pieces reported there can be used as `PieceId` with
`AssetFileName: null` — the client already has them.

For pieces the client does not have, put a persona piece zip (`.mcpersona`-style:
manifest with the piece uuid, geometry, textures) into `staticdata/playfab/persona/`
and reference it by file name in `AssetFileName`.

## Diagnosing what the client asks for

The Api server logs every catalog search filter at `Debug` level, and every search
that **matches nothing** at `Information` level:

```
Catalog search matched no items: filter=... top=...
```

Walk through the empty dressing room categories once and check the Api server log —
the filters show exactly which content types/tags/piece types the client requests per
category, which is the fastest way to see why a category is empty.
