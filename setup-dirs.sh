#!/bin/bash
# Apace - tworzy katalogi persistent na hoście przed pierwszym uruchomieniem
# Uruchom: sudo ./setup-dirs.sh

set -e

PERSISTENT_DIR="/opt/apace-persistent"
# UID 1654 = użytkownik 'app' w obrazie mcr.microsoft.com/dotnet/aspnet:10.0
CONTAINER_UID=1654

echo "=== Apace: tworzenie katalogów persistent ==="

sudo mkdir -p \
    "$PERSISTENT_DIR/launcher-data" \
    "$PERSISTENT_DIR/launcher-logs" \
    "$PERSISTENT_DIR/data" \
    "$PERSISTENT_DIR/dataprotection-keys" \
    "$PERSISTENT_DIR/resourcepacks" \
    "$PERSISTENT_DIR/server-template-dir" \
    "$PERSISTENT_DIR/logs" \
    "$PERSISTENT_DIR/fabric-data" \
    "$PERSISTENT_DIR/api-config"

# config.json MUSI być plikiem, nie katalogiem
# Jeśli przypadkiem istnieje jako katalog — usuń go
if [ -d "$PERSISTENT_DIR/config.json" ]; then
    echo "  ⚠ config.json jest katalogiem — usuwam"
    sudo rm -rf "$PERSISTENT_DIR/config.json"
fi

if [ ! -f "$PERSISTENT_DIR/config.json" ]; then
    # ApiPort=1808 matches the compose port mapping (and the code default)
    echo '{"ApiPort":1808}' | sudo tee "$PERSISTENT_DIR/config.json" > /dev/null
    echo "  ✓ config.json (utworzony, ApiPort=1808)"
else
    echo "  ✓ config.json (już istnieje)"
fi

# api_config.json (sekrety logowania ApiServer) — montowany jako pojedynczy PLIK.
# Jeśli plik nie istnieje na hoście, Docker utworzy w tym miejscu KATALOG i ApiServer
# nie zapisze konfiguracji — tworzymy pusty plik (ApiServer wypełni go domyślnymi).
if [ -d "$PERSISTENT_DIR/api-config/api_config.json" ]; then
    echo "  ⚠ api-config/api_config.json jest katalogiem — usuwam"
    sudo rm -rf "$PERSISTENT_DIR/api-config/api_config.json"
fi

if [ ! -f "$PERSISTENT_DIR/api-config/api_config.json" ]; then
    sudo touch "$PERSISTENT_DIR/api-config/api_config.json"
    echo "  ✓ api-config/api_config.json (utworzony pusty — ApiServer wypełni wartościami domyślnymi)"
else
    echo "  ✓ api-config/api_config.json (już istnieje)"
fi

# Ustaw właściciela na użytkownika kontenera (UID 1654)
sudo chown -R ${CONTAINER_UID}:${CONTAINER_UID} "$PERSISTENT_DIR" 2>/dev/null || {
    echo "  ⚠ Nie udało się ustawić właściciela (UID ${CONTAINER_UID} nie istnieje na hoście)"
    echo "  → ustawiam chmod 777 jako fallback"
    sudo chmod -R 777 "$PERSISTENT_DIR"
}

echo ""
echo "Wszystkie katalogi gotowe: $PERSISTENT_DIR"
ls -la "$PERSISTENT_DIR"
