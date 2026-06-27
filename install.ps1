# Apace — Minecraft Earth replacement server
# Windows PowerShell installer
# Downloads the pre-built Docker image from GitHub Container Registry

Write-Host "=== Apace Installer ===" -ForegroundColor Cyan
Write-Host ""

# ─── Check Docker ─────────────────────────────────────────────────────
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "Docker not found. Download and install Docker Desktop:" -ForegroundColor Yellow
    Write-Host "  https://docs.docker.com/desktop/setup/install/windows-install/" -ForegroundColor White
    Write-Host "After installing, restart this script." -ForegroundColor Yellow
    exit 1
}

# ─── Create directories ───────────────────────────────────────────────
$APACE_DIR = "$env:USERPROFILE\apace"
$PERSISTENT = "C:\apace-persistent"

New-Item -ItemType Directory -Force -Path $APACE_DIR | Out-Null
Set-Location $APACE_DIR

# ─── Download compose file ────────────────────────────────────────────
Write-Host "Downloading docker-compose.yml..."
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml" -OutFile "docker-compose.yml"

# ─── Create persistent directories ────────────────────────────────────
Write-Host "Setting up persistent storage..."
$dirs = @("launcher-data", "launcher-logs", "data", "dataprotection-keys", "resourcepacks", "server-template-dir", "logs")
foreach ($d in $dirs) {
    New-Item -ItemType Directory -Force -Path "$PERSISTENT\$d" | Out-Null
}
if (-not (Test-Path "$PERSISTENT\config.json")) {
    '{}' | Out-File -FilePath "$PERSISTENT\config.json" -Encoding utf8
}

# ─── Update compose for Windows paths ─────────────────────────────────
Write-Host "Updating paths for Windows..."
$compose = Get-Content docker-compose.yml -Raw
$compose = $compose -replace '/opt/apace-persistent/', 'C:/apace-persistent/'
$compose | Set-Content docker-compose.yml -NoNewline

# ─── Detect Docker Compose command ───────────────────────────────────
$composeCmd = if (docker compose version 2>$null) { "docker compose" } else { "docker-compose" }

# ─── Pull and start ───────────────────────────────────────────────────
Write-Host "Pulling Apace image..."
Invoke-Expression "$composeCmd pull"
Write-Host "Starting Apace..."
Invoke-Expression "$composeCmd up -d"

# ─── Done ─────────────────────────────────────────────────────────────
$IP = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object InterfaceAlias -notlike "*Loopback*" | Select-Object -First 1).IPAddress

Write-Host ""
Write-Host "Apace is running!" -ForegroundColor Green
Write-Host ""
Write-Host "  Panel:  http://localhost:5000  (or http://${IP}:5000)" -ForegroundColor White
Write-Host "  API:    http://localhost:1808" -ForegroundColor White
Write-Host ""
Write-Host "  Next steps:"
Write-Host "  1. Open the panel and create an account"
Write-Host "  2. Server Options -> set your IP address ($IP)"
Write-Host "  3. Server Status -> click Start All"
Write-Host "  4. Accept the Minecraft EULA when prompted"
Write-Host ""
Write-Host "  To update:  cd $APACE_DIR; $composeCmd pull; $composeCmd up -d"
Write-Host "  To stop:    cd $APACE_DIR; $composeCmd down"
