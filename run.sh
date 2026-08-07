#!/usr/bin/env bash
set -e

# Apace — Minecraft Earth replacement server
# Quick-launch script. Run this after installation to start Apace.

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

echo -e "${BLD}=== Apace Launcher ===${RST}"
echo ""

APACE_DIR="$HOME/apace"
PERSISTENT="/opt/apace-persistent"

# ─── Ensure persistent directories exist ───────────────────────────
sudo mkdir -p "$PERSISTENT"/{launcher-data,launcher-logs,data,dataprotection-keys,resourcepacks,server-template-dir,logs} 2>/dev/null || true
if [ ! -f "$PERSISTENT/config.json" ]; then
    echo '{}' | sudo tee "$PERSISTENT/config.json" > /dev/null 2>/dev/null || true
fi
sudo chown -R 1654:1654 "$PERSISTENT" 2>/dev/null || sudo chmod -R 777 "$PERSISTENT" 2>/dev/null || true

# ─── Detect Docker access ───────────────────────────────────────────
DOCKER="docker"
if ! docker info &>/dev/null 2>&1; then
    if sudo docker info &>/dev/null 2>&1; then
        DOCKER="sudo docker"
    else
        echo -e "${YLW}Docker daemon is not running. Starting...${RST}"
        if command -v systemctl &>/dev/null; then
            sudo systemctl start docker 2>/dev/null || true
            sleep 3
        fi
        if ! sudo docker info &>/dev/null 2>&1; then
            echo -e "${RED}Cannot connect to Docker!${RST}"
            echo -e "${YLW}Run: sudo systemctl start docker${RST}"
            exit 1
        fi
        DOCKER="sudo docker"
    fi
fi

# ─── Detect compose command ─────────────────────────────────────────
if $DOCKER compose version &>/dev/null 2>&1; then
    COMPOSE="$DOCKER compose"
else
    COMPOSE="$DOCKER-compose"
fi

# ─── Detect architecture ─────────────────────────────────────────────
HOST_ARCH=$(uname -m)
case "$HOST_ARCH" in
    x86_64|amd64)   DOCKER_PLATFORM="linux/amd64" ;;
    aarch64|arm64)  DOCKER_PLATFORM="linux/arm64" ;;
    *)              DOCKER_PLATFORM="" ;;
esac

# ─── Download compose file if missing ───────────────────────────────
mkdir -p "$APACE_DIR"
cd "$APACE_DIR"
if [ ! -f docker-compose.yml ]; then
    echo "Downloading docker-compose.yml..."
    curl -sSLO https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml
    if [ -n "$DOCKER_PLATFORM" ]; then
        echo -e "  Detected platform: ${BLD}$DOCKER_PLATFORM${RST}"
        sed -i "/^    image:/a\    platform: $DOCKER_PLATFORM" docker-compose.yml
    fi
fi

# ─── Pull and start ─────────────────────────────────────────────────
echo "Pulling latest Apace image..."
$COMPOSE pull
echo "Starting Apace..."
$COMPOSE up -d

IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo ""
echo -e "${GRN}${BLD}Apace is running!${RST}"
echo ""
echo -e "  Panel:  ${BLD}http://localhost:5000${RST}  (or http://${IP:-YOUR_IP}:5000)"
echo -e "  API:    ${BLD}http://localhost:1808${RST}"
echo ""
echo -e "  To stop:    ${BLD}cd $APACE_DIR && $COMPOSE down${RST}"
