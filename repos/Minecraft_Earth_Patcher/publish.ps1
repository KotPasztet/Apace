#!/usr/bin/env pwsh
param (
    [ValidateSet('Debug', 'Release')]
    [string]$config = 'Release',

    [string[]]$frameworks = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),

    [bool]$publishCli = $false
)

$ErrorActionPreference = 'Stop'

# Setup Paths
$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) { $scriptRoot = Get-Location }

$propsPath = Join-Path $scriptRoot "Directory.Build.props"
$version = "1.0.0" # Fallback version

if (Test-Path $propsPath) {
    [xml]$propsXml = Get-Content $propsPath
    # Try <Version> first, then fallback to <VersionPrefix>
    $parsedVersion = $propsXml.Project.PropertyGroup.Version
    if ([string]::IsNullOrWhiteSpace($parsedVersion)) {
        $parsedVersion = $propsXml.Project.PropertyGroup.VersionPrefix
    }
    
    if (-not [string]::IsNullOrWhiteSpace($parsedVersion)) {
        $version = $parsedVersion.Trim()
    }
}
else {
    Write-Warning "Directory.Build.props not found at $propsPath. Defaulting to version $version"
}

$projects = @(
    @{ Name = "UI.Desktop"; DisplayName = "MCEPatcher_UI" }
)

if ($publishCli) {
    $projects += @{ Name = "Cli"; DisplayName = "MCEPatcher_CLI" }
}

# Create output directory
$outDir = Join-Path $scriptRoot "build/$config"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

# Load the compression assembly (Required for Windows PowerShell 5.1; already loaded in PS Core)
Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($proj in $projects) {
    $projName = $proj.Name
    $displayName = $proj.DisplayName
    $csprojPath = Join-Path $scriptRoot "src/$projName/MCEPatcher.$projName.csproj"

    if (-not (Test-Path $csprojPath)) {
        Write-Error "Project file not found: $csprojPath"
        continue
    }

    foreach ($fx in $frameworks) {
        Write-Host "Publishing $displayName for $fx ($config)..." -ForegroundColor Cyan

        $zipFileName = "$displayName-$fx-$version.zip"
        $zipFilePath = Join-Path $outDir $zipFileName

        # Create a unique temporary directory for the raw publish output
        $tempPubDir = Join-Path $scriptRoot "temp_publish_$([guid]::NewGuid().ToString().Substring(0,8))"

        try {
            $publishArgs = @(
                "publish", "`"$csprojPath`"",
                "--configuration", $config,
                "--runtime", $fx,
                "--output", "`"$tempPubDir`"",
                "--self-contained",
                "-p:PublishSingleFile=true",
                "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:PublishTrimmed=false",
                "-p:DebugType=None",
                "-p:DebugSymbols=false"
            )
            
            $process = Start-Process dotnet -ArgumentList $publishArgs -NoNewWindow -Wait -PassThru
            if ($process.ExitCode -ne 0) {
                Write-Error "dotnet publish failed for $projName ($fx). Exit Code: $($process.ExitCode)"
                continue
            }

            # Delete the zip if it already exists from a previous run
            if (Test-Path $zipFilePath) {
                Remove-Item $zipFilePath -Force
            }

            Write-Host "Zipping artifacts to $zipFileName..."
            [System.IO.Compression.ZipFile]::CreateFromDirectory($tempPubDir, $zipFilePath)
            
            Write-Host "Success: $zipFilePath`n" -ForegroundColor Green
        }
        finally {
            if (Test-Path $tempPubDir) {
                Remove-Item -Path $tempPubDir -Recurse -Force
            }
        }
    }
}

Write-Host "All publish tasks completed successfully!" -ForegroundColor Green