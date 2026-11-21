#!/usr/bin/env python3
"""Extract persona piece definitions from the Minecraft Earth client resource pack.

The MCE client (and Bedrock in general) applies character creator pieces by UUID.
The pieces it knows locally live in its resource pack — for an Apace server that is
staticdata/resourcepacks/vanilla.zip (genoa.mcpack inside), extracted by the panel
to staticdata/resourcepacks/genoa_cache while the server runs.

This script scans a directory (or a .zip/.mcpack file) for anything persona
related: json files with "persona"/"piece" in the path or content, and prints
every definition with its uuid/pieceType-like fields so the values can be used
as PieceId in custom persona catalog items (see docs/custom-persona-items.md).

Usage:
    python3 extract-persona-pieces.py [path]

    path  a directory (default: staticdata/resourcepacks/genoa_cache),
          or a zip/mcpack file (e.g. genoa.mcpack, vanilla.zip)
"""

import io
import json
import os
import re
import sys
import zipfile

UUID_RE = re.compile(
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}"
)
INTERESTING_KEYS = ("uuid", "piece", "persona", "type", "parent", "version")


def iter_files(path):
    """Yield (relative path, text content or None) for candidate files."""
    if os.path.isfile(path) and path.lower().endswith((".zip", ".mcpack")):
        with zipfile.ZipFile(path) as zf:
            for name in zf.namelist():
                if name.endswith("/"):
                    continue
                lower = name.lower()
                if lower.endswith((".json", ".material", ".txt", ".lang")):
                    try:
                        yield name, zf.read(name).decode("utf-8", errors="replace")
                    except Exception:
                        pass
                elif "persona" in lower:
                    yield name, None
        return

    if not os.path.isdir(path):
        print(f"error: {path} is not a directory or a zip file", file=sys.stderr)
        sys.exit(1)

    for root, _dirs, files in os.walk(path):
        for name in files:
            full = os.path.join(root, name)
            rel = os.path.relpath(full, path)
            lower = rel.lower()
            try:
                if lower.endswith((".json", ".material", ".txt", ".lang")):
                    yield rel, open(full, encoding="utf-8", errors="replace").read()
                elif "persona" in lower:
                    yield rel, None
            except Exception:
                pass


def find_uuid_fields(obj, out, prefix=""):
    """Recursively collect fields whose key or value looks persona/uuid-ish."""
    if isinstance(obj, dict):
        for key, value in obj.items():
            find_uuid_fields(value, out, f"{prefix}.{key}" if prefix else str(key))
    elif isinstance(obj, list):
        for index, value in enumerate(obj):
            find_uuid_fields(value, out, f"{prefix}[{index}]")
    else:
        key = prefix.lower()
        text = str(obj)
        if "persona" in key or "piece" in key or "uuid" in key:
            out.append((prefix, text))


def main():
    if len(sys.argv) > 2:
        print(__doc__)
        sys.exit(2)
    path = sys.argv[1] if len(sys.argv) == 2 else "staticdata/resourcepacks/genoa_cache"

    print(f"scanning {path} ...")
    found = 0

    for rel, content in iter_files(path):
        if content is None:
            print(f"\n[binary] {rel}")
            found += 1
            continue

        if "persona" not in rel.lower() and "persona" not in content.lower():
            continue

        found += 1
        print(f"\n=== {rel} ===")

        try:
            data = json.loads(content)
        except json.JSONDecodeError:
            # not json: dump lines that mention persona or a uuid
            for line_no, line in enumerate(content.splitlines(), 1):
                if "persona" in line.lower() or UUID_RE.search(line):
                    print(f"  {line_no}: {line.strip()[:200]}")
            continue

        fields = []
        find_uuid_fields(data, fields)
        if fields:
            for key, value in fields:
                print(f"  {key} = {value}")
        else:
            print(f"  (json, keys: {list(data.keys()) if isinstance(data, dict) else type(data).__name__})")

    print(f"\n{found} persona-related file(s) found" if found else
          "\nno persona data found in this pack — the client may keep its pieces "
          "elsewhere (try scanning the genoa.mcpack inside vanilla.zip directly)")


if __name__ == "__main__":
    main()
