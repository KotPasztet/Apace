#!/usr/bin/env bash
set -euo pipefail

# Apace — Minecraft Earth replacement server
# Self-update for Docker installs: backs up the persistent data, pulls a new
# image and restarts the container. Supports rolling the image (and data) back.
#
# It works on installs made by OLDER versions too: nothing this script would
# have created is assumed to exist — the layout is detected by FEATURE
# (missing persistent subdirs, config.json stored as a directory, compose
# without the BRIDGE_PORT parameter, installer-injected platform line).
#
# Usage: curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/update.sh | bash
#        bash update.sh --rollback
#        bash update.sh --backup-only
#        bash update.sh --help

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

APACE_DIR="$HOME/apace"
TAG=""              # empty = keep the tag the compose file already uses
MODE="update"       # update | backup-only | rollback
RESTORE_BACKUP=""   # tarball path, or "latest"
MAKE_BACKUP=1
ASSUME_YES=0

STATE_FILE=""       # <dir>/.apace-update.json, set once the install dir is known
BACKUP_DIR=""       # <parent-of-persistent-root>/apace-persistent-backups
LAST_BACKUP=""      # tarball created by backup_tarball
BACKUP_KEEP=3
CONTAINER_UID=1654  # uid of the 'app' user inside the image

err()   { echo -e "${RED}$*${RST}" >&2; }
warn()  { echo -e "${YLW}$*${RST}"; }
ok()    { echo -e "${GRN}$*${RST}"; }
head_() { echo -e "${BLD}$*${RST}"; }

die() { err "ERROR: $*"; exit 1; }

usage() {
    head_ "=== Apace Updater ==="
    echo ""
    echo "Usage: update.sh [options]"
    echo ""
    echo "Options:"
    echo "  --dir <path>            Install root holding docker-compose.yml (default: ~/apace)"
    echo "  --tag <main|dev>        Image tag to move to (default: keep the current one)"
    echo "  --backup-only           Back up the persistent data and exit"
    echo "  --rollback              Go back to the image that ran before the last update"
    echo "  --restore-backup <f>    With --rollback: also restore a data backup (file path, or 'latest')"
    echo "  --no-backup             Skip the pre-update backup (NOT recommended)"
    echo "  --yes                   Do not ask for confirmation"
    echo "  -h, --help              This help"
    echo ""
}

# ─── Flags ────────────────────────────────────────────────────────────────
while [ $# -gt 0 ]; do
    case "$1" in
        --dir)            [ $# -ge 2 ] || die "--dir needs a path"; APACE_DIR="$2"; shift 2 ;;
        --tag)            [ $# -ge 2 ] || die "--tag needs a value"; TAG="$2"; shift 2 ;;
        --backup-only)    MODE="backup-only"; shift ;;
        --rollback)       MODE="rollback"; shift ;;
        --restore-backup) [ $# -ge 2 ] || die "--restore-backup needs a file name or 'latest'"; RESTORE_BACKUP="$2"; shift 2 ;;
        --no-backup)      MAKE_BACKUP=0; shift ;;
        --yes|-y)         ASSUME_YES=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *)                usage >&2; die "unknown option: $1" ;;
    esac
done

case "$TAG" in ""|main|dev) ;; *) die "--tag must be 'main' or 'dev' (got '$TAG')" ;; esac
if [ -n "$RESTORE_BACKUP" ] && [ "$MODE" != "rollback" ]; then
    die "--restore-backup only makes sense together with --rollback"
fi

confirm() { # $1 = prompt; succeeds on "y"
    if [ "$ASSUME_YES" -eq 1 ]; then return 0; fi
    local reply=""
    if [ -r /dev/tty ]; then
        read -r -p "$1" reply < /dev/tty || return 1
    elif [ -t 0 ]; then
        read -r -p "$1" reply || return 1
    else
        err "Cannot ask for confirmation (no terminal) — re-run with --yes."
        return 1
    fi
    case "$reply" in y|Y|yes|YES|Yes) return 0 ;; *) return 1 ;; esac
}

# ─── Privileges: docker may need sudo, the persistent root usually does ───
AS_ROOT=""
if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null 2>&1 || die "not running as root and sudo is not available"
    AS_ROOT="sudo"
fi

DOCKER="docker"
if ! docker info >/dev/null 2>&1; then
    if $AS_ROOT docker info >/dev/null 2>&1; then
        DOCKER="$AS_ROOT docker"
    else
        die "docker is not running (try: sudo systemctl start docker)"
    fi
fi

if $DOCKER compose version >/dev/null 2>&1; then
    COMPOSE="$DOCKER compose"
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE="docker-compose"
    if [ -n "$AS_ROOT" ]; then COMPOSE="$AS_ROOT docker-compose"; fi
else
    die "neither 'docker compose' nor 'docker-compose' is available"
fi

echo -e "${BLD}=== Apace Updater ===${RST}"
echo ""

[ -d "$APACE_DIR" ] || die "install dir $APACE_DIR does not exist (run install.sh first)"

# ─── Compose file ─────────────────────────────────────────────────────────
COMPOSE_FILE=""
for cand in "$APACE_DIR/docker-compose.yml" "$APACE_DIR/docker-compose.yaml" "$APACE_DIR/compose.yaml" "$APACE_DIR/compose.yml"; do
    if [ -f "$cand" ]; then COMPOSE_FILE="$cand"; break; fi
done
if [ -z "$COMPOSE_FILE" ]; then
    die "no docker-compose.yml in $APACE_DIR — (re)run install.sh first: curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash"
fi
STATE_FILE="$APACE_DIR/.apace-update.json"

# Print the entries of a service section (ports:, volumes:, ...) without the
# leading "- ". Handles indentation and comments; stops at the next key that is
# not deeper than the section key, so "environment:" never leaks in.
read_section() { # $1 = section name
    awk -v SECT="$1" '
        { n = match($0, /[^ \t#]/); ind = (n == 0 ? 9999 : n - 1) }
        f && ind <= sectind { f = 0 }
        !f && $0 ~ "^[ \t]*" SECT ":" { sectind = ind; f = 1; next }
        f && ind > sectind && $0 ~ /^[ \t]*-[ \t]/ { sub(/^[ \t]*-[ \t]*/, "", $0); print }
    ' "$COMPOSE_FILE"
}

trim() { # strips whitespace, CR, quotes and trailing comments
    local s="$1"
    s="${s%$'\r'}"
    s="${s%%#*}"
    s="${s#"${s%%[![:space:]]*}"}"
    s="${s%"${s##*[![:space:]]}"}"
    s="${s%\"}"; s="${s#\"}"
    s="${s%\'}"; s="${s#\'}"
    printf '%s' "$s"
}

# ─── Parse image / volumes / ports out of the existing compose file ───────
IMAGE_REF=""
IMAGE_NAME=""
IMAGE_TAG=""
PANEL_PORT=""       # host port reaching the panel (container port 5000)
BIND_HOSTS=""       # every bind-mount host path, newline separated

img_line=$(grep -E '^[[:space:]]*image:[[:space:]]*[^#]' "$COMPOSE_FILE" | head -n1 || true)
[ -n "$img_line" ] || die "no 'image:' line in $COMPOSE_FILE — this does not look like an Apace compose file; re-run install.sh"
IMAGE_REF=$(trim "${img_line#*image:}")
if [[ "$IMAGE_REF" == *@* ]]; then
    IMAGE_NAME="${IMAGE_REF%%@*}"; IMAGE_TAG="${IMAGE_REF##*@}"
else
    last="${IMAGE_REF##*/}"
    if [[ "$last" == *:* ]]; then IMAGE_NAME="${IMAGE_REF%:*}"; IMAGE_TAG="${last##*:}"; else IMAGE_NAME="$IMAGE_REF"; IMAGE_TAG="latest"; fi
fi

while IFS= read -r entry; do
    if [ -n "$entry" ]; then
        entry=$(trim "$entry")
        case "$entry" in
            *:*)
                host="${entry%:*}"
                # named volumes (no path separator) are not bind mounts
                case "$host" in */*|[A-Za-z]:/*|./*|../*) BIND_HOSTS="$BIND_HOSTS$host"$'\n' ;; esac
                ;;
        esac
    fi
done < <(read_section volumes)

while IFS= read -r entry; do
    if [ -n "$entry" ]; then
        entry=$(trim "$entry")
        case "$entry" in
            *:*)
                host="${entry%:*}"; cont="${entry##*:}"; cont="${cont%%/*}"
                case "$host" in *'$'*) continue ;; esac   # "${VAR:-19132}" — not resolvable here
                if [ "$cont" = "5000" ] && [ -z "$PANEL_PORT" ]; then PANEL_PORT="$host"; fi
                ;;
        esac
    fi
done < <(read_section ports)

# Persistent data root = the most common parent directory of the bind mounts.
# Works for /opt/apace-persistent, C:/apace-persistent and arbitrary layouts
# (Coolify, custom paths) without hardcoding anything.
PERSISTENT=$(printf '%s' "$BIND_HOSTS" | awk '
    NF {
        d = $0
        sub(/\/[^\/]*$/, "", d)
        c[d]++
    }
    END {
        best = ""; bn = -1
        for (d in c) if (c[d] > bn || (c[d] == bn && length(d) > length(best))) { bn = c[d]; best = d }
        print best
    }')
[ -n "$PERSISTENT" ] || die "could not detect the persistent data root from the volumes in $COMPOSE_FILE"
if [ -z "$PANEL_PORT" ]; then
    PANEL_PORT="5000"
    warn "could not find a published panel port in the compose file — assuming 5000"
fi

head_ "Install root:    $APACE_DIR"
head_ "Compose file:    $COMPOSE_FILE"
head_ "Image:           $IMAGE_REF"
head_ "Persistent data: $PERSISTENT"
head_ "Panel port:      $PANEL_PORT"
echo ""

# ─── Heal old layouts (idempotent, feature-detected) ───────────────────────
heal_layout() {
    local d cfg bp envf cur owner
    local -a missing=()
    head_ "Checking the persistent data layout"
    echo "→ Ensuring $PERSISTENT and its subdirs exist (older installs may miss some)"
    for d in launcher-data launcher-logs data dataprotection-keys resourcepacks server-template-dir logs fabric-data; do
        if [ ! -d "$PERSISTENT/$d" ]; then missing+=("$PERSISTENT/$d"); fi
    done
    if [ "${#missing[@]}" -gt 0 ]; then
        printf '  creating: %s\n' "${missing[@]}"
        $AS_ROOT mkdir -p "$PERSISTENT" "${missing[@]}"
    else
        $AS_ROOT mkdir -p "$PERSISTENT"
    fi

    cfg="$PERSISTENT/config.json"
    if [ -d "$cfg" ]; then
        warn "→ $cfg is a DIRECTORY (old broken install) — removing it"
        $AS_ROOT rm -rf "$cfg"
    fi
    if [ -f "$cfg" ]; then
        echo "  config.json present — left untouched (the app rewrites it on every load)"
    else
        echo "→ Seeding $cfg with {\"ApiPort\":1808} (it was missing)"
        printf '{"ApiPort":1808}\n' | $AS_ROOT tee "$cfg" >/dev/null
    fi

    # Drift prevention: the panel can listen on a non-default bridge port while
    # the compose file still publishes 19132. Mirror it into .env so the UDP
    # mapping keeps matching the app after the container is recreated.
    bp=$(sed -n 's/.*"BridgePort"[[:space:]]*:[[:space:]]*"\{0,1\}\([0-9][0-9]*\)"\{0,1\}.*/\1/p' "$cfg" 2>/dev/null | head -n1 || true)
    envf="$APACE_DIR/.env"
    if [ -n "$bp" ] && [ "$bp" != "19132" ]; then
        if [ ! -f "$envf" ]; then
            echo "→ config.json says BridgePort=$bp but there is no .env next to the compose file — writing BRIDGE_PORT=$bp to $envf"
            if ! printf 'BRIDGE_PORT=%s\n' "$bp" > "$envf" 2>/dev/null; then
                printf 'BRIDGE_PORT=%s\n' "$bp" | $AS_ROOT tee "$envf" >/dev/null
            fi
        elif ! grep -q '^BRIDGE_PORT=' "$envf"; then
            echo "→ Appending BRIDGE_PORT=$bp to the existing $envf"
            printf 'BRIDGE_PORT=%s\n' "$bp" >> "$envf"
        else
            cur=$(sed -n 's/^BRIDGE_PORT=//p' "$envf" | head -n1)
            if [ "$cur" != "$bp" ]; then
                warn "  note: $envf has BRIDGE_PORT=$cur but config.json says $bp — keeping .env as is"
            fi
        fi
    fi

    owner=$(stat -c %u "$PERSISTENT" 2>/dev/null || stat -f %u "$PERSISTENT" 2>/dev/null || echo "?")
    if [ "$owner" = "$CONTAINER_UID" ]; then
        echo "  ownership is already uid $CONTAINER_UID — skipped"
    else
        echo "→ Setting the owner of $PERSISTENT (recursively) to uid $CONTAINER_UID"
        if ! $AS_ROOT chown -R "$CONTAINER_UID:$CONTAINER_UID" "$PERSISTENT" 2>/dev/null; then
            warn "  chown failed (uid $CONTAINER_UID has no host user) → falling back to chmod 777"
            $AS_ROOT chmod -R 777 "$PERSISTENT"
        fi
    fi
    echo ""

    if ! grep -q 'BRIDGE_PORT' "$COMPOSE_FILE"; then
        warn "note: this compose file predates the configurable bridge port (no \${BRIDGE_PORT:-19132} line)."
        warn "      Apace still runs, but re-download the compose file to pick up the new mapping —"
        warn "      and re-add your platform: line if the installer had injected one:"
        warn "        curl -sSLo '$COMPOSE_FILE' https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml"
        echo ""
    fi
}

# ─── Backups ───────────────────────────────────────────────────────────────
BACKUP_DIR="$(dirname "$PERSISTENT")/apace-persistent-backups"

list_backups() { # newest first
    ls -1t "$BACKUP_DIR" 2>/dev/null | grep -E '^apace-[0-9]+T[0-9]+Z\.(tar\.gz|tar\.zst)$' || true
}

backup_tarball() { # sets LAST_BACKUP
    local stamp ext out
    stamp=$(date -u +%Y%m%dT%H%M%SZ)
    if command -v zstd >/dev/null 2>&1; then ext="tar.zst"; else ext="tar.gz"; fi
    out="$BACKUP_DIR/apace-$stamp.$ext"
    $AS_ROOT mkdir -p "$BACKUP_DIR"
    echo "→ Backing up $PERSISTENT to $out"
    echo "  (excluding logs, launcher-logs, resourcepacks — they are reproducible)"
    if [ "$ext" = "tar.zst" ]; then
        $AS_ROOT tar --zstd -cf "$out" \
            --exclude="$(basename "$PERSISTENT")/logs" \
            --exclude="$(basename "$PERSISTENT")/launcher-logs" \
            --exclude="$(basename "$PERSISTENT")/resourcepacks" \
            -C "$(dirname "$PERSISTENT")" "$(basename "$PERSISTENT")"
    else
        $AS_ROOT tar -czf "$out" \
            --exclude="$(basename "$PERSISTENT")/logs" \
            --exclude="$(basename "$PERSISTENT")/launcher-logs" \
            --exclude="$(basename "$PERSISTENT")/resourcepacks" \
            -C "$(dirname "$PERSISTENT")" "$(basename "$PERSISTENT")"
    fi
    echo "  backup size: $($AS_ROOT du -h "$out" | cut -f1)"
    rotate_backups
    LAST_BACKUP="$out"
}

rotate_backups() {
    local old
    # shellcheck disable=SC2044
    for old in $(list_backups | tail -n +"$((BACKUP_KEEP + 1))"); do
        echo "→ Rotation: removing old backup $BACKUP_DIR/$old (keeping the newest $BACKUP_KEEP)"
        $AS_ROOT rm -f "$BACKUP_DIR/$old"
    done
}

resolve_backup() { # $1 = path or "latest"; prints the resolved path
    local want="$1" newest
    if [ "$want" = "latest" ]; then
        newest=$(list_backups | head -n1)
        if [ -z "$newest" ]; then
            die "no backups found in $BACKUP_DIR"
        fi
        printf '%s' "$BACKUP_DIR/$newest"
    else
        if [ ! -f "$want" ]; then
            die "backup tarball not found: $want"
        fi
        printf '%s' "$want"
    fi
}

# ─── Image / container helpers ─────────────────────────────────────────────
ROLLBACK_REF=""     # registry digest when known, else the local image id

detect_running_image() {
    local cid img
    cid=$($COMPOSE ps -q 2>/dev/null | head -n1 || true)
    img=""
    if [ -n "$cid" ]; then
        img=$($DOCKER inspect -f '{{.Image}}' "$cid" 2>/dev/null || true)
    fi
    if [ -z "$img" ]; then
        img=$($DOCKER image inspect -f '{{.Id}}' "$IMAGE_REF" 2>/dev/null || true)
    fi
    if [ -n "$img" ]; then
        ROLLBACK_REF=$($DOCKER image inspect -f '{{index .RepoDigests 0}}' "$img" 2>/dev/null || true)
        if [ -z "$ROLLBACK_REF" ]; then
            # locally built / never pushed image: there is no registry digest
            ROLLBACK_REF="$img"
            warn "  note: no registry digest for the image — using the local image id as the rollback point"
        fi
    fi
    if [ -z "$ROLLBACK_REF" ]; then
        warn "  note: no running container and no local image — the rollback point cannot be recorded"
    fi
}

patch_image() { # $1 = new image reference
    local newref="$1" tmp
    tmp="$COMPOSE_FILE.apace-new"
    echo "→ Rewriting the image line in $COMPOSE_FILE to: $newref"
    # only the image: line is touched, so an installer-injected platform: line survives
    if [ -w "$COMPOSE_FILE" ]; then
        sed -E "s|^([[:space:]]*image:).*$|\1 $newref|" "$COMPOSE_FILE" > "$tmp"
    else
        $AS_ROOT sh -c "sed -E 's|^([[:space:]]*image:).*$|\1 $newref|' '$COMPOSE_FILE' > '$tmp'"
    fi
    if ! grep -E '^[[:space:]]*image:' "$tmp" | grep -qF -- "$newref"; then
        rm -f "$tmp"
        die "failed to rewrite the image line — $COMPOSE_FILE left unchanged"
    fi
    if [ -w "$COMPOSE_FILE" ]; then
        mv "$tmp" "$COMPOSE_FILE"
    else
        $AS_ROOT mv "$tmp" "$COMPOSE_FILE"
    fi
    IMAGE_REF="$newref"
}

write_state() { # $1 = image ref to roll back to
    local tmp="$STATE_FILE.apace-new"
    {
        printf '{\n'
        printf '  "previousDigest": "%s",\n' "$1"
        printf '  "previousTag": "%s",\n' "$IMAGE_TAG"
        printf '  "backup": "%s",\n' "$LAST_BACKUP"
        printf '  "updatedAt": "%s"\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        printf '}\n'
    } > "$tmp"
    if [ -w "$APACE_DIR" ]; then
        mv "$tmp" "$STATE_FILE"
    else
        $AS_ROOT mv "$tmp" "$STATE_FILE"
    fi
    echo "→ Rollback point recorded in $STATE_FILE (previous image: ${1:-unknown})"
}

health_check() { # $1 = host panel port; the panel serves the login page on /
    local port="$1" i cid
    if ! command -v curl >/dev/null 2>&1; then
        warn "curl not found — falling back to a container-state check only"
        cid=$($COMPOSE ps -q 2>/dev/null | head -n1 || true)
        for i in $(seq 1 12); do
            if [ -n "$cid" ] && [ "$($DOCKER inspect -f '{{.State.Status}}' "$cid" 2>/dev/null || echo gone)" = "running" ]; then
                return 0
            fi
            sleep 5
        done
        return 1
    fi
    for i in $(seq 1 20); do
        if curl -fsS -o /dev/null --max-time 3 "http://127.0.0.1:$port/" 2>/dev/null; then
            return 0
        fi
        sleep 3
    done
    return 1
}

report_failure() {
    local cid
    err ""
    err "Apace did not come up healthy."
    cid=$($COMPOSE ps -q 2>/dev/null | head -n1 || true)
    if [ -n "$cid" ]; then
        echo "─── last 50 log lines ───"
        $DOCKER logs --tail 50 "$cid" 2>&1 || true
        echo "─────────────────────────"
    else
        warn "no container is running for this compose project"
    fi
    err "Nothing was rolled back automatically — your data is untouched."
    err "Watch it live:   cd $APACE_DIR && $COMPOSE logs -f"
    err "Go back instead: bash $0 --dir $APACE_DIR --rollback"
    exit 1
}

# ─── Update flow ───────────────────────────────────────────────────────────
do_update() {
    local new_ref new_digest
    if [ -n "$TAG" ] && [ "$TAG" != "$IMAGE_TAG" ]; then
        new_ref="$IMAGE_NAME:$TAG"
    else
        new_ref="$IMAGE_REF"
        if [ -z "$TAG" ]; then
            echo "Keeping the image tag the compose file already uses ($IMAGE_TAG)"
        fi
    fi

    heal_layout
    detect_running_image
    echo -e "Currently running: ${BLD}${ROLLBACK_REF:-unknown}${RST}"
    echo "About to: stop the container (flushes the SQLite WAL), back up $PERSISTENT,"
    echo "pull $new_ref and start it again. Downtime: a few minutes."
    if [ "$MAKE_BACKUP" -eq 0 ]; then
        warn "!! --no-backup given: NO data backup will be taken."
    fi
    if ! confirm "Continue? [y/N] "; then
        echo "Aborted — nothing was changed."
        return 0
    fi

    # fail early on an unusable backup location, BEFORE the container goes down
    if [ "$MAKE_BACKUP" -eq 1 ]; then
        echo "→ Creating the backup directory $BACKUP_DIR"
        $AS_ROOT mkdir -p "$BACKUP_DIR"
    fi

    LAST_BACKUP=""
    echo "→ Stopping the container (compose stop — flushes the SQLite WAL)"
    $COMPOSE stop

    if [ "$MAKE_BACKUP" -eq 1 ]; then
        if ! backup_tarball; then
            warn "backup failed — starting the previous container again"
            $COMPOSE up -d || true
            die "backup failed; Apace was started again and nothing was updated"
        fi
        echo ""
    else
        warn "Skipping the backup (--no-backup)."
    fi

    if [ "$new_ref" != "$IMAGE_REF" ]; then
        patch_image "$new_ref"
    fi

    echo "→ Pulling the new image"
    if ! $COMPOSE pull; then
        warn "pull failed — starting the previous image again"
        $COMPOSE up -d || true
        die "could not pull $new_ref (registry/network problem?); the previous container is running again"
    fi

    echo "→ Starting the updated container (compose up -d)"
    $COMPOSE up -d

    echo "→ Waiting for the panel on http://127.0.0.1:$PANEL_PORT/ (up to ~60s)"
    if health_check "$PANEL_PORT"; then
        new_digest=$($DOCKER image inspect -f '{{index .RepoDigests 0}}' "$IMAGE_REF" 2>/dev/null || true)
        write_state "$ROLLBACK_REF"
        echo ""
        ok "Apace updated!"
        echo -e "  Previous image: ${BLD}${ROLLBACK_REF:-unknown}${RST}"
        echo -e "  New image:      ${BLD}${new_digest:-$IMAGE_REF}${RST}"
        echo -e "  Backup:         ${BLD}${LAST_BACKUP:-none}${RST}"
        if [ -n "$LAST_BACKUP" ]; then
            echo "  If anything looks wrong: bash $0 --dir $APACE_DIR --rollback"
        fi
    else
        report_failure
    fi
}

# ─── Backup-only flow ──────────────────────────────────────────────────────
do_backup_only() {
    heal_layout
    echo "About to archive $PERSISTENT (data only — no image change, no downtime)."
    if ! confirm "Continue? [y/N] "; then
        echo "Aborted — nothing was changed."
        return 0
    fi
    backup_tarball
    echo ""
    ok "Backup done: $LAST_BACKUP"
}

# ─── Rollback flow ─────────────────────────────────────────────────────────
do_rollback() {
    local prev tarball="" top dest
    if [ ! -f "$STATE_FILE" ]; then
        die "no $STATE_FILE — this install has no recorded update to roll back (run an update first)"
    fi
    prev=$(sed -n 's/.*"previousDigest"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE_FILE")
    if [ -z "$prev" ]; then
        die "$STATE_FILE has no previousDigest — cannot roll back"
    fi
    if [ -n "$RESTORE_BACKUP" ]; then
        tarball=$(resolve_backup "$RESTORE_BACKUP")
    fi

    heal_layout
    echo -e "Rolling the image back to: ${BLD}$prev${RST}"
    if [ -n "$tarball" ]; then
        echo -e "Also restoring data backup: ${BLD}$tarball${RST}"
        warn "!! Restoring older app.db / live.db / earth.db is one-way: the EF Core migrations"
        warn "!! the newer version already applied cannot be undone by the app. The supported"
        warn "!! combination is older data + older image — exactly what this does — but any"
        warn "!! accounts or buildplates created since that backup are lost."
    fi
    if ! confirm "Continue? [y/N] "; then
        echo "Aborted — nothing was changed."
        return 0
    fi

    echo "→ Stopping the container (compose stop — flushes the SQLite WAL)"
    $COMPOSE stop

    if [ -n "$tarball" ]; then
        # The tarball was written as "<basename-of-persistent-root>/..." — extract
        # next to the root so its contents land back inside it. A tarball from a
        # different layout is extracted into the root instead.
        top=$($AS_ROOT tar -tf "$tarball" 2>/dev/null | head -n1 | cut -d/ -f1)
        if [ "$top" = "$(basename "$PERSISTENT")" ]; then
            dest="$(dirname "$PERSISTENT")"
        else
            dest="$PERSISTENT"
            warn "  tarball layout differs from this install — extracting into $PERSISTENT"
        fi
        echo "→ Extracting $tarball into $dest (overwrites the existing data)"
        $AS_ROOT tar -xf "$tarball" -C "$dest"
        # a restore can resurrect old ownership
        if ! $AS_ROOT chown -R "$CONTAINER_UID:$CONTAINER_UID" "$PERSISTENT" 2>/dev/null; then
            $AS_ROOT chmod -R 777 "$PERSISTENT" 2>/dev/null || true
        fi
    fi

    if ! $DOCKER image inspect "$prev" >/dev/null 2>&1; then
        echo "→ $prev is not in the local docker cache — pulling it"
        $DOCKER pull "$prev" || die "cannot obtain $prev (gone from the registry and not cached locally)"
    fi

    patch_image "$prev"

    echo "→ Starting the rolled-back container (compose up -d)"
    # pull_policy is "always"; for a digest ref that pull returns the identical
    # image, so an old release stays old. If the digest vanished from the
    # registry, run with --pull never and use the cached copy instead.
    if ! $COMPOSE up -d --pull never; then
        warn "compose rejected --pull never (older docker-compose?) — retrying with the default pull policy"
        $COMPOSE up -d
    fi

    echo "→ Waiting for the panel on http://127.0.0.1:$PANEL_PORT/ (up to ~60s)"
    if health_check "$PANEL_PORT"; then
        echo ""
        ok "Rolled back to $prev"
        echo "  The image line in $COMPOSE_FILE now points at the old digest; a later"
        echo "  update.sh run (or --tag main) moves it back to a normal tag."
    else
        report_failure
    fi
}

case "$MODE" in
    update)      do_update ;;
    backup-only) do_backup_only ;;
    rollback)    do_rollback ;;
esac
