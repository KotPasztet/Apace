# Apace — Minecraft Earth replacement server
# Windows quick-launch script. Run after installation to start Apace.

param()

Write-Host "=== Apace Launcher ===" -ForegroundColor Cyan
Write-Host ""

$APACE_DIR = "$env:USERPROFILE\apace"

if (-not (Test-Path "$APACE_DIR\docker-compose.yml")) {
    Write-Host "Apace is not installed. Run the installer first:" -ForegroundColor Red
    Write-Host "  iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex"
    exit 1
}

# ─── Ensure Docker is running ──────────────────────────────────────
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "Docker not found! Install Docker Desktop:" -ForegroundColor Red
    Write-Host "  https://docs.docker.com/desktop/setup/install/windows-install/"
    exit 1
}

$dockerOk = $false
for ($i = 0; $i -lt 10; $i++) {
    docker info 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $dockerOk = $true
        break
    }
    if ($i -eq 0) {
        Write-Host "Docker is not running. Attempting to start Docker Desktop..." -ForegroundColor Yellow
        $dockerPaths = @(
            "${env:ProgramFiles}\Docker\Docker\Docker Desktop.exe",
            "${env:LocalAppData}\Docker\Docker Desktop.exe"
        )
        foreach ($p in $dockerPaths) {
            if (Test-Path $p) {
                Start-Process -FilePath $p -WindowStyle Hidden
                break
            }
        }
        Start-Service -Name "docker" -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 5
}

if (-not $dockerOk) {
    Write-Host "Docker failed to start!" -ForegroundColor Red
    Write-Host "Start Docker Desktop manually and re-run this script." -ForegroundColor Yellow
    exit 1
}
Write-Host "Docker is running." -ForegroundColor Green

# ─── Detect compose command ─────────────────────────────────────────
$composeCmd = if (docker compose version 2>$null) { "docker compose" } else { "docker-compose" }

# ─── Start ──────────────────────────────────────────────────────────
Set-Location $APACE_DIR
Invoke-Expression "$composeCmd up -d"

try {
    $IP = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object InterfaceAlias -notlike "*Loopback*" | Select-Object -First 1).IPAddress
} catch { $IP = "YOUR_IP" }

Write-Host ""
Write-Host "Apace is running!" -ForegroundColor Green
Write-Host "  Panel: http://localhost:5000 (or http://${IP}:5000)"
Write-Host ""
Write-Host "  To stop: cd $APACE_DIR; $composeCmd down"
