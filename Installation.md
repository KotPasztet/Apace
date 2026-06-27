# Installation

## Quick Start (Docker — recommended)

The easiest way to run Apace. Works on Windows, Linux, and macOS.

### 1. Install Docker

- **Windows:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/windows-install/)
- **macOS:** [Docker Desktop](https://docs.docker.com/desktop/setup/install/mac-install/)
- **Linux:** `curl -fsSL https://get.docker.com | sh` (then `sudo usermod -aG docker $USER`, log out and back in)

### 2. Create a folder for Apace

```bash
mkdir apace && cd apace
```

### 3. Download the compose file

```bash
# Linux/macOS
curl -O https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml

# Windows (PowerShell)
Invoke-WebRequest -Uri https://raw.githubusercontent.com/KotPasztet/Apace/main/docker-compose.yml -OutFile docker-compose.yml
```

### 4. Create persistent directories

```bash
# Linux/macOS
sudo mkdir -p /opt/apace-persistent/{launcher-data,launcher-logs,data,dataprotection-keys,resourcepacks,server-template-dir,logs}
echo '{}' | sudo tee /opt/apace-persistent/config.json > /dev/null

# Windows (PowerShell as Administrator)
New-Item -ItemType Directory -Force -Path C:\apace-persistent\launcher-data, C:\apace-persistent\launcher-logs, C:\apace-persistent\data, C:\apace-persistent\dataprotection-keys, C:\apace-persistent\resourcepacks, C:\apace-persistent\server-template-dir, C:\apace-persistent\logs
'{}' | Out-File -FilePath C:\apace-persistent\config.json -Encoding utf8
```

> On Windows, also update `docker-compose.yml` — replace `/opt/apace-persistent/` with `C:/apace-persistent/` in all `volumes:` paths.

### 5. Start Apace

```bash
docker compose up -d
```

The image is pulled automatically from GitHub Container Registry. The server starts at:

- **Panel:** http://localhost:5000
- **API:** http://localhost:1808
- **Minecraft UDP:** port 19132

### 6. Configure and start

1. Open http://localhost:5000 in your browser
2. Create an account (first account gets admin automatically)
3. Go to **Server Options** → set your PC's IP address under "Network/IPv4 Address"
4. Go to **Server Status** → click **Start All**
5. Accept the Minecraft EULA when the button appears in the panel

### Updating

```bash
docker compose pull && docker compose up -d
```

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

### Android — MCE Patcher (recommended)

1. Download [MCE Patcher](https://github.com/Earth-Restored/Minecraft_Earth_Patcher/releases) (UI version)
2. Obtain a Minecraft Earth APK (dump from your phone or find online)
3. Open the patcher, select the APK
4. Set **Locator Hostname/IP** to `YOUR_PC_IP:1808` (e.g. `192.168.1.5:1808`)
5. Click **Patch** → install the patched APK on your phone
6. Open the app and sign in with your panel account

### Android — Project Earth Patcher

1. Install [Project Earth Patcher APK](https://archive.org/download/dev.projectearth.patcher-1.0/dev.projectearth.patcher-1.0.apk)
2. Have a legal copy of Minecraft Earth installed
3. Open patcher → Settings → set Locator Server to `http://YOUR_PC_IP:1808`
4. Go back and start patching

### iOS

Use [ProjectEarthiOSPatcher](https://github.com/catdogmat/ProjectEarthiOSPatcher). Not officially supported.

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
| App closes when joining buildplate | Server cold-starting — try joining the same buildplate again immediately |
| "Start Server" button greyed out | First account only gets admin. Log into the first account, or delete `launcher/Data/app.db` to reset |
| Resource pack download fails | The file is downloaded automatically on first run. If it fails, download manually from the Internet Archive link shown in the logs |
| Port not reachable | Make sure ports 1808, 19132/udp, and 5000 are allowed through your firewall |
