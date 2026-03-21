# ViennaDotNet
An unofficial port of [Vienna](https://github.com/Project-Genoa/Vienna) to .NET

> [!WARNING]
> **Work In Progress (WIP):** This project is currently under active development. Some features may be incomplete, and you may encounter bugs or breaking changes. Use at your own risk.

## New Features
In addition to the original Vienna feature set, this port adds:
- shop
- map
- admin panel

## Setup

- make sure you have the [.net10.0 sdk](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) installed
- run "publish.ps1"
- go to build/{configuration}/{profile}
- run "run_launcher.ps1"
- create an account and login
- in "Server Options" set "Network/IPv4 Address" to your PC's address and either dissable "Map/Enable Tile Rendering" or set the "Map/MapTiler API Key" - can be found [here](https://cloud.maptiler.com/account/keys/) when logged in
- in "Server Status" click "Start"
- accept the eula when prompted in the "Launcher" log
- download and move the resourcepack as described in the "Launcher" log
