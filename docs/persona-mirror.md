# The persona mirror: reviving the character-creator tabs

The dressing room lists ONLY pieces whose binary is on disk (mirror-first:
without a binary the equip hangs the client on "wczytywanie…"). The 2019
character-creator launch pieces (hair/eyes/mouth/skin tones) have dead binaries
everywhere upstream — xforge is gone (0/231 on every URL shape, re-probed
22.11), the content1/2 re-publications never migrated the old GUIDs, and the
Internet Archive has nothing. That is why the hair/eyes/mouth tabs were empty
while newer marketplace pieces (republished on the live CDN) filled the other
tabs.

## The one remaining copy of the real binaries

The 234 original `personabinary` zips (one `ppack0.zip` container per piece)
still exist in the repository's **git history** (the state before commit
8e0ec99 "remove Mojang persona binaries and thumbnails").

`scripts/restore_persona_mirror.py` (python3, stdlib only) reads them straight
out of the git objects and never writes inside the repository worktree:

```
# what is recoverable (default ref 8e0ec99^, override with --ref)
python3 -I scripts/restore_persona_mirror.py --list

# extract every zip as <itemId>.zip into the volume mirror (idempotent:
# already-present files with a matching size are skipped)
python3 -I scripts/restore_persona_mirror.py \
    --target /opt/apace-persistent/data/mojang-assets/playfab/persona

# or build one flat tar.gz of all 234 zips, adb push it to the phone and
# untar inside the volume (tar -xzf <name>.tar.gz -C .../persona/)
python3 -I scripts/restore_persona_mirror.py --tarball /tmp/persona-mirror-restore.tar.gz

# numeric coverage afterwards: how many restore-set zips are in the dir
python3 -I scripts/restore_persona_mirror.py \
    --verify /opt/apace-persistent/data/mojang-assets/playfab/persona
```

The files can reach the server volume three ways: run `--target` directly on
the docker host, `--tarball` + `adb push` + untar on the device, or upload the
zips through the panel. After landing files by hand, **restart the ApiServer**
— the mirror is re-enumerated by the running server, but a clean restart is
the safest way to rebuild the mirror/stamp state.

On the docker host that target dir is
`/opt/apace-persistent/data/mojang-assets/playfab/persona/`
(the compose maps `/opt/apace-persistent/data` to `/app/data`, and the
ApiServer reads `<data>/mojang-assets/playfab/persona/<itemId>.zip`).

(Equivalent manual extraction, if the script is unavailable:
`git -C <repo> show '8e0ec99^:staticdata-extras/playfab/persona/<itemId>.zip'
> <volume>/mojang-assets/playfab/persona/<itemId>.zip`.)

## What happens when the files land there

- `GetLocalPersonaBinaries` enumerates the dir on every stamp check — the dir
  mtime change is the signal; the very next catalog request rebuilds the
  hidden-id set, and a locally mirrored file flips a dead probe verdict to
  "live" (the dead→alive bridge in `TryEnsureBinaryAsync`). Still, restart the
  ApiServer after a manual drop (see above).
- The offer, the blank-slot filler and every face tab pick the pieces up
  immediately; `PersonaCdnController` serves the mirror file for the
  `/playfab/persona/<id>.zip` contents URL of the offered item.
- `GetPublishedItem` resolves the item and the CDN proxy hands out the same
  bytes, so the client imports the REAL geometry/textures — equipping works
  and renders correctly.
- Accepting new mirror files through the offer path has been hardened in the
  code (content stamps plus `MarkBinaryLanded`), so a piece whose binary
  arrives later re-arms cleanly instead of staying hidden.

## Measured coverage after landing the history set

Measured against the live marketplace (26.11):

| piece type   | covered / marketplace |
|--------------|-----------------------|
| hair         | 41 / 139              |
| eyes         | 31 / 57               |
| mouth        | 18 / 35               |
| skin         | 7 / 37                |
| facial_hair  | 0 / 39                |

`face_accessory` remains to be measured on the volume after the restore (the
zip names are opaque UUIDs, so per-type coverage can only be counted against
the landed mirror). Plus 34 head, 22 top, 20 outerwear, 15 bottom, 12 feet and
the rest of the 234. **Facial hair stays empty: 0 of its 39 marketplace pieces
have a recoverable binary anywhere** (not in history, not re-published, not
archived) — that tab cannot be filled without new source material.

## Licensing

The zips are Mojang/Microsoft Marketplace assets. They must never be
committed back into the public tree (that is exactly what commit 8e0ec99
removed) — the mirror dir is a private server-side volume, like the
mirrored catalog itself. The restore script refuses any `--target`/`--tarball`
path inside the repository worktree.
