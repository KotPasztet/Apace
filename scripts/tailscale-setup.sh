#!/usr/bin/env bash
set -euo pipefail

# Apace — Tailscale setup helper (run on the SERVER host, not inside Docker)
# Installs Tailscale from the official repository, brings the tailnet up,
# then prints the addresses to put into Server Options / the patched client.
# Safe to re-run: an existing Tailscale install is detected and reused.
#
# Usage: sudo ./scripts/tailscale-setup.sh

RED='\033[1;31m'
GRN='\033[1;32m'
YLW='\033[1;33m'
BLD='\033[1m'
RST='\033[0m'

echo -e "${BLD}=== Apace Tailscale Setup ===${RST}"
echo ""

if [ "$(id -u)" -ne 0 ]; then
    echo -e "${RED}Please run as root (sudo ./scripts/tailscale-setup.sh)${RST}"
    exit 1
fi

if [ "$(uname -s)" != "Linux" ]; then
    echo -e "${RED}This script targets Linux. On Windows/macOS install Tailscale from https://tailscale.com/download${RST}"
    exit 1
fi

# ─── 1. Install Tailscale (official repo) ──────────────────────────────────────
if command -v tailscale >/dev/null 2>&1; then
    echo -e "${GRN}Tailscale already installed:$(command -v tailscale)${RST}"
else
    echo "Installing Tailscale from the official repository..."
    # curl is only needed for the repo bootstrap; every installer pulls the rest.
    if ! command -v curl >/dev/null 2>&1; then
        echo -e "${RED}curl is required to bootstrap the Tailscale repository and is not installed.${RST}"
        exit 1
    fi

    # The official installer detects the distro, wires up the repo and package
    # manager, and starts tailscaled. Anything it can't handle prints its own error.
    curl -fsSL https://tailscale.com/install.sh | sh

    if ! command -v tailscale >/dev/null 2>&1; then
        echo -e "${RED}Tailscale installation failed. See https://tailscale.com/kb/1031/install-linux${RST}"
        exit 1
    fi
fi

# ─── 2. Bring the tailnet up (interactive the first time) ──────────────────────
if tailscale status >/dev/null 2>&1; then
    echo -e "${GRN}Tailscale is connected.${RST}"
else
    echo ""
    echo -e "${YLW}tailscale up will print a login link — open it in a browser and approve this machine.${RST}"
    tailscale up
fi

# ─── 3. Read the ports Apace is configured to use ──────────────────────────────
# API Port: the port patched clients call (fresh installs write 1808 into config.json).
API_PORT="${APACE_API_PORT:-}"
if [ -z "$API_PORT" ]; then
    for cfg in /opt/apace-persistent/config.json "$HOME/apace/config.json" ./config.json; do
        if [ -r "$cfg" ]; then
            API_PORT=$(grep -o '"ApiPort"[[:space:]]*:[[:space:]]*[0-9]*' "$cfg" | grep -o '[0-9]*' | head -1 || true)
            [ -n "$API_PORT" ] && break
        fi
    done
fi
API_PORT="${API_PORT:-1808}"

# Bedrock bridge port: BRIDGE_PORT in docker-compose.yml, else the default 19132.
BRIDGE_PORT="${BRIDGE_PORT:-}"
if [ -z "$BRIDGE_PORT" ]; then
    for compose in "$HOME/apace/docker-compose.yml" ./docker-compose.yml; do
        if [ -r "$compose" ]; then
            BRIDGE_PORT=$(grep -o 'BRIDGE_PORT:-[0-9]*' "$compose" | head -1 | cut -d- -f3 || true)
            [ -n "$BRIDGE_PORT" ] && break
        fi
    done
fi
BRIDGE_PORT="${BRIDGE_PORT:-19132}"

# ─── 4. Print the addresses to use ─────────────────────────────────────────────
TS_IP="$(tailscale ip -4 | head -1)"

# MagicDNS name of THIS machine (Self.DNSName, only present when MagicDNS is on).
TS_JSON="$(tailscale status --json 2>/dev/null || true)"
TS_NAME=""
if [ -n "$TS_JSON" ] && command -v python3 >/dev/null 2>&1; then
    TS_NAME="$(printf '%s\n' "$TS_JSON" | python3 -c '
import json, sys
try:
    print(json.load(sys.stdin).get("Self", {}).get("DNSName", "").rstrip("."))
except Exception:
    pass' 2>/dev/null || true)"
fi
# No python3: fall back to the first DNSName in the JSON (this machine is listed first).
if [ -z "$TS_NAME" ]; then
    TS_NAME="$(printf '%s\n' "$TS_JSON" | grep -o '"DNSName"[[:space:]]*:[[:space:]]*"[^"]*"' | head -1 | cut -d'"' -f4 | sed 's/\.$//' || true)"
fi

TS_HOST="${TS_NAME:-$TS_IP}"

echo ""
echo -e "${BLD}Tailscale is up.${RST}"
echo ""
echo "  IPv4:        $TS_IP"
echo "  MagicDNS:    ${TS_NAME:-<not available — MagicDNS disabled, use the IPv4>}"
echo ""
echo -e "${BLD}Next steps:${RST}"
echo ""
echo "  1. Install the Tailscale app on the phone and join the same tailnet."
echo ""
echo "  2. Panel → Server Options → PC Ipv4 Address or Hostname (without port):"
echo -e "         ${GRN}${TS_HOST}${RST}"
echo ""
echo "  3. Re-patch the phone client (Patcher page, Auto mode). It will use:"
echo -e "         ${GRN}http://${TS_HOST}:${API_PORT}${RST}"
echo ""
echo "     and reach Bedrock on:"
echo -e "         ${GRN}${TS_HOST}:${BRIDGE_PORT}/udp${RST}"
echo ""
echo -e "${YLW}Only devices in your tailnet can reach these ports — no router port forwarding needed.${RST}"
echo ""

# ─── 5. Status ─────────────────────────────────────────────────────────────────
echo -e "${BLD}tailscale status:${RST}"
tailscale status
