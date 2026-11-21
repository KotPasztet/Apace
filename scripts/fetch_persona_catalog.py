#!/usr/bin/env python3
"""Fetch persona pieces from the still-live PlayFab catalog of the Minecraft title.

The Minecraft Earth title (TitleId 20ca2) still answers anonymous device logins
and serves the full Bedrock character creator catalog through Catalog/Search —
the same API the MCE client's dressing room used. This script logs in anonymously
(AndroidDeviceID), exchanges the entity token for a master_player_account token
and fetches the oldest items of every persona piece type, ordered by creation
date (the first pages are the 2019 character creator launch defaults that MCE's
dressing room showed).

Raw responses are stored as work/persona_raw/<pieceType>_<skip>.json for
scripts/generate_persona_items.py.

Usage:
    python3 scripts/fetch_persona_catalog.py
"""
import json
import os
import time
import urllib.request

API = "https://20ca2.playfabapi.com"
SCID = "4fc10100-5f7a-4470-899b-280835760c07"
OUT = os.path.abspath(os.path.join(os.path.dirname(__file__), "../work/persona_raw"))
DEVICE_ID = "apace-catalog-fetcher"

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


def post(path, body, headers):
    req = urllib.request.Request(
        API + path,
        data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json", **headers},
        method="POST",
    )
    with urllib.request.urlopen(req, timeout=40) as r:
        return json.load(r)


def main():
    os.makedirs(OUT, exist_ok=True)
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

    for ptype in TYPES:
        skip = 0
        total = None
        while True:
            body = {
                "Filter": f"(contentType eq 'PersonaDurable' and displayProperties/pieceType eq '{ptype}')",
                "OrderBy": "creationDate asc",
                "scid": SCID,
                "skip": skip,
                "top": TOP,
            }
            try:
                d = post("/Catalog/Search", body, {"X-EntityToken": master})
            except Exception as e:
                print(ptype, "error:", e)
                time.sleep(3)
                continue
            data = d.get("data", {})
            total = data.get("Count", 0)
            items = data.get("Items", [])
            if not items:
                break
            fn = os.path.join(OUT, f"{ptype}_{skip:05d}.json")
            with open(fn, "w") as f:
                json.dump(items, f)
            oldest = items[0].get("CreationDate", "?")[:10]
            newest = items[-1].get("CreationDate", "?")[:10]
            print(f"{ptype}: skip={skip} ({len(items)} items, {oldest}..{newest}), total={total}")
            skip += TOP
            if skip >= PAGES * TOP or skip >= total:
                break
            time.sleep(1.0)


if __name__ == "__main__":
    main()
