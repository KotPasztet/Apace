#!/usr/bin/env python3
"""Generate Apace persona (dressing room) items from fetched catalog data.

Selection: free (price 0) 1P items (creatorName "Minecraft") created before
2021-01-01 — the MCE client froze in January 2021 and does not know newer
pieces — across every piece type, excluding the Bold Rabbit Feet (hardcoded in
CatalogController). These are exactly the free items the real MCE dressing room
showed before the sunset.

Output (relative to the repo root):
- staticdata-extras/playfab/items/<catalogItemId>.json
- src/Apace.ApiServer/wwwroot/playfab/images/<imageId>.png (thumbnails)

Usage:
    python3 scripts/fetch_persona_catalog.py     # first: raw data -> work/persona_raw/
    python3 scripts/generate_persona_items.py
"""
import glob
import json
import os
import time
import urllib.request

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
RAW = os.path.join(REPO, "work/persona_raw")
ITEMS_OUT = os.path.join(REPO, "staticdata-extras/playfab/items")
IMAGES_OUT = os.path.join(REPO, "src/Apace.ApiServer/wwwroot/playfab/images")

BRF_PIECE = "4f7cdadd-a33c-489d-8969-752ca689f567"  # hardcoded in CatalogController
BRF_ITEM = "230f5996-04b2-4f0e-83e5-4056c7f1d946"
CUTOFF = "2020-12-31"  # MCE client freeze: January 2021


def main():
    items = []
    for fn in glob.glob(os.path.join(RAW, "*.json")):
        with open(fn) as f:
            items.extend(json.load(f))
    print("fetched items:", len(items))

    # existing item ids in the staticdata submodule (collision check)
    existing = set()
    for fn in glob.glob(os.path.join(REPO, "staticdata/playfab/items/*.json")):
        existing.add(os.path.basename(fn)[:-5])
    existing.add(BRF_ITEM)

    selected = []
    seen_piece = set()
    no_image = 0
    for it in sorted(items, key=lambda x: x.get("CreationDate", "")):
        dp = it.get("DisplayProperties", {})
        pack = (dp.get("packIdentity") or [{}])[0]
        piece_id = pack.get("uuid")
        piece_type = dp.get("pieceType")
        if dp.get("creatorName") != "Minecraft":
            continue
        if dp.get("price") != 0:
            continue
        if (it.get("CreationDate", "")[:10]) > CUTOFF:
            continue
        if piece_id == BRF_PIECE or it.get("Id") == BRF_ITEM:
            continue
        if pack.get("type") != "persona_piece" or not piece_id or not piece_type:
            continue
        if (piece_type, piece_id) in seen_piece:
            continue
        if not it.get("Images"):
            no_image += 1
            continue
        if it["Id"] in existing:
            print("id collision:", it["Id"], it.get("Title", {}).get("neutral"))
            continue
        seen_piece.add((piece_type, piece_id))
        selected.append(it)

    print(f"selected: {len(selected)} (skipped without thumbnail: {no_image})")
    by_type = {}
    for it in selected:
        by_type.setdefault(it["DisplayProperties"]["pieceType"], []).append(it)
    for t, lst in sorted(by_type.items()):
        print(f"  {t}: {len(lst)}")

    os.makedirs(ITEMS_OUT, exist_ok=True)
    os.makedirs(IMAGES_OUT, exist_ok=True)

    # download thumbnails
    todo = []
    for it in selected:
        img = next((i for i in it["Images"] if i.get("Type") == "Thumbnail"), it["Images"][0])
        url = img["Url"]
        ext = ".png" if ".png" in url.lower() else ".jpg"
        local = img["Id"] + ext
        path = os.path.join(IMAGES_OUT, local)
        if not os.path.exists(path):
            todo.append((url, path))
        it["_thumb_local"] = local
        it["_thumb_id"] = img["Id"]
    print("thumbnails to download:", len(todo))

    ok = fail = 0
    for url, path in todo:
        for attempt in range(3):
            try:
                req = urllib.request.Request(url, headers={"User-Agent": "ApaceDataGen/1.0"})
                with urllib.request.urlopen(req, timeout=30) as r:
                    data = r.read()
                if len(data) < 100:
                    raise RuntimeError("file too short")
                with open(path, "wb") as f:
                    f.write(data)
                ok += 1
                break
            except Exception as e:
                if attempt == 2:
                    print("  FAIL:", url, e)
                    fail += 1
                time.sleep(1.5)
        time.sleep(0.15)
    print(f"thumbnails: ok={ok} fail={fail}")
    if fail:
        # drop items whose thumbnail could not be downloaded
        missing = set(os.path.basename(p) for _, p in todo if not os.path.exists(p))
        before = len(selected)
        selected = [it for it in selected if it["_thumb_local"] not in missing]
        print(f"dropped {before - len(selected)} items without thumbnails")

    # generate the staticdata item jsons
    for it in selected:
        dp = it["DisplayProperties"]
        pack = dp["packIdentity"][0]
        offer_id = dp.get("offerId")
        if not offer_id:
            for alt in it.get("AlternateIds", []):
                if alt.get("Type") == "FriendlyId" and alt.get("Value"):
                    offer_id = alt["Value"]
                    break
        if not offer_id:
            offer_id = it["Id"]
        keywords = {}
        for k, v in (it.get("Keywords") or {}).items():
            keywords[k] = {"Values": v.get("Values", [])}
        out = {
            "Purchasable": bool(dp.get("purchasable", True)),
            "Data": {
                "Type": "Persona",
                "PieceType": dp["pieceType"],
                "PieceId": pack["uuid"],
                "OfferId": offer_id,
                "Version": pack.get("version", "1.0.0"),
                "Rarity": dp.get("rarity", "common"),
                "Price": dp.get("price", 0),
                "CreatorName": dp.get("creatorName", "Minecraft"),
                "AssetFileName": None,
            },
            "Title": it["Title"].get("neutral", it["Title"].get("NEUTRAL", "")),
            "Description": it["Description"].get("neutral", it["Description"].get("NEUTRAL", "§")),
            "ThumbnailImageId": it["_thumb_id"],
            "CreationDate": it["CreationDate"],
            "LastModifiedDate": it["LastModifiedDate"],
            "StartDate": it["CreationDate"],
            "Id": it["Id"],
            "FriendlyId": offer_id,
            "SourceEntityId": "B63A0803D3653643",
            "CreatorEntityId": it.get("CreatorEntityKey", {}).get("Id", "301F442C3B63DC20"),
            "Tags": it.get("Tags", []),
            "Keywords": keywords,
            "Contents": [],
            "ItemReferences": [
                {"Id": r["Id"], "Amount": r["Amount"]} for r in it.get("ItemReferences", [])
            ],
            "TitleTranslations": {},
            "DescriptionTranslations": {},
        }
        with open(os.path.join(ITEMS_OUT, it["Id"] + ".json"), "w") as f:
            json.dump(out, f, indent=2, ensure_ascii=False)
            f.write("\n")

    print("saved items:", len(selected))
    os.makedirs(os.path.join(REPO, "work"), exist_ok=True)
    with open(os.path.join(REPO, "work/persona_manifest.json"), "w") as f:
        json.dump([{
            "id": it["Id"], "title": it["Title"].get("neutral"),
            "pieceType": it["DisplayProperties"]["pieceType"],
            "pieceId": it["DisplayProperties"]["packIdentity"][0]["uuid"],
            "thumb": it["_thumb_local"],
            "created": it["CreationDate"][:10],
        } for it in selected], f, indent=1)


if __name__ == "__main__":
    main()
