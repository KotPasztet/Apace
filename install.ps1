# Apace — Minecraft Earth replacement server
# Windows PowerShell installer
# Usage: iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex                (Docker, recommended)
#        iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex -args '--no-docker'  (direct download)

param([string]$Mode = "docker")

Write-Host "=== Apace Installer ===" -ForegroundColor Cyan
Write-Host ""

if ($Mode -eq "--no-docker") {
    # ─── Direct download from GitHub Releases ─────────────────────────
    Write-Host "Downloading latest Apace release..."

    # Detect CPU architecture (matches the release asset naming: $os-$arch)
    # 9 = x64/AMD64, 12 = ARM64, 6 = IA64, 5 = ARM (32-bit)
    $cpuArch = (Get-CimInstance Win32_Processor).Architecture
    $arch = switch ($cpuArch) {
        9  { "x64" }
        12 { "arm64" }
        default { $null }
    }
    if (-not $arch) {
        Write-Host "Unsupported CPU architecture: $cpuArch" -ForegroundColor Red
        exit 1
    }

    $APACE_DIR = "$env:USERPROFILE\apace"
    New-Item -ItemType Directory -Force -Path $APACE_DIR | Out-Null
    Set-Location $APACE_DIR

    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/KotPasztet/Apace/releases/latest"
    $asset = $release.assets | Where-Object { $_.name -like "*win-$arch*" } | Select-Object -First 1
    if (-not $asset) {
        Write-Host "No Windows release found for win-$arch. Try Docker mode instead." -ForegroundColor Red
        Write-Host "  iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex"
        exit 1
    }

    Write-Host "Downloading $($asset.name) ($([math]::Round($asset.size/1MB, 1)) MB)..."
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $asset.name
    Expand-Archive -Path $asset.name -DestinationPath . -Force
    Remove-Item $asset.name

    Write-Host ""
    Write-Host "Apace downloaded!" -ForegroundColor Green
    Write-Host ""
    Write-Host "  To run:  cd $APACE_DIR; pwsh .\run_launcher.ps1"
    Write-Host "  Panel:   http://localhost:5000"
    Write-Host ""
    Write-Host "  Requirements: .NET 10 Runtime + Java 17 + PowerShell 7"
    Write-Host "  Install .NET:  https://dotnet.microsoft.com/download/dotnet/10.0"
    Write-Host "  Install Java:  https://adoptium.net/download/"

} else {
    # ─── Docker mode ──────────────────────────────────────────────────
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Write-Host "Docker not found. Install Docker Desktop:" -ForegroundColor Yellow
        Write-Host "  https://docs.docker.com/desktop/setup/install/windows-install/"
        exit 1
    }

    # Try up to 30 s for Docker to be responsive
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
            # Also try starting the Docker Engine service
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

    $APACE_DIR = "$env:USERPROFILE\apace"
    $PERSISTENT = "C:\apace-persistent"
    New-Item -ItemType Directory -Force -Path $APACE_DIR | Out-Null
    Set-Location $APACE_DIR

    Write-Host "Downloading docker-compose.yml..."
    Invoke-WebRequest -Uri "https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml" -OutFile "docker-compose.yml"

    # Detect architecture and inject platform
    $hostArch = (Get-CimInstance Win32_Processor).Architecture
    # 9 = x64/AMD64, 12 = ARM64
    $dockerPlatform = switch ($hostArch) {
        9  { "linux/amd64" }
        12 { "linux/arm64" }
        default { $null }
    }
    if ($dockerPlatform) {
        Write-Host "  Detected platform: $dockerPlatform"
        $compose = Get-Content docker-compose.yml -Raw
        $compose = $compose -replace '(?m)^(    image:.*\n)', "`$1    platform: $dockerPlatform`n"
        $compose | Set-Content docker-compose.yml -NoNewline
    }

    Write-Host "Setting up persistent storage..."
    $dirs = @("launcher-data", "launcher-logs", "data", "dataprotection-keys", "resourcepacks", "server-template-dir", "logs", "fabric-data")
    foreach ($d in $dirs) { New-Item -ItemType Directory -Force -Path "$PERSISTENT\$d" | Out-Null }
    # ApiPort=1808 matches the compose port mapping (and the code default)
    if (-not (Test-Path "$PERSISTENT\config.json")) { '{"ApiPort":1808}' | Out-File -FilePath "$PERSISTENT\config.json" -Encoding utf8 }

    $compose = Get-Content docker-compose.yml -Raw
    $compose = $compose -replace '/opt/apace-persistent/', 'C:/apace-persistent/'
    $compose | Set-Content docker-compose.yml -NoNewline

    $composeCmd = if (docker compose version 2>$null) { "docker compose" } else { "docker-compose" }

    Write-Host "Pulling Apace image..."
    Invoke-Expression "$composeCmd pull"
    Write-Host "Starting Apace..."
    Invoke-Expression "$composeCmd up -d"

    $IP = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object InterfaceAlias -notlike "*Loopback*" | Select-Object -First 1).IPAddress
    Write-Host ""
    Write-Host "Apace is running!" -ForegroundColor Green
    Write-Host "  Panel: http://localhost:5000 (or http://${IP}:5000)"
    Write-Host "  API:   http://localhost:1808"
    Write-Host ""
    Write-Host "  Next steps:"
    Write-Host "  1. Open the panel and create an account"
    Write-Host "  2. Server Options → set your IP address ($IP)"
    Write-Host "  3. Server Status → click Start All"
    Write-Host "  4. Accept the Minecraft EULA when prompted"
}
