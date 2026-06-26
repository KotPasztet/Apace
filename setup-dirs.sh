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
    "$PERSISTENT_DIR/logs"

# config.json MUSI być plikiem, nie katalogiem
# Jeśli przypadkiem istnieje jako katalog — usuń go
if [ -d "$PERSISTENT_DIR/config.json" ]; then
    echo "  ⚠ config.json jest katalogiem — usuwam"
    sudo rm -rf "$PERSISTENT_DIR/config.json"
fi

if [ ! -f "$PERSISTENT_DIR/config.json" ]; then
    echo "{}" | sudo tee "$PERSISTENT_DIR/config.json" > /dev/null
    echo "  ✓ config.json (utworzony)"
else
    echo "  ✓ config.json (już istnieje)"
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
