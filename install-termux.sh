#!/usr/bin/env bash
set -e

# Apace — Minecraft Earth replacement server
# Quick installer for Android (Termux). No Docker, no flags needed.
# Usage: curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install-termux.sh | bash
#
# Android cannot run the prebuilt Linux binaries natively (they are glibc builds,
# Android uses Bionic), so this installer sets up a minimal Ubuntu inside
# proot-distro and runs Apace in there. Still no Docker — everything below runs
# inside the Termux app on the phone itself.

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

if [ -n "$1" ]; then
    echo -e "${YLW}Note: this installer takes no options — Termux is always no-docker.${RST}"
    echo ""
fi

echo -e "${BLD}=== Apace Installer (Termux) ===${RST}"
echo ""

# ─── Preflight: Termux only ──────────────────────────────────────────
if [ -z "${TERMUX_VERSION:-}" ] || ! command -v pkg &>/dev/null; then
    echo -e "${RED}This is not a Termux environment.${RST}"
    echo "On a normal Linux/macOS machine use the regular installer instead:"
    echo "  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash"
    exit 1
fi

ARCH=$(uname -m)
case "$ARCH" in
    aarch64|arm64) DISTRO_ARCH="arm64" ;;
    x86_64)        DISTRO_ARCH="amd64" ;;
    *) echo -e "${RED}Unsupported CPU: $ARCH (need a 64-bit device).${RST}"; exit 1 ;;
esac

DISTRO="ubuntu"
ROOTFS="$PREFIX/var/lib/proot-distro/installed-rootfs/$DISTRO"
APACE_DIR="$HOME/apace"

echo "Device: Android/Termux $DISTRO_ARCH"
echo ""

# ─── Termux packages ─────────────────────────────────────────────────
echo "Installing Termux packages (proot-distro, curl, unzip)..."
pkg install -y proot-distro curl unzip >/dev/null 2>&1 || pkg install -y proot-distro curl unzip
echo -e "${GRN}Termux packages ready.${RST}"

# ─── Ubuntu inside proot-distro ──────────────────────────────────────
# The release binaries are built for glibc Linux; Android's Bionic libc cannot
# load them (and neither the .NET host nor the bundled SQLite/SkiaSharp native
# libraries). A small Ubuntu under proot-distro runs them unmodified.
if [ ! -d "$ROOTFS" ]; then
    echo "Downloading Ubuntu (one-time, ~30 MB)..."
    proot-distro install "$DISTRO"
else
    echo -e "${GRN}Ubuntu already installed.${RST}"
fi

echo "Installing Ubuntu packages (Java 17, libraries; one-time, can take a few minutes)..."
proot-distro login "$DISTRO" -- /bin/bash -e -c '
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq ca-certificates curl unzip libicu-dev libstdc++6 >/dev/null
    apt-get install -y -qq openjdk-17-jre-headless >/dev/null \
        || apt-get install -y -qq openjdk-21-jre-headless >/dev/null
    java -version 2>&1 | head -1
'
echo -e "${GRN}Ubuntu packages ready.${RST}"

# ─── Download Apace ──────────────────────────────────────────────────
echo "Downloading latest Apace release..."
mkdir -p "$APACE_DIR"
cd "$APACE_DIR"

RELEASE_URL=$(curl -sS https://api.github.com/repos/KotPasztet/Apace/releases/latest | grep "browser_download_url.*linux-$DISTRO_ARCH" | head -1 | cut -d'"' -f4)
if [ -z "$RELEASE_URL" ]; then
    echo -e "${RED}No release found for linux-$DISTRO_ARCH.${RST}"
    exit 1
fi

echo "Downloading $RELEASE_URL..."
curl -sSLO "$RELEASE_URL"
ZIP=$(basename "$RELEASE_URL")

# Re-runs must never clobber user data: config.json is restored after the
# extract, and nothing under data/ or launcher/Data is ever deleted.
CONFIG_BAK=""
if [ -f "launcher/config.json" ]; then
    CONFIG_BAK=".apace-config-backup.json"
    cp "launcher/config.json" "$CONFIG_BAK"
fi

unzip -o "$ZIP" -x "launcher/Data/*" "launcher/logs/*" "launcher/persistent_fabric/*" "data/*" "logs/*" >/dev/null
rm -f "$ZIP"

if [ -n "$CONFIG_BAK" ]; then
    mv -f "$CONFIG_BAK" "launcher/config.json"
fi

# ─── Runtime directories + config (mirror the Docker image layout) ──
mkdir -p "launcher/Data" "launcher/logs" "launcher/persistent_fabric" \
         "data" "logs" "staticdata/resourcepacks" "staticdata/server_template_dir/mods"
if [ ! -f "launcher/config.json" ]; then
    # ApiPort=1808 matches the code default and the Docker install
    echo '{"ApiPort":1808}' > "launcher/config.json"
fi
chmod +x "launcher/Launcher" components/* 2>/dev/null || true

# ─── Launcher scripts ────────────────────────────────────────────────
# run.sh        (Termux side) — takes a wake lock and enters Ubuntu
# run-inside.sh (Ubuntu side) — exports the Docker env and starts the launcher
cat > "run-inside.sh" <<'INSIDE_EOF'
#!/bin/bash
set -e
# Runs INSIDE the Ubuntu proot-distro (started by run.sh). Not meant to be run directly.

APACE_DIR="$(cd "$(dirname "$(readlink -f "$0")")" && pwd)"

# Same environment the Docker image uses (512 MB managed-heap cap keeps .NET
# small enough for a phone)
export ASPNETCORE_URLS="http://0.0.0.0:5000"
export DOTNET_SYSTEM_NET_DISABLEIPV6=1
export COMPlus_gcServer=0
export COMPlus_gcConcurrent=1
export DOTNET_GCHeapHardLimit=536870912
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# JavaLocator checks JAVA_HOME/bin/java first, then falls back to "java" on PATH
if command -v java >/dev/null 2>&1; then
    JAVA_BIN="$(readlink -f "$(command -v java)")"
    export JAVA_HOME="$(dirname "$(dirname "$JAVA_BIN")")"
else
    echo "Java was not found — reinstall it with: proot-distro login ubuntu -- apt install -y openjdk-17-jre-headless"
    exit 1
fi

cd "$APACE_DIR/launcher"
exec ./Launcher
INSIDE_EOF

cat > "run.sh" <<'RUN_EOF'
#!/@BASH@
set -e
# Apace launcher (Termux) — generated by install-termux.sh. Start Apace with: ./run.sh

APACE_DIR="$HOME/apace"
DISTRO="ubuntu"

if ! command -v proot-distro >/dev/null 2>&1; then
    echo "proot-distro is missing — reinstall with: pkg install proot-distro"
    exit 1
fi

# Keep Android from freezing the server when the screen turns off
command -v termux-wake-lock >/dev/null 2>&1 && termux-wake-lock 2>/dev/null || true
echo "Starting Apace (Ubuntu inside proot-distro)... Ctrl+C stops it."
echo ""

# The install dir is bind-mounted at the same path inside the distro, so data
# written by the server lands in $APACE_DIR on the Termux side too.
exec proot-distro login "$DISTRO" --bind "$APACE_DIR:$APACE_DIR" -- /bin/bash "$APACE_DIR/run-inside.sh"
RUN_EOF
sed -i "s|@BASH@|$PREFIX/bin/bash|" "run.sh"
chmod +x "run.sh" "run-inside.sh"

# ─── Done ────────────────────────────────────────────────────────────
echo ""
echo -e "${GRN}${BLD}Apace is installed!${RST}"
echo ""
echo -e "  Install dir:  ${BLD}$APACE_DIR${RST} (worlds and accounts live here — back this up)"
echo -e "  To start:     ${BLD}cd $APACE_DIR && ./run.sh${RST}"
echo -e "  To stop:      press ${BLD}Ctrl+C${RST} (then: termux-wake-unlock)"
echo ""
echo -e "  First run — once the launcher is up:"
echo -e "  1. Open the panel:  ${BLD}http://localhost:5000${RST}"
echo -e "  2. Create an account (the first one becomes the admin)"
echo -e "  3. Server Options → set this phone's Wi-Fi IP (find it with: ifconfig wlan0)"
echo -e "  4. Server Status → click Start All, accept the Minecraft EULA when prompted"
echo -e "  5. Patch your phone's Minecraft Earth APK on a PC (Patcher page) and sign in"
echo ""
echo -e "  ${YLW}Tips for Android:${RST}"
echo -e "  - Install Termux from ${BLD}F-Droid${RST} (the Play Store build is outdated)"
echo -e "  - Disable battery optimization for Termux, or Android will kill the server"
echo -e "  - The phone and the player's phone must be on the same Wi-Fi network"
echo ""
