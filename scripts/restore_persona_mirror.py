#!/usr/bin/env python3
"""Restore the 2019 persona piece binaries from the repository's git history.

WHY THIS EXISTS
The dressing room lists ONLY pieces whose binary is on disk (mirror-first),
and every upstream copy of the 2019 character-creator binaries is dead
(xforge gone, re-publications never migrated the old GUIDs, nothing on the
Internet Archive). The 234 original `personabinary` zips still exist in THIS
repository's git history — in the tree of the commit BEFORE
8e0ec99 ("chore(assets): remove Mojang persona binaries and thumbnails").
This script reads them straight out of git objects and puts them where the
ApiServer mirror expects them: <volume>/mojang-assets/playfab/persona/.

Modes (at least one required; they run in the order listed):

  --list           count the persona zips in the restore tree (and their size)
  --target DIR     idempotent extract: write every zip as <itemId>.zip into
                   DIR; files already present with a matching size are
                   skipped and counted, not rewritten
  --tarball OUT    build one tar.gz (flat <itemId>.zip entries) for moving to
                   a phone volume: adb push + untar inside the container
  --verify DIR     report how many zips of the restore set are present in DIR

The git ref is parameterized with --ref (default: 8e0ec99^). It must resolve
to exactly one commit; anything else is a readable error.

The script NEVER writes inside the repository worktree (--target/--tarball
paths inside it are refused): these are Mojang/Microsoft Marketplace assets
that must not be committed back into the public tree — that is exactly what
commit 8e0ec99 removed.

Usage:
    python3 -I scripts/restore_persona_mirror.py --list
    python3 -I scripts/restore_persona_mirror.py \
        --target /opt/apace-persistent/data/mojang-assets/playfab/persona
    python3 -I scripts/restore_persona_mirror.py --tarball /tmp/persona.tar.gz
    python3 -I scripts/restore_persona_mirror.py \
        --verify /opt/apace-persistent/data/mojang-assets/playfab/persona

Stdlib only (argparse, io, os, subprocess, sys, tarfile).
"""

import argparse
import io
import os
import subprocess
import sys
import tarfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PERSONA_GIT_PREFIX = "staticdata-extras/playfab/persona"
DEFAULT_REF = "8e0ec99^"


# --------------------------------------------------------------------------
# git plumbing
# --------------------------------------------------------------------------

def run_git(args):
    return subprocess.run(
        ["git", "-C", REPO_ROOT] + args, capture_output=True
    )


def fail(message):
    print(f"error: {message}", file=sys.stderr)
    sys.exit(1)


def resolve_commit(ref):
    """Resolve --ref to exactly one commit object id, with readable errors."""
    r = run_git(["rev-parse", "--verify", ref])
    if r.returncode != 0:
        stderr = r.stderr.decode(errors="replace").strip().splitlines()
        detail = stderr[-1] if stderr else "git rev-parse failed"
        if "ambiguous" in detail:
            fail(
                f"ref '{ref}' is ambiguous (matches several objects); "
                "pass a longer object id"
            )
        if "unknown revision" in detail or "Not a valid object name" in detail:
            fail(
                f"ref '{ref}' does not exist in this repository "
                f"({REPO_ROOT}); nothing to restore"
            )
        fail(f"ref '{ref}' does not resolve: {detail}")
    oid = r.stdout.decode().strip()
    t = run_git(["cat-file", "-t", oid])
    otype = t.stdout.decode().strip()
    if otype == "commit":
        return oid
    if otype == "tag":
        # peel an annotated tag; safe because oid is a full object id
        r2 = run_git(["rev-parse", "--verify", oid + "^{commit}"])
        if r2.returncode != 0:
            fail(f"tag '{ref}' does not peel to a commit")
        return r2.stdout.decode().strip()
    fail(f"ref '{ref}' resolves to a {otype} object, but a single commit "
         "is required")


def persona_entries(commit):
    """Return the persona zips in the commit tree as (path, blob_sha, size)."""
    r = run_git(["ls-tree", "-r", "-l", commit, "--", PERSONA_GIT_PREFIX])
    if r.returncode != 0:
        stderr = r.stderr.decode(errors="replace").strip()
        fail(f"git ls-tree failed for commit {commit}: {stderr}")
    entries = []
    for line in r.stdout.decode(errors="replace").splitlines():
        if not line:
            continue
        meta, path = line.split("\t", 1)
        parts = meta.split()
        if len(parts) != 4 or parts[1] != "blob":
            continue
        _mode, _type, sha, size = parts
        if not path.endswith(".zip"):
            continue
        entries.append((path, sha, int(size)))
    entries.sort(key=lambda e: e[0])
    return entries


def read_blob(commit, path):
    r = run_git(["cat-file", "blob", f"{commit}:{path}"])
    if r.returncode != 0:
        stderr = r.stderr.decode(errors="replace").strip()
        fail(f"git cat-file failed for {path}: {stderr}")
    return r.stdout


# --------------------------------------------------------------------------
# safety: never write inside the repository worktree
# --------------------------------------------------------------------------

def ensure_outside_repo(path, what):
    real_repo = os.path.realpath(REPO_ROOT)
    real_path = os.path.realpath(os.path.abspath(path))
    try:
        rel = os.path.relpath(real_path, real_repo)
    except ValueError:  # different root (Windows drive); nothing in common
        return
    if not rel.startswith(os.pardir):
        fail(
            f"refusing to write: {what} '{path}' resolves inside the "
            f"repository worktree ({REPO_ROOT}); these binaries must never "
            "land in the public tree — use a volume path or /tmp"
        )


def mib(nbytes):
    return nbytes / (1024.0 * 1024.0)


# --------------------------------------------------------------------------
# modes
# --------------------------------------------------------------------------

def do_list(ref, commit, entries):
    total = sum(size for _, _, size in entries)
    print(f"ref {ref} -> commit {commit}")
    print(f"{PERSONA_GIT_PREFIX}: {len(entries)} persona zips, "
          f"{mib(total):.1f} MiB of blobs")
    if entries:
        print(f"first: {os.path.basename(entries[0][0])}")
        print(f"last:  {os.path.basename(entries[-1][0])}")
    if not entries:
        print("nothing to restore at this ref — is this the right commit?")


def do_target(commit, entries, target):
    ensure_outside_repo(target, "--target")
    os.makedirs(target, exist_ok=True)
    extracted = skipped = 0
    for path, _sha, size in entries:
        dest = os.path.join(target, os.path.basename(path))
        if os.path.isfile(dest) and os.path.getsize(dest) == size:
            skipped += 1
            continue
        data = read_blob(commit, path)
        tmp = dest + ".part"
        with open(tmp, "wb") as f:
            f.write(data)
        os.replace(tmp, dest)
        extracted += 1
        print(f"  + {os.path.basename(path)}")
    print(f"{target}: extracted {extracted}, skipped {skipped} "
          f"(of {len(entries)} zips in the restore set)")
    print("restart the ApiServer after landing files by hand — safest way "
          "to rebuild the mirror state")


def do_tarball(commit, entries, out_path):
    ensure_outside_repo(out_path, "--tarball")
    with tarfile.open(out_path, "w:gz") as tar:
        for path, _sha, size in entries:
            data = read_blob(commit, path)
            info = tarfile.TarInfo(os.path.basename(path))
            info.size = len(data)
            info.mtime = 0
            info.mode = 0o644
            info.type = tarfile.REGTYPE
            info.uid = info.gid = 0
            info.uname = info.gname = ""
            tar.addfile(info, io.BytesIO(data))
    print(f"wrote {out_path}: {len(entries)} zips, "
          f"{mib(os.path.getsize(out_path)):.1f} MiB tarball")
    print("move it to the device (adb push), then untar inside the volume: "
          "tar -xzf <name>.tar.gz -C <volume>/mojang-assets/playfab/persona")


def do_verify(entries, target):
    if not os.path.isdir(target):
        fail(f"--verify dir '{target}' does not exist (nothing to verify)")
    present = matched = 0
    missing = []
    mismatched = []
    for path, _sha, size in entries:
        dest = os.path.join(target, os.path.basename(path))
        if not os.path.isfile(dest):
            missing.append(os.path.basename(path))
            continue
        present += 1
        if os.path.getsize(dest) == size:
            matched += 1
        else:
            mismatched.append(os.path.basename(path))
    print(f"coverage in {target}: {present}/{len(entries)} zips present")
    print(f"  size-matched: {matched}, size-mismatched: {len(mismatched)}")
    for name in mismatched[:10]:
        print(f"  size differs: {name}")
    if len(mismatched) > 10:
        print(f"  ... and {len(mismatched) - 10} more with size mismatch")
    for name in missing[:10]:
        print(f"  missing: {name}")
    if len(missing) > 10:
        print(f"  ... and {len(missing) - 10} more missing")


# --------------------------------------------------------------------------
# entry point
# --------------------------------------------------------------------------

def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Restore persona piece binaries from git history "
                    "(pre-8e0ec99 tree) into the server volume mirror.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter)
    parser.add_argument("--list", action="store_true",
                        help="count the persona zips in the restore tree")
    parser.add_argument("--target", default=None, metavar="DIR",
                        help="idempotently extract every zip into DIR")
    parser.add_argument("--tarball", default=None, metavar="OUT.tar.gz",
                        help="build one tar.gz of all zips for adb push")
    parser.add_argument("--verify", default=None, metavar="DIR",
                        help="report how many restore-set zips are in DIR")
    parser.add_argument("--ref", default=DEFAULT_REF,
                        help="git ref of the pre-removal tree (must resolve "
                             "to exactly one commit)")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    if not (args.list or args.target or args.tarball or args.verify):
        fail("pick at least one mode: --list, --target DIR, "
             "--tarball OUT.tar.gz, --verify DIR")

    commit = resolve_commit(args.ref)
    entries = persona_entries(commit)

    if args.list:
        do_list(args.ref, commit, entries)
    if args.target:
        do_target(commit, entries, args.target)
    if args.tarball:
        do_tarball(commit, entries, args.tarball)
    if args.verify:
        do_verify(entries, args.verify)


if __name__ == "__main__":
    main()
