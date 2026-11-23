#!/usr/bin/env python3
"""Rebuild the Apace Cape persona piece binaries in the shape the frozen MCE
client actually accepts (proven-working shape, cross-checked 23.11 against
real marketplace pieces from the mirror payload, the sample new-CDN binary,
and MerelyMiserable/MerelyCapes, which serves custom capes to the live persona
pipeline successfully):

  outer zip  <itemId>.zip  ->  ppack0.zip  ->  piece files

  - ONE uuid everywhere: manifest.header.uuid == contents.json header uuid ==
    meta piece_id == the catalog item's Data.PieceId (every real piece does
    this; the old build used a separate pack uuid and the piece never applied).
  - contents.json = 256-byte ContentIdentity header + AES-256-CFB8 body, keyed
    with the client's BUILT-IN "Skin & Persona Key" (hardcoded inside the
    Minecraft client, "used to decrypt/encrypt every Skin Pack and Persona on
    the Marketplace... the same key is used every single time"; derivation
    key[:32] = AES key, key[:16] = IV). No receipt-delivered key is involved.
  - art files (meta/geometry/textures) are per-file encrypted with generated
    keys, exactly like real pieces; manifest.json / contents.json / texts/ /
    signatures.json stay raw (no "key" field).
  - signatures.json = [{"hash": base64(sha256(manifest.json)), "path": "manifest.json"}].

The flat .mcpersona sources are restored from git history (SOURCE_REFS) and
removed again after the build. Piece uuids come from the committed catalog
item JSONs (staticdata-extras/playfab/items/<itemId>.json Data.PieceId).
"""

import base64
import hashlib
import io
import json
import random
import subprocess
import sys
import zipfile
from pathlib import Path

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

ROOT = Path(__file__).resolve().parent.parent
PERSONA_DIR = ROOT / "staticdata-extras" / "playfab" / "persona"
ITEMS_DIR = ROOT / "staticdata-extras" / "playfab" / "items"

# The client's built-in Skin & Persona pack key (found inside the Minecraft
# client binary; "used to decrypt/encrypt every Skin Pack and Persona on the
# Minecraft Marketplace... the same key is used every single time" — AES-256-CFB8,
# IV = key[:16]). Mirrored in InventoryController.cs (ApaceContentKey).
SKIN_PERSONA_KEY = "s5s5ejuDru4uchuF2drUFuthaspAbepE"

# itemId -> flat .mcpersona source in git history
VARIANTS = {
    "6e8671bd-7d35-442c-8e7c-39b6562490ca": "apace_cape.mcpersona",
    "a5d108fa-13d8-4b42-ac22-9ddc659d1eaf": "apace_cape_256.mcpersona",
    "1981967a-7662-45a6-813d-176792cfc68e": "apace_cape_512.mcpersona",
}

# manifest header/module version shipped in the BUILT pack. [2,0,0] was the
# first nested-container build (old pack uuid); [3,0,0] is the single-uuid
# built-in-key rebuild — the bump makes the client re-import instead of using
# its cached copy of a pack that never applied.
BUILD_VERSION = [3, 0, 0]

SOURCE_REFS = ["a04a762^"]  # git revisions that carry the flat .mcpersona sources

# fixed timestamps like the real pieces (2020-01-29 23:06), keeps builds deterministic
OUTER_DT = (2020, 1, 29, 23, 6, 56)
INNER_DT = (2020, 1, 29, 23, 6, 48)

# entry order mirrors real pieces; everything else follows in source order
ORDER = [
    "manifest.json",
    "texts/en_US.lang",
    "texts/languages.json",
]

# files that stay RAW inside the pack (no per-file key) — the set MerelyCapes
# ships against the live persona pipeline; everything else (meta/geometry/
# textures) is per-file encrypted like the real pieces.
RAW_COMPONENTS = ["manifest.json", "contents.json", "texts", "pack_icon.png", "ui"]


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


def catalog_piece_id(item_id: str) -> str:
    """The piece uuid the committed catalog item advertises — the ONE uuid the
    pack must use (real pieces: catalog PieceId == manifest uuid == contents
    header uuid == meta piece_id)."""
    item = json.loads((ITEMS_DIR / f"{item_id}.json").read_text())
    piece_id = item["Data"]["PieceId"]
    if len(piece_id) != 36:
        raise SystemExit(f"catalog item {item_id}: PieceId is not a uuid: {piece_id}")
    return piece_id


def aes256_cfb8_encrypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    """AES-256-CFB8 (8-bit feedback, stream cipher), zero-padded to a 16-byte
    boundary exactly like the proven-working builder (the client stops parsing
    at the JSON end / file end; the real sample binary is unaligned too)."""
    pad = (-len(data)) % 16
    if pad:
        data = data + b"\x00" * pad
    enc = Cipher(algorithms.AES(key), modes.CFB8(iv)).encryptor()
    return enc.update(data) + enc.finalize()


def aes256_cfb8_decrypt(key: bytes, iv: bytes, data: bytes) -> bytes:
    dec = Cipher(algorithms.AES(key), modes.CFB8(iv)).decryptor()
    return dec.update(data) + dec.finalize()


def generate_file_key(rng: random.Random) -> str:
    alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890"
    return "".join(rng.choice(alphabet) for _ in range(32))


def is_raw(rel_path: str) -> bool:
    return any(part in RAW_COMPONENTS for part in rel_path.split("/"))


def build_contents_json(header_uuid: str, entries: list[tuple[str, str | None]]) -> bytes:
    """256-byte ContentIdentity header + AES-256-CFB8 body. Header (verified
    byte-for-byte against a real new-CDN piece sample): u32@0x00 = 0,
    u32@0x04 = 0x9BCFB9FC LE, u64@0x08 = 0, byte@0x10 = uuid len, uuid ASCII
    at 0x11, zero padding through byte 255, body starts at 256. Body:
    {"version":1,"content":[{"key","path"} | {"path"}]}."""
    header = bytearray(256)
    header[0x00:0x04] = (0).to_bytes(4, "little")
    header[0x04:0x08] = (0x9BCFB9FC).to_bytes(4, "little")
    uuid_bytes = header_uuid.encode("ascii")
    header[0x10] = len(uuid_bytes)
    header[0x11: 0x11 + len(uuid_bytes)] = uuid_bytes

    content = []
    for path, file_key in entries:
        if file_key is None:
            content.append({"path": path})
        else:
            content.append({"key": file_key, "path": path})
    body = json.dumps({"version": 1, "content": content}, separators=(",", ":"))
    key = SKIN_PERSONA_KEY.encode("ascii")
    encrypted = aes256_cfb8_encrypt(key, key[:16], body.encode("utf-8"))
    return bytes(header) + encrypted


def build_inner(source_path: Path, piece_uuid: str) -> tuple[bytes, list[tuple[str, str | None]]]:
    """Return (inner ppack0.zip bytes, contents entries [(path, fileKey|None)])."""
    rng = random.Random(hashlib.sha256(piece_uuid.encode()).digest())
    with zipfile.ZipFile(source_path) as src:
        names = [n for n in src.namelist()]
        manifest = json.loads(src.read("manifest.json"))

        # ONE uuid everywhere: the catalog item's PieceId.
        manifest["header"]["uuid"] = piece_uuid
        manifest["header"]["version"] = BUILD_VERSION
        for module in manifest.get("modules", []):
            module["version"] = BUILD_VERSION

        contents_names = [n for n in names if n != "contents.json"]
        ordered = [n for n in ORDER if n in contents_names]

        work: dict[str, bytes] = {}
        manifest_bytes = json.dumps(manifest, indent=2).encode()
        work["manifest.json"] = manifest_bytes
        for name in contents_names:
            if name != "manifest.json":
                work[name] = src.read(name)

        # signatures.json first (hash of the FINAL manifest bytes), raw like
        # real pieces and MerelyCapes
        signature = [{
            "path": "manifest.json",
            "hash": base64.b64encode(hashlib.sha256(manifest_bytes).digest()).decode("ascii"),
        }]
        work["signatures.json"] = json.dumps(signature).encode("ascii")

        # per-file encryption for art files, raw entries for manifest/texts
        entries: list[tuple[str, str | None]] = []
        for name in contents_names:
            if is_raw(name):
                entries.append((name, None))
            else:
                file_key = generate_file_key(rng)
                key = file_key.encode("ascii")
                work[name] = aes256_cfb8_encrypt(key, key[:16], work[name])
                entries.append((name, file_key))

        entries.sort(key=lambda e: (ORDER.index(e[0]) if e[0] in ORDER else len(ORDER)))

        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
            write_order = ordered + [n for n in work if n not in ordered] + ["contents.json"]
            for name in write_order:
                info = zipfile.ZipInfo(name, date_time=INNER_DT)
                info.compress_type = zipfile.ZIP_DEFLATED
                info.external_attr = 0
                payload = build_contents_json(piece_uuid, entries) if name == "contents.json" else work[name]
                dst.writestr(info, payload)
    return buf.getvalue(), entries


def build_outer(inner_bytes: bytes, target: Path) -> None:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as dst:
        info = zipfile.ZipInfo("ppack0.zip", date_time=OUTER_DT)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0
        dst.writestr(info, inner_bytes)
    target.write_bytes(buf.getvalue())


SOURCE_BYTES: dict[str, dict[str, bytes]] = {}


def verify(target: Path, piece_uuid: str, expected_entries: list[tuple[str, str | None]]) -> None:
    """Byte-level self-check of the finished outer zip (the same checks the
    client performs: header parse, key derivation, per-file roundtrip)."""
    with zipfile.ZipFile(target) as outer:
        assert outer.namelist() == ["ppack0.zip"], f"outer zip must hold exactly ppack0.zip: {outer.namelist()}"
        inner = zipfile.ZipFile(io.BytesIO(outer.read("ppack0.zip")))
    contents = inner.read("contents.json")
    assert len(contents) >= 256 + 16
    assert int.from_bytes(contents[0x00:0x04], "little") == 0
    assert int.from_bytes(contents[0x04:0x08], "little") == 0x9BCFB9FC
    assert contents[0x10] == 36
    assert contents[0x11:0x11 + 36].decode("ascii") == piece_uuid, "uuid@0x11 mismatch"
    assert contents[0x11 + 36] == 0

    key = SKIN_PERSONA_KEY.encode("ascii")
    body = aes256_cfb8_decrypt(key, key[:16], contents[256:])
    parsed = json.loads(body.rstrip(b"\x00"))
    assert parsed.get("version") == 1
    listed = {entry["path"]: entry.get("key") for entry in parsed["content"]}
    expected = dict(expected_entries)
    assert set(listed) == set(expected), f"contents list mismatch: {set(listed) ^ set(expected)}"
    for path, file_key in expected.items():
        assert listed[path] == file_key, f"key mismatch for {path}"

    # per-file roundtrip: encrypted entries decrypt back to the source bytes
    # (manifest.json is derived — uuid/version overrides — so only the other
    # raw files are compared against the flat source)
    source = SOURCE_BYTES[piece_uuid]
    for path, file_key in expected.items():
        raw = inner.read(path)
        if file_key is None:
            if path != "manifest.json":
                assert raw == source[path], f"raw file changed: {path}"
        else:
            fk = file_key.encode("ascii")
            plain = aes256_cfb8_decrypt(fk, fk[:16], raw).rstrip(b"\x00")
            assert plain == source[path], f"per-file roundtrip failed: {path}"

    signatures = json.loads(inner.read("signatures.json"))
    assert signatures[0]["path"] == "manifest.json"
    expected_hash = base64.b64encode(hashlib.sha256(inner.read("manifest.json")).digest()).decode("ascii")
    assert signatures[0]["hash"] == expected_hash, "signatures.json hash mismatch"
    manifest = json.loads(inner.read("manifest.json"))
    assert manifest["header"]["uuid"] == piece_uuid, "manifest header uuid mismatch"


def main() -> int:
    restore_sources()
    for item_id, source_name in VARIANTS.items():
        source_path = PERSONA_DIR / source_name
        piece_uuid = catalog_piece_id(item_id)
        with zipfile.ZipFile(source_path) as src:
            SOURCE_BYTES.setdefault(piece_uuid, {n: src.read(n) for n in src.namelist()})

        inner, entries = build_inner(source_path, piece_uuid)
        target = PERSONA_DIR / f"{item_id}.zip"
        build_outer(inner, target)
        verify(target, piece_uuid, entries)

        print(f"{source_name} -> {target.name}")
        print(f"    piece uuid (manifest/contents header/meta/catalog): {piece_uuid}")
        print(f"    manifest version: {BUILD_VERSION[0]}.{BUILD_VERSION[1]}.{BUILD_VERSION[2]}")
        print(f"    contents.json: 256B header + AES-256-CFB8 body, built-in Skin & Persona key")
        print(f"    raw files: {', '.join(p for p, k in entries if k is None)}")
        print(f"    per-file encrypted: {', '.join(p for p, k in entries if k is not None)}")

    # the flat .mcpersona files are superseded by the nested {itemId}.zip builds
    for source_name in VARIANTS.values():
        (PERSONA_DIR / source_name).unlink()
        print(f"removed build-time source {source_name}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
