#Requires -Version 5.1

# Apace - Minecraft Earth replacement server
# Windows twin of scripts/update.sh: self-update for Docker and bare-metal
# installs. Keeps ITSELF current (always runs the newest updater logic, even on
# legacy installs), backs up the persistent data, repairs old layouts, refreshes
# docker-compose.yml when it is outdated, pulls a new image and restarts the
# container (Docker) - or downloads the newest release zip over the install root
# (no Docker). Supports rolling the image (and the data) back.
#
# It works on installs made by OLDER versions too: nothing this script would
# have created is assumed to exist - the layout is detected by FEATURE (missing
# persistent subdirs, config.json stored as a directory, a compose file without
# the BRIDGE_PORT parameter or the api_config.json mount, an installer-injected
# platform: line, a non-default persistent data root).
#
# Update channels (default -Tag):
#   - the newest GitHub RELEASE: release.yml publishes a matching versioned
#     image (ghcr.io/kotpasztet/apace:vX.Y.Z) and a matching Apace-win-<arch>.zip
#   - dev installs (compose image :dev) STAY ON DEV unless -Tag is explicit
#   - -Tag main|dev|latest|vX.Y.Z overrides the default
#
# Usage: irm https://raw.githubusercontent.com/KotPasztet/Apace/main/scripts/update.ps1 | iex
#        .\update.ps1 -Rollback
#        .\update.ps1 -BackupOnly
#        .\update.ps1 -Help

param(
    [string]$Dir = "",
    [string]$PersistentRoot = "",
    [string]$Tag = "",
    [switch]$BackupOnly,
    [switch]$Rollback,
    [string]$RestoreBackup = "",
    [switch]$NoBackup,
    [switch]$RefreshCompose,
    [switch]$Yes,
    [switch]$NoSelfUpdate,
    [switch]$Help
)

# ---------------------------------------------------------------------------
# Constants and state
# ---------------------------------------------------------------------------

$script:RepoRaw = "https://raw.githubusercontent.com/KotPasztet/Apace"
$script:RepoApi = "https://api.github.com/repos/KotPasztet/Apace"
$script:SelfUpdateUrl = "$script:RepoRaw/main/scripts/update.ps1"
$script:RequestUserAgent = "KotPasztet/Apace-update.ps1"

$script:ApaceDir = $Dir
$script:PersistentArg = $PersistentRoot
$script:PersistentGiven = $false
if ($PersistentRoot) { $script:PersistentGiven = $true }
if (-not $script:PersistentArg) { $script:PersistentArg = "C:\apace-persistent" }
$script:Persistent = ""
$script:Tag = $Tag
$script:Mode = "update"                 # update | backup-only | rollback
if ($BackupOnly) { $script:Mode = "backup-only" }
if ($Rollback) { $script:Mode = "rollback" }
$script:RestoreBackup = $RestoreBackup
$script:MakeBackup = $true
if ($NoBackup) { $script:MakeBackup = $false }
$script:AssumeYes = $false
if ($Yes) { $script:AssumeYes = $true }
$script:RefreshComposeWanted = $false
if ($RefreshCompose) { $script:RefreshComposeWanted = $true }
$script:NoSelfUpdateWanted = $false
if ($NoSelfUpdate) { $script:NoSelfUpdateWanted = $true }
$script:BackupKeep = 3
$script:ComposeBakKeep = 3

$script:ComposeFile = ""
$script:ComposeIsStandalone = $false
$script:ComposeLines = @()
$script:IsDocker = $false
$script:StateFile = ""
$script:BackupDir = ""
$script:LastBackup = ""

$script:ImageRef = ""
$script:ImageName = ""
$script:ImageTag = ""
$script:InitialImageTag = ""
$script:PanelPort = ""
$script:BindHosts = @()
$script:TargetTag = ""
$script:RollbackRef = ""
$script:WillRefresh = $false

$script:ReinvokeArgs = @()

# Feature-detected layout: dirs a legacy install may be missing entirely.
$script:LayoutDirs = @("launcher-data", "launcher-logs", "data", "dataprotection-keys", "resourcepacks", "server-template-dir", "logs", "fabric-data", "api-config")
# Named processes only - anything else on the machine is never touched.
$script:ProcessNames = @("Apace.LauncherUI", "Apace.ApiServer", "Apace.Buildplate", "Apace.PreviewGenerator", "Solace.LauncherUI", "Solace.ApiServer", "Solace.Buildplate")
$script:RunningProcs = @()
$script:RunningJava = @()
# Only these top-level entries of a release zip are ever extracted over a
# bare-metal install (mirrors the panel's own one-click updater).
$script:ZipAllowedRoots = @("components", "launcher", "staticdata")
$script:ZipAllowedRootFiles = @("run_launcher.ps1")
$script:ZipExcludedPrefixes = @("launcher/Data/", "launcher/logs/", "launcher/persistent_fabric/", "staticdata/resourcepacks/")
$script:ZipExcludedFiles = @("launcher/config.json", "components/api_config.json")

# GitHub and older TLS: Windows PowerShell 5.1 does not negotiate TLS 1.2 by
# default, which is what api.github.com and the CDN require.
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072
} catch {
    # older framework without the Tls12 constant - leave the default in place
}
# Invoke-WebRequest renders a progress bar that slows big downloads ~10x.
$ProgressPreference = "SilentlyContinue"

# ---------------------------------------------------------------------------
# Output helpers
# ---------------------------------------------------------------------------

function Write-Fail([string]$Message) {
    Write-Host $Message -ForegroundColor Red
}

function Write-WarnNote([string]$Message) {
    Write-Host $Message -ForegroundColor Yellow
}

function Write-Ok([string]$Message) {
    Write-Host $Message -ForegroundColor Green
}

function Write-Head([string]$Message) {
    Write-Host $Message -ForegroundColor Cyan
}

function Die([string]$Message) {
    Write-Fail "ERROR: $Message"
    exit 1
}

function Write-Usage {
    Write-Head "=== Apace Updater ==="
    Write-Host ""
    Write-Host "Usage: update.ps1 [options]"
    Write-Host ""
    Write-Host "Options:"
    Write-Host "  -Dir <path>             Install root holding docker-compose.yml (default: %USERPROFILE%\apace)"
    Write-Host "  -Tag <main|dev|vX.Y.Z>  Image tag / release to move to."
    Write-Host "                            default: the newest GitHub release (vX.Y.Z image + zip);"
    Write-Host "                            dev installs stay on dev unless -Tag is explicit"
    Write-Host "  -RefreshCompose         Re-download docker-compose.yml for the target version"
    Write-Host "                          (also happens automatically when the local one predates"
    Write-Host "                          a required feature, e.g. the api_config.json mount)"
    Write-Host "  -BackupOnly             Back up the persistent data and exit"
    Write-Host "  -Rollback               Go back to the image that ran before the last update"
    Write-Host "  -RestoreBackup <f>      With -Rollback: also restore a data backup (file path, or 'latest')"
    Write-Host "  -NoBackup               Skip the pre-update backup (NOT recommended)"
    Write-Host "  -NoSelfUpdate           Do not fetch the newest updater from main first"
    Write-Host "  -Yes                    Do not ask for confirmation"
    Write-Host "  -Help                   This help"
    Write-Host ""
}

function Confirm-Step([string]$Prompt) {
    if ($script:AssumeYes) { return $true }
    if ([Console]::IsInputRedirected) {
        Write-Fail "Cannot ask for confirmation (no terminal) - re-run with -Yes."
        return $false
    }
    $reply = ""
    try {
        $reply = Read-Host "$Prompt [y/N]"
    } catch {
        Write-Fail "Cannot ask for confirmation - re-run with -Yes."
        return $false
    }
    return ($reply -match '^(y|yes)$')
}

# ---------------------------------------------------------------------------
# Downloads (curl.exe on Win10+, Invoke-WebRequest as the fallback)
# ---------------------------------------------------------------------------

function Get-RemoteFile {
    param([string]$Url, [string]$Dest, [int]$TimeoutSec = 60, [switch]$ShowProgress)
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
        if ($ShowProgress) {
            # no -s so the progress bar and real errors stay visible
            & curl.exe -fSL --max-time $TimeoutSec --progress-bar -o $Dest $Url
        } else {
            & curl.exe -fsSL --max-time $TimeoutSec -o $Dest $Url 2>$null
        }
        if ($LASTEXITCODE -ne 0) { return $false }
        if (-not (Test-Path -LiteralPath $Dest -PathType Leaf)) { return $false }
        return ((Get-Item -LiteralPath $Dest).Length -gt 0)
    }
    try {
        Invoke-WebRequest -Uri $Url -OutFile $Dest -UseBasicParsing -TimeoutSec $TimeoutSec -ErrorAction Stop
    } catch {
        return $false
    }
    return $true
}

# ---------------------------------------------------------------------------
# Self-update: always run the newest updater logic
# ---------------------------------------------------------------------------

function Test-SelfUpdate {
    if ($script:NoSelfUpdateWanted) { return }
    if (-not $PSCommandPath) { return }   # piped run: this copy IS the newest main-branch updater
    if (-not (Test-Path -LiteralPath $PSCommandPath -PathType Leaf)) { return }
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("apace-update-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8) + ".ps1")
    if (-not (Get-RemoteFile $script:SelfUpdateUrl $tmp 60)) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Write-WarnNote "Warning: could not fetch the newest updater (offline?) - continuing with the local copy"
        return
    }
    $newText = ""
    try { $newText = [System.IO.File]::ReadAllText($tmp) } catch { $newText = "" }
    $oldText = ""
    try { $oldText = [System.IO.File]::ReadAllText($PSCommandPath) } catch { $oldText = "" }
    if (-not $newText -or $newText -eq $oldText) {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        return
    }
    Write-Host "-> The updater itself has a newer version - restarting with it"
    Unblock-File -LiteralPath $tmp -ErrorAction SilentlyContinue
    $exe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    # Same arguments, plus the loop breaker (-NoSelfUpdate).
    $callArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $tmp) + $script:ReinvokeArgs + @("-NoSelfUpdate")
    & $exe @callArgs
    $code = $LASTEXITCODE
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    exit $code
}

# ---------------------------------------------------------------------------
# Flags (validated after the helper definitions, before anything runs)
# ---------------------------------------------------------------------------

if ($Help) { Write-Usage; exit 0 }

# "0.1.3" and "v0.1.3" are the same image tag
if ($script:Tag -match '^v?[0-9]+\.[0-9]+\.[0-9]+$') {
    $script:Tag = "v" + ($script:Tag -replace '^v', '')
}
if ($script:Tag -and $script:Tag -ne "main" -and $script:Tag -ne "dev" -and $script:Tag -ne "latest") {
    if ($script:Tag -notmatch '^(v[0-9]+|[0-9]+)') {
        Die "-Tag must be 'main', 'dev', 'latest' or a version like v0.1.3 (got '$($script:Tag)')"
    }
}
if ($script:RestoreBackup -and $script:Mode -ne "rollback") {
    Die "-RestoreBackup only makes sense together with -Rollback"
}
if (-not $script:ApaceDir) {
    if (-not $env:USERPROFILE) {
        Die "cannot determine the user profile directory - pass -Dir <path>"
    }
    $script:ApaceDir = Join-Path $env:USERPROFILE "apace"
}
try {
    $script:ApaceDir = [System.IO.Path]::GetFullPath(($script:ApaceDir -replace '/', '\'))
} catch {
    Die "-Dir must be a usable path (got '$($script:ApaceDir)')"
}

# The arguments a re-invocation (self-update) has to reproduce.
if ($script:ApaceDir) { $script:ReinvokeArgs += @("-Dir", $script:ApaceDir) }
if ($script:PersistentGiven) { $script:ReinvokeArgs += @("-PersistentRoot", $script:PersistentArg) }
if ($script:Tag) { $script:ReinvokeArgs += @("-Tag", $script:Tag) }
if ($script:Mode -eq "backup-only") { $script:ReinvokeArgs += "-BackupOnly" }
if ($script:Mode -eq "rollback") { $script:ReinvokeArgs += "-Rollback" }
if ($script:RestoreBackup) { $script:ReinvokeArgs += @("-RestoreBackup", $script:RestoreBackup) }
if (-not $script:MakeBackup) { $script:ReinvokeArgs += "-NoBackup" }
if ($script:RefreshComposeWanted) { $script:ReinvokeArgs += "-RefreshCompose" }
if ($script:AssumeYes) { $script:ReinvokeArgs += "-Yes" }

Test-SelfUpdate

# ---------------------------------------------------------------------------
# Docker / docker compose plumbing
# ---------------------------------------------------------------------------

function Get-ComposeDisplayName {
    if ($script:ComposeIsStandalone) { return "docker-compose" }
    return "docker compose"
}

function Resolve-ComposeCommand {
    # Docker must be installed AND running before anything is stopped.
    $script:ComposeIsStandalone = $false
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
        Die "docker was not found - install Docker Desktop: https://docs.docker.com/desktop/setup/install/windows-install/"
    }
    & docker info 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Die "docker is not running - start Docker Desktop and re-run this script"
    }
    & docker compose version 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { return }
    if (Get-Command docker-compose -ErrorAction SilentlyContinue) {
        $script:ComposeIsStandalone = $true
        return
    }
    Die "neither 'docker compose' nor 'docker-compose' is available (update Docker Desktop)"
}

function Invoke-Compose {
    if ($script:ComposeIsStandalone) { & docker-compose -f $script:ComposeFile @args }
    else { & docker compose -f $script:ComposeFile @args }
}

function Invoke-ComposeCapture {
    if ($script:ComposeIsStandalone) { return @(& docker-compose -f $script:ComposeFile @args 2>$null) }
    return @(& docker compose -f $script:ComposeFile @args 2>$null)
}

function Find-ComposeFile([string]$Root) {
    foreach ($name in @("docker-compose.yml", "docker-compose.yaml", "compose.yaml", "compose.yml")) {
        $candidate = Join-Path $Root $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    return ""
}

# ---------------------------------------------------------------------------
# Compose file parsing (the YAML subset the Apace compose files use)
# ---------------------------------------------------------------------------

function Trim-ComposeValue([string]$Value) {
    if ($null -eq $Value) { return "" }
    $s = $Value.Trim()
    $hash = $s.IndexOf("#")
    if ($hash -ge 0) { $s = $s.Substring(0, $hash).Trim() }
    if ($s.Length -ge 2) {
        $first = $s.Substring(0, 1)
        $lastCh = $s.Substring($s.Length - 1, 1)
        if (($first -eq '"') -and ($lastCh -eq '"')) { $s = $s.Substring(1, $s.Length - 2) }
        elseif (($first -eq "'") -and ($lastCh -eq "'")) { $s = $s.Substring(1, $s.Length - 2) }
    }
    return $s
}

# Entries of a list section ("ports:", "volumes:", ...) without the leading "- ".
# Stops at the next key that is not deeper than the section key, so
# "environment:" never leaks in.
function Read-ComposeSection([string]$Key) {
    $entries = @()
    $prefix = $Key + ":"
    $inSection = $false
    $sectionIndent = -1
    foreach ($line in $script:ComposeLines) {
        $trimmed = $line.TrimStart()
        if ($trimmed.Length -eq 0) { continue }
        if ($trimmed.StartsWith("#")) { continue }
        $indent = $line.Length - $trimmed.Length
        if ($inSection -and ($indent -le $sectionIndent)) { $inSection = $false }
        if ((-not $inSection) -and $trimmed.StartsWith($prefix)) {
            $sectionIndent = $indent
            $inSection = $true
            continue
        }
        if ($inSection -and ($indent -gt $sectionIndent) -and $trimmed.StartsWith("- ")) {
            $entries += $trimmed.Substring(2)
        }
    }
    return $entries
}

# Runs once at startup and again after a compose refresh. Sets ImageRef,
# ImageName, ImageTag, PanelPort, Persistent and BackupDir.
function Read-Compose {
    $script:ImageRef = ""
    $script:ImageName = ""
    $script:ImageTag = ""
    $script:PanelPort = ""
    $script:BindHosts = @()

    $script:ComposeLines = @([System.IO.File]::ReadAllLines($script:ComposeFile))

    $imgLine = ""
    foreach ($line in $script:ComposeLines) {
        if ($line -match '^\s*image:\s*[^#]') { $imgLine = $line; break }
    }
    if (-not $imgLine) {
        Die "no 'image:' line in $($script:ComposeFile) - this does not look like an Apace compose file; re-run install.ps1"
    }
    $idx = $imgLine.IndexOf("image:")
    $script:ImageRef = Trim-ComposeValue $imgLine.Substring($idx + 6)

    if ($script:ImageRef.Contains("@")) {
        $at = $script:ImageRef.IndexOf("@")
        $script:ImageName = $script:ImageRef.Substring(0, $at)
        $script:ImageTag = $script:ImageRef.Substring($at + 1)
    } else {
        $slash = $script:ImageRef.LastIndexOf("/")
        $last = $script:ImageRef
        if ($slash -ge 0) { $last = $script:ImageRef.Substring($slash + 1) }
        $colon = $last.LastIndexOf(":")
        if ($colon -ge 0) {
            $nameLen = $script:ImageRef.Length - $last.Length + $colon
            $script:ImageName = $script:ImageRef.Substring(0, $nameLen)
            $script:ImageTag = $last.Substring($colon + 1)
        } else {
            $script:ImageName = $script:ImageRef
            $script:ImageTag = "latest"
        }
    }

    foreach ($entryRaw in (Read-ComposeSection "volumes")) {
        $entry = Trim-ComposeValue $entryRaw
        if (-not $entry) { continue }
        if (-not $entry.Contains(":")) { continue }
        # split at the LAST colon: the container path has none, while a Windows
        # host path does ("C:/apace-persistent/launcher-data:/app/launcher/Data")
        $sep = $entry.LastIndexOf(":")
        $hostPart = $entry.Substring(0, $sep)
        if ($hostPart.Contains("$")) { continue }   # ${VAR:-...} - not resolvable here
        # named volumes (no path separator) are not bind mounts
        if ($hostPart -match '[\\/]') { $script:BindHosts += $hostPart }
    }

    foreach ($entryRaw in (Read-ComposeSection "ports")) {
        $entry = Trim-ComposeValue $entryRaw
        if (-not $entry) { continue }
        if (-not $entry.Contains(":")) { continue }
        $entry2 = $entry -replace '/(tcp|udp)$', ''
        if ($entry2.Contains("$")) { continue }
        $parts = @($entry2 -split ':')
        if ($parts.Count -lt 2) { continue }
        $hostPort = $parts[$parts.Count - 2]
        $contPort = $parts[$parts.Count - 1]
        if (($contPort -eq "5000") -and (-not $script:PanelPort)) { $script:PanelPort = $hostPort }
    }
    if (-not $script:PanelPort) {
        $script:PanelPort = "5000"
        Write-WarnNote "could not find a published panel port in the compose file - assuming 5000"
    }

    # Persistent data root = the most common parent directory of the bind mounts.
    # Works for /opt/apace-persistent, C:/apace-persistent and arbitrary layouts
    # (Coolify, custom paths) without hardcoding anything.
    $parents = @{}
    foreach ($h in $script:BindHosts) {
        $hp = $h -replace '\\', '/'
        $d = $hp -replace '/[^/]*$', ''
        if (-not $d) { continue }
        if ($parents.ContainsKey($d)) { $parents[$d] = $parents[$d] + 1 }
        else { $parents[$d] = 1 }
    }
    $best = ""
    $bestCount = -1
    foreach ($key in $parents.Keys) {
        $better = $false
        if ($parents[$key] -gt $bestCount) { $better = $true }
        elseif (($parents[$key] -eq $bestCount) -and ($key.Length -gt $best.Length)) { $better = $true }
        if ($better) { $bestCount = $parents[$key]; $best = $key }
    }
    if ($best) {
        $script:Persistent = $best.TrimEnd('/')
        if ($script:PersistentGiven) {
            $given = ($script:PersistentArg -replace '\\', '/').TrimEnd('/')
            if ($given -ne $script:Persistent) {
                Write-WarnNote "note: the compose file mounts $($script:Persistent) but -PersistentRoot $given was given - using the compose file's paths"
            }
        }
    } else {
        $script:Persistent = ($script:PersistentArg -replace '\\', '/').TrimEnd('/')
        Write-WarnNote "could not detect the persistent data root from the volumes in $($script:ComposeFile) - assuming $($script:Persistent)"
    }
    if (-not $script:Persistent) {
        Die "could not detect the persistent data root from the volumes in $($script:ComposeFile)"
    }
    $script:BackupDir = Join-Path (Split-Path $script:Persistent -Parent) "apace-persistent-backups"
}

# ---------------------------------------------------------------------------
# Update target: which image tag is this run moving to?
# ---------------------------------------------------------------------------

function Get-LatestReleaseTag {
    try {
        $release = Invoke-RestMethod -Uri "$script:RepoApi/releases/latest" -Headers @{ "User-Agent" = $script:RequestUserAgent } -TimeoutSec 30 -ErrorAction Stop
    } catch {
        return ""
    }
    $t = ""
    if ($release -and $release.tag_name) { $t = "$($release.tag_name)" }
    if ($t -match '^(v[0-9]+|[0-9][0-9]+)') { return $t }
    return ""
}

function Resolve-TargetTag {
    # Sets $script:TargetTag ("" = keep the current tag). $true when resolved.
    $script:TargetTag = ""
    if ($script:Tag -and $script:Tag -ne "latest") {
        $script:TargetTag = $script:Tag
        Write-Host "Channel: explicit -Tag $script:TargetTag"
        return $true
    }
    if ((-not $script:Tag) -and ($script:ImageTag -eq "dev")) {
        # dev channel: stay on the rolling dev image unless asked otherwise
        $script:TargetTag = "dev"
        Write-Host "Channel: dev (dev installs stay on dev unless -Tag is given explicitly)"
        return $true
    }
    $latest = Get-LatestReleaseTag
    if ($latest) {
        $script:TargetTag = $latest
        Write-Host "Channel: release - the newest release is $script:TargetTag"
        return $true
    }
    if ($script:Tag -eq "latest") {
        Die "could not resolve the newest release via the GitHub API (offline or rate-limited)"
    }
    return $false
}

# ---------------------------------------------------------------------------
# Heal old layouts (idempotent, feature-detected)
# ---------------------------------------------------------------------------

function Repair-Layout {
    Write-Head "Checking the persistent data layout"
    Write-Host "-> Ensuring $($script:Persistent) and its subdirs exist (older installs may miss some)"
    try {
        New-Item -ItemType Directory -Force -Path $script:Persistent -ErrorAction Stop | Out-Null
    } catch {
        Die "cannot create $($script:Persistent): $($_.Exception.Message)"
    }
    $missing = @()
    foreach ($d in $script:LayoutDirs) {
        $candidate = Join-Path $script:Persistent $d
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { $missing += $candidate }
    }
    if ($missing.Count -gt 0) {
        foreach ($d in $missing) {
            Write-Host "  creating: $d"
            New-Item -ItemType Directory -Force -Path $d -ErrorAction SilentlyContinue | Out-Null
        }
    }

    $cfg = Join-Path $script:Persistent "config.json"
    if (Test-Path -LiteralPath $cfg -PathType Container) {
        Write-WarnNote "-> $cfg is a DIRECTORY (old broken install) - removing it"
        Remove-Item -LiteralPath $cfg -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $cfg -PathType Leaf) {
        Write-Host "  config.json present - left untouched (the app rewrites it on every load)"
    } else {
        Write-Host ('-> Seeding ' + $cfg + ' with {"ApiPort":1808} (it was missing)')
        try {
            [System.IO.File]::WriteAllText($cfg, '{"ApiPort":1808}' + [Environment]::NewLine)
        } catch {
            Die "cannot write $cfg : $($_.Exception.Message)"
        }
    }

    # api_config.json (the ApiServer login secrets) is mounted as a single FILE.
    # If the host file is missing, Docker creates a DIRECTORY at that path on the
    # next start and the ApiServer cannot write its config - seed an empty file
    # instead (existing secrets must survive updates).
    $acfg = Join-Path (Join-Path $script:Persistent "api-config") "api_config.json"
    if (Test-Path -LiteralPath $acfg -PathType Container) {
        Write-WarnNote "-> $acfg is a DIRECTORY (Docker created it for the file mount) - removing it"
        Remove-Item -LiteralPath $acfg -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $acfg -PathType Leaf) {
        Write-Host "  api-config\api_config.json present - left untouched (the login secrets in it must survive updates)"
    } else {
        Write-Host "-> Seeding $acfg as an empty file (the ApiServer fills in its defaults)"
        New-Item -ItemType File -Force -Path $acfg -ErrorAction SilentlyContinue | Out-Null
    }

    # Drift prevention: the panel can listen on a non-default bridge port while
    # the compose file still publishes 19132. Mirror it into .env so the UDP
    # mapping keeps matching the app after the container is recreated.
    $bp = ""
    if (Test-Path -LiteralPath $cfg -PathType Leaf) {
        $cfgText = ""
        try { $cfgText = [System.IO.File]::ReadAllText($cfg) } catch { $cfgText = "" }
        if ($cfgText) {
            $m = [regex]::Match($cfgText, '"BridgePort"\s*:\s*"?([0-9]+)"?')
            if ($m.Success) { $bp = $m.Groups[1].Value }
        }
    }
    if ($bp -and ($bp -ne "19132")) {
        $envFile = Join-Path $script:ApaceDir ".env"
        if (-not (Test-Path -LiteralPath $envFile -PathType Leaf)) {
            Write-Host "-> config.json says BridgePort=$bp but there is no .env next to the compose file - writing BRIDGE_PORT=$bp to $envFile"
            [System.IO.File]::WriteAllText($envFile, "BRIDGE_PORT=$bp" + [Environment]::NewLine)
        } else {
            $hit = Select-String -LiteralPath $envFile -Pattern '^BRIDGE_PORT=' | Select-Object -First 1
            if (-not $hit) {
                Write-Host "-> Appending BRIDGE_PORT=$bp to the existing $envFile"
                [System.IO.File]::AppendAllText($envFile, "BRIDGE_PORT=$bp" + [Environment]::NewLine)
            } else {
                $cur = ($hit.Line -replace '^BRIDGE_PORT=', '').Trim()
                if ($cur -ne $bp) {
                    Write-WarnNote "  note: $envFile has BRIDGE_PORT=$cur but config.json says $bp - keeping .env as is"
                }
            }
        }
    }

    # uid 1654 ownership (the 'app' user inside the image) is a Linux concern;
    # Docker Desktop on Windows maps it automatically.
    Write-Host ""
}

# ---------------------------------------------------------------------------
# Backups
# ---------------------------------------------------------------------------

function Get-BackupList {
    if ((-not $script:BackupDir) -or (-not (Test-Path -LiteralPath $script:BackupDir -PathType Container))) { return @() }
    return @(Get-ChildItem -LiteralPath $script:BackupDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^apace-[0-9]+T[0-9]+Z\.(tar\.gz|tar\.zst|zip)$' } |
        Sort-Object LastWriteTime -Descending)
}

function Invoke-BackupRotation {
    $backups = Get-BackupList
    if ($backups.Count -le $script:BackupKeep) { return }
    $old = @($backups | Select-Object -Skip $script:BackupKeep)
    foreach ($f in $old) {
        Write-Host "-> Rotation: removing old backup $($f.FullName) (keeping the newest $script:BackupKeep)"
        Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue
    }
}

# Archives $DataRoot into $script:BackupDir (tar.gz preferred - Win10+ ships
# bsdtar; Compress-Archive as the fallback). Returns $true on success and sets
# $script:LastBackup.
function New-DataBackup([string]$DataRoot, [string[]]$ExcludeNames, [string]$ExcludeLabel) {
    $script:LastBackup = ""
    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
    $parent = Split-Path $DataRoot -Parent
    $base = Split-Path $DataRoot -Leaf
    if (-not $parent) { $parent = $DataRoot }
    Write-Host "-> Backing up $DataRoot to $script:BackupDir"
    Write-Host "   (excluding $ExcludeLabel - they are reproducible)"
    try {
        New-Item -ItemType Directory -Force -Path $script:BackupDir -ErrorAction Stop | Out-Null
    } catch {
        Write-Fail "   cannot create the backup directory $($script:BackupDir): $($_.Exception.Message)"
        return $false
    }
    $out = ""
    $tarExe = Get-Command tar.exe -ErrorAction SilentlyContinue
    if ($tarExe) {
        $out = Join-Path $script:BackupDir "apace-$stamp.tar.gz"
        $tarArgs = @("-czf", $out)
        foreach ($e in $ExcludeNames) { $tarArgs += @("--exclude", "$base/$e") }
        $tarArgs += @("-C", $parent, $base)
        & tar @tarArgs 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
            Write-Fail "   tar failed"
            return $false
        }
    } else {
        $out = Join-Path $script:BackupDir "apace-$stamp.zip"
        $children = @(Get-ChildItem -Force -LiteralPath $DataRoot -ErrorAction SilentlyContinue |
            Where-Object { $ExcludeNames -notcontains $_.Name })
        if ($children.Count -eq 0) {
            Write-Fail "   nothing to back up in $DataRoot"
            return $false
        }
        try {
            $paths = @($children | ForEach-Object { $_.FullName })
            Compress-Archive -LiteralPath $paths -DestinationPath $out -Force -ErrorAction Stop
        } catch {
            Remove-Item -LiteralPath $out -Force -ErrorAction SilentlyContinue
            Write-Fail "   Compress-Archive failed: $($_.Exception.Message)"
            return $false
        }
    }
    $sizeMb = [math]::Round((Get-Item -LiteralPath $out).Length / 1MB, 1)
    Write-Host "   backup size: $sizeMb MB"
    Invoke-BackupRotation
    $script:LastBackup = $out
    return $true
}

function Backup-PersistentRoot {
    return (New-DataBackup $script:Persistent @("logs", "launcher-logs", "resourcepacks") "logs, launcher-logs, resourcepacks")
}

function Backup-InstallDir {
    return (New-DataBackup $script:ApaceDir @("logs", "launcher/logs", "launcher-logs", "staticdata/resourcepacks") "logs and resourcepacks")
}

function Resolve-BackupPath([string]$Want) {
    if ($Want -eq "latest") {
        $newest = @(Get-BackupList | Select-Object -First 1)
        if ($newest.Count -eq 0) {
            Die "no backups found in $($script:BackupDir)"
        }
        return $newest[0].FullName
    }
    if (-not (Test-Path -LiteralPath $Want -PathType Leaf)) {
        Die "backup archive not found: $Want"
    }
    return (Get-Item -LiteralPath $Want).FullName
}

# Restores a backup written by New-DataBackup. The archive was written as
# "<basename-of-data-root>/..." - extract next to the root so its contents land
# back inside it; a flat (zip fallback) archive is extracted into the root.
function Restore-DataBackup([string]$Archive, [string]$DataRoot) {
    $parent = Split-Path $DataRoot -Parent
    $base = Split-Path $DataRoot -Leaf
    if (-not $parent) { $parent = $DataRoot }
    $dest = $DataRoot
    if ($Archive -match '\.zip$') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
        if ($null -eq $zip) { Die "could not open the backup archive: $Archive" }
        $first = ""
        $firstEntry = @($zip.Entries | Select-Object -First 1)
        if ($firstEntry.Count -gt 0) { $first = ($firstEntry[0].FullName -replace '\\', '/') }
        $zip.Dispose()
        if ($first.StartsWith("$base/", [System.StringComparison]::OrdinalIgnoreCase)) { $dest = $parent }
        else { Write-WarnNote "  backup layout differs from this install - extracting into $DataRoot" }
        Write-Host "-> Extracting $Archive into $dest (overwrites the existing data)"
        Expand-Archive -LiteralPath $Archive -DestinationPath $dest -Force
    } else {
        $lines = @(& tar -tf $Archive 2>$null)
        if ($lines.Count -eq 0) { Die "could not read the backup archive: $Archive" }
        $top = (($lines[0] -replace '\\', '/') -replace '/.*$', '')
        if ($top -eq $base) { $dest = $parent }
        else { Write-WarnNote "  backup layout differs from this install - extracting into $DataRoot" }
        Write-Host "-> Extracting $Archive into $dest (overwrites the existing data)"
        & tar -xf $Archive -C $dest 2>$null
        if ($LASTEXITCODE -ne 0) { Die "extracting $Archive failed" }
    }
}

# ---------------------------------------------------------------------------
# Image / container helpers and the rollback state file
# ---------------------------------------------------------------------------

function Find-RollbackRef {
    # Registry digest when known, else the local image id.
    $script:RollbackRef = ""
    $ids = @(Invoke-ComposeCapture ps -q)
    $img = ""
    if ($ids.Count -gt 0) {
        $found = @(& docker inspect -f '{{.Image}}' $ids[0] 2>$null)
        if ($found.Count -gt 0) { $img = "$($found[0])" }
    }
    if (-not $img) {
        $found = @(& docker image inspect -f '{{.Id}}' $script:ImageRef 2>$null)
        if ($found.Count -gt 0) { $img = "$($found[0])" }
    }
    if ($img) {
        $digests = @(& docker image inspect -f '{{index .RepoDigests 0}}' $img 2>$null)
        $digest = ""
        if ($digests.Count -gt 0) { $digest = "$($digests[0])" }
        if ($digest -match '@sha256:') {
            $script:RollbackRef = $digest
        } else {
            # locally built / never pushed image: there is no registry digest
            $script:RollbackRef = $img
            Write-WarnNote "  note: no registry digest for the image - using the local image id as the rollback point"
        }
    }
    if (-not $script:RollbackRef) {
        Write-WarnNote "  note: no running container and no local image - the rollback point cannot be recorded"
    }
}

function Set-ImageReference([string]$NewRef) {
    # Only the image: line is touched, so an installer-injected platform: line survives.
    Write-Host "-> Rewriting the image line in $($script:ComposeFile) to: $NewRef"
    $lines = @([System.IO.File]::ReadAllLines($script:ComposeFile))
    $out = New-Object System.Collections.Generic.List[string]
    $done = $false
    foreach ($line in $lines) {
        if ((-not $done) -and ($line -match '^(\s*image:).*$')) {
            $out.Add(($Matches[1] + " " + $NewRef))
            $done = $true
        } else {
            $out.Add($line)
        }
    }
    $found = $false
    foreach ($line in $out) {
        if (($line -match '^\s*image:') -and $line.Contains($NewRef)) { $found = $true; break }
    }
    if (-not $found) {
        Die "failed to rewrite the image line - $($script:ComposeFile) left unchanged"
    }
    [System.IO.File]::WriteAllLines($script:ComposeFile, [string[]]($out.ToArray()))
    $script:ImageRef = $NewRef
}

function Write-UpdateState([string]$PreviousRef, [string]$PreviousTagIn, [string]$NewTagIn) {
    $state = [ordered]@{
        previousDigest = "$PreviousRef"
        previousTag = "$PreviousTagIn"
        newTag = "$NewTagIn"
        backup = "$($script:LastBackup)"
        updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
    }
    $json = ConvertTo-Json -InputObject $state
    [System.IO.File]::WriteAllText($script:StateFile, $json + [Environment]::NewLine)
    $shown = $PreviousRef
    if (-not $shown) { $shown = "unknown" }
    Write-Host "-> Rollback point recorded in $($script:StateFile) (previous image: $shown)"
}

function Read-UpdateState {
    if (-not (Test-Path -LiteralPath $script:StateFile -PathType Leaf)) { return $null }
    try {
        return ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($script:StateFile))
    } catch {
        return $null
    }
}

# ---------------------------------------------------------------------------
# Health check / failure report
# ---------------------------------------------------------------------------

function Test-PanelHealthy([string]$Port) {
    # The panel serves the login page on /.
    $url = "http://127.0.0.1:$Port/"
    Write-Host "-> Waiting for the panel on $url (up to ~60s)"
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    for ($i = 0; $i -lt 20; $i++) {
        if ($curl) {
            & curl.exe -fsS -o NUL --max-time 3 $url 2>$null
            if ($LASTEXITCODE -eq 0) { return $true }
        } else {
            try {
                Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 3 -ErrorAction Stop | Out-Null
                return $true
            } catch {
                # not up yet
            }
        }
        Start-Sleep -Seconds 3
    }
    # Last chance: maybe the container is healthy but the port is not reachable
    # from this shell (firewall, port remap).
    for ($i = 0; $i -lt 12; $i++) {
        $ids = @(Invoke-ComposeCapture ps -q)
        if ($ids.Count -gt 0) {
            $status = @(& docker inspect -f '{{.State.Status}}' $ids[0] 2>$null)
            if (($status.Count -gt 0) -and ("$($status[0])" -eq "running")) { return $true }
        }
        Start-Sleep -Seconds 5
    }
    return $false
}

function Write-FailureReport {
    Write-Fail ""
    Write-Fail "Apace did not come up healthy."
    $ids = @(Invoke-ComposeCapture ps -q)
    if ($ids.Count -gt 0) {
        Write-Host "--- last 50 log lines ---"
        & docker logs --tail 50 $ids[0] 2>&1 | ForEach-Object { Write-Host "$_" }
        Write-Host "-------------------------"
    } else {
        Write-WarnNote "no container is running for this compose project"
    }
    Write-Fail "Nothing was rolled back automatically - your data is untouched."
    Write-Fail "Watch it live:   cd $($script:ApaceDir); $(Get-ComposeDisplayName) logs -f"
    Write-Fail "Go back instead: .\update.ps1 -Dir $($script:ApaceDir) -Rollback"
    exit 1
}

# ---------------------------------------------------------------------------
# Compose refresh
# ---------------------------------------------------------------------------

# Legacy installs keep whatever docker-compose.yml their installer downloaded -
# missing the api_config.json mount, the ${BRIDGE_PORT:-19132} parameter, or
# newer volumes. When the local file predates a feature the target version
# needs, download the compose file FOR THE TARGET TAG and re-apply everything
# the installer/user had changed locally. Any ambiguity -> keep the old file
# and print manual instructions; never guess paths.
function Test-RefreshNeeded {
    if ($script:RefreshComposeWanted) { return $true }
    if (-not (Select-String -LiteralPath $script:ComposeFile -Pattern 'api_config\.json' -Quiet)) { return $true }
    if (-not (Select-String -LiteralPath $script:ComposeFile -Pattern 'BRIDGE_PORT' -Quiet)) { return $true }
    return $false
}

function Get-RemoteCompose([string]$Ref) {
    if ($Ref -eq "dev") {
        $names = @("docker-compose.dev.yml", "docker-compose.yml")
    } else {
        $names = @("docker-compose.yml", "docker-compose.dev.yml")
    }
    foreach ($name in $names) {
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("apace-compose-" + [System.Guid]::NewGuid().ToString("N").Substring(0, 8) + ".yml")
        $ok = Get-RemoteFile "$script:RepoRaw/$Ref/$name" $tmp 60
        if ($ok) {
            $hasImage = $false
            try {
                foreach ($line in [System.IO.File]::ReadAllLines($tmp)) {
                    if ($line -match '^\s*image:\s*[^#]') { $hasImage = $true; break }
                }
            } catch {
                $hasImage = $false
            }
            if ($hasImage) { return $tmp }
        }
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
    }
    return ""
}

function Get-DockerPlatform {
    # What this host is (for an unusable old platform: value).
    $arch = Get-WindowsArch
    if ($arch -eq "x64") { return "linux/amd64" }
    if ($arch -eq "arm64") { return "linux/arm64" }
    return ""
}

function Get-WindowsArch {
    # Release asset naming: Apace-win-x64.zip / Apace-win-arm64.zip.
    $arch = ""
    try {
        $cpus = @(Get-CimInstance Win32_Processor -ErrorAction Stop)
        if ($cpus.Count -gt 0) {
            # 9 = x64/AMD64, 12 = ARM64 (same mapping install.ps1 uses)
            switch ([int]$cpus[0].Architecture) {
                9 { $arch = "x64" }
                12 { $arch = "arm64" }
                default { $arch = "" }
            }
        }
    } catch {
        $arch = ""
    }
    if (-not $arch) {
        switch ($env:PROCESSOR_ARCHITECTURE) {
            "AMD64" { $arch = "x64" }
            "ARM64" { $arch = "arm64" }
            default { $arch = "" }
        }
    }
    return $arch
}

# Applies the local mutations the downloaded compose cannot know about.
function Write-RefreshedCompose([string]$Src, [string]$Dst) {
    $lines = @()
    try { $lines = @([System.IO.File]::ReadAllLines($Src)) } catch { return $false }
    if ($lines.Count -eq 0) { return $false }

    # 1) persistent-root path: installs made anywhere other than the Linux
    #    default (this Windows installer, custom prefixes, Coolify-style
    #    layouts) point the bind mounts elsewhere - copy the detected root over
    #    the stock /opt/apace-persistent.
    $root = ($script:Persistent -replace '\\', '/').TrimEnd('/')
    if ($root -ne "/opt/apace-persistent") {
        if ($root -match '[\r\n"]') { return $false }   # refuse to rewrite such a path
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($line in $lines) { $out.Add($line.Replace("/opt/apace-persistent/", $root + "/")) }
        $lines = @($out.ToArray())
        foreach ($line in $lines) {
            if ($line.Contains("/opt/apace-persistent")) {
                return $false   # rewrite incomplete - never leave a half-rewritten file behind
            }
        }
    }

    # 2) the platform: line the installers inject after image: (absence in the
    #    old file means the installer deliberately added none - the image is
    #    multi-arch, so do not invent one)
    $curPlatform = ""
    foreach ($line in $script:ComposeLines) {
        if ($line -match '^\s*platform:\s*([^#]+)') {
            $curPlatform = (Trim-ComposeValue $Matches[1])
            break
        }
    }
    if ($curPlatform) {
        if (($curPlatform -ne "linux/amd64") -and ($curPlatform -ne "linux/arm64")) {
            $curPlatform = Get-DockerPlatform
            if (-not $curPlatform) { return $false }
        }
        $hasPlatform = $false
        foreach ($line in $lines) {
            if ($line -match '^\s*platform:') { $hasPlatform = $true; break }
        }
        if (-not $hasPlatform) {
            $out = New-Object System.Collections.Generic.List[string]
            $inserted = $false
            foreach ($line in $lines) {
                $out.Add($line)
                if ((-not $inserted) -and ($line -match '^\s*image:')) {
                    $out.Add("    platform: $curPlatform")
                    $inserted = $true
                }
            }
            $lines = @($out.ToArray())
        }
    }

    # 3) a customized panel port (host side) - otherwise the refresh would move
    #    the panel to :5000 and the user's URL would stop working
    if ($script:PanelPort -and ($script:PanelPort -ne "5000")) {
        $wanted = '"' + $script:PanelPort + ':5000"'
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($line in $lines) { $out.Add($line.Replace('"5000:5000"', $wanted)) }
        $lines = @($out.ToArray())
        $found = $false
        foreach ($line in $lines) {
            if ($line.Contains($wanted)) { $found = $true; break }
        }
        if (-not $found) { return $false }
    }

    # sanity: the refreshed file must contain the features that triggered this
    $text = [string]::Join("`n", $lines)
    if ($text -notmatch 'BRIDGE_PORT') { return $false }
    if ($text -notmatch 'api_config\.json') { return $false }

    try {
        [System.IO.File]::WriteAllLines($Dst, [string[]]$lines)
    } catch {
        return $false
    }
    return $true
}

function Write-RefreshFallback([string]$Reason) {
    $ref = $script:TargetTag
    if (-not $ref) { $ref = "main" }
    Write-WarnNote "  !! compose refresh skipped: $Reason - keeping the existing $($script:ComposeFile)"
    Write-WarnNote "     To refresh it by hand (then re-apply your platform: line / paths):"
    Write-WarnNote "       Copy-Item '$($script:ComposeFile)' '$($script:ApaceDir)\compose.bak-manual'"
    Write-WarnNote "       iwr $script:RepoRaw/$ref/$(Split-Path $script:ComposeFile -Leaf) -OutFile '$($script:ComposeFile)'"
}

# Returns $true on success; on any problem the old compose file is kept.
function Invoke-ComposeRefresh {
    $ref = $script:TargetTag
    if (-not $ref) { $ref = "main" }
    Write-Host "-> Refreshing the compose file from version $ref of the repo"
    $dl = Get-RemoteCompose $ref
    if (-not $dl) {
        Write-RefreshFallback "could not download the compose file for $ref (offline?)"
        return $false
    }
    $cand = Join-Path $script:ApaceDir ".compose.apace-new"
    Remove-Item -LiteralPath $cand -Force -ErrorAction SilentlyContinue
    if (-not (Write-RefreshedCompose $dl $cand)) {
        Remove-Item -LiteralPath $dl -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $cand -Force -ErrorAction SilentlyContinue
        Write-RefreshFallback "the local adjustments (persistent-root paths, platform, panel port) could not be applied unambiguously"
        return $false
    }
    Remove-Item -LiteralPath $dl -Force -ErrorAction SilentlyContinue
    if ($script:ComposeIsStandalone) { & docker-compose -f $cand config --quiet 2>$null | Out-Null }
    else { & docker compose -f $cand config --quiet 2>$null | Out-Null }
    if ($LASTEXITCODE -ne 0) {
        Remove-Item -LiteralPath $cand -Force -ErrorAction SilentlyContinue
        Write-RefreshFallback "the refreshed compose file did not pass 'compose config' validation"
        return $false
    }
    # keep the old file (rotation: newest $script:ComposeBakKeep)
    $ts = (Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'")
    $bak = Join-Path $script:ApaceDir "compose.bak-$ts"
    Copy-Item -LiteralPath $script:ComposeFile -Destination $bak -Force -ErrorAction SilentlyContinue
    if (-not (Test-Path -LiteralPath $bak -PathType Leaf)) {
        Remove-Item -LiteralPath $cand -Force -ErrorAction SilentlyContinue
        Write-RefreshFallback "could not back up the current compose file"
        return $false
    }
    $oldBaks = @(Get-ChildItem -LiteralPath $script:ApaceDir -File -Filter "compose.bak-*" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -Skip $script:ComposeBakKeep)
    foreach ($old in $oldBaks) {
        Remove-Item -LiteralPath $old.FullName -Force -ErrorAction SilentlyContinue
    }
    Move-Item -LiteralPath $cand -Destination $script:ComposeFile -Force
    Write-Ok "  compose file refreshed (previous copy kept as $(Split-Path $bak -Leaf))"
    # re-read image / ports / volumes from the refreshed file
    Read-Compose
    return $true
}

# ---------------------------------------------------------------------------
# Docker mode: update
# ---------------------------------------------------------------------------

function Start-DockerUpdate {
    if (-not (Resolve-TargetTag)) {
        Write-WarnNote "could not resolve the newest release (offline / GitHub API rate limit) - staying on the current tag ($($script:ImageTag))"
    }
    $newRef = $script:ImageRef
    if ($script:TargetTag) { $newRef = "$($script:ImageName):$($script:TargetTag)" }

    Repair-Layout

    $script:WillRefresh = $false
    if (Test-RefreshNeeded) { $script:WillRefresh = $true }

    Find-RollbackRef
    $prevShown = $script:RollbackRef
    if (-not $prevShown) { $prevShown = "unknown" }
    Write-Head "Currently running: $prevShown"
    if ($script:TargetTag) { Write-Head "Update target:     $newRef" }
    else { Write-Host "Update target:     keep $($script:ImageRef) (refresh its bits only)" }
    Write-Host "About to: stop the container (flushes the SQLite WAL), back up $($script:Persistent),"
    if ($script:WillRefresh) {
        $refShown = $script:TargetTag
        if (-not $refShown) { $refShown = "main" }
        Write-Host "refresh docker-compose.yml from $refShown (the old copy is kept as compose.bak-*),"
    }
    Write-Host "pull $newRef and start it again. Downtime: a few minutes."
    if (-not $script:MakeBackup) {
        Write-WarnNote "!! -NoBackup given: NO data backup will be taken."
    }
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }

    # fail early on an unusable backup location, BEFORE the container goes down
    if ($script:MakeBackup) {
        Write-Host "-> Creating the backup directory $($script:BackupDir)"
        try {
            New-Item -ItemType Directory -Force -Path $script:BackupDir -ErrorAction Stop | Out-Null
        } catch {
            Die "cannot create the backup directory $($script:BackupDir): $($_.Exception.Message)"
        }
    }

    if ($script:WillRefresh) {
        # on failure this keeps the old compose file and prints manual instructions
        Invoke-ComposeRefresh | Out-Null
        if ($script:TargetTag) { $newRef = "$($script:ImageName):$($script:TargetTag)" }
    }

    $script:LastBackup = ""
    Write-Host "-> Stopping the container (compose stop - flushes the SQLite WAL)"
    Invoke-Compose stop
    if ($LASTEXITCODE -ne 0) {
        Die "could not stop the Apace stack - run: cd $($script:ApaceDir); $(Get-ComposeDisplayName) stop  (then re-run this script)"
    }

    if ($script:MakeBackup) {
        if (-not (Backup-PersistentRoot)) {
            Write-WarnNote "backup failed - starting the previous container again"
            Invoke-Compose up -d
            Die "backup failed; Apace was started again and nothing was updated"
        }
        Write-Host ""
    } else {
        Write-WarnNote "Skipping the backup (-NoBackup)."
    }

    if ($newRef -ne $script:ImageRef) {
        Set-ImageReference $newRef
    }

    Write-Host "-> Pulling the new image"
    Invoke-Compose pull
    if ($LASTEXITCODE -ne 0) {
        Write-WarnNote "pull failed - starting the previous image again"
        Invoke-Compose up -d
        Die "could not pull $newRef (registry/network problem?); the previous container is running again"
    }

    Write-Host "-> Starting the updated container (compose up -d)"
    Invoke-Compose up -d
    if ($LASTEXITCODE -ne 0) {
        Die "could not start the updated container (see the output above)"
    }

    if (Test-PanelHealthy $script:PanelPort) {
        $newDigest = ""
        $digests = @(& docker image inspect -f '{{index .RepoDigests 0}}' $script:ImageRef 2>$null)
        if (($digests.Count -gt 0) -and ("$($digests[0])" -match '@sha256:')) { $newDigest = "$($digests[0])" }
        Write-UpdateState $script:RollbackRef $script:InitialImageTag $script:TargetTag
        Write-Host ""
        Write-Ok "Apace updated!"
        Write-Head "  Previous image: $prevShown"
        $newShown = $newDigest
        if (-not $newShown) { $newShown = $script:ImageRef }
        Write-Head "  New image:      $newShown"
        if ($script:TargetTag) { Write-Head "  Release tag:    $($script:TargetTag)" }
        $backupShown = $script:LastBackup
        if (-not $backupShown) { $backupShown = "none" }
        Write-Head "  Backup:         $backupShown"
        if ($script:LastBackup) {
            Write-Host "  If anything looks wrong: .\update.ps1 -Dir $($script:ApaceDir) -Rollback"
        }
    } else {
        Write-FailureReport
    }
}

# ---------------------------------------------------------------------------
# Docker mode: backup-only / rollback
# ---------------------------------------------------------------------------

function Start-DockerBackupOnly {
    Repair-Layout
    Write-Host "About to archive $($script:Persistent) (data only - no image change, no downtime)."
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }
    if (-not (Backup-PersistentRoot)) {
        Die "the backup failed - nothing was changed"
    }
    Write-Host ""
    Write-Ok "Backup done: $($script:LastBackup)"
}

function Start-DockerRollback {
    if (-not (Test-Path -LiteralPath $script:StateFile -PathType Leaf)) {
        Die "no $($script:StateFile) - this install has no recorded update to roll back (run an update first)"
    }
    $state = Read-UpdateState
    $prev = ""
    if ($state) { $prev = "$($state.previousDigest)" }
    if (-not $prev) {
        Die "$($script:StateFile) has no previousDigest - cannot roll back"
    }
    $tarball = ""
    if ($script:RestoreBackup) { $tarball = Resolve-BackupPath $script:RestoreBackup }

    Repair-Layout
    Write-Head "Rolling the image back to: $prev"
    if ($tarball) {
        Write-Head "Also restoring data backup: $tarball"
        Write-WarnNote "!! Restoring older app.db / live.db / earth.db is one-way: the EF Core migrations"
        Write-WarnNote "!! the newer version already applied cannot be undone by the app. The supported"
        Write-WarnNote "!! combination is older data + older image - exactly what this does - but any"
        Write-WarnNote "!! accounts or buildplates created since that backup are lost."
    }
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }

    Write-Host "-> Stopping the container (compose stop - flushes the SQLite WAL)"
    Invoke-Compose stop
    if ($LASTEXITCODE -ne 0) {
        Die "could not stop the Apace stack - run: cd $($script:ApaceDir); $(Get-ComposeDisplayName) stop  (then re-run this script)"
    }

    if ($tarball) {
        Restore-DataBackup $tarball $script:Persistent
    }

    & docker image inspect "$prev" 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "-> $prev is not in the local docker cache - pulling it"
        & docker pull "$prev"
        if ($LASTEXITCODE -ne 0) {
            Die "cannot obtain $prev (gone from the registry and not cached locally)"
        }
    }

    Set-ImageReference $prev

    Write-Host "-> Starting the rolled-back container (compose up -d)"
    # pull_policy is "always"; for a digest ref that pull returns the identical
    # image, so an old release stays old. If the digest vanished from the
    # registry, run with --pull never and use the cached copy instead.
    Invoke-Compose up -d --pull never
    if ($LASTEXITCODE -ne 0) {
        Write-WarnNote "compose rejected --pull never (older docker-compose?) - retrying with the default pull policy"
        Invoke-Compose up -d
        if ($LASTEXITCODE -ne 0) {
            Die "could not start the rolled-back container (see the output above)"
        }
    }

    if (Test-PanelHealthy $script:PanelPort) {
        Write-Host ""
        Write-Ok "Rolled back to $prev"
        Write-Host "  The image line in $($script:ComposeFile) now points at the old digest; a later"
        Write-Host "  update.ps1 run (or -Tag <version>) moves it back to a normal tag."
    } else {
        Write-FailureReport
    }
}

# ---------------------------------------------------------------------------
# Bare-metal mode (no Docker): release zip over the install root
# ---------------------------------------------------------------------------

function Find-ApaceProcesses {
    $script:RunningProcs = @(Get-Process -Name $script:ProcessNames -ErrorAction SilentlyContinue)
    $script:RunningJava = @()
    try {
        $script:RunningJava = @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and ("$($_.CommandLine)" -match 'apace|fountain') })
    } catch {
        $script:RunningJava = @()
    }
}

function Stop-ApaceProcesses {
    Find-ApaceProcesses
    if (($script:RunningProcs.Count -eq 0) -and ($script:RunningJava.Count -eq 0)) {
        Write-Ok "Apace is not running."
        return
    }
    Write-WarnNote "These Apace processes are still running:"
    foreach ($p in $script:RunningProcs) { Write-Host "  PID $($p.Id)  $($p.ProcessName)" }
    foreach ($p in $script:RunningJava) { Write-Host "  PID $($p.ProcessId)  java" }
    Write-WarnNote "Updating while they run would fail on locked files."
    if (-not (Confirm-Step "Stop them now?")) {
        Die "aborted - close Apace and re-run this script"
    }
    foreach ($p in $script:RunningProcs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
    foreach ($p in $script:RunningJava) { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 2
    Find-ApaceProcesses
    if (($script:RunningProcs.Count -gt 0) -or ($script:RunningJava.Count -gt 0)) {
        Die "Apace is still running (files are locked) - close the panel (and its servers) and re-run this script"
    }
}

# Extracts only the code entries of a release zip over the install root -
# runtime data, config files and logs are never touched and nothing is deleted.
function Expand-ReleaseZip([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    if ($null -eq $archive) { Die "could not open the downloaded zip" }
    $items = @()
    foreach ($entry in $archive.Entries) {
        if (-not $entry.Name) { continue }   # directory entries
        $rel = $entry.FullName -replace '\\', '/'
        $sep = $rel.IndexOf("/")
        if ($sep -lt 0) {
            if ($script:ZipAllowedRootFiles -notcontains $rel) { continue }
        } else {
            $rootName = $rel.Substring(0, $sep)
            if ($script:ZipAllowedRoots -notcontains $rootName) { continue }
        }
        $skip = $false
        foreach ($prefix in $script:ZipExcludedPrefixes) {
            if ($rel.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { $skip = $true; break }
        }
        if (-not $skip) {
            if ($script:ZipExcludedFiles -icontains $rel) { $skip = $true }
        }
        if ($skip) { continue }
        $items += , @($entry, $rel)
    }
    if ($items.Count -eq 0) {
        $archive.Dispose()
        Die "the release zip contains no installable files"
    }
    # Nothing is modified until every existing target proved writable - a locked
    # file (Windows: the running panel itself) aborts the whole run.
    $locked = @()
    foreach ($item in $items) {
        $target = Join-Path $script:ApaceDir $item[1]
        if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { continue }
        try {
            $fs = [System.IO.File]::Open($target, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $fs.Close()
        } catch {
            $locked += $item[1]
        }
    }
    if ($locked.Count -gt 0) {
        $archive.Dispose()
        Die "$($locked.Count) file(s) are locked by a running Apace process (e.g. '$($locked[0])') - close the panel and re-run this script"
    }
    Write-Host "-> Extracting $($items.Count) files over $($script:ApaceDir)"
    Write-Host "   (never touched: launcher\Data, launcher\logs, launcher\config.json, data\, logs\,"
    Write-Host "    staticdata\resourcepacks, launcher\persistent_fabric, components\api_config.json)"
    $done = 0
    foreach ($item in $items) {
        $target = Join-Path $script:ApaceDir $item[1]
        $dir = Split-Path $target -Parent
        if ($dir -and (-not (Test-Path -LiteralPath $dir -PathType Container))) {
            New-Item -ItemType Directory -Force -Path $dir -ErrorAction SilentlyContinue | Out-Null
        }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($item[0], $target, $true)
        $done = $done + 1
        if (($done % 250) -eq 0) { Write-Host "   $done/$($items.Count) files extracted" }
    }
    $archive.Dispose()
    Write-Host "-> Extracted $done files"
}

function Start-BareMetalUpdate {
    if (($script:Tag -eq "main") -or ($script:Tag -eq "dev")) {
        Die "no release zips are published for the '$($script:Tag)' channel - use -Tag v0.1.3, -Tag latest, or nothing (the newest release)"
    }
    if (-not (Resolve-TargetTag)) {
        Die "could not resolve the newest release via the GitHub API (offline or rate-limited)"
    }
    $arch = Get-WindowsArch
    if (-not $arch) { Die "could not detect the CPU architecture (supported: x64, arm64)" }
    $assetName = "Apace-win-$arch.zip"
    Write-Host "-> Resolving release $($script:TargetTag) ($assetName)"
    $release = $null
    try {
        $release = Invoke-RestMethod -Uri "$script:RepoApi/releases/tags/$($script:TargetTag)" -Headers @{ "User-Agent" = $script:RequestUserAgent } -TimeoutSec 30 -ErrorAction Stop
    } catch {
        Die "could not fetch release $($script:TargetTag) from the GitHub API (offline, rate-limited, or the tag does not exist)"
    }
    $asset = @($release.assets | Where-Object { "$($_.name)" -eq $assetName })[0]
    if (-not $asset) {
        $available = (@($release.assets | ForEach-Object { "$($_.name)" }) -join ", ")
        Die "no $assetName asset on release $($release.tag_name) (available: $available)"
    }

    $script:BackupDir = Join-Path (Split-Path $script:ApaceDir -Parent) "apace-backups"
    $sizeMb = [math]::Round($asset.size / 1MB, 1)
    Write-Head "Install root:    $($script:ApaceDir) (bare-metal, no Docker)"
    Write-Head "Release:         $($release.tag_name)  ($assetName, $sizeMb MB)"
    Write-Host ""
    Write-Host "About to: stop the running Apace processes, back up the data in $($script:ApaceDir),"
    Write-Host "download the release zip and extract the program files over the install root."
    Write-Host "Never touched: launcher\Data, launcher\config.json, data\, logs\, staticdata\resourcepacks,"
    Write-Host "launcher\persistent_fabric, components\api_config.json (accounts, worlds, login secrets)."
    if (-not $script:MakeBackup) {
        Write-WarnNote "!! -NoBackup given: NO data backup will be taken."
    }
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }

    if ($script:MakeBackup) {
        Write-Host "-> Creating the backup directory $($script:BackupDir)"
        try {
            New-Item -ItemType Directory -Force -Path $script:BackupDir -ErrorAction Stop | Out-Null
        } catch {
            Die "cannot create the backup directory $($script:BackupDir): $($_.Exception.Message)"
        }
    }

    Stop-ApaceProcesses

    if ($script:MakeBackup) {
        if (-not (Backup-InstallDir)) {
            Die "the backup failed; nothing was updated (restart Apace with: pwsh $($script:ApaceDir)\run.ps1)"
        }
        Write-Host ""
    } else {
        Write-WarnNote "Skipping the backup (-NoBackup)."
    }

    $zipPath = Join-Path ([System.IO.Path]::GetTempPath()) ("apace-update-$($script:TargetTag).zip")
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    Write-Host "-> Downloading $($asset.browser_download_url)"
    if (-not (Get-RemoteFile "$($asset.browser_download_url)" $zipPath 3600 -ShowProgress)) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        Die "the download failed - nothing was updated"
    }
    $zipLen = (Get-Item -LiteralPath $zipPath).Length
    if (($asset.size -gt 0) -and ($zipLen -lt $asset.size)) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        Die "the downloaded zip is too small ($zipLen bytes, expected $($asset.size)) - nothing was extracted"
    }
    try {
        Expand-ReleaseZip $zipPath
    } finally {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    }

    Write-UpdateState "" "release" $script:TargetTag
    Write-Host ""
    Write-Ok "Apace updated!"
    Write-Head "  Release: $($release.tag_name)"
    $backupShown = $script:LastBackup
    if (-not $backupShown) { $backupShown = "none" }
    Write-Head "  Backup:  $backupShown"
    Write-Host ""
    Write-Host "  Restart the panel to run the new build:"
    Write-Host "    pwsh $($script:ApaceDir)\run.ps1      (or run_launcher.ps1)"
    Write-Host "  Panel: http://localhost:5000"
    if ($script:LastBackup) {
        Write-Host "  If anything looks wrong: .\update.ps1 -Dir $($script:ApaceDir) -Rollback -RestoreBackup latest"
    }
}

function Start-BareMetalBackupOnly {
    $script:BackupDir = Join-Path (Split-Path $script:ApaceDir -Parent) "apace-backups"
    Write-Host "About to archive the data in $($script:ApaceDir) (no file is changed)."
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }
    if (-not (Backup-InstallDir)) {
        Die "the backup failed - nothing was changed"
    }
    Write-Host ""
    Write-Ok "Backup done: $($script:LastBackup)"
}

function Start-BareMetalRollback {
    if (-not (Test-Path -LiteralPath $script:StateFile -PathType Leaf)) {
        Die "no $($script:StateFile) - this install has no recorded update to roll back (run an update first)"
    }
    if (-not $script:RestoreBackup) {
        Die "a bare-metal install records no previous build to restore - re-run with -Tag <older-version> to pin an older release, or add -RestoreBackup latest (or a file path) to restore the pre-update data"
    }
    $script:BackupDir = Join-Path (Split-Path $script:ApaceDir -Parent) "apace-backups"
    $tarball = Resolve-BackupPath $script:RestoreBackup
    Write-Head "Restoring the data backup: $tarball"
    Write-WarnNote "!! Restoring older databases is one-way: the migrations the newer version already"
    Write-WarnNote "!! applied cannot be undone by the app; anything created since that backup is lost."
    Write-WarnNote "!! The program files stay as they are - the supported combination is older data"
    Write-WarnNote "!! plus the matching older build (pin it later with -Tag <version>)."
    if (-not (Confirm-Step "Continue?")) {
        Write-Host "Aborted - nothing was changed."
        return
    }
    Stop-ApaceProcesses
    Restore-DataBackup $tarball $script:ApaceDir
    Write-Host ""
    Write-Ok "Data restored from $tarball"
    Write-Host "  Restart the panel: pwsh $($script:ApaceDir)\run.ps1"
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

Write-Head "=== Apace Updater ==="
Write-Host ""

if (-not (Test-Path -LiteralPath $script:ApaceDir -PathType Container)) {
    Die "install dir $($script:ApaceDir) does not exist (run the installer first: iwr $($script:RepoRaw)/main/install.ps1 | iex)"
}

$script:ComposeFile = Find-ComposeFile $script:ApaceDir
$script:StateFile = Join-Path $script:ApaceDir ".apace-update.json"

if ($script:ComposeFile) {
    $script:IsDocker = $true
    if (-not $script:BackupOnly) { Resolve-ComposeCommand }
    Read-Compose
    $script:InitialImageTag = $script:ImageTag
    Write-Head "Install root:    $($script:ApaceDir)"
    Write-Head "Compose file:    $($script:ComposeFile)"
    Write-Head "Image:           $($script:ImageRef)"
    Write-Head "Persistent data: $($script:Persistent)"
    Write-Head "Panel port:      $($script:PanelPort)"
    Write-Host ""
    if ($script:BackupOnly) { Start-DockerBackupOnly }
    elseif ($script:Rollback) { Start-DockerRollback }
    else { Start-DockerUpdate }
} else {
    $bare = $false
    foreach ($marker in @(@("launcher", "Container"), @("components", "Container"), @("run_launcher.ps1", "Leaf"))) {
        if (Test-Path -LiteralPath (Join-Path $script:ApaceDir $marker[0]) -PathType $marker[1]) { $bare = $true; break }
    }
    if (-not $bare) {
        Die "no docker-compose.yml and no bare-metal Apace install in $($script:ApaceDir) - run the installer first: iwr $($script:RepoRaw)/main/install.ps1 | iex"
    }
    Write-Head "Install root:    $($script:ApaceDir) (bare-metal, no Docker)"
    Write-Host ""
    if ($script:BackupOnly) { Start-BareMetalBackupOnly }
    elseif ($script:Rollback) { Start-BareMetalRollback }
    else { Start-BareMetalUpdate }
}
