#!/usr/bin/env python3
"""Validate the theme-pack grouping algorithm on a real raw sample.

Fetches a few polite pages of the live PersonaDurable catalog, writes a
synthetic index.json in the exact shape LivePersonaCatalogSync produces, then
reimplements PersonaPackIndex.ReadThemePacks / LivePersonaCatalog.GetOffer
theme rules 1:1 (piece-type suffix exclusion, tag.* themes, base pieces) and
prints what the panel picker would show.
"""
import json
import os
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
from probe_persona_tags import API, SCID, login, search, TOP  # noqa: E402

OUT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../work/theme_packs_validation.json"))
PAGES = 4


def main():
    master = login()
    print("logged in")

    items = []
    for page in range(PAGES):
        data = search(master, "(contentType eq 'PersonaDurable')", "creationDate desc", page * TOP)
        items.extend(data.get("Items") or [])
        if len(data.get("Items") or []) < TOP:
            break
        time.sleep(0.8)
    print(f"sampled {len(items)} raw entries")

    # --- build a synthetic index.json in the SyncIndex shape (what the panel reads) ---
    by_piece = {}
    for raw in items:
        dp = raw.get("DisplayProperties") or {}
        packs = dp.get("packIdentity") or []
        first = packs[0] if packs else {}
        if dp.get("pieceType") is None or first.get("type") != "persona_piece":
            continue
        piece_id = first.get("uuid")
        tags = [t for t in (raw.get("Tags") or []) if isinstance(t, str)]
        if "hidden_offer" in [t.lower() for t in tags]:
            continue
        title = (raw.get("Title") or {}).get("neutral") or (raw.get("Title") or {}).get("NEUTRAL") or raw.get("Id")
        existing = by_piece.get(piece_id)
        if existing is not None and existing["_created"] >= raw.get("CreationDate", ""):
            continue
        by_piece[piece_id] = {
            "id": raw.get("Id"), "title": title, "pieceType": dp["pieceType"],
            "tags": tags, "isEmote": dp["pieceType"] == "persona_emote",
            "_created": raw.get("CreationDate", ""),
        }
    index_items = [{k: v for k, v in it.items() if not k.startswith("_")} for it in by_piece.values()]
    index = {
        "syncedAt": None,
        "rawItemCount": len(items),
        "emoteCount": sum(1 for it in index_items if it["isEmote"]),
        "items": index_items,
        "skinPacks": [],
    }
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(index, f, indent=1)
    print(f"synthetic index written: {OUT} ({len(index_items)} unique pieces)")

    # --- ReadThemePacks 1:1 ---
    suffixes = {it["pieceType"][len("persona_"):] for it in index_items
                if not it["isEmote"] and it["pieceType"].startswith("persona_")}
    def is_theme(tag):
        return tag.lower().startswith("tag.") and tag[4:] not in suffixes

    by_tag, themed, base = {}, 0, 0
    for it in index_items:
        if it["isEmote"]:
            continue
        themes = sorted({t for t in (it.get("tags") or []) if is_theme(t)})
        if not themes:
            base += 1
            continue
        themed += 1
        for t in themes:
            e = by_tag.setdefault(t.lower(), {"count": 0, "types": {}})
            e["count"] += 1
            e["types"][it["pieceType"]] = e["types"].get(it["pieceType"], 0) + 1

    packs = sorted(by_tag.items(), key=lambda kv: (-kv[1]["count"], kv[0]))
    print(f"\nthemed pieces: {themed}, base pieces: {base}, distinct packs: {len(packs)}")
    for tag, e in packs[:15]:
        types = ", ".join(f"{c} {t[len('persona_'):]}" for t, c in
                          sorted(e["types"].items(), key=lambda kv: -kv[1])[:3])
        title = tag[4:].replace("_", " ")
        title = title[:1].upper() + title[1:]
        print(f"  {title:<28} {e['count']:>3} pieces ({types})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
