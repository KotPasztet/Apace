# Installation

## Quick Start (Docker — recommended)

The easiest way to run Apace. Works on Windows, Linux, and macOS.

### 1. Install Docker

- **Windows:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)
- **macOS:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/mac-install/)
- **Linux:** `curl -fsSL https://get.docker.com | sh` (then `sudo usermod -aG docker $USER`, log out and back in)

### 2. Run the auto-installer

```bash
# Linux/macOS
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash

# Windows (PowerShell as Administrator)
iwr https://raw.githubusercontent.com/KotPasztet/Apace/main/install.ps1 | iex
```

After install: open http://localhost:5000, create an account, set your IP in Server Options, click Start.

---

## Download (no Docker)

Pre-built binaries for each platform. Requires [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) and Java 17.

1. Go to [GitHub Releases](https://github.com/KotPasztet/Apace/releases/latest)
2. Download the zip for your platform:
   - `Apace-win-x64.zip` — Windows 64-bit
   - `Apace-linux-x64.zip` — Linux 64-bit
   - `Apace-linux-arm64.zip` — Raspberry Pi 64-bit
3. Extract the zip
4. Run: `pwsh ./run_launcher.ps1` (or `./run_launcher.ps1` on Linux/macOS)
5. Open http://localhost:5000

---

## Auto-install Script (Linux)

One command, fully automatic:

```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install.sh | bash
```

This script: installs Docker if needed, downloads the compose file, creates directories, and starts Apace.

---

## Manual Installation (without Docker)

You need: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Java 17, and PowerShell 7+.

```bash
# 1. Clone the repo
git clone --recurse-submodules https://github.com/KotPasztet/Apace.git
cd Apace

# 2. Build
# Linux/macOS:
pwsh ./publish.ps1 -profiles framework-dependent-linux-x64
# Linux ARM64 (Raspberry Pi):
pwsh ./publish.ps1 -profiles framework-dependent-linux-arm64
# Windows:
pwsh ./publish.ps1 -profiles framework-dependent-win-x64

# 3. Run
cd build/Release/framework-dependent-*/  # go to your build output folder
pwsh ./run_launcher.ps1

# 4. Open http://localhost:5000
# 5. First account = admin
# 6. Set your PC's IP in Server Options → Start
```

---

## Client Setup (phone)

The patcher is built into the panel — **Patcher** page. No separate download.

1. Open the **Patcher** page and pick **Android (APK)** or **iOS (IPA)**.
2. Choose a mode:
   - **Auto** — server addresses are derived from your Apace configuration; just drop the file
   - **Simple** — Auto defaults, but you can override the server addresses
   - **Advanced** — every option, like the desktop patcher
3. Drop in the **original** Minecraft Earth APK/IPA (dumped from your device) and start the patch
4. Install the patched file on your phone, open the app, and sign in with your panel account

The client connects to the **API Port** from Server Options on your PC's IP — fresh installs
default to `1808` (matching the Docker port mapping; the in-code fallback is `8080`) —
make sure that port is reachable from the phone (and published if you run Apace in Docker).

---

## Finding Your IP Address

- **Windows:** `ipconfig` → look for `IPv4 Address` under your network adapter
- **Linux:** `hostname -I` or `ip addr show`
- **macOS:** `ifconfig | grep inet`

Your IP usually looks like `192.168.X.XXX`.

---

## Common Issues

| Problem | Fix |
|---------|-----|
| "Cannot connect to the network" | Server not running, wrong IP, or firewall blocking |
| App closes when joining buildplate | A buildplate dimension is created in ~1 s by the persistent Fabric server — just join the same buildplate again and it reappears |
| "Start Server" button greyed out | First account only gets admin. Log into the first account, or delete `launcher/Data/app.db` to reset |
| Resource pack download fails | The file is downloaded automatically on first run. If it fails, download manually from the Internet Archive link shown in the logs |
| Port not reachable | Make sure ports 5000, 1808, and 19132/udp are allowed through your firewall |
