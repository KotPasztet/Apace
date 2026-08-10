#!/bin/sh
set -eu

# ── Seed defaults into volume-mounted directories ──────────────────────────────
# Coolify / Docker volumes override the baked-in files in the image.
# Each volume mount needs manual seeding: if the target is missing, copy it
# from the baked-in default so first deploy works without manual SCP.

SRC_MODS="/app/defaults/mods"
DST_MODS="/app/staticdata/server_template_dir/mods"

if [ -d "$SRC_MODS" ] && [ ! -f "$DST_MODS/fountain-0.0.1.jar" ]; then
    echo "entrypoint: seeding Fabric mods into server_template_dir..."
    mkdir -p "$DST_MODS"
    cp -v "$SRC_MODS"/*.jar "$DST_MODS/" 2>&1
fi

# ── Launch ─────────────────────────────────────────────────────────────────────
exec pwsh ./run_launcher.ps1
