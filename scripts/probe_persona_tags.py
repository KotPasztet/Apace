#!/usr/bin/env python3
"""Probe the live PlayFab 20ca2 catalog for THEMATIC PERSONA PACK evidence.

Answers two questions the pack-gating design depends on (sandbox-safe, polite —
one request per ~0.8 s, ~25 requests total):

1. Do persona pieces carry pack membership (more than one packIdentity entry,
   a bundle-type entry, or a pack-ish contentType)? — the "thematic persona
   accessory packs" hypothesis. Samples the PersonaDurable catalog across the
   creationDate range (newest + oldest pages).
2. What do the marketplace-native theme tags (tag.*) on pieces look like? —
   how many distinct themes, how many pieces carry one, distribution, so the
   panel can group accessories into thematic packs by tag.

Also walks the MarketplaceDurableCatalog_V1.2 bundles and tallies packIdentity
types (skinpack / resourcepack / worldtemplate / behaviorpack / anything else).

Results land in work/persona_tags_probe.json + stdout. Nothing is committed.
"""
import json
import os
import sys
import time
import urllib.request

API = "https://20ca2.playfabapi.com"
SCID = "4fc10100-5f7a-4470-899b-280835760c07"
OUT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../work/persona_tags_probe.json"))
DEVICE_ID = "apace-persona-tags-probe"

PIECE_PAGES = 6       # pages per direction (asc + desc) — 6 × 100 × 2 = 1200 pieces sampled
BUNDLE_PAGES = 16     # the whole bundle catalog (~1.4k entries) — walk it fully
TOP = 100


def post(path, body, headers):
    req = urllib.request.Request(
        API + path,
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.load(r)


def login():
    login = post("/Client/LoginWithAndroidDeviceID", {
        "TitleId": "20ca2",
        "AndroidDeviceId": DEVICE_ID,
        "AndroidDevice": "Pixel", "OS": "Linux", "CreateAccount": True,
    }, {})["data"]
    token = login["EntityToken"]["EntityToken"]
    master = post("/Authentication/GetEntityToken", {
        "Entity": {"Id": login["PlayFabId"], "Type": "master_player_account"},
    }, {"X-EntityToken": token})["data"]["EntityToken"]
    return master


def search(master, flt, order, skip):
    return post("/Catalog/Search", {
        "Filter": flt, "OrderBy": order, "scid": SCID, "skip": skip, "top": TOP,
    }, {"X-EntityToken": master})["data"]


def walk(master, flt, order, pages):
    items = []
    for page in range(pages):
        data = search(master, flt, order, page * TOP)
        got = data.get("Items") or []
        items.extend(got)
        if len(got) < TOP:
            break
        time.sleep(0.8)
    return items


def main():
    master = login()
    print("logged in")

    piece_flt = "(contentType eq 'PersonaDurable')"
    pieces = []
    for order in ("creationDate desc", "creationDate asc"):
        pieces.extend(walk(master, piece_flt, order, PIECE_PAGES))
        time.sleep(0.8)
    print(f"sampled {len(pieces)} PersonaDurable entries")

    # 1. pack membership evidence
    identity_lengths, identity_types, content_types = {}, {}, {}
    tagged_pieces = 0
    tag_freq, theme_pieces = {}, {}
    piece_types = {}
    for it in pieces:
        dp = it.get("DisplayProperties") or {}
        content_types[it.get("ContentType") or "?"] = content_types.get(it.get("ContentType") or "?", 0) + 1
        packs = dp.get("packIdentity") or []
        identity_lengths[len(packs)] = identity_lengths.get(len(packs), 0) + 1
        for p in packs:
            identity_types[p.get("type")] = identity_types.get(p.get("type"), 0) + 1
        pt = dp.get("pieceType") or "?"
        piece_types[pt] = piece_types.get(pt, 0) + 1
        tags = [t for t in (it.get("Tags") or []) if isinstance(t, str)]
        themes = [t for t in tags if t.startswith("tag.")]
        if themes:
            tagged_pieces += 1
        for t in tags:
            tag_freq[t] = tag_freq.get(t, 0) + 1
        for t in themes:
            theme_pieces.setdefault(t, set()).add(it.get("Id"))

    print("\n--- pack membership evidence (PersonaDurable) ---")
    print("contentTypes:", content_types)
    print("packIdentity array lengths:", dict(sorted(identity_lengths.items())))
    print("packIdentity types:", identity_types)
    print("pieceTypes:", json.dumps(dict(sorted(piece_types.items(), key=lambda kv: -kv[1]))))

    print("\n--- theme tags (tag.*) on pieces ---")
    print(f"pieces carrying at least one tag.*: {tagged_pieces}/{len(pieces)}")
    print(f"distinct tag.* values: {sum(1 for t in tag_freq if t.startswith('tag.'))}")
    themes = sorted(theme_pieces.items(), key=lambda kv: -len(kv[1]))
    for t, ids in themes[:40]:
        print(f"  {t}: {len(ids)} piece(s)")
    top_plain = sorted(((t, c) for t, c in tag_freq.items() if not t.startswith("tag.")), key=lambda kv: -kv[1])[:25]
    print("top non-tag.* tags:", top_plain)

    # 2. bundle catalog — packIdentity types across ALL bundles
    bundles = walk(master, "(contentType eq 'MarketplaceDurableCatalog_V1.2')", "creationDate asc", BUNDLE_PAGES)
    bundle_types, bundle_titles = {}, {}
    for it in bundles:
        dp = it.get("DisplayProperties") or {}
        for p in dp.get("packIdentity") or []:
            bundle_types[p.get("type")] = bundle_types.get(p.get("type"), 0) + 1
        title = (it.get("Title") or {}).get("neutral") or (it.get("Title") or {}).get("NEUTRAL")
        bundle_titles[title] = bundle_types
    print(f"\n--- bundles: {len(bundles)} entries ---")
    print("packIdentity types:", bundle_types)

    out = {
        "sampledPieces": len(pieces),
        "pieceContentTypes": content_types,
        "pieceIdentityLengths": {str(k): v for k, v in identity_lengths.items()},
        "pieceIdentityTypes": identity_types,
        "pieceTypes": piece_types,
        "piecesCarryingTagStar": tagged_pieces,
        "distinctTagStar": sum(1 for t in tag_freq if t.startswith("tag.")),
        "themes": [{"tag": t, "pieces": len(ids)} for t, ids in themes],
        "topNonTagTags": dict(top_plain),
        "bundleCount": len(bundles),
        "bundleIdentityTypes": bundle_types,
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(out, f, indent=1)
    print(f"\nsaved {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
