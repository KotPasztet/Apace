#!/bin/sh
set -eu

# ── Sync defaults into volume-mounted directories ──────────────────────────────
# Coolify / Docker volumes override the baked-in files in the image.
# Mods must be SYNCED, not just seeded on first deploy: a bind mount shadows
# image content, so a mod updated in a new image would otherwise never replace
# the stale copy inside the volume (this exact bug shipped an old Fountain mod
# without the control channel). Changed jars are overwritten on every start.

SRC_MODS="/app/defaults/mods"
DST_MODS="/app/staticdata/server_template_dir/mods"

if [ -d "$SRC_MODS" ]; then
    mkdir -p "$DST_MODS"
    for jar in "$SRC_MODS"/*.jar; do
        [ -f "$jar" ] || continue
        name="$(basename "$jar")"
        if [ ! -f "$DST_MODS/$name" ] || ! cmp -s "$jar" "$DST_MODS/$name"; then
            echo "entrypoint: updating mod $name in server_template_dir..."
            cp -f "$jar" "$DST_MODS/$name"
        fi
    done
fi

# ── Launch ─────────────────────────────────────────────────────────────────────
exec pwsh ./run_launcher.ps1
