# Marketplace management (admin panel)

The **Marketplace Management** tab (`/Admin/PersonaPacks`) controls what the
dressing-room marketplace serves: the per-category size limit, the
client-compatibility filter, the default packs, per-player grants and custom
uploaded pieces. The Availability card's Save button writes
`<mojang-assets>/persona-catalog/packs.json`, which the ApiServer hot-reloads —
no restart needed.

## Availability (every player)

- **Max pieces per category** caps how many pieces the client renders per
  dressing-room category group: hoodies + outerwear count together, tops +
  capes + beards each count on their own where the client groups them. The
  limit is enforced on equips too, even when pack filtering is on.
  `0` = no limit, default `30`.
- **Allow only supported cosmetics** hides pieces the frozen Minecraft Earth
  client cannot render. Every piece carries a compat profile from the metadata
  pipeline; the **Compat** badge on each item shows the verdict:
  - *supported* — the piece files parse, geometry `format_version`
    1.12.0–1.20.x, no animations / render controllers / flipbook / particles
    and no face bones.
  - *risky* — geometry 1.21.0+ (UV rotation, transform pivots) or below 1.12.0
    (legacy), any animation/effect signal, or face bones (rig 1.19.50/1.20.10,
    MCPE-165232). Those render broken: floating parts, missing meshes. Risky
    pieces are hidden from the marketplace and refused on equip.
  - *unknown* — no scan yet; unknown pieces keep serving (the filter only ever
    hides pieces with positive evidence of breakage). Turn the switch off to
    serve everything.

## Pack filtering (defaults and grants)

Pack filtering needs no toggle of its own: the gate runs on the allowed pack
set (defaults ∪ per-player grants). With **no packs configured at all**, the
whole live marketplace is gated off — players get **0 live marketplace pieces**
(themed and tagless alike, live classic skin packs included); the always-served
staticdata still shows: the built-in "Minecraft Default Skins" pack (Steve/Alex
and friends), the client's vanilla Standard pack scaffolding, Bold Rabbit Feet,
the Apace capes, the custom cosmetics, the layout and StoreConfig. With **at
least one pack configured**, the live marketplace serves those packs' pieces
plus the tagless base pieces without a theme tag. The per-category limit above
applies in both cases — it caps whatever serves, it is not a nothing-selected
fallback. The pack list comes from the synced catalog index: thematic packs are
the pieces' marketplace theme tags, classic skin packs are selected by their
pack uuid. The summary line under the pickers reads "N accessory bundles
found".

## Per-player grants (extra packs beyond the defaults)

Grants add packs for a single player beyond the defaults. They are stored in
the game database (`personaPacks` key per player); the ApiServer reads them
from there and caches them for ~30 seconds — saved grants apply when the
player's dressing room reloads.

## Custom cosmetics (own pieces, served from the mojang-assets volume)

Piece zips uploaded here become marketplace items: the ApiServer builds the
catalog offers and the entitlement receipt (pack uuid + content key) from
`persona-catalog/custom-cosmetics.json`, the zip downloads at
`/playfab/persona/<id>.zip` and the thumbnail at
`/playfab/images/<thumbnailId>.png`. Items are served immediately — no restart
needed; the table's Hide/Show toggle withdraws or restores the offer and the
entitlement at once.

- The `custom` tag, the pack uuid and the 64-hex content key are generated
  when the item is created. The **Creator** field is editable per item and
  shown as the item's creator; an empty value falls back to `KotPasztet` on
  the ApiServer.
- The piece zip must be a complete piece binary — the outer zip with the
  nested `ppack0.zip` container holding the manifest, the `.geo.json` geometry
  and the texture. A bare `.geo.json` cannot be served to the client.

## More documentation

- `docs/custom-persona-items.md` — the piece file format and how custom pieces are built
- `docs/live-persona-catalog.md` — the live marketplace sync and the unlock-all offer
- `scripts/build_apace_piece.py` — builds a complete piece binary (outer zip + nested `ppack0.zip`)
