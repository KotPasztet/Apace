#!/usr/bin/env bash
set -e

# Apace — Minecraft Earth replacement server
# Auto-installer for Linux and macOS
# Usage: curl .../install.sh | bash          (Docker, recommended)
#        curl .../install.sh | bash -s -- --no-docker  (direct download)

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

MODE="${1:-docker}"

echo -e "${BLD}=== Apace Installer ===${RST}"
echo ""

if [ "$MODE" = "--no-docker" ]; then
    # ─── Direct download from GitHub Releases ─────────────────────────
    echo "Downloading latest Apace release..."

    # Detect OS and arch
    OS="linux"
    ARCH=$(uname -m)
    case "$ARCH" in
        x86_64|amd64) ARCH="x64" ;;
        aarch64|arm64) ARCH="arm64" ;;
        *) echo -e "${RED}Unsupported CPU: $ARCH${RST}"; exit 1 ;;
    esac

    APACE_DIR="$HOME/apace"
    mkdir -p "$APACE_DIR"
    cd "$APACE_DIR"

    # Get latest release download URL
    RELEASE_URL=$(curl -sS https://api.github.com/repos/KotPasztet/Apace/releases/latest | grep "browser_download_url.*$OS-$ARCH" | head -1 | cut -d'"' -f4)
    if [ -z "$RELEASE_URL" ]; then
        echo -e "${RED}No release found for $OS-$ARCH. Try Docker mode instead:${RST}"
        echo "  curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash"
        exit 1
    fi

    echo "Downloading $RELEASE_URL..."
    curl -sSLO "$RELEASE_URL"
    ZIP=$(basename "$RELEASE_URL")
    unzip -o "$ZIP"
    rm "$ZIP"

    echo ""
    echo -e "${GRN}${BLD}Apace downloaded!${RST}"
    echo ""
    echo -e "  To run:  ${BLD}cd $APACE_DIR && pwsh ./run_launcher.ps1${RST}"
    echo -e "  Panel:   ${BLD}http://localhost:5000${RST}"
    echo ""
    echo -e "  Requirements: .NET 10 Runtime + Java 17 + PowerShell 7"
    echo -e "  Install .NET:  https://dotnet.microsoft.com/download/dotnet/10.0"
    echo -e "  Install Java:  https://adoptium.net/download/"

else
    # ─── Docker mode ──────────────────────────────────────────────────
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
        echo -e "${YLW}You may need to log out and back in.${RST}"
        echo ""
    fi

    if ! docker info &>/dev/null; then
        echo -e "${RED}Docker is not running!${RST}"
        echo -e "${YLW}sudo systemctl start docker    (Linux)${RST}"
        echo -e "${YLW}Open Docker Desktop           (macOS)${RST}"
        exit 1
    fi

    APACE_DIR="$HOME/apace"
    mkdir -p "$APACE_DIR"
    cd "$APACE_DIR"

    curl -sSLO https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml

    PERSISTENT="/opt/apace-persistent"
    sudo mkdir -p "$PERSISTENT"/{launcher-data,launcher-logs,data,dataprotection-keys,resourcepacks,server-template-dir,logs}
    if [ ! -f "$PERSISTENT/config.json" ]; then
        echo '{}' | sudo tee "$PERSISTENT/config.json" > /dev/null
    fi
    sudo chown -R 1654:1654 "$PERSISTENT" 2>/dev/null || sudo chmod -R 777 "$PERSISTENT" 2>/dev/null

    if docker compose version &>/dev/null 2>&1; then
        COMPOSE="docker compose"
    else
        COMPOSE="docker-compose"
    fi

    echo "Pulling Apace image..."
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
    echo -e "  Next steps:"
    echo -e "  1. Open the panel and create an account"
    echo -e "  2. Server Options → set your IP address (${IP:-find it with 'hostname -I'})"
    echo -e "  3. Server Status → click Start All"
    echo -e "  4. Accept the Minecraft EULA when prompted"
    echo ""
    echo -e "  To stop:    ${BLD}cd $APACE_DIR && $COMPOSE down${RST}"
fi
