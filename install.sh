#!/usr/bin/env bash
set -e

# Apace — Minecraft Earth replacement server
# Auto-installer for Linux and macOS
# Downloads the pre-built Docker image from GitHub Container Registry

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

echo -e "${BLD}=== Apace Auto-Installer ===${RST}"
echo ""

# ─── Check / install Docker ──────────────────────────────────────────
if ! command -v docker &>/dev/null; then
    echo -e "${YLW}Docker not found. Installing...${RST}"
    if command -v apt &>/dev/null; then
        sudo apt update && sudo apt install -y docker.io docker-compose-v2
    elif command -v dnf &>/dev/null; then
        sudo dnf install -y docker docker-compose
    elif command -v pacman &>/dev/null; then
        sudo pacman -S --noconfirm docker docker-compose
    elif command -v brew &>/dev/null; then
        brew install docker docker-compose
    else
        curl -fsSL https://get.docker.com | sh
    fi
    sudo systemctl enable --now docker 2>/dev/null || true
    sudo usermod -aG docker "$USER" 2>/dev/null || true
    echo -e "${GRN}Docker installed.${RST}"
    echo -e "${YLW}You may need to log out and back in for docker group to take effect.${RST}"
    echo ""
fi

# Check if Docker daemon is running
if ! docker info &>/dev/null; then
    echo -e "${RED}Docker is not running!${RST}"
    echo -e "${YLW}Start the Docker daemon and try again:${RST}"
    echo -e "  sudo systemctl start docker    (Linux)"
    echo -e "  Open Docker Desktop           (macOS / Windows)"
    exit 1
fi

# ─── Create Apace directory ───────────────────────────────────────────
APACE_DIR="$HOME/apace"
mkdir -p "$APACE_DIR"
cd "$APACE_DIR"

# ─── Download compose file ────────────────────────────────────────────
echo "Downloading docker-compose.yml..."
curl -sSLO https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml

# ─── Create persistent directories ────────────────────────────────────
echo "Setting up persistent storage..."
PERSISTENT="/opt/apace-persistent"
sudo mkdir -p "$PERSISTENT"/{launcher-data,launcher-logs,data,dataprotection-keys,resourcepacks,server-template-dir,logs}
if [ ! -f "$PERSISTENT/config.json" ]; then
    echo '{}' | sudo tee "$PERSISTENT/config.json" > /dev/null
fi
sudo chown -R 1654:1654 "$PERSISTENT" 2>/dev/null || sudo chmod -R 777 "$PERSISTENT" 2>/dev/null

# ─── Detect Docker Compose command ───────────────────────────────────
if docker compose version &>/dev/null 2>&1; then
    COMPOSE="docker compose"
else
    COMPOSE="docker-compose"
fi

# ─── Pull and start ───────────────────────────────────────────────────
echo "Pulling Apace image..."
$COMPOSE pull
echo "Starting Apace..."
$COMPOSE up -d

# ─── Done ─────────────────────────────────────────────────────────────
IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo ""
echo -e "${GRN}${BLD}Apace is running!${RST}"
echo ""
echo -e "  Panel:  ${BLD}http://localhost:5000${RST}  (or http://${IP:-YOUR_IP}:5000)"
echo -e "  API:    ${BLD}http://localhost:1808${RST}"
echo ""
echo -e "  Next steps:"
echo -e "  1. Open the panel and create an account"
echo -e "  2. Go to Server Options → set your IP address (${IP:-find it with 'hostname -I'})"
echo -e "  3. Go to Server Status → click Start All"
echo -e "  4. Accept the Minecraft EULA when prompted"
echo ""
echo -e "  To update:  ${BLD}cd $APACE_DIR && $COMPOSE pull && $COMPOSE up -d${RST}"
echo -e "  To stop:    ${BLD}cd $APACE_DIR && $COMPOSE down${RST}"
