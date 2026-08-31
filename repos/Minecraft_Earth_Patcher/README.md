# Minecraft Earth Patcher

A tool to patch the api and login server used by a Minecraft Earth apk.

Patches from:

- [Project-Genoa/patches](https://github.com/Project-Genoa/patches)
- [Project-Earth-Team/Patches](https://github.com/Project-Earth-Team/Patches)

## Usage Instructions

1) Acquire a Minecraft Earth apk, such as by dumping in from you phone.
2) Run the patcher.
3) Select the downloaded APK file.
4) Enter the api server's IP address or hostname, including the port number.

### Configure Custom Login Server

If you are using a custom login server, choose the appropriate mode based on your network setup:

* **Option A: Multi-Domain Mode**
  * *Use this if:* You have a registered domain name, or you are using a VPN/DNS to map a domain to an IP address.
  * Enter your domain(s) and ports.
  * *Note:* You can use the exact same domain for all addresses.

* **Option B: Single-Domain Mode**
  * *Use this if:* You do *not* have a domain or DNS mapping.
  * Enter the login server's IP address or hostname, including the port.
  * *Note:* There are no disadvantages to using this method over Multi-Domain Mode.
