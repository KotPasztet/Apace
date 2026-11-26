# Marketplace management (admin panel)

The **Marketplace Management** tab (`/Admin/PersonaPacks`) controls what the
dressing-room marketplace serves: the cosmetics sources, the per-category size
limit, the client-compatibility filter, the default packs, per-player grants
and custom uploaded pieces. The Availability card's Save button writes
`<mojang-assets>/persona-catalog/packs.json`, which the ApiServer hot-reloads —
no restart needed.

## Cosmetics sources

Two separate checkboxes, both **default ON**, each gating one source of
cosmetics:

- **Enable legacy cosmetics** (packs.json `enableLegacyCosmetics`,
  hot-reloaded — a Save applies on the next client request, no restart):
  ON = pieces with a binary mirrored in the data volume
  (`<mojang-assets>/playfab/persona/<itemId>.zip` — e.g. the recovered
  original binaries) serve even when the marketplace CDN is dead: a mirrored
  binary makes the piece alive again whatever an earlier probe concluded
  (the dead→alive bridge). OFF = the server ignores the legacy mirror
  ENTIRELY — pieces without a live-synced binary disappear from the dressing
  room, the warm-up and the lazy pipeline skip them, and direct binary
  requests fall through to the placeholder. Custom cosmetics, the live volume
  cache and the built-in defaults are never affected.
- **Enable marketplace sync** (config.json `marketplaceSync`): ON = the
  periodic crawl of the still-live Bedrock marketplace refreshes the catalog
  cache; the header's "Sync now" button and the System Configuration one
  follow the same switch. OFF = the crawl stops (the scheduled start skips,
  "Sync now" refuses with a clear message) — already cached catalog and
  mirrored binaries keep serving. Independent of the System Configuration
  "Live persona marketplace" master switch, which must stay on for the sync
  to run at all.

The card's Save writes packs.json as a read-modify-write (it never clobbers
the Availability card's unsaved edits or hand-edited keys like
`maxGeometryFormatVersion`) and mirrors both values into config.json.

## Availability (every player)

- **Max pieces per category** caps how many pieces the client renders per
  dressing-room category group: hoodies + outerwear count together, tops +
  capes + beards each count on their own where the client groups them. The
  limit is enforced on equips too, even when pack filtering is on.
  `0` = no limit, default `45`.
- **Allow only supported cosmetics** hides pieces the frozen Minecraft Earth
  client cannot render. Every piece carries a compat profile from the metadata
  pipeline; the **Compat** badge on each item shows the verdict:
  - *supported* — the piece files parse, every geometry variant
    `format_version` 1.12.0 up to (excluding) the configured max (default
    1.16.0), no animations / render controllers / flipbook / particles and no
    face bones. The worst variant wins — one broken body-size variant marks
    the whole piece.
  - *risky* — geometry at or past the max (UV rotation, transform pivots) or
    below 1.12.0 (legacy), any animation/effect signal, or face bones (rig
    1.19.50/1.20.10, MCPE-165232) while no serve-time transform is on. Those
    render broken: floating parts, missing meshes. Risky pieces are hidden
    from the marketplace and refused on equip.
  - *auto-repairable* — shown instead of *risky* when the ONLY breakage
    evidence is custom rig bones (or, on a face accessory, the face rig bone
    itself) and a serve-time transform is on — the **Experimental: auto-repair
    broken 3D rigs** switch or a **customSkeletonMode** strip mode below:
    the ApiServer rewrites the bones at serve time (re-parent onto the
    standard `head` bone, or a rigid strip — face accessories always take the
    rigid strip, with the face bone removed/renamed onto `head`), so the
    piece serves and renders attached. Oversized bounds or any other signal
    keeps the badge *risky* — the repair fixes bones only.
  - *unknown* — no scan yet; unknown pieces keep serving (the filter only ever
    hides pieces with positive evidence of breakage). Turn the switch off to
    serve everything.

  The max is `packs.json`'s `maxGeometryFormatVersion` (default `1.16.0`,
  hot-reloaded, hand-edit only — the panel's Save does not write the key).

- **Experimental: auto-repair broken 3D rigs** (default off) — pieces hidden
  ONLY because of custom rig bones get their geometry rewritten at serve time:
  each custom bone-subtree root is re-parented onto the standard `head` bone
  so the frozen client can bind it and the model holds together instead of
  scattering (posed subtrees strip rigid instead — a bound bone's rotation
  starts applying around its pivot). Rewritten binaries carry a rewriter
  marker (`r3`) in their ETag so clients re-download after a knob flips;
  oversized, particle/render-controller/texture-binding-flagged or
  format-flagged pieces are never rewritten (they stay hidden) — animations
  are NOT in that list anymore, the rewriter blanks them at serve time. Each
  rewritten piece logs one line (item id + bones) in the ApiServer log — and
  when a knob is ON but a custom-boned piece was served UNCHANGED, one line
  explains why (piece type out of scope, gate blocks, nothing repairable).

- **Experimental: extra skeleton handling** (packs.json `customSkeletonMode`,
  default `flatOnly` — the requested baseline) — what the server does with the
  invented bone trees the frozen client cannot bind (the scattered "3D"
  cosmetics):
  - `off` — serve as they are (hidden by the supported-cosmetics gate).
  - `strip` — every custom bone is REMOVED from the served geometry and its
    cubes merge into the nearest standard bone (cube coordinates are absolute
    model-space values, so nothing moves at rest — the part just follows the
    bone the client actually binds instead of flying apart). Posed custom
    subtrees (bones carrying a `rotation`) strip rigid instead of
    re-parenting — a bound bone's rotation starts APPLYING around its pivot,
    which was the "completely breaks the geometry" class.
  - `flatOnly` — the strip applies at serve time; pieces the strip CAN
    flatten (any custom-bone piece — the face tabs' head/eyes/mouth pieces
    almost all carry custom bones) count as FLAT and serve stripped, so every
    dressing-room category fills to the limit with them. This includes the
    face accessories: their misplaced face rig bone (MCPE-165232) is removed
    / renamed onto `head` by the same strip. Only pieces the rewriter cannot
    fix (particles, render controllers, missing texture binding, format cap,
    oversized head bounds) stay hidden.
  Rewritten binaries carry the mode marker (`r3-strip` / `r3-flat`) in their
  ETag; the strip covers every piece type (head-family re-parents ONLY when
  no strip mode is set — see the auto-repair knob; otherwise it strips rigid
  too) and also REMOVES animations (geometry `animations` blocks and
  `*.animation.json` payloads blanked). Pieces a client already downloaded
  are cached client-side and only refresh after the client re-downloads them
  (fresh install / cleared app cache).

## Pack filtering (defaults and grants)

Pack filtering needs no toggle of its own: the gate runs on the allowed pack
set (defaults ∪ per-player grants). **Accessories serve only from the selected
packs** — every live accessory is gated (any creator, not just Minecraft-creator
pieces); a piece needs one of its marketplace theme tags in the set, so
tagless accessories never serve. With **no packs configured at all**, 0
accessories serve (skins still do); the non-accessory staticdata still shows:
the built-in "Minecraft Default Skins" pack (Steve/Alex and friends), the
client's vanilla Standard pack scaffolding, the layout and StoreConfig — plus
Bold Rabbit Feet and the custom cosmetics, which are exempt
from the gate. The legacy Minecraft-creator staticdata accessories no longer
serve anywhere: staticdata carries no pack tags, so the gate can never allow
them. **Skins are exempt**:
`persona_skin` pieces and the classic skin packs serve regardless of the
selection. The per-category limit above applies in all cases — it caps
whatever serves, it is not a nothing-selected fallback. The picker lists the
synced catalog index's thematic packs — the pieces' marketplace theme tags —
so every row yields accessories; skin-only bundles are not listed (skins
serve regardless of selection, so selecting them would do nothing). The
summary line under the pickers reads "N accessory bundles found" (N counts
the accessory-bearing bundles only).

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
