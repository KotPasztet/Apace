#!/usr/bin/env bash
# shellcheck shell=bash
set -euo pipefail

# Apace — Minecraft Earth replacement server
# One-command migration from Solace (Linux / macOS / Termux / VPS).
#
# Detects an existing Solace install, installs Apace if it is missing, stops
# both servers, runs the converter's dry run, asks once, then migrates.
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.sh | bash
#   curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.sh | bash -s -- --dry-run
#   ./scripts/migrate-from-solace.sh --solace-dir ~/solace/solace-server --target /opt/apace-persistent
#
# The DB conversion itself lives in scripts/migrate-from-solace.py — this
# script only orchestrates (detect / install / stop / dry-run / confirm / run)
# and never touches the data itself. Nothing in the Solace directory is ever
# modified or deleted; the converter backs up the Apace target first.

RAW_BASE="https://raw.githubusercontent.com/KotPasztet/Apace/main"
CONVERTER_URL="$RAW_BASE/scripts/migrate-from-solace.py"

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

msg()    { echo -e "$*"; }
info()   { echo -e "${GRN}→${RST} $*"; }
warn()   { echo -e "${YLW}[warn]${RST} $*" >&2; }
err()    { echo -e "${RED}[error]${RST} $*" >&2; }
die()    { err "$*"; exit 1; }
head_()  { msg ""; msg "${BLD}$*${RST}"; }

usage() {
    echo "Apace ← Solace migration (Linux/macOS/Termux)"
    echo ""
    echo "Usage: migrate-from-solace.sh [options]"
    echo ""
    echo "Options:"
    echo "  --solace-dir <path>  Solace server directory (contains data/earth.db)."
    echo "                       Default: auto-detect (\$SOLACE_DIR, ~/solace/solace-server,"
    echo "                       ~/solace, ~/Solace, ~/Solace/solace-server)."
    echo "  --target <dir>       Apace persistent data directory."
    echo "                       Default: /opt/apace-persistent (Docker) or ~/apace (bare)."
    echo "  --docker             Migrate into / migrate for a Docker install."
    echo "  --no-docker          Migrate for a bare-metal install (no Docker)."
    echo "  --dry-run            Stop after showing the plan; write nothing."
    echo "  --no-backup          Skip the pre-migration backup of the Apace target."
    echo "  --yes, -y            Do not ask for confirmation."
    echo "  -h, --help           Show this help."
    echo ""
    echo "A dry run is ALWAYS shown first; the real migration only runs after"
    echo "you confirm. The Solace directory is only ever read, never deleted."
}

# ── Flags ─────────────────────────────────────────────────────────────────────

ENV_SOLACE_DIR="${SOLACE_DIR:-}"   # $SOLACE_DIR from the environment is a hint
SOLACE_DIR=""
TARGET=""
MODE=""            # docker | bare | "" (auto)
DRY_RUN_ONLY=0
ASSUME_YES=0
NO_BACKUP=0

while [ $# -gt 0 ]; do
    case "$1" in
        --solace-dir)  [ $# -ge 2 ] || die "--solace-dir needs a path";   SOLACE_DIR="$2"; shift 2 ;;
        --solace-dir=*) SOLACE_DIR="${1#*=}"; shift ;;
        --target)      [ $# -ge 2 ] || die "--target needs a directory";  TARGET="$2"; shift 2 ;;
        --target=*)    TARGET="${1#*=}"; shift ;;
        --docker)      MODE="docker"; shift ;;
        --no-docker)   MODE="bare"; shift ;;
        --dry-run)     DRY_RUN_ONLY=1; shift ;;
        --no-backup)   NO_BACKUP=1; shift ;;
        --yes|-y)      ASSUME_YES=1; shift ;;
        -h|--help)     usage; exit 0 ;;
        *)             usage >&2; die "unknown option: $1" ;;
    esac
done

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

# ── Environment helpers ───────────────────────────────────────────────────────

SUDO=""
if [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1; then
    SUDO="sudo"
fi

is_termux() {
    [ -n "${TERMUX_VERSION:-}" ] && return 0
    case "${PREFIX:-}" in */com.termux*) return 0 ;; esac
    return 1
}

fetch() { # $1 = url, $2 = output file
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL "$1" -o "$2"
    elif command -v wget >/dev/null 2>&1; then
        wget -qO "$2" "$1"
    else
        return 1
    fi
}

TMPDIR_MIGRATE=""
ensure_tmpdir() {
    if [ -z "$TMPDIR_MIGRATE" ]; then
        TMPDIR_MIGRATE="$(mktemp -d)" || die "mktemp failed"
    fi
}
trap '[ -n "$TMPDIR_MIGRATE" ] && rm -rf "$TMPDIR_MIGRATE"' EXIT

DOCKER="docker"
COMPOSE=""
detect_docker() {
    DOCKER="docker"
    if ! docker info >/dev/null 2>&1; then
        if [ -n "$SUDO" ] && $SUDO docker info >/dev/null 2>&1; then
            DOCKER="$SUDO docker"
        else
            COMPOSE=""
            return 1
        fi
    fi
    if $DOCKER compose version >/dev/null 2>&1; then
        COMPOSE="$DOCKER compose"
    elif command -v docker-compose >/dev/null 2>&1; then
        COMPOSE="docker-compose"
        if [ -n "$SUDO" ]; then COMPOSE="$SUDO docker-compose"; fi
    else
        COMPOSE=""
        return 1
    fi
    return 0
}

# ── Banner ────────────────────────────────────────────────────────────────────

msg "${BLD}=== Apace ← Solace migration ===${RST}"
msg ""
msg "This will: detect Solace, install Apace if needed, stop both servers,"
msg "show a dry-run plan, and — after your confirmation — migrate accounts,"
msg "progress, buildplates and world data into Apace."
msg ""

# ── Step 1/5: locate the converter + python ──────────────────────────────────

head_ "Step 1/5 — converter and python"

CONVERTER=""
script_src="${BASH_SOURCE[0]:-$0}"
if [ -f "$script_src" ]; then
    cand="$(dirname "$script_src")/migrate-from-solace.py"
    if [ -s "$cand" ]; then
        CONVERTER="$cand"
        info "Using the converter from this checkout: $CONVERTER"
    fi
fi
if [ -z "$CONVERTER" ]; then
    # Script was piped in (curl | bash) — fetch the converter next to it.
    ensure_tmpdir
    info "Fetching the converter from $CONVERTER_URL ..."
    fetch "$CONVERTER_URL" "$TMPDIR_MIGRATE/migrate-from-solace.py" \
        || die "could not download the converter — check your connection, or clone the repo and run scripts/migrate-from-solace.sh from it"
    [ -s "$TMPDIR_MIGRATE/migrate-from-solace.py" ] || die "downloaded converter is empty"
    CONVERTER="$TMPDIR_MIGRATE/migrate-from-solace.py"
fi

PY=""
if command -v python3 >/dev/null 2>&1; then
    PY="python3"
elif command -v python >/dev/null 2>&1 && python -c 'import sys; sys.exit(0 if sys.version_info[0] >= 3 else 1)' >/dev/null 2>&1; then
    PY="python"
fi
if [ -z "$PY" ]; then
    warn "python3 is not installed (the converter needs Python 3.8+)."
    if confirm "Install python3 now with the system package manager? [y/N] "; then
        if command -v apt-get >/dev/null 2>&1; then
            $SUDO apt-get update -qq && $SUDO apt-get install -y python3 || true
        elif command -v pkg >/dev/null 2>&1; then
            pkg install -y python || true
        elif command -v dnf >/dev/null 2>&1; then
            $SUDO dnf install -y python3 || true
        elif command -v pacman >/dev/null 2>&1; then
            $SUDO pacman -S --noconfirm python || true
        elif command -v brew >/dev/null 2>&1; then
            brew install python3 || true
        fi
    fi
    if command -v python3 >/dev/null 2>&1; then
        PY="python3"
    elif command -v python >/dev/null 2>&1; then
        PY="python"
    fi
fi
[ -n "$PY" ] || die "Python 3 is required but could not be found or installed.
  Install it manually (apt install python3 / dnf install python3 / pkg install python)
  and re-run this script."
if ! "$PY" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    die "$("$PY" -V 2>&1) is too old — Python 3.8+ is required."
fi
info "Python: $("$PY" -V 2>&1) ($("$PY" -c 'import sys; print(sys.executable)'))"
info "Converter: $CONVERTER"

# ── Step 2/5: find Solace ─────────────────────────────────────────────────────

head_ "Step 2/5 — locate the Solace install"

if [ -n "$SOLACE_DIR" ]; then
    [ -d "$SOLACE_DIR" ] || die "Solace directory not found: $SOLACE_DIR"
    [ -f "$SOLACE_DIR/data/earth.db" ] || die "$SOLACE_DIR does not look like a Solace server directory (no data/earth.db)."
else
    tried=""
    for c in "$ENV_SOLACE_DIR" "$HOME/solace/solace-server" "$HOME/solace" "$HOME/Solace" "$HOME/Solace/solace-server"; do
        [ -n "$c" ] || continue
        tried="$tried  $c"$'\n'
        if [ -f "$c/data/earth.db" ]; then
            SOLACE_DIR="$c"
            break
        fi
    done
    [ -n "$SOLACE_DIR" ] || die "Could not find a Solace installation. Paths checked:
$tried  Set the location explicitly:  $0 --solace-dir /path/to/solace-server"
fi
SOLACE_DIR="$(cd "$SOLACE_DIR" && pwd)"
info "Solace: $SOLACE_DIR"
[ -d "$SOLACE_DIR/staticdata" ] || warn "no staticdata/ in the Solace directory — resourcepacks will not migrate"

# ── Step 3/5: Apace install + mode, stop everything ───────────────────────────

head_ "Step 3/5 — Apace install and stopping servers"

APACE_DIR="$HOME/apace"
APACE_DOCKER=0
APACE_BARE=0

apace_docker_install() {
    local f
    for f in docker-compose.yml docker-compose.yaml compose.yml compose.yaml; do
        [ -f "$APACE_DIR/$f" ] && return 0
    done
    return 1
}
apace_compose_project() {
    [ -n "$COMPOSE" ] || return 1
    $COMPOSE ls 2>/dev/null | grep -qi apace
}
apace_bare_install() {
    [ -d "$APACE_DIR" ] || return 1
    [ -f "$APACE_DIR/run_launcher.ps1" ] || [ -d "$APACE_DIR/launcher" ] || [ -d "$APACE_DIR/data" ]
}

DOCKER_OK=0
if detect_docker; then DOCKER_OK=1; fi

if [ -z "$MODE" ]; then
    if [ "$DOCKER_OK" -eq 1 ] && { apace_docker_install || apace_compose_project; }; then
        MODE="docker"
        if apace_docker_install; then
            info "Found $APACE_DIR/docker-compose.yml → Docker mode"
        else
            info "docker compose lists an Apace project → Docker mode"
        fi
    elif apace_bare_install; then
        MODE="bare"
        info "Found a bare-metal Apace install in $APACE_DIR → bare-metal mode"
    elif is_termux; then
        MODE="bare"
        info "Termux detected → will install Apace with the Termux installer (bare-metal)"
    else
        # Nothing installed: Docker is the recommended default — the installer
        # bootstraps Docker itself if it is missing.
        MODE="docker"
        info "No Apace install detected → will install Apace (Docker, recommended)"
    fi
fi

INSTALL_NEEDED=0
if [ "$MODE" = "docker" ]; then
    if apace_docker_install || apace_compose_project; then
        APACE_DOCKER=1
        if [ "$DOCKER_OK" -eq 0 ]; then
            die "An Apace Docker install exists in $APACE_DIR, but Docker is not usable (try: sudo systemctl start docker)."
        fi
    else
        INSTALL_NEEDED=1
    fi
    if [ -z "$TARGET" ]; then TARGET="/opt/apace-persistent"; fi
else
    if apace_bare_install; then
        APACE_BARE=1
    else
        INSTALL_NEEDED=1
    fi
    if [ -z "$TARGET" ]; then TARGET="$APACE_DIR"; fi
fi

if [ "$INSTALL_NEEDED" -eq 1 ]; then
    if [ "$MODE" = "docker" ]; then
        msg ""
        msg "Apace (Docker) is not installed yet. The standard installer will:"
        msg "  • install Docker if it is missing (needs sudo)"
        msg "  • create $APACE_DIR with docker-compose.yml"
        msg "  • create $TARGET and pull + START the Apace image"
        msg "    (this script stops the stack again before migrating)"
        if ! confirm "Install Apace now? [y/N] "; then
            die "aborted — install Apace first:  curl -sSL $RAW_BASE/install.sh | bash"
        fi
        info "Running the Apace Docker installer ..."
        ensure_tmpdir
        fetch "$RAW_BASE/install.sh" "$TMPDIR_MIGRATE/apace-install.sh" || die "could not download install.sh"
        bash "$TMPDIR_MIGRATE/apace-install.sh" || die "the Apace installer failed — fix the problem above and re-run this script"
        detect_docker || die "Docker is still not usable after the install — re-run this script."
        APACE_DOCKER=1
        info "Stopping the freshly started Apace stack (migration needs it down) ..."
        (cd "$APACE_DIR" && $COMPOSE stop) \
            || die "could not stop the Apace stack — run: cd $APACE_DIR && $COMPOSE stop  (then re-run this script)"
    elif is_termux; then
        msg ""
        msg "Apace is not installed yet. The Termux installer will set up a minimal"
        msg "Ubuntu (proot-distro), install Java + .NET and download Apace into $APACE_DIR."
        msg "It does NOT start the server."
        if ! confirm "Install Apace now? [y/N] "; then
            die "aborted — install Apace first:  curl -sSL $RAW_BASE/install-termux.sh | bash"
        fi
        ensure_tmpdir
        info "Running the Apace Termux installer ..."
        fetch "$RAW_BASE/install-termux.sh" "$TMPDIR_MIGRATE/apace-install-termux.sh" || die "could not download install-termux.sh"
        bash "$TMPDIR_MIGRATE/apace-install-termux.sh" || die "the Apace installer failed — fix the problem above and re-run this script"
        APACE_BARE=1
    else
        msg ""
        msg "Apace is not installed yet. The bare-metal installer will download the"
        msg "latest Apace release into $APACE_DIR (it does NOT start anything)."
        msg "Still required afterwards: .NET 10 Runtime + Java 17 + PowerShell 7."
        if ! confirm "Install Apace now? [y/N] "; then
            die "aborted — install Apace first:  curl -sSL $RAW_BASE/install.sh | bash -s -- --no-docker"
        fi
        ensure_tmpdir
        info "Running the Apace bare-metal installer ..."
        fetch "$RAW_BASE/install.sh" "$TMPDIR_MIGRATE/apace-install.sh" || die "could not download install.sh"
        bash "$TMPDIR_MIGRATE/apace-install.sh" --no-docker || die "the Apace installer failed — fix the problem above and re-run this script"
        APACE_BARE=1
    fi
fi

# Target writability — fail BEFORE anything is stopped or written.
parent="$TARGET"
while [ ! -d "$parent" ] && [ "$parent" != "/" ]; do
    parent="$(dirname "$parent")"
done
if [ ! -w "$parent" ]; then
    die "cannot write to $TARGET (you are $(id -un)).
  Re-run with sudo, or pass --target <dir> you can write to."
fi

# ── Stop Solace ───────────────────────────────────────────────────────────────

if command -v systemctl >/dev/null 2>&1 \
   && systemctl list-unit-files 2>/dev/null | grep -q '^solace\.service'; then
    if systemctl is-active --quiet solace 2>/dev/null; then
        info "Stopping Solace (systemd unit solace.service) ..."
        $SUDO systemctl stop solace || warn "could not stop solace.service — stop it manually: sudo systemctl stop solace"
    else
        info "Solace systemd unit exists but is not running."
    fi
fi

EARTH_DB="$SOLACE_DIR/data/earth.db"
holders=""
if command -v fuser >/dev/null 2>&1; then
    holders="$(fuser "$EARTH_DB" 2>/dev/null || true)"
fi
if [ -z "${holders// /}" ]; then
    if command -v lsof >/dev/null 2>&1; then
        holders="$(lsof -t "$EARTH_DB" 2>/dev/null || true)"
    fi
fi
holders="$(echo "$holders" | tr ' ' '\n' | sed '/^$/d' | sort -u)"
if [ -n "$holders" ]; then
    warn "These processes still hold $EARTH_DB — Solace looks like it is running:"
    ps -o pid=,cmd= -p "$(echo "$holders" | tr '\n' ',' | sed 's/,$//')" 2>/dev/null || echo "$holders"
    warn "Migrating a running server can produce torn data."
    if ! confirm "Stop them and continue anyway? [y/N] "; then
        die "aborted — stop Solace (and its Minecraft servers) and re-run this script."
    fi
    # shellcheck disable=SC2086
    kill $holders 2>/dev/null || true
    sleep 2
    holders="$(fuser "$EARTH_DB" 2>/dev/null || true)"
    [ -z "${holders// /}" ] || warn "still holding the database: $holders — continuing at your own risk"
else
    info "Solace is not running (nothing holds $EARTH_DB)."
fi

# ── Stop Apace ────────────────────────────────────────────────────────────────

if [ "$APACE_DOCKER" -eq 1 ]; then
    if (cd "$APACE_DIR" && $COMPOSE ps -q 2>/dev/null | grep -q .); then
        info "Stopping the Apace Docker stack ..."
        (cd "$APACE_DIR" && $COMPOSE stop) \
            || die "could not stop Apace — run: cd $APACE_DIR && $COMPOSE stop  (then re-run this script)"
    else
        info "The Apace Docker stack is not running."
    fi
else
    apace_pids="$(pgrep -f 'Apace\.(LauncherUI|ApiServer|Buildplate|PreviewGenerator)|Solace\.(LauncherUI|ApiServer|Buildplate)' 2>/dev/null || true)"
    if [ -n "$apace_pids" ]; then
        warn "These Apace processes are still running:"
        ps -o pid=,cmd= -p "$(echo "$apace_pids" | tr '\n' ',' | sed 's/,$//')" 2>/dev/null || echo "$apace_pids"
        if confirm "Stop them now? [y/N] "; then
            # shellcheck disable=SC2086
            kill $apace_pids 2>/dev/null || true
            sleep 2
        fi
    fi
    if pgrep -f 'Apace\.(LauncherUI|ApiServer|Buildplate)|Solace\.(LauncherUI|ApiServer|Buildplate)' >/dev/null 2>&1; then
        die "Apace is still running — close the panel (and its servers) and re-run this script."
    fi
    warn "Make sure the Apace panel is closed (it locks the same databases)."
    confirm "Is Apace fully stopped? [y/N] " || die "aborted — close Apace and re-run this script."
fi

# ── Summary of what is about to happen ────────────────────────────────────────

msg ""
msg "${BLD}Migration target:${RST}"
msg "  Solace : $SOLACE_DIR   (read-only)"
msg "  Apace  : $TARGET  ($([ "$APACE_DOCKER" -eq 1 ] && echo "Docker" || echo "bare-metal"))"
msg "  Backup : $([ "$NO_BACKUP" -eq 1 ] && echo "DISABLED (--no-backup)" || echo "enabled (tar.gz next to the target)")"

# ── Step 4/5: dry run (always) ────────────────────────────────────────────────

head_ "Step 4/5 — dry run (nothing is written)"

dry_args=(--dry-run)
if [ "$NO_BACKUP" -eq 1 ]; then dry_args+=("--no-backup"); fi
if ! "$PY" "$CONVERTER" --solace-dir "$SOLACE_DIR" --target "$TARGET" "${dry_args[@]}"; then
    die "the dry run failed — fix the problem reported above and re-run this script.
  Apace was stopped; start it again with:
  $([ "$APACE_DOCKER" -eq 1 ] && echo "  cd $APACE_DIR && $COMPOSE up -d" || echo "  pwsh $APACE_DIR/run_launcher.ps1")"
fi

if [ "$DRY_RUN_ONLY" -eq 1 ]; then
    msg ""
    info "--dry-run was given — stopping here, nothing was written."
    info "Re-run without --dry-run (and review the plan above) to migrate."
    exit 0
fi

# ── Step 5/5: confirm + migrate ───────────────────────────────────────────────

head_ "Step 5/5 — migrate"

msg "A dry-run backup is NOT taken; the real run backs up $TARGET first"
msg "(unless --no-backup). The Solace directory is only ever read."
if ! confirm "Proceed with the migration? [y/N] "; then
    warn "aborted — nothing was changed."
    warn "Apace is stopped; start it again with:"
    if [ "$APACE_DOCKER" -eq 1 ]; then
        warn "  cd $APACE_DIR && $COMPOSE up -d"
    else
        warn "  pwsh $APACE_DIR/run_launcher.ps1"
    fi
    exit 2
fi

set +e
"$PY" "$CONVERTER" --solace-dir "$SOLACE_DIR" --target "$TARGET" --yes
rc=$?
set -e

if [ "$rc" -ne 0 ]; then
    msg ""
    err "The migration failed (exit $rc) — see the converter output above."
    warn "The Solace directory was NOT modified (it is only ever read)."
    if [ "$NO_BACKUP" -eq 0 ]; then
        warn "A pre-migration backup of the Apace target is in apace-backup-*"
        warn "next to $TARGET — restore it with the tar command in docs/solace-migration.md."
    fi
    warn "To bring Apace back up anyway:"
    if [ "$APACE_DOCKER" -eq 1 ]; then
        warn "  cd $APACE_DIR && $COMPOSE up -d"
    else
        warn "  pwsh $APACE_DIR/run_launcher.ps1"
    fi
    exit "$rc"
fi

# ── Next steps ────────────────────────────────────────────────────────────────

msg ""
msg "${GRN}${BLD}=== Migration finished ===${RST}"
msg ""
msg "Next steps:"
if [ "$APACE_DOCKER" -eq 1 ]; then
    msg "  1. Start Apace:      ${BLD}cd $APACE_DIR && $COMPOSE up -d${RST}"
else
    msg "  1. Start Apace:      ${BLD}pwsh $APACE_DIR/run_launcher.ps1${RST}"
fi
msg "  2. Open the panel (http://localhost:5000) and log in with your old"
msg "     Solace panel credentials."
msg "  3. Check config.json in $TARGET — the API port must match the"
msg "     published Docker port (1808) if Solace used a different one."
if [ "$(id -u)" -ne 0 ] && [ "$APACE_DOCKER" -eq 1 ]; then
    warn "  You ran this as a non-root user — if the panel cannot read the data,"
    warn "  fix ownership:  $SUDO chown -R 1654:1654 '$TARGET'"
fi
msg "  4. Every player must LOG IN AGAIN (sessions/secrets are not migrated)."
msg ""
msg "Once you have verified Apace, you can retire Solace yourself — this"
msg "script never deletes anything, e.g.:  mv ~/solace ~/solace.retired"
if [ "$NO_BACKUP" -eq 0 ]; then
    msg "Rollback if needed: restore the apace-backup-* tar.gz next to $TARGET"
    msg "(see docs/solace-migration.md → Rollback)."
fi
