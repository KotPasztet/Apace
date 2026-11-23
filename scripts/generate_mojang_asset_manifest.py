#!/usr/bin/env python3
"""Generate the Mojang asset manifest.

The repo ships ONLY this manifest (facts about the additional Mojang assets the
server can serve) — never the assets themselves. The manifest records source
URLs and sha256/size facts; the files live in the persistent data volume
(<data>/mojang-assets/), which the CDN controllers serve from and the live
persona marketplace sync replaces the legacy pieces of.

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
"verified": true. Persona binaries are probed on TWO URL shapes per item:
the original catalog (xforge) URL and the same GUID re-hosted on the new
PlayFab content CDN (content1/content2.prod.catalog.playfab.com) — whichever
answers verifies the entry, so a Mojang CDN migration or restore is picked
up automatically on the next regeneration. On a failed download the
generator falls back to hashing the byte-identical copy shipped in the repo
(staticdata-extras/playfab/persona/, src/Apace.ApiServer/wwwroot/playfab/images/)
or, when that was cleaned out of the tree, the byte-identical git-history
copy (043d7bb) — either way it records "verified": false: the URL is still
recorded as a fact, but the gate will not block startup on unverified items
(no reachable source at generation time). Definitive HTTP failures (404/410)
are not retried.

Persona entries additionally carry "fallbackUrls": mirror locations the gate
tries in order when the primary (xforge) URL answers with a non-200. The only
mirror is the mojang-persona-mirror GitHub release (KotPasztet/Apace); the
release may not exist yet — the field is a retry path for the gate, not a
guarantee, and unverified persona items still never block startup.

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
import subprocess
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

# GitHub Releases mirror for the persona piece binaries (the gate walks these
# URLs when the primary xforge URL returns a non-200). The release is built
# from the byte-identical copies of commit 043d7bb; it may not exist yet —
# that is fine: unverified persona entries never block startup.
PERSONA_MIRROR_BASE = "https://github.com/KotPasztet/Apace/releases/download/mojang-persona-mirror"

# The catalog's old asset CDN (xforgeassets*.xboxlive.com) went dark for the
# 231 persona binaries (22.11 probe: 0/231 alive). The PlayFab content CDN
# hosts the same namespace/GUID path under two hostnames — the probe found
# 8/8 NEW catalog items alive there (the old GUIDs did not migrate). Both
# shapes are probed per item so a migration/restore is detected at regen.
ALTERNATE_CDN_HOSTS = [
    "content1.prod.catalog.playfab.com",
    "content2.prod.catalog.playfab.com",
]

# The MCE PlayFab namespace (the title hash): the path segment the old xforge
# URLs carried as pf-namespace-<ns>, and the shape the new content CDN serves.
MCE_NAMESPACE = "b63a0803d3653643"

# Byte-identical git-history copy of the persona binaries (removed from the
# tree by the repo cleanup): used to record sha256/size facts when neither a
# live download nor a repo-shipped copy is available.
HISTORY_REF = "043d7bb"
HISTORY_PERSONA_DIR = "staticdata-extras/playfab/persona"

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


def hash_history_copy(rel_path):
    """Hash the byte-identical git-history copy of a file removed from the tree.

    Same contract as hash_repo_copy: a recorded fact about the payload, never a
    verified reachable source. Used for the persona binaries cleaned out of the
    tree (they live in HISTORY_REF, which is also what the mirror release ships).
    """
    try:
        blob = subprocess.run(
            ["git", "-C", REPO, "show", f"{HISTORY_REF}:{rel_path}"],
            capture_output=True, check=True,
        ).stdout
        return sha256_bytes(blob), len(blob)
    except (subprocess.CalledProcessError, OSError):
        return None


def history_persona_ids():
    """Item ids that have a persona zip in the history copy (HISTORY_REF)."""
    try:
        out = subprocess.run(
            ["git", "-C", REPO, "ls-tree", "--name-only", HISTORY_REF, "--", HISTORY_PERSONA_DIR + "/"],
            capture_output=True, text=True, check=True,
        ).stdout
    except (subprocess.CalledProcessError, OSError) as e:
        print(f"WARNING: cannot list {HISTORY_REF}:{HISTORY_PERSONA_DIR}/ — {e}")
        return set()
    return {os.path.basename(line)[:-4] for line in out.splitlines() if line.endswith(".zip")}


def alternate_cdn_urls(url):
    """The same payload re-hosted on the PlayFab content CDN (content1/content2).

    The new CDN serves pf-namespace/<guid>/<file> paths (the 22.11 probe found
    8/8 NEW catalog items alive there). A URL already carrying a pf-namespace
    path keeps it as-is; a pf-title path (what Catalog/GetItem serves today)
    contributes its trailing <guid>/<file> tail rebuilt as pf-namespace-<ns>.
    Anything else has no known alternate shape.
    """
    def guidish(segment):
        return 32 <= len(segment) <= 36 and all(c in "0123456789abcdefABCDEF-" for c in segment)

    if "/pf-namespace-" in url:
        tail = url.split("/pf-namespace-", 1)[1]
        if not tail or "://" in tail.split("/", 1)[0]:
            return []
        return [f"https://{host}/pf-namespace-{tail}" for host in ALTERNATE_CDN_HOSTS]
    if "/pf-title-" in url:
        segments = [s for s in url.split("/") if s]
        if len(segments) < 2 or not guidish(segments[-2]):
            return []
        tail = f"{segments[-2]}/{segments[-1]}"
        return [f"https://{host}/pf-namespace-{MCE_NAMESPACE}/{tail}" for host in ALTERNATE_CDN_HOSTS]
    return []


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
    and are excluded automatically. Since the repo cleanup removed the binaries
    from the tree, a history copy (HISTORY_REF) also counts as present.
    """
    items = {}
    history = history_persona_ids()
    for fn in sorted(os.listdir(ITEMS_DIR)):
        if not fn.endswith(".json"):
            continue
        with open(os.path.join(ITEMS_DIR, fn)) as f:
            d = json.load(f)
        data = d.get("Data") or {}
        if data.get("CreatorName") != "Minecraft":
            continue
        item_id = d["Id"]
        in_repo = os.path.exists(os.path.join(PERSONA_DIR, item_id + ".zip"))
        in_history = item_id in history
        if not in_repo and not in_history:
            print(f"  skipped (no persona zip in repo or history): {item_id} {d.get('Title')}")
            continue
        items[item_id] = d
    in_tree = sum(1 for i in items if os.path.exists(os.path.join(PERSONA_DIR, i + ".zip")))
    print("repo Mojang persona items:", len(items),
          f"(zip in tree: {in_tree}, in history only: {len(items) - in_tree})")
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


def validate_manifest(manifest):
    """Schema + value validation for the manifest we are about to publish.

    Hard-fails (SystemExit) on anything that would confuse the gate:
      - items: kind/file/url shape, safe relative file paths, known kinds
      - sha256: 64 hex chars when present; size: positive int when present
      - verified: bool
      - fallbackUrls (persona only, optional): list of non-empty https URLs,
        no duplicates inside one entry, no duplicates with the primary url
    Returns the number of validated fallbackUrls (for the summary line).
    """
    problems = []
    fallback_count = 0
    seen_files = set()
    kinds = {"persona", "thumbnail", "skin-texture"}

    for idx, item in enumerate(manifest["items"]):
        where = f"item[{idx}] {item.get('file') or item.get('url') or '?'}"
        kind = item.get("kind")
        if kind not in kinds:
            problems.append(f"{where}: unknown kind {kind!r}")
        file = item.get("file", "")
        if not file or file in seen_files:
            problems.append(f"{where}: duplicate or empty file {file!r}")
        seen_files.add(file)
        if file.startswith("/") or ".." in file.split("/"):
            problems.append(f"{where}: unsafe relative file path {file!r}")
        if not isinstance(item.get("url"), str):
            problems.append(f"{where}: url must be a string")
        if "verified" in item and not isinstance(item["verified"], bool):
            problems.append(f"{where}: verified must be a bool")
        sha = item.get("sha256")
        if sha is not None and (len(sha) != 64 or any(c not in "0123456789abcdef" for c in sha.lower())):
            problems.append(f"{where}: sha256 is not 64 hex chars: {sha!r}")
        size = item.get("size")
        if size is not None and (not isinstance(size, int) or size <= 0):
            problems.append(f"{where}: size must be a positive int: {size!r}")

        fallbacks = item.get("fallbackUrls")
        if fallbacks is not None:
            if kind != "persona":
                problems.append(f"{where}: fallbackUrls only expected on persona entries")
            if not isinstance(fallbacks, list) or not fallbacks or not all(isinstance(u, str) and u for u in fallbacks):
                problems.append(f"{where}: fallbackUrls must be a non-empty list of strings")
            else:
                fallback_count += len(fallbacks)
                if len(set(fallbacks)) != len(fallbacks):
                    problems.append(f"{where}: duplicate fallbackUrls")
                for u in fallbacks:
                    if not u.startswith("https://") or not u.split("://", 1)[-1]:
                        problems.append(f"{where}: fallback URL is not an absolute https URL: {u!r}")
                    elif u == item.get("url"):
                        problems.append(f"{where}: fallback URL duplicates the primary url: {u!r}")

    if problems:
        print(f"manifest validation FAILED ({len(problems)} problem(s)):")
        for p in problems[:20]:
            print("  " + p)
        sys.exit(1)
    return fallback_count


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
        # probe the original catalog URL first, then the same GUID on the new
        # PlayFab content CDN (content1/content2) — first shape that downloads
        # a real zip wins and the entry is verified
        probe_urls = ([url] if url else []) + (alternate_cdn_urls(url) if url else [])
        data = None
        used_url = None
        if not skip_binaries:
            for probe in probe_urls:
                label = probe.split("//", 1)[-1].split("/", 1)[0].split(".")[0]
                data = cached_get("persona", f"{label}-{item_id}.zip", probe)
                if data is not None and len(data) > 100 and data[:4] == b"PK\x03\x04":
                    used_url = probe
                    break
                data = None
        # mirror locations the gate tries (in order) when the primary URL
        # answers with a non-200: a working alternate CDN shape first (a live
        # one beats a not-yet-existing release), then the GitHub release built
        # from the byte-identical 043d7bb copies — so the gate's sha256 check
        # still applies to every source
        fallbacks = []
        if used_url and used_url != url:
            fallbacks.append(used_url)
        fallbacks.append(f"{PERSONA_MIRROR_BASE}/{item_id}.zip")
        entry = {
            "kind": "persona",
            "file": f"playfab/persona/{item_id}.zip",
            "url": url,
            "verified": False,
            "fallbackUrls": fallbacks,
        }
        if data is not None:
            entry["sha256"] = sha256_bytes(data)
            entry["size"] = len(data)
            entry["verified"] = True
            if used_url != url:
                print(f"  persona verified via alternate CDN: {item_id} ({used_url.split('/', 3)[2]})")
        else:
            # fall back to the byte-identical copy shipped in the repo or, after
            # the repo cleanup, the byte-identical history copy (043d7bb) — the
            # sha256/size stay recorded facts even with no reachable source
            fact = hash_repo_copy(os.path.join(PERSONA_DIR, item_id + ".zip")) \
                or hash_history_copy(f"{HISTORY_PERSONA_DIR}/{item_id}.zip")
            if fact:
                entry["sha256"], entry["size"] = fact
            if url:
                print(f"  persona source offline on {len(probe_urls)} shape(s), hashed repo/history copy: {item_id}")
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
            "alternateAssetCdn": ", ".join(ALTERNATE_CDN_HOSTS),
            "personaMirror": PERSONA_MIRROR_BASE,
            "skinTextures": "https://minecraft.wiki",
        },
        "items": manifest_items,
    }

    fallback_count = validate_manifest(manifest)

    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
        f.write("\n")

    print(f"manifest written: {OUT}")
    print(f"  fallbackUrls: {fallback_count} (persona mirror base: {PERSONA_MIRROR_BASE})")
    for k, (total, ok, size) in sorted(by_kind.items()):
        print(f"  {k}: {ok}/{total} verified, {size / 1024:.0f} KiB")
    print(f"  total: {verified}/{len(manifest_items)} verified")
    unverified = [e["file"] for e in manifest_items if not e["verified"]]
    if unverified:
        print(f"  unverified ({len(unverified)}): {', '.join(unverified[:10])}"
              + (f", ... ({len(unverified)} total)" if len(unverified) > 10 else ""))


if __name__ == "__main__":
    main()
