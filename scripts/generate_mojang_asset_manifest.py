#!/usr/bin/env python3
"""Generate the Mojang asset manifest for the Apace asset gate.

The repo ships ONLY this manifest (facts about the additional Mojang assets the
server needs) — never the assets themselves. The panel's asset gate
(see docs/mojang-asset-gate.md) reads the manifest, shows the consent prompt and
downloads the files into the persistent data volume (<data>/mojang-assets/).

The script reuses the fetch_persona_catalog.py login/selection logic:

1. Anonymous PlayFab login (AndroidDeviceID) to the still-live Minecraft Earth
   title (20ca2) and a master_player_account entity token.
2. Catalog/Search across every persona piece type (oldest first, the 2019
   character creator launch set) — the same selection as
   generate_persona_items.py: free (price 0) 1P "Minecraft" items created
   before the MCE client freeze (2021-01-01), deduplicated per piece.
3. Catalog/GetItem per selected item — Search returns empty Contents, GetItem
   carries the real personabinary entry with its xforge CDN URL.
4. Every asset URL is probed: a successful download yields sha256 + size and
   "verified": true. A failed download falls back to hashing the byte-identical
   copy in staticdata-extras/playfab/persona/ (the exact bytes the real service
   served) and records "verified": false — the URL is still recorded as a fact,
   but the gate will not block startup on unverified items (no reachable source
   at generation time).
5. Default skins section: the 9 Minecraft default skins (Steve, Alex, Sunny,
   Kai, Makena, Zuri, Efe, Ari, Noor) as raw 64x64 textures, verified by
   download (PNG magic + IHDR dimensions).

Output: staticdata-extras/playfab/mojang-assets.manifest.json (commit this).
Working directory: /tmp/mojang-manifest-work (override with MOJANG_MANIFEST_WORKDIR;
NEVER inside the repo — no Mojang content is stored in the repo).

Usage:
    python3 scripts/generate_mojang_asset_manifest.py [--skip-binaries] [--skip-thumbs]
"""
import hashlib
import json
import os
import struct
import sys
import time
import urllib.request
import urllib.error

API = "https://20ca2.playfabapi.com"
SCID = "4fc10100-5f7a-4470-899b-280835760c07"
REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
OUT = os.path.join(REPO, "staticdata-extras/playfab/mojang-assets.manifest.json")
WORK = os.environ.get("MOJANG_MANIFEST_WORKDIR", "/tmp/mojang-manifest-work")
DEVICE_ID = "apace-manifest-fetcher"
UA = "ApaceDataGen/1.0"

# every piece type the catalog has ever served; the MCE-era ones matter most
TYPES = [
    "persona_eyes", "persona_hair", "persona_mouth", "persona_face",
    "persona_face_accessory", "persona_head", "persona_arms", "persona_legs",
    "persona_top", "persona_bottom", "persona_outerwear", "persona_hands",
    "persona_hand", "persona_feet", "persona_ears", "persona_cape",
    "persona_hood", "persona_back", "persona_body", "persona_skins",
    "persona_skin", "persona_innerwear",
]
TOP = 100     # items per page
PAGES = 2     # pages per type: the two oldest pages reach well past the MCE freeze (Jan 2021)

BRF_PIECE = "4f7cdadd-a33c-489d-8969-752ca689f567"  # hardcoded in CatalogController
BRF_ITEM = "230f5996-04b2-4f0e-83e5-4056c7f1d946"
CUTOFF = "2020-12-31"  # MCE client freeze: January 2021

# Default skins (Bedrock vanilla skin pack art). Raw 64x64 textures hosted on
# minecraft.wiki (File: pages of the default skin articles). Verified by
# download: PNG magic + 64x64 IHDR. One texture per skin is enough for the
# gate's default set (Steve/Alex duplicate the standard pieces — fine).
DEFAULT_SKINS = [
    ("Steve", "https://minecraft.wiki/images/Steve_BE_skin.png"),
    ("Alex", "https://minecraft.wiki/images/Alex_skin.png"),
    ("Sunny", "https://minecraft.wiki/images/Sunny_%28texture%29_BE1.png"),
    ("Kai", "https://minecraft.wiki/images/Kai_%28texture%29_BE1.png"),
    ("Makena", "https://minecraft.wiki/images/Makena_%28texture%29_BE1.png"),
    ("Zuri", "https://minecraft.wiki/images/Zuri_%28texture%29_BE1.png"),
    ("Efe", "https://minecraft.wiki/images/Efe_%28texture%29_BE1.png"),
    ("Ari", "https://minecraft.wiki/images/Ari_%28texture%29_BE1.png"),
    ("Noor", "https://minecraft.wiki/images/Noor_%28texture%29_BE1.png"),
]


def post(path, body, headers):
    req = urllib.request.Request(
        API + path,
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.load(r)


def fetch_bytes(url, attempts=3):
    """Download url, return bytes or None."""
    for attempt in range(attempts):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=45) as r:
                return r.read()
        except Exception as e:
            if attempt == attempts - 1:
                print(f"  download FAIL ({e}): {url[:120]}")
                return None
            time.sleep(1.5)


def sha256_bytes(data):
    return hashlib.sha256(data).hexdigest()


def png_dimensions(data):
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", data[16:24])


def cache_path(kind, name):
    d = os.path.join(WORK, kind)
    os.makedirs(d, exist_ok=True)
    return os.path.join(d, name)


def cached_get(kind, name, url):
    """Cache a download under the work dir; returns (data, sha256, size) or None."""
    path = cache_path(kind, name)
    if os.path.exists(path):
        with open(path, "rb") as f:
            return f.read()
    data = fetch_bytes(url)
    if data is not None:
        with open(path, "wb") as f:
            f.write(data)
    return data


def login():
    login = post("/Client/LoginWithAndroidDeviceID", {
        "TitleId": "20ca2",
        "AndroidDeviceId": DEVICE_ID,
        "AndroidDevice": "Pixel", "OS": "Android 10", "CreateAccount": True,
    }, {})["data"]
    token = login["EntityToken"]["EntityToken"]
    master = post("/Authentication/GetEntityToken", {
        "Entity": {"Id": login["PlayFabId"], "Type": "master_player_account"},
    }, {"X-EntityToken": token})["data"]["EntityToken"]
    print("logged in:", login["PlayFabId"])
    return master


def select_catalog_items(master):
    """Same selection rules as generate_persona_items.py (the shipped 231 set)."""
    items = []
    for ptype in TYPES:
        skip = 0
        while True:
            body = {
                "Filter": f"(contentType eq 'PersonaDurable' and displayProperties/pieceType eq '{ptype}')",
                "OrderBy": "creationDate asc",
                "scid": SCID,
                "skip": skip,
                "top": TOP,
            }
            for attempt in range(3):
                try:
                    d = post("/Catalog/Search", body, {"X-EntityToken": master})
                    break
                except Exception as e:
                    if attempt == 2:
                        print(ptype, "search error:", e)
                        d = None
                    time.sleep(3)
            if d is None:
                break
            data = d.get("data", {})
            page = data.get("Items", [])
            if not page:
                break
            items.extend(page)
            skip += TOP
            if skip >= PAGES * TOP or skip >= data.get("Count", 0):
                break
            time.sleep(1.0)

    print("fetched items:", len(items))
    selected = []
    seen_piece = set()
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
            continue
        seen_piece.add((piece_type, piece_id))
        selected.append(it)
    print("selected persona items:", len(selected))
    return selected


def get_full_item(master, item_id):
    """Catalog/GetItem — the only endpoint that returns Contents (xforge URLs)."""
    fn = cache_path("getitem", item_id + ".json")
    if os.path.exists(fn):
        with open(fn) as f:
            return json.load(f)
    for attempt in range(3):
        try:
            d = post("/Catalog/GetItem", {"Id": item_id, "Scid": SCID}, {"X-EntityToken": master})
            item = d["data"]["Item"]
            with open(fn, "w") as f:
                json.dump(item, f)
            time.sleep(0.15)
            return item
        except Exception as e:
            if attempt == 2:
                print(f"  GetItem FAIL {item_id}: {e}")
                return None
            time.sleep(3)


def main():
    skip_binaries = "--skip-binaries" in sys.argv
    skip_thumbs = "--skip-thumbs" in sys.argv
    os.makedirs(WORK, exist_ok=True)

    master = login()
    selected = select_catalog_items(master)

    manifest_items = []
    stats = {}

    for it in selected:
        item_id = it["Id"]
        title = (it.get("Title") or {}).get("neutral") or (it.get("Title") or {}).get("NEUTRAL") or item_id
        full = get_full_item(master, item_id)

        # --- persona binary ---
        url = ""
        if full:
            contents = full.get("Contents") or []
            binary = next((c for c in contents if c.get("Type") == "personabinary"), contents[0] if contents else None)
            if binary:
                url = binary.get("Url", "")
        if skip_binaries:
            data = None
        else:
            data = cached_get("persona", item_id + ".zip", url) if url else None
        entry = {
            "kind": "persona",
            "file": f"playfab/persona/{item_id}.zip",
            "url": url,
            "verified": False,
        }
        if data is not None and len(data) > 100:
            entry["sha256"] = sha256_bytes(data)
            entry["size"] = len(data)
            entry["verified"] = True
        else:
            # fall back to the byte-identical copy shipped in the repo
            # (staticdata-extras/playfab/persona/) to still record the facts
            local = os.path.join(REPO, "staticdata-extras/playfab/persona", item_id + ".zip")
            if os.path.exists(local):
                with open(local, "rb") as f:
                    blob = f.read()
                entry["sha256"] = sha256_bytes(blob)
                entry["size"] = len(blob)
                print(f"  persona source offline, hashed repo copy: {item_id}")
        manifest_items.append(entry)

        # --- thumbnail ---
        img = next((i for i in (it.get("Images") or []) if i.get("Type") == "Thumbnail"), (it.get("Images") or [None])[0])
        if img:
            t_url = img.get("Url", "")
            ext = ".png" if ".png" in t_url.lower() else ".jpg"
            t_file = f"images/{img['Id']}{ext}"
            if skip_thumbs:
                t_data = None
            else:
                t_data = cached_get("thumbnails", img["Id"] + ext, t_url) if t_url else None
            t_entry = {
                "kind": "thumbnail",
                "file": t_file,
                "url": t_url,
                "verified": False,
            }
            if t_data is not None and len(t_data) > 100:
                t_entry["sha256"] = sha256_bytes(t_data)
                t_entry["size"] = len(t_data)
                t_entry["verified"] = True
            manifest_items.append(t_entry)

        if len(manifest_items) % 40 == 0:
            print(f"  progress: {len(manifest_items)} entries, last: {title}")

    # --- default skins ---
    for name, url in DEFAULT_SKINS:
        data = cached_get("skins", name + ".png", url)
        entry = {
            "kind": "skin-texture",
            "file": f"skins/{name}.png",
            "name": name,
            "url": url,
            "verified": False,
        }
        if data is not None:
            dims = png_dimensions(data)
            if dims == (64, 64):
                entry["sha256"] = sha256_bytes(data)
                entry["size"] = len(data)
                entry["verified"] = True
            else:
                print(f"  skin {name}: bad dimensions {dims}")
        manifest_items.append(entry)

    verified = sum(1 for e in manifest_items if e["verified"])
    by_kind = {}
    for e in manifest_items:
        k = e["kind"]
        by_kind.setdefault(k, [0, 0, 0])
        by_kind[k][0] += 1
        by_kind[k][1] += 1 if e["verified"] else 0
        by_kind[k][2] += e.get("size") or 0

    manifest = {
        "version": 1,
        "generatedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "sources": {
            "catalog": API,
            "assetCdn": "https://xforgeassets001.xboxlive.com",
            "skinTextures": "https://minecraft.wiki",
        },
        "items": manifest_items,
    }

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"manifest written: {OUT}")
    for k, (total, ok, size) in sorted(by_kind.items()):
        print(f"  {k}: {ok}/{total} verified, {size / 1024:.0f} KiB")
    print(f"  total: {verified}/{len(manifest_items)} verified")


if __name__ == "__main__":
    main()
