#!/usr/bin/env python3
"""Convert the raw smoke-sync sample into the volume index.json shape.

Mirrors the BuildIndexRecord rules of the panel's LivePersonaCatalogSync (facts
only — no unlock-all stripping here; the strip happens on the offer output in
the ApiServer). The result feeds the C# unlock-all verification harness.

Usage:
    python3 scripts/verify_unlock_all_sample.py
"""
import glob
import json
import os

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
SAMPLES = os.path.join(REPO, "work/live_raw_sample")
OUT = "/tmp/apace-unlock-check/assets/persona-catalog"

HIDDEN_TAG = "hidden_offer"


def first_translation(translations):
    if isinstance(translations, dict):
        for key in ("neutral", "NEUTRAL", "en-US"):
            value = translations.get(key)
            if isinstance(value, str) and value.strip():
                return value
    return "§"


def build_record(raw):
    piece_id = raw.get("Id", "")
    display = raw.get("DisplayProperties") or {}
    piece_type = display.get("pieceType")
    packs = display.get("packIdentity") or [{}]
    pack = packs[0] or {}
    pack_type = pack.get("type")
    uuid = pack.get("uuid")
    version = pack.get("version") or "1.0.0"
    if not piece_type or not uuid or pack_type != "persona_piece":
        return None
    try:
        piece_uuid = str(uuid)
    except Exception:
        return None

    tags = [t for t in (raw.get("Tags") or []) if isinstance(t, str)]
    if HIDDEN_TAG in tags:
        return None

    keywords = {}
    for lang, values in (raw.get("Keywords") or {}).items():
        vals = [v for v in (values or {}).get("Values", []) if isinstance(v, str) and v]
        if vals:
            keywords[lang] = vals

    thumb_id = thumb_url = None
    for image in raw.get("Images") or []:
        if image.get("Type") == "Thumbnail" or thumb_id is None:
            thumb_id = image.get("Id")
            thumb_url = image.get("Url")
            if image.get("Type") == "Thumbnail":
                break

    refs = [{"id": r.get("Id"), "amount": r.get("Amount", 1)} for r in (raw.get("ItemReferences") or []) if r.get("Id")]
    created = (raw.get("CreationDate") or "").replace("Z", "+00:00")
    modified = (raw.get("LastModifiedDate") or created).replace("Z", "+00:00")
    start = (raw.get("StartDate") or "").replace("Z", "+00:00") or None

    return {
        "id": piece_id,
        "title": first_translation(raw.get("Title")),
        "description": first_translation(raw.get("Description")),
        "keywords": keywords or None,
        "tags": tags,
        "pieceType": piece_type,
        "pieceId": piece_uuid,
        "pieceVersion": version,
        "rarity": display.get("rarity") or "common",
        "creatorName": display.get("creatorName") or "Minecraft",
        "creatorEntityId": (raw.get("CreatorEntityKey") or {}).get("Id") or "301F442C3B63DC20",
        "offerId": display.get("offerId"),
        "itemReferences": refs,
        "thumbnailId": thumb_id,
        "thumbnailUrl": thumb_url,
        "creationDate": created,
        "lastModifiedDate": modified,
        "startDate": start,
        "isEmote": piece_type == "persona_emote",
    }


def main():
    raw_items = []
    for fn in ("raw_items.json", "raw_items_newest.json", "raw_emotes.json"):
        path = os.path.join(SAMPLES, fn)
        if os.path.exists(path):
            with open(path) as f:
                raw_items.extend(json.load(f))
    print("raw sample entries:", len(raw_items))

    records, seen_piece = [], {}
    for raw in raw_items:
        record = build_record(raw)
        if record is None:
            continue
        pid = record["pieceId"]
        if pid in seen_piece:
            # newest wins, exactly like the sync dedupe
            if record["creationDate"] > seen_piece[pid]["creationDate"]:
                records[records.index(seen_piece[pid])] = record
                seen_piece[pid] = record
            continue
        seen_piece[pid] = record
        records.append(record)

    emotes = sum(1 for r in records if r["isEmote"])
    index = {"syncedAt": "2026-11-22T18:00:00+00:00", "rawItemCount": len(raw_items), "emoteCount": emotes, "items": records}
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "index.json"), "w") as f:
        json.dump(index, f, indent=1)
    print(f"index written: {len(records)} unique piece(s) ({emotes} emotes) -> {OUT}/index.json")
    lock_tagged = [r["id"] for r in records if set(r["tags"]) & {"is_achievement", "earth_achievement", "vanilla_achievement"}]
    print("records with achievement tags (strip must remove them):", len(lock_tagged), lock_tagged[:5])


if __name__ == "__main__":
    main()
