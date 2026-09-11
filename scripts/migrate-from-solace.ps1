# Apace — Minecraft Earth replacement server
# One-command migration from Solace (Windows).
#
# Detects an existing Solace install, installs Apace if it is missing, stops
# both servers, runs the converter's dry run, asks once, then migrates.
#
# Usage:
#   iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.ps1 | iex
#
#   With options, download it first (recommended):
#     iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/migrate-from-solace.ps1 -OutFile migrate-from-solace.ps1
#     .\migrate-from-solace.ps1 -DryRun
#     .\migrate-from-solace.ps1 -SolaceDir "$env:USERPROFILE\solace\solace-server" -Target C:\apace-persistent
#
# The DB conversion itself lives in scripts/migrate-from-solace.py — this
# script only orchestrates (detect / install / stop / dry-run / confirm / run)
# and never touches the data itself. Nothing in the Solace directory is ever
# modified or deleted; the converter backs up the Apace target first.

param(
    [string]$SolaceDir = "",
    [string]$Target = "",
    [switch]$Docker,
    [switch]$NoDocker,
    [switch]$DryRun,
    [switch]$NoBackup,
    [switch]$Yes,
    [switch]$Help
)

$RawBase = "https://raw.githubusercontent.com/KotPasztet/Apace/main"
$ConverterUrl = "$RawBase/scripts/migrate-from-solace.py"
$ApaceDir = "$env:USERPROFILE\apace"

function Write-Usage {
    Write-Host "Apace <- Solace migration (Windows)"
    Write-Host ""
    Write-Host "Usage: migrate-from-solace.ps1 [options]"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -SolaceDir <path>   Solace server directory (contains data\earth.db)."
    Write-Host "                      Default: auto-detect (`$env:SOLACE_DIR,"
    Write-Host "                      %USERPROFILE%\solace\solace-server, %USERPROFILE%\solace,"
    Write-Host "                      %USERPROFILE%\Solace, %USERPROFILE%\Solace\solace-server)."
    Write-Host "  -Target <dir>       Apace persistent data directory."
    Write-Host "                      Default: C:\apace-persistent (Docker) or $ApaceDir (bare)."
    Write-Host "  -Docker             Migrate for a Docker install."
    Write-Host "  -NoDocker           Migrate for a bare-metal install (no Docker)."
    Write-Host "  -DryRun             Stop after showing the plan; write nothing."
    Write-Host "  -NoBackup           Skip the pre-migration backup of the Apace target."
    Write-Host "  -Yes                Do not ask for confirmation."
    Write-Host "  -Help               Show this help."
    Write-Host ""
    Write-Host "A dry run is ALWAYS shown first; the real migration only runs after"
    Write-Host "you confirm. The Solace directory is only ever read, never deleted."
}

function Confirm-Step([string]$Prompt) {
    if ($Yes) { return $true }
    $reply = Read-Host "$Prompt [y/N]"
    return ($reply -match '^(y|yes)$')
}

function Get-ComposeCommand {
    # Returns the display string for hints, and sets $script:ComposeIsStandalone.
    $script:ComposeIsStandalone = $false
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        docker compose version 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { return "docker compose" }
        if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
            $script:ComposeIsStandalone = $true
            return "docker-compose"
        }
    }
    $script:ComposeIsStandalone = $false
    return $null
}

function Invoke-Compose {
    # Invoke-Compose compose ps -q   ->  "docker compose ps -q" / "docker-compose ps -q"
    if ($script:ComposeIsStandalone) { & docker-compose @args }
    else                             { & docker @args }
}

if ($Help) { Write-Usage; exit 0 }

Write-Host "=== Apace <- Solace migration ===" -ForegroundColor Cyan
Write-Host ""
Write-Host "This will: detect Solace, install Apace if needed, stop both servers,"
Write-Host "show a dry-run plan, and - after your confirmation - migrate accounts,"
Write-Host "progress, buildplates and world data into Apace."
Write-Host ""

# ─── Step 1/5: converter + python ──────────────────────────────────────────
Write-Host "Step 1/5 - converter and python" -ForegroundColor Cyan
Write-Host ""

$Converter = $null
if ($PSScriptRoot -and (Test-Path (Join-Path $PSScriptRoot "migrate-from-solace.py") -PathType Leaf)) {
    $Converter = Join-Path $PSScriptRoot "migrate-from-solace.py"
    Write-Host "Using the converter from this checkout: $Converter" -ForegroundColor Green
}

$TempDir = $null
if (-not $Converter) {
    # Script was piped in (iwr | iex) - fetch the converter next to it.
    $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("apace-migrate-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    $Converter = Join-Path $TempDir "migrate-from-solace.py"
    Write-Host "Fetching the converter from $ConverterUrl ..." -ForegroundColor Green
    try {
        Invoke-WebRequest -Uri $ConverterUrl -OutFile $Converter -UseBasicParsing -ErrorAction Stop
    } catch {
        Write-Host "Could not download the converter: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Check your connection, or clone the repo and run scripts\migrate-from-solace.ps1 from it." -ForegroundColor Yellow
        exit 1
    }
    if (-not (Test-Path $Converter) -or (Get-Item $Converter).Length -eq 0) {
        Write-Host "Downloaded converter is empty." -ForegroundColor Red
        exit 1
    }
}

$PyCmd = $null
$PyArgs = @()
foreach ($candidate in @("py -3", "python3", "python")) {
    $parts = @($candidate -split ' ')
    $exe = $parts[0]
    $extra = @()
    if ($parts.Count -gt 1) { $extra = @($parts[1..($parts.Count - 1)]) }
    if (-not (Get-Command $exe -ErrorAction SilentlyContinue)) { continue }
    & $exe @extra -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)" 2>$null
    if ($LASTEXITCODE -eq 0) { $PyCmd = $exe; $PyArgs = $extra; break }
}
if (-not $PyCmd) {
    Write-Host "Python 3 is required (the converter needs Python 3.8+) and was not found." -ForegroundColor Red
    Write-Host "Install it from https://www.python.org/downloads/ (tick 'Add python.exe to PATH')," -ForegroundColor Yellow
    Write-Host "or run this migration inside WSL instead:" -ForegroundColor Yellow
    Write-Host "  wsl bash -c 'curl -sSL $RawBase/scripts/migrate-from-solace.sh | bash'" -ForegroundColor Yellow
    exit 1
}
$PyVersion = (& $PyCmd @PyArgs -V 2>$null)
if (-not $PyVersion) { $PyVersion = (& $PyCmd @PyArgs --version 2>$null) }
Write-Host "Python: $PyVersion" -ForegroundColor Green
Write-Host "Converter: $Converter" -ForegroundColor Green

# ─── Step 2/5: locate Solace ───────────────────────────────────────────────
Write-Host ""
Write-Host "Step 2/5 - locate the Solace install" -ForegroundColor Cyan
Write-Host ""

if ($SolaceDir) {
    if (-not (Test-Path $SolaceDir -PathType Container)) {
        Write-Host "Solace directory not found: $SolaceDir" -ForegroundColor Red
        exit 1
    }
} else {
    $tried = @()
    $candidates = @()
    if ($env:SOLACE_DIR) { $candidates += $env:SOLACE_DIR }
    $candidates += @(
        "$env:USERPROFILE\solace\solace-server",
        "$env:USERPROFILE\solace",
        "$env:USERPROFILE\Solace",
        "$env:USERPROFILE\Solace\solace-server"
    )
    foreach ($c in $candidates) {
        $tried += "  $c"
        if (Test-Path (Join-Path $c "data\earth.db") -PathType Leaf) {
            $SolaceDir = $c
            break
        }
    }
    if (-not $SolaceDir) {
        Write-Host "Could not find a Solace installation. Paths checked:" -ForegroundColor Red
        $tried | ForEach-Object { Write-Host $_ }
        Write-Host "  Set the location explicitly:  .\migrate-from-solace.ps1 -SolaceDir C:\path\to\solace-server" -ForegroundColor Yellow
        exit 1
    }
}
$SolaceDir = (Get-Item $SolaceDir).FullName
Write-Host "Solace: $SolaceDir" -ForegroundColor Green
if (-not (Test-Path (Join-Path $SolaceDir "staticdata") -PathType Container)) {
    Write-Host "No staticdata\ in the Solace directory - resourcepacks will not migrate." -ForegroundColor Yellow
}

# ─── Step 3/5: Apace install + mode, stop everything ───────────────────────
Write-Host ""
Write-Host "Step 3/5 - Apace install and stopping servers" -ForegroundColor Cyan
Write-Host ""

$Compose = Get-ComposeCommand

$apaceDockerInstall = $false
foreach ($f in @("docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml")) {
    if (Test-Path (Join-Path $ApaceDir $f) -PathType Leaf) { $apaceDockerInstall = $true; break }
}
$apaceComposeProject = $false
if ($Compose) {
    $projects = @(Invoke-Compose compose ls 2>$null)
    if ($LASTEXITCODE -eq 0 -and @($projects | Where-Object { "$_" -match 'apace' }).Count -gt 0) {
        $apaceComposeProject = $true
    }
}
$apaceBareInstall = (Test-Path $ApaceDir -PathType Container) -and (
    (Test-Path (Join-Path $ApaceDir "run_launcher.ps1") -PathType Leaf) -or
    (Test-Path (Join-Path $ApaceDir "launcher") -PathType Container) -or
    (Test-Path (Join-Path $ApaceDir "data") -PathType Container)
)

$mode = ""
if ($Docker)       { $mode = "docker" }
elseif ($NoDocker) { $mode = "bare" }
elseif ($apaceDockerInstall -or $apaceComposeProject) {
    $mode = "docker"
    if ($apaceDockerInstall) { Write-Host "Found $ApaceDir\docker-compose.yml - Docker mode" -ForegroundColor Green }
    else                     { Write-Host "docker compose lists an Apace project - Docker mode" -ForegroundColor Green }
}
elseif ($apaceBareInstall) {
    $mode = "bare"
    Write-Host "Found a bare-metal Apace install in $ApaceDir - bare-metal mode" -ForegroundColor Green
}
else {
    $mode = "docker"
    Write-Host "No Apace install detected - will install Apace (Docker, recommended)" -ForegroundColor Green
}

$installNeeded = $false
if ($mode -eq "docker") {
    if ($apaceDockerInstall -or $apaceComposeProject) {
        if (-not $Compose) {
            Write-Host "An Apace Docker install exists in $ApaceDir, but Docker is not usable." -ForegroundColor Red
            Write-Host "Start Docker Desktop and re-run this script." -ForegroundColor Yellow
            exit 1
        }
    } else {
        $installNeeded = $true
    }
    if (-not $Target) { $Target = "C:\apace-persistent" }
} else {
    if (-not $apaceBareInstall) { $installNeeded = $true }
    if (-not $Target) { $Target = $ApaceDir }
}

if ($installNeeded) {
    if (-not $TempDir) {
        $TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("apace-migrate-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $TempDir | Out-Null
    }
    $installer = Join-Path $TempDir "apace-install.ps1"
    if ($mode -eq "docker") {
        Write-Host ""
        Write-Host "Apace (Docker) is not installed yet. The standard installer will:"
        Write-Host "  - require Docker Desktop (it tries to start it for you)"
        Write-Host "  - create $ApaceDir with docker-compose.yml"
        Write-Host "  - create $Target and pull + START the Apace image"
        Write-Host "    (this script stops the stack again before migrating)"
        if (-not (Confirm-Step "Install Apace now?")) {
            Write-Host "aborted - install Apace first:  iwr $RawBase/install.ps1 | iex" -ForegroundColor Yellow
            exit 1
        }
        Write-Host "Running the Apace Docker installer ..." -ForegroundColor Green
        try {
            Invoke-WebRequest -Uri "$RawBase/install.ps1" -OutFile $installer -UseBasicParsing -ErrorAction Stop
        } catch {
            Write-Host "Could not download install.ps1: $($_.Exception.Message)" -ForegroundColor Red
            exit 1
        }
        & $installer
        if ($LASTEXITCODE -ne 0) {
            Write-Host "The Apace installer failed - fix the problem above and re-run this script." -ForegroundColor Red
            exit 1
        }
        Write-Host "Stopping the freshly started Apace stack (migration needs it down) ..." -ForegroundColor Green
        Push-Location $ApaceDir
        Invoke-Compose compose stop
        $stopRc = $LASTEXITCODE
        Pop-Location
        if ($stopRc -ne 0) {
            Write-Host "Could not stop the Apace stack - run: cd $ApaceDir; $Compose stop  (then re-run this script)." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host ""
        Write-Host "Apace is not installed yet. The bare-metal installer will download the"
        Write-Host "latest Apace release into $ApaceDir (it does NOT start anything)."
        Write-Host "Still required afterwards: .NET 10 Runtime + Java 17 + PowerShell 7."
        if (-not (Confirm-Step "Install Apace now?")) {
            Write-Host "aborted - install Apace first:  iwr $RawBase/install.ps1 | iex -args '--no-docker'" -ForegroundColor Yellow
            exit 1
        }
        Write-Host "Running the Apace bare-metal installer ..." -ForegroundColor Green
        try {
            Invoke-WebRequest -Uri "$RawBase/install.ps1" -OutFile $installer -UseBasicParsing -ErrorAction Stop
        } catch {
            Write-Host "Could not download install.ps1: $($_.Exception.Message)" -ForegroundColor Red
            exit 1
        }
        & $installer "--no-docker"
        if ($LASTEXITCODE -ne 0) {
            Write-Host "The Apace installer failed - fix the problem above and re-run this script." -ForegroundColor Red
            exit 1
        }
    }
}

# Target writability - fail BEFORE anything is stopped or written.
$Target = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Target)
$probeParent = $Target
while ((-not (Test-Path $probeParent)) -and $probeParent -and ($probeParent -ne [System.IO.Path]::GetPathRoot($probeParent))) {
    $probeParent = Split-Path $probeParent -Parent
}
if ((-not $probeParent) -or (-not (Test-Path $probeParent))) {
    Write-Host "Cannot resolve a parent directory for $Target - pass -Target with an absolute path." -ForegroundColor Red
    exit 1
}
$probe = Join-Path $probeParent ".apace-migrate-probe"
try {
    Set-Content -Path $probe -Value "probe" -ErrorAction Stop
    Remove-Item $probe -Force -ErrorAction SilentlyContinue
} catch {
    Write-Host "Cannot write to $Target - run this window as Administrator, or pass -Target with a directory you can write to." -ForegroundColor Red
    exit 1
}

# ─── Stop Solace ───────────────────────────────────────────────────────────
$stopList = @("Solace.LauncherUI", "Solace.ApiServer", "Solace.Buildplate")
$solaceProcesses = @(Get-Process -Name $stopList -ErrorAction SilentlyContinue)
$solaceJava = @()
try {
    $solaceJava = @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and ("$($_.CommandLine)" -match 'solace') })
} catch { $solaceJava = @() }

if ($solaceProcesses.Count -gt 0 -or $solaceJava.Count -gt 0) {
    Write-Host "These Solace processes are still running:" -ForegroundColor Yellow
    $solaceProcesses | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.ProcessName)" }
    $solaceJava | ForEach-Object { Write-Host "  PID $($_.ProcessId)  java" }
    Write-Host "Migrating a running server can produce torn data." -ForegroundColor Yellow
    if (Confirm-Step "Stop them now?") {
        $solaceProcesses | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
        $solaceJava | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
        Start-Sleep -Seconds 2
    }
} else {
    Write-Host "Solace is not running." -ForegroundColor Green
}
if (@(Get-Process -Name $stopList -ErrorAction SilentlyContinue).Count -gt 0) {
    Write-Host "Solace is still running - close the panel (and its Minecraft servers) and re-run this script." -ForegroundColor Red
    exit 1
}

# ─── Stop Apace ────────────────────────────────────────────────────────────
if ($mode -eq "docker") {
    if (-not (Test-Path $ApaceDir -PathType Container)) {
        Write-Host "Apace install directory not found: $ApaceDir" -ForegroundColor Red
        exit 1
    }
    Push-Location $ApaceDir
    $ids = @(Invoke-Compose compose ps -q 2>$null)
    if ($LASTEXITCODE -eq 0 -and $ids.Count -gt 0) {
        Write-Host "Stopping the Apace Docker stack ..." -ForegroundColor Green
        Invoke-Compose compose stop
        if ($LASTEXITCODE -ne 0) {
            Pop-Location
            Write-Host "Could not stop Apace - run: cd $ApaceDir; $Compose stop  (then re-run this script)." -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "The Apace Docker stack is not running." -ForegroundColor Green
    }
    Pop-Location
} else {
    $apaceStopList = @("Apace.LauncherUI", "Apace.ApiServer", "Apace.Buildplate", "Apace.PreviewGenerator", "Solace.LauncherUI", "Solace.ApiServer", "Solace.Buildplate")
    $apaceProcesses = @(Get-Process -Name $apaceStopList -ErrorAction SilentlyContinue)
    if ($apaceProcesses.Count -gt 0) {
        Write-Host "These Apace processes are still running:" -ForegroundColor Yellow
        $apaceProcesses | ForEach-Object { Write-Host "  PID $($_.Id)  $($_.ProcessName)" }
        if (Confirm-Step "Stop them now?") {
            $apaceProcesses | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            Start-Sleep -Seconds 2
        }
    }
    if (@(Get-Process -Name $apaceStopList -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Host "Apace is still running - close the panel (and its servers) and re-run this script." -ForegroundColor Red
        exit 1
    }
    Write-Host "Make sure the Apace panel is closed (it locks the same databases)." -ForegroundColor Yellow
    if (-not (Confirm-Step "Is Apace fully stopped?")) {
        Write-Host "aborted - close Apace and re-run this script." -ForegroundColor Yellow
        exit 1
    }
}

# ─── Summary ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Migration target:" -ForegroundColor White
$modeLabel = if ($mode -eq "docker") { "Docker" } else { "bare-metal" }
$backupLabel = if ($NoBackup) { "DISABLED (-NoBackup)" } else { "enabled (tar.gz next to the target)" }
Write-Host "  Solace : $SolaceDir   (read-only)"
Write-Host "  Apace  : $Target  ($modeLabel)"
Write-Host "  Backup : $backupLabel"

# ─── Step 4/5: dry run (always) ────────────────────────────────────────────
Write-Host ""
Write-Host "Step 4/5 - dry run (nothing is written)" -ForegroundColor Cyan
Write-Host ""

$dryArgs = @("--solace-dir", $SolaceDir, "--target", $Target, "--dry-run")
if ($NoBackup) { $dryArgs += "--no-backup" }
& $PyCmd @PyArgs $Converter @dryArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "The dry run failed - fix the problem reported above and re-run this script." -ForegroundColor Red
    Write-Host "Apace was stopped; start it again with:" -ForegroundColor Yellow
    if ($mode -eq "docker") { Write-Host "  cd $ApaceDir; $Compose up -d" -ForegroundColor Yellow }
    else                    { Write-Host "  pwsh $ApaceDir\run_launcher.ps1" -ForegroundColor Yellow }
    exit 1
}

if ($DryRun) {
    Write-Host ""
    Write-Host "-DryRun was given - stopping here, nothing was written." -ForegroundColor Green
    Write-Host "Re-run without -DryRun (and review the plan above) to migrate." -ForegroundColor Green
    if ($TempDir -and (Test-Path $TempDir)) { Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue }
    exit 0
}

# ─── Step 5/5: confirm + migrate ───────────────────────────────────────────
Write-Host ""
Write-Host "Step 5/5 - migrate" -ForegroundColor Cyan
Write-Host ""
Write-Host "The real run backs up $Target first (unless -NoBackup)."
Write-Host "The Solace directory is only ever read."
if (-not (Confirm-Step "Proceed with the migration?")) {
    Write-Host "aborted - nothing was changed." -ForegroundColor Yellow
    Write-Host "Apace is stopped; start it again with:" -ForegroundColor Yellow
    if ($mode -eq "docker") { Write-Host "  cd $ApaceDir; $Compose up -d" -ForegroundColor Yellow }
    else                    { Write-Host "  pwsh $ApaceDir\run_launcher.ps1" -ForegroundColor Yellow }
    if ($TempDir -and (Test-Path $TempDir)) { Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue }
    exit 2
}

$runArgs = @("--solace-dir", $SolaceDir, "--target", $Target, "--yes")
& $PyCmd @PyArgs $Converter @runArgs
$migrateRc = $LASTEXITCODE
if ($migrateRc -ne 0) {
    Write-Host ""
    Write-Host "The migration failed (exit $migrateRc) - see the converter output above." -ForegroundColor Red
    Write-Host "The Solace directory was NOT modified (it is only ever read)." -ForegroundColor Yellow
    if (-not $NoBackup) {
        Write-Host "A pre-migration backup of the Apace target is in apace-backup-*" -ForegroundColor Yellow
        Write-Host "next to $Target - restore it with the tar command in docs/solace-migration.md." -ForegroundColor Yellow
    }
    Write-Host "To bring Apace back up anyway:" -ForegroundColor Yellow
    if ($mode -eq "docker") { Write-Host "  cd $ApaceDir; $Compose up -d" -ForegroundColor Yellow }
    else                    { Write-Host "  pwsh $ApaceDir\run_launcher.ps1" -ForegroundColor Yellow }
    if ($TempDir -and (Test-Path $TempDir)) { Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue }
    exit $migrateRc
}

# ─── Next steps ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Migration finished ===" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
if ($mode -eq "docker") { Write-Host "  1. Start Apace:      cd $ApaceDir; $Compose up -d" }
else                    { Write-Host "  1. Start Apace:      pwsh $ApaceDir\run_launcher.ps1" }
Write-Host "  2. Open the panel (http://localhost:5000) and log in with your old"
Write-Host "     Solace panel credentials."
Write-Host "  3. Check config.json in $Target - the API port must match the"
Write-Host "     published Docker port (1808) if Solace used a different one."
Write-Host "  4. Every player must LOG IN AGAIN (sessions/secrets are not migrated)."
Write-Host ""
Write-Host "Once you have verified Apace, you can retire Solace yourself - this"
Write-Host "script never deletes anything, e.g.:  Rename-Item $env:USERPROFILE\solace solace.retired"
if (-not $NoBackup) {
    Write-Host "Rollback if needed: restore the apace-backup-* tar.gz next to $Target"
    Write-Host "(see docs/solace-migration.md - Rollback)."
}

if ($TempDir -and (Test-Path $TempDir)) { Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue }
