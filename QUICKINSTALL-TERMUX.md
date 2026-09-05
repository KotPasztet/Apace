# Quick Install (Termux / Android)

Run Apace on an Android phone with [Termux](https://f-droid.org/packages/com.termux/) — no Docker needed.
The installer puts a minimal Ubuntu inside [proot-distro](https://github.com/termux/proot-distro) and runs
Apace in it, because the prebuilt Linux binaries need glibc and Android only offers Bionic.

## Requirements

- [Termux](https://f-droid.org/en/packages/com.termux/) installed from **F-Droid** (the Play Store build is outdated)
- A 64-bit phone (arm64), ~3 GB free storage, ~3 GB free RAM
- The phone and every player's phone on the **same Wi-Fi network**

## Install

Inside Termux:
```bash
curl -sSL https://raw.githubusercontent.com/KotPasztet/Apace/main/install-termux.sh | bash
```

The installer installs `proot-distro` + a small Ubuntu with Java 17, downloads the newest
Apace release into `~/apace`, writes `launcher/config.json` (`ApiPort: 1808`) and creates the
data directories. Re-running the same command **updates** Apace: worlds, accounts and
`config.json` are kept.

## First run

```bash
cd ~/apace && ./run.sh
```

1. Open the panel: **http://localhost:5000**
2. Create an account — the first one becomes the admin.
3. **Server Options** → set the phone's Wi-Fi IP (`ifconfig wlan0`).
4. **Server Status** → click **Start All**, then accept the Minecraft EULA when the yellow banner appears.
5. Patch your original Minecraft Earth APK with the **Patcher** page — on a PC; the
   Android build tools it needs are x86-64-only, so patching on the phone itself may fail.
6. Install the patched APK on the player's phone and sign in with your panel account.

`run.sh` takes a Termux wake lock so the server survives the screen turning off; press
**Ctrl+C** to stop Apace (then `termux-wake-unlock` if you want to release the wake lock).

## Notes

- Everything Apace writes (worlds, accounts, logs) lives in `~/apace` — back that folder up.
- Android may still kill Termux in the background: disable battery optimization for Termux.
- proot adds overhead — expect a phone to host one or two players comfortably.

No-Docker install on a PC, Docker details and troubleshooting: [QUICKINSTALL.md](QUICKINSTALL.md).
