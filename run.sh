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

if [ ! -d "$APACE_DIR" ] || [ ! -f "$APACE_DIR/docker-compose.yml" ]; then
    echo -e "${RED}Apace is not installed. Run the installer first:${RST}"
    echo "  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash"
    exit 1
fi

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

cd "$APACE_DIR"
$COMPOSE up -d

IP=$(hostname -I 2>/dev/null | awk '{print $1}')
echo ""
echo -e "${GRN}${BLD}Apace is running!${RST}"
echo ""
echo -e "  Panel:  ${BLD}http://localhost:5000${RST}  (or http://${IP:-YOUR_IP}:5000)"
echo -e "  API:    ${BLD}http://localhost:1808${RST}"
echo ""
echo -e "  To stop:    ${BLD}cd $APACE_DIR && $COMPOSE down${RST}"
