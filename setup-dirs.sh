#!/bin/bash
# Apace - tworzy katalogi persistent na hoście przed pierwszym uruchomieniem
# Uruchom: sudo ./setup-dirs.sh

set -e

PERSISTENT_DIR="/opt/apace-persistent"

echo "=== Apace: tworzenie katalogów persistent ==="

sudo mkdir -p \
    "$PERSISTENT_DIR/launcher-data" \
    "$PERSISTENT_DIR/launcher-logs" \
    "$PERSISTENT_DIR/data" \
    "$PERSISTENT_DIR/dataprotection-keys" \
    "$PERSISTENT_DIR/resourcepacks" \
    "$PERSISTENT_DIR/server-template-dir" \
    "$PERSISTENT_DIR/logs"

# Tworzy config.json jeśli nie istnieje
if [ ! -f "$PERSISTENT_DIR/config.json" ]; then
    echo "{}" | sudo tee "$PERSISTENT_DIR/config.json" > /dev/null
    echo "  ✓ config.json (pusty)"
else
    echo "  ✓ config.json (już istnieje)"
fi

echo "Wszystkie katalogi gotowe: $PERSISTENT_DIR"
ls -la "$PERSISTENT_DIR"
