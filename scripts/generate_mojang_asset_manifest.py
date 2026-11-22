#!/usr/bin/env python3
"""Generate the Mojang asset manifest for the Apace asset gate.

The repo ships ONLY this manifest (facts about the additional Mojang assets the
server needs) — never the assets themselves. The panel's asset gate
(see docs/mojang-asset-gate.md) reads the manifest, shows the consent prompt and
downloads the files into the persistent data volume (<data>/mojang-assets/).

Item set (ground truth = the repo itself):

1. Persona pieces: the item JSONs shipped under staticdata-extras/playfab/items/
   with Data.CreatorName == "Minecraft" AND a matching binary in
   staticdata-extras/playfab/persona/ — exactly the 231 real dressing-room items
   fetched from the live catalog (commit 043d7bb). The 3 Apace capes and the
   local-only packs (skin packs, store config, ...) are excluded automatically:
   they are not creator "Minecraft" / have no persona zip.
2. Thumbnails: one per selected item, keyed by the item's ThumbnailImageId —
   the same id the client requests at /playfab/images/<id>.png and the runtime
   serves from <mojang-assets>/images/<id>.png (MojangImagesCdnController).
   The live catalog is only the URL source; the file name always comes from the
   shipped item JSON so it cannot drift.
3. Default skins: the 9 Minecraft default skins (Steve, Alex, Sunny, Kai,
   Makena, Zuri, Efe, Ari, Noor) as raw skin textures.

Source URLs come from the still-live PlayFab catalog (Minecraft Earth title
20ca2, anonymous AndroidDeviceID login, same as fetch_persona_catalog.py):

- Catalog/Search (oldest first, the 2019 character creator launch set) maps
  item id -> Images (thumbnail URLs). Search never returns Contents.
- Catalog/GetItem per item carries the real personabinary Contents entry with
  its xforge CDN URL.

Every asset URL is probed: a successful download with the expected payload
signature (zip magic / PNG magic + dimensions) yields sha256 + size and
"verified": true. On a failed download the generator falls back to hashing the
byte-identical copy shipped in the repo (staticdata-extras/playfab/persona/,
src/Apace.ApiServer/wwwroot/playfab/images/) and records "verified": false —
the URL is still recorded as a fact, but the gate will not block startup on
unverified items (no reachable source at generation time). Definitive HTTP
failures (404/410) are not retried.

Default skins are hosted on minecraft.wiki (the File: pages of the default skin
articles; the Bedrock vanilla skin pack textures — the "(texture)" files
WITHOUT the BE suffix are scaled 640x640 renders, do NOT use those). Each URL
was verified by download: PNG magic + IHDR 64x64. The wiki's Steve/Alex "BE"
files match the Bedrock vanilla skin pack set, consistent with the 7
"(texture)_BE1" files of the 1.20.60 default-skin update.

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

PERSONA_DIR = os.path.join(REPO, "staticdata-extras/playfab/persona")
ITEMS_DIR = os.path.join(REPO, "staticdata-extras/playfab/items")
IMAGES_DIR = os.path.join(REPO, "src/Apace.ApiServer/wwwroot/playfab/images")

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

# Default skins (Bedrock vanilla skin pack art). Raw 64x64 skin textures hosted
# on minecraft.wiki (the File: pages of the default skin articles; the wiki's
# "(texture)" files without the BE suffix are scaled 640x640 renders — do NOT
# use those). Verified by download: PNG magic + 64x64 IHDR. One texture
# per skin is enough for the gate's default set (Steve/Alex duplicate the
# standard pieces — fine).
DEFAULT_SKINS = [
    ("Steve", "https://minecraft.wiki/images/Steve_BE_skin.png"),
    ("Alex", "https://minecraft.wiki/images/Alex_BE_skin.png"),
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
    """Download url, return bytes or None. 404/410 are definitive (no retry)."""
    for attempt in range(attempts):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=45) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code in (404, 410):
                print(f"  download FAIL ({e.code}, definitive): {url[:120]}")
                return None
            if attempt == attempts - 1:
                print(f"  download FAIL ({e}): {url[:120]}")
                return None
            time.sleep(1.5)
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


def hash_repo_copy(path):
    """Hash a repo-shipped fallback copy (a fact, not a verified source)."""
    try:
        with open(path, "rb") as f:
            blob = f.read()
        return sha256_bytes(blob), len(blob)
    except OSError:
        return None


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


def load_repo_items():
    """Ground truth: the shipped Mojang persona set (creator 'Minecraft' + zip).

    Exactly the 231 items committed in 043d7bb — Apace's own pieces (capes,
    skin packs, store config, ...) have a different creator or no persona zip
    and are excluded automatically.
    """
    items = {}
    for fn in sorted(os.listdir(ITEMS_DIR)):
        if not fn.endswith(".json"):
            continue
        with open(os.path.join(ITEMS_DIR, fn)) as f:
            d = json.load(f)
        data = d.get("Data") or {}
        if data.get("CreatorName") != "Minecraft":
            continue
        item_id = d["Id"]
        if not os.path.exists(os.path.join(PERSONA_DIR, item_id + ".zip")):
            print(f"  skipped (no persona zip): {item_id} {d.get('Title')}")
            continue
        items[item_id] = d
    print("repo Mojang persona items:", len(items))
    return items


def search_live_items(master):
    """Map item id -> live catalog item (Search returns Images, never Contents).

    Selection params are the same as fetch_persona_catalog.py / the shipped set
    (oldest two pages per piece type). Only used as the URL source: the item set
    itself is ground-truthed against the repo (load_repo_items).
    """
    live = {}
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
            for it in page:
                live.setdefault(it.get("Id"), it)
            skip += TOP
            if skip >= PAGES * TOP or skip >= data.get("Count", 0):
                break
            time.sleep(1.0)
    print("live catalog items scanned:", len(live))
    return live


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

    repo_items = load_repo_items()
    if not repo_items:
        sys.exit("no repo Mojang persona items found — wrong checkout?")

    master = login()
    live = search_live_items(master)

    missing_from_live = set(repo_items) - set(live)
    if missing_from_live:
        print(f"WARNING: {len(missing_from_live)} repo items not in the live search pages "
              f"(thumbnail URLs will fall back to GetItem)")

    manifest_items = []
    stats = {}

    for item_id, local in sorted(repo_items.items()):
        title = local.get("Title") or item_id
        full = get_full_item(master, item_id)
        live_item = live.get(item_id) or {}
        images = live_item.get("Images") or (full or {}).get("Images") or []

        # --- persona binary (URL = live fact; payload ground truth = shipped zip) ---
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
        if data is not None and len(data) > 100 and data[:4] == b"PK\x03\x04":
            entry["sha256"] = sha256_bytes(data)
            entry["size"] = len(data)
            entry["verified"] = True
        else:
            # fall back to the byte-identical copy shipped in the repo
            # (staticdata-extras/playfab/persona/) to still record the facts
            fact = hash_repo_copy(os.path.join(PERSONA_DIR, item_id + ".zip"))
            if fact:
                entry["sha256"], entry["size"] = fact
            if url:
                print(f"  persona source offline, hashed repo copy: {item_id}")
        manifest_items.append(entry)

        # --- thumbnail (file name = shipped ThumbnailImageId; URL = live fact) ---
        thumb_id = (local.get("ThumbnailImageId") or "").strip()
        img = next((i for i in images if i.get("Type") == "Thumbnail"), images[0] if images else None)
        live_img_id = ((img or {}).get("Id") or "").strip()
        if live_img_id and thumb_id and live_img_id != thumb_id:
            print(f"  WARN thumbnail id mismatch for {item_id}: repo {thumb_id} != live {live_img_id} (using repo)")
        file_id = thumb_id or live_img_id
        if not file_id:
            print(f"  no thumbnail id for {item_id} — skipped")
            continue
        t_url = (img or {}).get("Url", "")
        t_entry = {
            "kind": "thumbnail",
            "file": f"images/{file_id}.png",
            "url": t_url,
            "verified": False,
        }
        if skip_thumbs:
            t_data = None
        else:
            t_data = cached_get("thumbnails", file_id + ".png", t_url) if t_url else None
        if t_data is not None and len(t_data) > 100 and png_dimensions(t_data):
            t_entry["sha256"] = sha256_bytes(t_data)
            t_entry["size"] = len(t_data)
            t_entry["verified"] = True
        else:
            # fall back to the byte-identical repo copy served at /playfab/images/
            fact = hash_repo_copy(os.path.join(IMAGES_DIR, file_id + ".png"))
            if fact:
                t_entry["sha256"], t_entry["size"] = fact
            if t_url:
                print(f"  thumbnail source offline, hashed repo copy: {file_id}")
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
            if dims in ((64, 64), (128, 128)):
                entry["sha256"] = sha256_bytes(data)
                entry["size"] = len(data)
                entry["verified"] = True
                print(f"  skin {name}: verified {dims[0]}x{dims[1]}, {len(data)} B")
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
    unverified = [e["file"] for e in manifest_items if not e["verified"]]
    if unverified:
        print(f"  unverified ({len(unverified)}): {', '.join(unverified[:10])}"
              + (f", ... ({len(unverified)} total)" if len(unverified) > 10 else ""))


if __name__ == "__main__":
    main()
