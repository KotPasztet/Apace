$serviceNames = @("ViennaDotNet.EventBus.Server", "ViennaDotNet.ObjectStore.Server", "ViennaDotNet.ApiServer")

Write-Host "--- Initiating Graceful Shutdown ---" -ForegroundColor Cyan

foreach ($name in $serviceNames) {
    Write-Host "Targeting: $name" -ForegroundColor Gray

    if ($IsWindows) {
        $query = "SELECT ProcessId, CommandLine FROM Win32_Process WHERE CommandLine LIKE '%$name%'"
        $winProcs = Get-CimInstance -Query $query

        foreach ($p in $winProcs) {
            Write-Host "Sending Close signal to PID: $($p.ProcessId)..." -ForegroundColor Yellow
            $procObj = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue
            if ($procObj) {
                $procObj.CloseMainWindow() | Out-Null
            }
        }
    }
    else {
        Write-Host "Sending SIGTERM to $name..." -ForegroundColor Yellow
        Start-Process "pkill" -ArgumentList "-15", "-f", "$name" -Wait
    }
}

Write-Host "--- Cleaning Up Terminal Windows ---" -ForegroundColor Cyan

if ($IsWindows) {
    $terminals = Get-CimInstance -Query "SELECT ProcessId FROM Win32_Process WHERE (Name = 'pwsh.exe' OR Name = 'powershell.exe') AND CommandLine LIKE '%ViennaDotNet%'"
    foreach ($t in $terminals) {
        $procObj = Get-Process -Id $t.ProcessId -ErrorAction SilentlyContinue
        if ($procObj) { $procObj.CloseMainWindow() | Out-Null }
    }
}
elseif ($IsMacOS) {
    $appleScript = @"
    tell application "Terminal"
        close (every window whose name contains "ViennaDotNet")
    end tell
"@
    Start-Process osascript -ArgumentList "-e", "'$appleScript'"
}
elseif ($IsLinux) {
    Start-Process "pkill" -ArgumentList "-15", "-f", "terminal.*ViennaDotNet"
}

Write-Host "Cleanup complete." -ForegroundColor Green