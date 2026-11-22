#!/usr/bin/env python3
"""Smoke sync of the live PlayFab 20ca2 persona catalog (small sample, sandbox-safe).

Mirrors the panel's LivePersonaCatalogSync (anonymous AndroidDeviceID login ->
master_player_account entity token -> Catalog/Search pagination via
ContinuationToken, ~1 request/second) but stops after a few pages so it can be
run in a sandbox. Everything lands in work/ (gitignored) — nothing here is
meant for the repo.

Reports the numbers the real sync would log: item count seen, how many items
carry a live Contents[0].Url, piece-type breakdown, and how many items carry
lock fields (achievement tags / price / purchasable / hidden_offer) that the
unlock-all offer strip must neutralize.

Also probes one binary URL on both CDN shapes (old xforge + new
content1/content2.prod.catalog.playfab.com) to validate the lazy-probe shape.

Usage:
    python3 scripts/smoke_live_catalog_sync.py
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

API = "https://20ca2.playfabapi.com"
SCID = "4fc10100-5f7a-4470-899b-280835760c07"
OUT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../work/live_raw_sample"))
DEVICE_ID = "apace-live-catalog-smoke"

PAGES = 3          # pages fetched for the sample
TOP = 25           # items per page
MCE_NAMESPACE = "b63a0803d3653643"
ALTERNATE_CDN_HOSTS = [
    "content1.prod.catalog.playfab.com",
    "content2.prod.catalog.playfab.com",
]

LOCK_TAGS = ["is_achievement", "earth_achievement", "vanilla_achievement"]


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
        "AndroidDevice": "Pixel", "OS": "Android 10", "CreateAccount": True,
    }, {})["data"]
    token = login["EntityToken"]["EntityToken"]
    master = post("/Authentication/GetEntityToken", {
        "Entity": {"Id": login["PlayFabId"], "Type": "master_player_account"},
    }, {"X-EntityToken": token})["data"]["EntityToken"]
    return master


def search(master, body):
    return post("/Catalog/Search", body, {"X-EntityToken": master})["data"]


def lock_stats(items):
    """Which lock fields the unlock-all strip must neutralize (per raw item)."""
    stats = {
        "achievement_tags": 0, "price_gt_zero": 0, "dp_price_gt_zero": 0,
        "purchasable_false": 0, "hidden_offer": 0, "clean": 0,
    }
    for it in items:
        dp = it.get("DisplayProperties") or {}
        tags = set(it.get("Tags") or [])
        price_block = it.get("Price") or {}
        price_amount = 0
        for price in price_block.get("Prices") or []:
            for amount in price.get("Amounts") or []:
                price_amount += int(amount.get("Amount") or 0)
        locked = False
        if tags & set(LOCK_TAGS):
            stats["achievement_tags"] += 1
            locked = True
        if price_amount > 0:
            stats["price_gt_zero"] += 1
            locked = True
        if int(dp.get("price") or 0) > 0:
            stats["dp_price_gt_zero"] += 1
            locked = True
        if it.get("Purchasable") is False or dp.get("purchasable") is False:
            stats["purchasable_false"] += 1
            locked = True
        if "hidden_offer" in tags:
            stats["hidden_offer"] += 1
            locked = True
        if not locked:
            stats["clean"] += 1
    return stats


def probe_head(url):
    """HEAD (fallback GET range 0-0) status for one URL; returns status or error string."""
    for method, headers in (("HEAD", {}), ("GET", {"Range": "bytes=0-0"})):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "ApaceLiveCatalog/1.0", **headers}, method=method)
            with urllib.request.urlopen(req, timeout=20) as r:
                return r.status
        except urllib.error.HTTPError as e:
            if method == "GET":
                return e.code
        except Exception as e:
            if method == "GET":
                return f"error: {e}"
        time.sleep(0.4)
    return "unreachable"


def alternate_urls(url):
    """Same payload on the PlayFab content CDN (content1/content2) — the shape
    generate_mojang_asset_manifest.py probes."""
    def guidish(segment):
        return 32 <= len(segment) <= 36 and all(c in "0123456789abcdefABCDEF-" for c in segment)

    if "/pf-namespace-" in url:
        tail = url.split("/pf-namespace-", 1)[1]
        return [f"https://{host}/pf-namespace-{tail}" for host in ALTERNATE_CDN_HOSTS]
    if "/pf-title-" in url:
        segments = [s for s in url.split("/") if s]
        if len(segments) >= 2 and guidish(segments[-2]):
            tail = f"{segments[-2]}/{segments[-1]}"
            return [f"https://{host}/pf-namespace-{MCE_NAMESPACE}/{tail}" for host in ALTERNATE_CDN_HOSTS]
    return []


def main():
    os.makedirs(OUT, exist_ok=True)
    master = login()
    print("logged in")

    # 1. what the sync sees for the full PersonaDurable filter (server-reported count)
    data = search(master, {
        "Filter": "(contentType eq 'PersonaDurable')",
        "OrderBy": "creationDate asc",
        "scid": SCID,
        "top": TOP,
    })
    total = data.get("Count")
    print(f"server-reported PersonaDurable Count: {total}")

    items, token, pages = [], data.get("ContinuationToken"), 0
    while pages < PAGES:
        if pages > 0:
            data = search(master, {
                "Filter": "(contentType eq 'PersonaDurable')",
                "OrderBy": "creationDate asc",
                "scid": SCID,
                "top": TOP,
                **({"ContinuationToken": token} if token else {}),
            })
            token = data.get("ContinuationToken")
        items.extend(data.get("Items") or [])
        pages += 1
        if pages < PAGES:
            time.sleep(1.1)  # ~1 req/s, polite

    print(f"sampled {len(items)} items over {pages} page(s)")
    if token:
        print(f"continuation token present: yes ({len(token)} chars)")

    by_type = {}
    for it in items:
        dp = it.get("DisplayProperties") or {}
        by_type.setdefault(dp.get("pieceType") or "?", []).append(it)
    for t, lst in sorted(by_type.items()):
        print(f"  {t}: {len(lst)}")

    live_urls = sum(
        1 for it in items
        if (it.get("Contents") or []) and (it["Contents"][0] or {}).get("Url", "").startswith("http")
    )
    emotes = sum(1 for it in items if (it.get("DisplayProperties") or {}).get("pieceType") == "persona_emote")
    print(f"items with a live Contents[0].Url: {live_urls}/{len(items)}")
    print(f"emote items in sample: {emotes}")
    print("lock fields in raw sample:", json.dumps(lock_stats(items)))

    with open(os.path.join(OUT, "raw_items.json"), "w") as f:
        json.dump(items, f, indent=1)
    print(f"raw sample saved to {OUT}/raw_items.json")

    # 2. emote filter — the catalog does serve a dedicated emote piece type; the
    #    client-side verdict (does the MCE dressing room ever query it?) is decided
    #    in docs/live-persona-catalog.md from the layout manifest evidence.
    emote_data = search(master, {
        "Filter": "(contentType eq 'PersonaDurable' and displayProperties/pieceType eq 'persona_emote')",
        "OrderBy": "creationDate asc",
        "scid": SCID,
        "top": 10,
    })
    emote_total = emote_data.get("Count")
    emote_items = emote_data.get("Items") or []
    print(f"emote filter Count: {emote_total} (sampled {len(emote_items)})")
    if emote_items:
        dp = emote_items[0].get("DisplayProperties") or {}
        print("first emote:", json.dumps({
            "id": emote_items[0].get("Id"),
            "creatorName": dp.get("creatorName"),
            "price": dp.get("price"),
            "purchasable": dp.get("purchasable"),
            "packIdentity": dp.get("packIdentity"),
        }))
        with open(os.path.join(OUT, "raw_emotes.json"), "w") as f:
            json.dump(emote_items, f, indent=1)
    time.sleep(1.1)

    # 3. one lazy probe against both CDN shapes (newest sample item first: new
    #    items live on the new content CDN, old ones are the dark xforge ones)
    probe_targets = [
        it for it in items
        if (it.get("Contents") or []) and (it["Contents"][0] or {}).get("Url", "").startswith("http")
    ]
    probe_targets.reverse()  # newest first — the new CDN is the interesting shape
    for it in probe_targets[:2]:
        url = it["Contents"][0]["Url"]
        statuses = {"primary": probe_head(url)}
        for alt in alternate_urls(url):
            statuses[alt.split("/")[2]] = probe_head(alt)
        print(f"probe {it.get('Id')}: {json.dumps(statuses)}")
        time.sleep(1.0)

    return 0


if __name__ == "__main__":
    sys.exit(main())
