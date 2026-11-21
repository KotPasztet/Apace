#!/usr/bin/env python3
"""Rebuild the Apace Cape persona pieces in the encrypted-container shape.

Background (device crash of 2026-11-21, logcat-crash-2040): the nested
ppack0.zip format itself was right, but a PLAINTEXT, undersized contents.json
made the client crash. The client picks the DERIVED access strategy
(DirectoryPackWithEncryption) purely from the first 256 bytes of contents.json:
u32@0x00 == 0, magic u32@0x04 == 0x9BCFB9FC (LE), byte@0x10 == length of the
pack uuid, NUL-terminated uuid ASCII at 0x11 (exactly the layout of the 231
real piece binaries). With that header present it asks its key provider for
the ContentIdentity and DECRYPTS the rest of contents.json with AES-256-CBC
(key = keyString[0:32], IV = keyString[0:16], PKCS7 padding - the same
Mojang-marketplace scheme the real pieces use). Our old synthesized
contents.json was 133 bytes, so `decryptLen - 256` underflowed
(malloc(18446744073709551504)) and std::bad_alloc killed the game ~100ms
after the piece download.

This build emits a REAL encrypted container:

  outer zip
    └── ppack0.zip
          ├── manifest.json            (plaintext, header/module version bumped)
          ├── texts/en_US.lang         (plaintext)
          ├── texts/languages.json     (plaintext)
          ├── <piece>.meta.json        (plaintext)
          ├── <piece>.geometry.json    (plaintext)
          ├── <piece>.png              (plaintext)
          └── contents.json            (256-byte ContentIdentity header +
                                        AES-256-CBC body listing every file
                                        with an EMPTY per-file key)

- Empty per-file keys make the client serve the piece files raw, so the art
  stays plaintext and openable.
- No signatures.json: a foreign one fails the hash check; absence is fine
  (the client validates content, not signature presence).
- The pack key is APACE_CONTENT_KEY below. The client resolves keys per
  ContentIdentity from its PlayFab entitlement receipts - specifically a
  "Receipt" token whose payload JSON validates with iss == "https://playfab.com"
  and aud == "https://playfab.com/B63A0803D3653643/", and carries
  Receipt.Entitlements[] with {FriendlyId: <pack uuid>, ContentKey: <key>}.
  Apace serves exactly that receipt from /inventory/GetInventoryItems
  (src/Apace.ApiServer/Controllers/PlayfabApi/InventoryController.cs) with the
  SAME key constant. If the receipt path does not pan out on device, the
  client decrypts with whatever fallback key it has, gets garbage, fails the
  contents parse and rejects the pack GRACEFULLY - the underflow crash is
  impossible now because the body is always >= 16 bytes (total >= 272).

The flat .mcpersona sources live in git history (a04a762^), not in the tree:
this script restores them automatically via git checkout, builds, and removes
them again so the working tree stays clean.

Outer zips are written as {catalogItemId}.zip because that is how real pieces
are served (the catalog item's Contents[].Url names the file after the item
id). Rebuild after changing art, then restart the Api server.
"""

import io
import json
import shutil
import subprocess
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PERSONA_DIR = ROOT / "staticdata-extras" / "playfab" / "persona"

# fixed content key shared with InventoryController.cs (ApaceContentKey constant)
# the client takes the first 32 bytes as the AES-256 key and the first 16 as the IV
APACE_CONTENT_KEY = "9f2c7a51e8b04d63a7c15f8e2d94b6a0c3e8517d0fa94b62c8d7e1365a04bf19"

# itemId -> source .mcpersona
VARIANTS = {
    "6e8671bd-7d35-442c-8e7c-39b6562490ca": "apace_cape.mcpersona",
    "a5d108fa-13d8-4b42-ac22-9ddc659d1eaf": "apace_cape_256.mcpersona",
    "1981967a-7662-45a6-813d-176792cfc68e": "apace_cape_512.mcpersona",
}

# manifest header/module version shipped in the BUILT pack. Bumped from
# [1,1,0]/[1,0,0] to [2,0,0] because the client caches imported pieces by
# version (docs/custom-persona-items.md gotcha).
BUILD_VERSION = [2, 0, 0]

SOURCE_REFS = ["a04a762^"]  # git revisions that carry the flat .mcpersona sources

# fixed timestamps like the real pieces (2020-01-29 23:06), keeps builds deterministic
OUTER_DT = (2020, 1, 29, 23, 6, 56)
INNER_DT = (2020, 1, 29, 23, 6, 48)

# entry order mirrors real pieces
ORDER = [
    "manifest.json",
    "texts/en_US.lang",
    "texts/languages.json",
]


def restore_sources() -> None:
    """The flat .mcpersona sources are deleted after each build; bring them
    back from git history if they are missing."""
    missing = [name for name in VARIANTS.values() if not (PERSONA_DIR / name).exists()]
    if not missing:
        return
    for ref in SOURCE_REFS:
        rc = subprocess.run(
            ["git", "checkout", ref, "--"] + [f"staticdata-extras/playfab/persona/{name}" for name in missing],
            cwd=ROOT,
            capture_output=True,
        )
        if rc.returncode == 0 and all((PERSONA_DIR / name).exists() for name in missing):
            print(f"restored {len(missing)} .mcpersona sources from {ref}")
            return
    raise SystemExit(f"missing .mcpersona sources: {missing}; restore them from git history first")


def aes256cbc_encrypt(plaintext: bytes, key: bytes) -> bytes:
    """AES-256-CBC with PKCS7 padding, IV = key[:16] - the exact scheme the
    client's Crypto::Symmetric uses for pack contents (System AES256, CBC)."""
    try:
        from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
        from cryptography.hazmat.primitives.padding import PKCS7

        padder = PKCS7(128).padder()
        padded = padder.update(plaintext) + padder.finalize()
        enc = Cipher(algorithms.AES(key[:32]), modes.CBC(key[:16])).encryptor()
        return enc.update(padded) + enc.finalize()
    except ImportError:
        # fallback: openssl CLI (default padding is PKCS7; -nosalt drops the
        # "Salted__" header openssl would otherwise prepend)
        result = subprocess.run(
            ["openssl", "enc", "-aes-256-cbc", "-nosalt", "-K", key[:32].hex(), "-iv", key[:16].hex()],
            input=plaintext,
            capture_output=True,
            check=True,
        )
        return result.stdout


def build_contents_json(pack_uuid: str, file_names: list[str]) -> bytes:
    """256-byte ContentIdentity header + AES-256-CBC body.

    Header (verified against real pieces and the parse in libgenoa.so):
      u32@0x00 = 0, u32@0x04 = 0x9BCFB9FC LE, byte@0x10 = 36,
      pack uuid as NUL-terminated ASCII at 0x11, remainder zero.
    Body: compact {"content":[{"path":..,"key":""},..]} with an EMPTY per-file
    key for every file inside ppack0.zip so piece files stay plaintext.
    """
    header = bytearray(256)
    header[0x00:0x04] = (0).to_bytes(4, "little")
    header[0x04:0x08] = (0x9BCFB9FC).to_bytes(4, "little")
    uuid_bytes = pack_uuid.encode("ascii")
    header[0x10] = len(uuid_bytes)
    header[0x11 : 0x11 + len(uuid_bytes)] = uuid_bytes
    body = json.dumps({"content": [{"path": name, "key": ""} for name in file_names]}, separators=(",", ":"))
    return bytes(header) + aes256cbc_encrypt(body.encode("utf-8"), APACE_CONTENT_KEY.encode("ascii"))


def build_inner(source_path: Path) -> tuple[bytes, str, list[str]]:
    """Return (inner ppack0.zip bytes, pack uuid, contents.json file list)."""
    with zipfile.ZipFile(source_path) as src:
        names = [n for n in src.namelist()]
        manifest = json.loads(src.read("manifest.json"))
        pack_uuid = manifest["header"]["uuid"]

        # bump version so the client re-imports instead of using its cached copy
        manifest["header"]["version"] = BUILD_VERSION
        for module in manifest.get("modules", []):
            module["version"] = BUILD_VERSION

        contents_names = [n for n in names if n != "contents.json"]
        ordered = [n for n in ORDER if n in contents_names]
        rest = [n for n in contents_names if n not in ordered] + ["contents.json"]

        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
            for name in ordered:
                info = zipfile.ZipInfo(name, date_time=INNER_DT)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0
                payload = json.dumps(manifest, indent=2).encode() if name == "manifest.json" else src.read(name)
                dst.writestr(info, payload)
            for name in rest:
                info = zipfile.ZipInfo(name, date_time=INNER_DT)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0
                if name == "contents.json":
                    dst.writestr(info, build_contents_json(pack_uuid, contents_names))
                else:
                    dst.writestr(info, src.read(name))
    return buf.getvalue(), pack_uuid, contents_names


def build_outer(inner_bytes: bytes, target: Path) -> None:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
        info = zipfile.ZipInfo("ppack0.zip", date_time=OUTER_DT)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0
        dst.writestr(info, inner_bytes)
    target.write_bytes(buf.getvalue())


def verify(target: Path, pack_uuid: str, expected_files: list[str]) -> None:
    """Byte-level self-check of the finished outer zip."""
    from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
    from cryptography.hazmat.primitives.padding import PKCS7

    with zipfile.ZipFile(target) as outer:
        assert outer.namelist() == ["ppack0.zip"], f"outer zip must hold exactly ppack0.zip: {outer.namelist()}"
        inner_bytes = outer.read("ppack0.zip")
    inner = zipfile.ZipFile(io.BytesIO(inner_bytes))
    contents = inner.read("contents.json")
    assert len(contents) >= 256 + 16, f"contents.json too small: {len(contents)}"
    assert int.from_bytes(contents[0x00:0x04], "little") == 0, "u32@0x00 must be 0"
    assert int.from_bytes(contents[0x04:0x08], "little") == 0x9BCFB9FC, "magic@0x04 mismatch"
    assert contents[0x10] == 36, "uuid length byte@0x10 must be 36"
    assert contents[0x11 : 0x11 + 36].decode("ascii") == pack_uuid, "uuid@0x11 mismatch"
    assert contents[0x11 + 36] == 0, "uuid must be NUL-terminated"

    key = APACE_CONTENT_KEY.encode("ascii")
    dec = Cipher(algorithms.AES(key[:32]), modes.CBC(key[:16])).decryptor()
    padded = dec.update(contents[256:]) + dec.finalize()
    unpadder = PKCS7(128).unpadder()
    body = unpadder.update(padded) + unpadder.finalize()
    parsed = json.loads(body)
    listed = [entry["path"] for entry in parsed["content"]]
    assert listed == expected_files, f"contents list mismatch:\n  {listed}\n  vs {expected_files}"
    assert all(entry["key"] == "" for entry in parsed["content"]), "per-file keys must be empty"
    assert set(inner.namelist()) == set(expected_files) | {"contents.json"}, "inner zip file set mismatch"


def main() -> int:
    restore_sources()
    key = APACE_CONTENT_KEY.encode("ascii")

    for item_id, source_name in VARIANTS.items():
        source = PERSONA_DIR / source_name
        target = PERSONA_DIR / f"{item_id}.zip"
        inner, pack_uuid, file_list = build_inner(source)
        build_outer(inner, target)
        verify(target, pack_uuid, file_list)

        print(f"{source_name} -> {target.name}")
        print(f"    pack uuid (ContentIdentity): {pack_uuid}")
        print(f"    manifest version: {BUILD_VERSION[0]}.{BUILD_VERSION[1]}.{BUILD_VERSION[2]}")
        print(f"    contents.json: 256B header + AES-256-CBC body, key[:32]=AES key, key[:16]=IV")
        print(f"    files: {', '.join(sorted(file_list))}")
        print("    header[0:0x40]:")
        with zipfile.ZipFile(target) as outer:
            head = zipfile.ZipFile(io.BytesIO(outer.read("ppack0.zip"))).read("contents.json")[:0x40]
        for row in range(0, 0x40, 16):
            print("      " + head[row : row + 16].hex(" "))

    # the flat .mcpersona files are superseded by the nested {itemId}.zip builds
    for source_name in VARIANTS.values():
        (PERSONA_DIR / source_name).unlink()
        print(f"removed build-time source {source_name}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
