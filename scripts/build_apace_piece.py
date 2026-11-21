#!/usr/bin/env python3
"""Rebuild the Apace Cape persona pieces in Mojang's nested ppack format.

WHY: every real piece binary shipped by Mojang (all 231 in
staticdata-extras/playfab/persona/*.zip) has the exact same structure:

    outer zip
      └── ppack0.zip
            ├── manifest.json            (plaintext json, pack uuid + persona_piece module)
            ├── texts/en_US.lang         (plaintext)
            ├── texts/languages.json     (plaintext)
            ├── <piece>.geometry.json    (encrypted on real pieces, plaintext here)
            ├── <piece>.meta.json        (encrypted on real pieces, plaintext here)
            ├── <piece>.png              (encrypted on real pieces, plaintext here)
            ├── signatures.json          (80 bytes, binary, always present on real pieces)
            └── contents.json            (binary, header contains the manifest pack uuid)

The first Apace Cape build (a0b4d51) shipped a FLAT zip (no nested ppack0.zip,
no signatures.json/contents.json) and none of the Apace capes could be equipped
in-game, while every real piece could. This script repackages the existing
plaintext piece content into the nested format.

Because we cannot produce Mojang's signatures or encrypted content, three
variants ship as a device test matrix (one per cape item):

  Apace Cape      (128px) - contents.json synthesized, NO signatures.json
                            -> probes: is contents.json presence required?
  Apace Cape 256          - bare inner pack (neither file)
                            -> probes: does plaintext/unsigned content load at all?
  Apace Cape 512          - contents.json synthesized + signatures.json copied
                            verbatim from a real piece
                            -> probes: is signature PRESENCE (not validity) enough?

Reading the results:
  * 256 works ........................ client ignores both files; bare is the format
  * only 128 works ................... contents.json must exist but is not validated
  * only 512 works ................... signatures.json must exist but is not validated
  * none work, real pieces still do .. the client validates the crypto (signature and
                                      /or content decryption); custom art capes are not
                                       possible without Mojang's keys.

Outer zips are written as {catalogItemId}.zip because that is how real pieces are
served (the catalog item's Contents[].Url names the file after the item id, e.g.
/playfab/persona/8c722223-....zip for Vex Wings).
"""

import io
import json
import shutil
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PERSONA_DIR = ROOT / "staticdata-extras" / "playfab" / "persona"

# itemId -> (source .mcpersona, variant)
VARIANTS = {
    "6e8671bd-7d35-442c-8e7c-39b6562490ca": ("apace_cape.mcpersona", "contents"),
    "a5d108fa-13d8-4b42-ac22-9ddc659d1eaf": ("apace_cape_256.mcpersona", "bare"),
    "1981967a-7662-45a6-813d-176792cfc68e": ("apace_cape_512.mcpersona", "contents+signatures"),
}

# fixed timestamps like the real pieces (2020-01-29 23:06), keeps builds deterministic
OUTER_DT = (2020, 1, 29, 23, 6, 56)
INNER_DT = (2020, 1, 29, 23, 6, 48)

# entry order mirrors real pieces
ORDER = [
    "manifest.json",
    "texts/en_US.lang",
    "texts/languages.json",
]


def synth_contents_json(pack_uuid: str) -> bytes:
    """Synthesize a contents.json shaped like the real ones.

    Real layout: 00 00 00 00 | fc b9 cf 9b | 8 zero bytes | '$' + pack uuid
    (36 chars) | zero padding | per-file entries (encrypted) | 64-byte tail.
    We cannot reproduce the encrypted middle, so it is zero-filled.
    """
    header = bytearray()
    header += bytes.fromhex("00000000fcb9cf9b")
    header += b"\x00" * 8
    header += f"${pack_uuid}".encode("ascii")
    header += b"\x00" * 16
    tail = b"\x00" * 64
    return bytes(header) + tail


def load_signatures_template() -> bytes:
    """Copy signatures.json verbatim from a real piece (turban)."""
    with zipfile.ZipFile(PERSONA_DIR / "c0b7cde4-ff04-4028-a80b-258741a41980.zip") as outer:
        with zipfile.ZipFile(io.BytesIO(outer.read("ppack0.zip"))) as inner:
            return inner.read("signatures.json")


def build_inner(source_path: Path, variant: str, signatures_template: bytes) -> tuple[bytes, str]:
    """Return (inner ppack0.zip bytes, pack uuid)."""
    with zipfile.ZipFile(source_path) as src:
        names = src.namelist()
        manifest = json.loads(src.read("manifest.json"))
        pack_uuid = manifest["header"]["uuid"]

        ordered = [n for n in ORDER if n in names]
        rest = [n for n in names if n not in ordered]
        if variant in ("contents", "contents+signatures"):
            rest += ["signatures.json"] if variant == "contents+signatures" else []
            rest += ["contents.json"]

        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
            for name in ordered:
                info = zipfile.ZipInfo(name, date_time=INNER_DT)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0
                dst.writestr(info, src.read(name))
            for name in rest:
                info = zipfile.ZipInfo(name, date_time=INNER_DT)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0
                if name == "signatures.json":
                    dst.writestr(info, signatures_template)
                elif name == "contents.json":
                    dst.writestr(info, synth_contents_json(pack_uuid))
                else:
                    dst.writestr(info, src.read(name))
    return buf.getvalue(), pack_uuid


def build_outer(inner_bytes: bytes, target: Path) -> None:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
        info = zipfile.ZipInfo("ppack0.zip", date_time=OUTER_DT)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0
        dst.writestr(info, inner_bytes)
    target.write_bytes(buf.getvalue())


def main() -> int:
    signatures_template = load_signatures_template()

    for item_id, (source_name, variant) in VARIANTS.items():
        source = PERSONA_DIR / source_name
        target = PERSONA_DIR / f"{item_id}.zip"
        inner, pack_uuid = build_inner(source, variant, signatures_template)
        build_outer(inner, target)

        with zipfile.ZipFile(target) as outer:
            inner_names = zipfile.ZipFile(io.BytesIO(outer.read("ppack0.zip"))).namelist()
        print(f"{source_name} -> {target.name}  [{variant}]")
        print(f"    pack uuid: {pack_uuid}")
        for n in inner_names:
            print(f"      {n}")

    # the flat .mcpersona files are superseded by the nested {itemId}.zip builds
    for _, (source_name, _) in VARIANTS.items():
        (PERSONA_DIR / source_name).unlink()
        print(f"removed superseded {source_name}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
