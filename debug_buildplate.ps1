$eventBusPort = 5532
$objectStorePort = 5396
$apiPort = 8080

$testDir = [IO.Path]::GetFullPath("test")
$dataDir = "$testDir/data"

Copy-Item -Path "staticdata" -Destination "$testDir/staticdata" -Recurse -Force

New-Item "$dataDir/earth.db" -type file -Force
New-Item "$dataDir/live.db" -type file -Force

$services = @(
    @{
        Name = "EventBus"
        Cmd  = "dotnet run --project ./src/ViennaDotNet.EventBus.Server -- --port=$eventBusPort"
        Wait = $false
    },
    @{
        Name = "ObjectStore"
        Cmd  = "dotnet run --project ./src/ViennaDotNet.ObjectStore.Server -- --port=$objectStorePort --dataDir='$dataDir/object_store'"
        Wait = $true
    },
    @{
        Name = "ApiServer"
        Cmd  = "dotnet run --project ./src/ViennaDotNet.ApiServer -- --port=$apiPort --earth-db='$dataDir/earth.db' --live-db='$dataDir/live.db' --eventbus=localhost:$eventBusPort --objectstore=localhost:$objectStorePort --dir='$testDir/staticdata'"
        Wait = $false
    }
)

foreach ($service in $services) {
    Write-Host "Launching $($service.Name)..." -ForegroundColor Cyan
    
    if ($IsWindows) {
        Start-Process pwsh -ArgumentList "-NoExit", "-Command", $service.Cmd
    }
    elseif ($IsLinux) {
        Start-Process x-terminal-emulator -ArgumentList "-e", "pwsh -NoExit -Command $($service.Cmd)"
    }
    elseif ($IsMacOS) {
        $appleScript = "tell application `"Terminal`" to do script `"$($service.Cmd)`""
        Start-Process osascript -ArgumentList "-e", "'$appleScript'"
    }

    if ($service.Wait -eq $true) {
        Write-Host "$($service.Name) launched. Press Enter to continue to the next service..." -ForegroundColor Magenta
        Read-Host
    }
}