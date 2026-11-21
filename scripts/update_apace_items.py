#!/usr/bin/env python3
"""Point the three Apace Cape catalog items at the nested {itemId}.zip pieces.

The items previously used AssetFileName (the controller synthesized a Contents
entry with Url /playfab/persona/apace_cape.mcpersona). Real Mojang persona
items carry an explicit Contents array whose Url names the piece file after
the ITEM id (/playfab/persona/{itemId}.zip, MinClientVersion 1.13.0) plus one
dangling ItemReferences entry - both replicated here so the Apace items are
structurally indistinguishable from real ones. Idempotent.
"""

import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ITEMS_DIR = ROOT / "staticdata-extras" / "playfab" / "items"

# itemId -> (content id, item reference id)
# both are arbitrary uuids like the real items carry; the item reference
# target does not exist in our catalog exactly like for the 231 real items
CONTENT_IDS = {
    "6e8671bd-7d35-442c-8e7c-39b6562490ca": "b1e7f282-4c5a-4e02-9f31-6d0a1e5c7001",
    "a5d108fa-13d8-4b42-ac22-9ddc659d1eaf": "4d2c9a13-8e6b-4b7f-a25c-31f8d9e04a02",
    "1981967a-7662-45a6-813d-176792cfc68e": "7a9d5e64-2b18-4c93-b6f0-58e2c1d49003",
}

REFERENCE_IDS = {
    "6e8671bd-7d35-442c-8e7c-39b6562490ca": "c8f2a9d1-5e34-4b67-9a02-14d6b3c85104",
    "a5d108fa-13d8-4b42-ac22-9ddc659d1eaf": "e3a67b20-9c41-4d58-8f73-62b9d0e17505",
    "1981967a-7662-45a6-813d-176792cfc68e": "94b1d6a3-2f85-4e09-bc47-a08d3f2e6906",
}

LAST_MODIFIED = "2026-11-21T15:30:00.000Z"

# kept in sync with BUILD_VERSION in scripts/build_apace_piece.py: the client
# caches imported pieces by version, so the catalog entry must advertise the
# version the shipped pack's manifest carries
DATA_VERSION = "2.0.0"


def main() -> int:
    for item_id, content_id in CONTENT_IDS.items():
        path = ITEMS_DIR / f"{item_id}.json"
        item = json.loads(path.read_text())
        data = item["Data"]

        changed = []

        # AssetFileName makes the controller synthesize flat Contents; real
        # items carry explicit Contents instead - remove it
        if "AssetFileName" in data:
            del data["AssetFileName"]
            changed.append("dropped AssetFileName")

        if data.get("Version") != DATA_VERSION:
            data["Version"] = DATA_VERSION
            changed.append(f"Data.Version -> {DATA_VERSION}")

        contents = [
            {
                "Id": content_id,
                "Url": f"/playfab/persona/{item_id}.zip",
                "MaxClientVersion": "65535.65535.65535",
                "MinClientVersion": "1.13.0",
                "Tags": [],
                "Type": "personabinary",
            }
        ]
        if item.get("Contents") != contents:
            item["Contents"] = contents
            changed.append("Contents -> /playfab/persona/{id}.zip (min 1.13.0)")

        references = [{"Id": REFERENCE_IDS[item_id], "Amount": 1}]
        if item.get("ItemReferences") != references:
            item["ItemReferences"] = references
            changed.append("ItemReferences -> dangling piece reference (real-item pattern)")

        if item.get("LastModifiedDate") != LAST_MODIFIED:
            item["LastModifiedDate"] = LAST_MODIFIED
            changed.append("LastModifiedDate bumped")

        if changed:
            path.write_text(json.dumps(item, indent=2) + "\n")
            print(f"{item['Title']}: " + "; ".join(changed))
        else:
            print(f"{item['Title']}: already up to date")

    return 0


if __name__ == "__main__":
    sys.exit(main())
