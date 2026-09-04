# Playing from anywhere: Tailscale

Apace has no built-in accounts-on-the-internet mode — the patched phone client talks
straight to your server over your LAN. The usual fix is port forwarding on the router.
[Tailscale](https://tailscale.com) avoids that: it builds a private WireGuard network
(a *tailnet*) between your devices, so your phone reaches the server from mobile data,
a friend's flat, or anywhere else — with nothing exposed to the public internet.

The idea in one line: **install Tailscale on the server host and on the phone, then
point the patched client at the server's Tailscale name instead of its LAN IP.**

## Which ports matter

Apace publishes four ports, but only two are client-facing:

| Port | Protocol | Purpose | Needed over Tailscale? |
|---|---|---|---|
| 1808 | TCP | Game API (the **API Port** in Server Options) — everything the patched client calls, including map tiles | **Yes** |
| 19132 | UDP | Bedrock — the single port every player connects to (**Buildplate Bridge Port**) | **Yes** |
| 5000 | TCP | Web panel (admin browser UI) | Optional |
| 5532 | TCP | Event bus (internal orchestrator ↔ bridge traffic) | No |

The ObjectStore (default 5396) and the Fabric server (25565) are never published — the
ObjectStore only listens on loopback and its objects reach the phone through the game
API's tile endpoints, so there is nothing extra to open.

`1808` is the API Port written to `config.json` by the installers (the in-code fallback
is `8080`). The Bedrock port is `19132` unless you set `BRIDGE_PORT` in
`docker-compose.yml` — if you did, that value must match **Buildplate Bridge Port** in
Server Options.

## Option A — Tailscale on the Docker host (recommended)

Works unchanged for the Docker install: the container keeps publishing its ports on the
host, and Tailscale simply makes the host reachable from your tailnet.

1. **Install Tailscale on the server host** (not inside the container):

   ```bash
   curl -fsSL https://tailscale.com/install.sh | sh
   sudo tailscale up
   ```

   `tailscale up` prints a login link — open it and sign in. Or use the helper script
   in the repo: `sudo ./scripts/tailscale-setup.sh`.

2. **Install the Tailscale app on the phone** and sign in to the *same* tailnet.

3. **Find the server's Tailscale name:**

   ```bash
   tailscale ip -4      # 100.x.y.z — always reachable, on any network
   tailscale status     # MagicDNS name, e.g. apace.tail1234.ts.net
   ```

   Prefer the MagicDNS name — the `100.x.y.z` address can change if you reinstall.

4. **Point Apace at the Tailscale address.** In the panel open **Server Options** and
   set **PC Ipv4 Address or Hostname (without port)** to the Tailscale name or IP, e.g.
   `apace.tail1234.ts.net` or `100.101.102.103`. Hostnames are accepted, not just IPs.

   This one setting serves both clients: the patcher builds its server address as
   `<that host>:<API Port>`, and the buildplate launcher advertises the same host (with
   the bridge port) to players joining over Bedrock.

5. **Re-patch the phone client.** The server address is baked into the APK/IPA at patch
   time, so open the **Patcher** page again and patch with **Auto** — it picks up the new
   address. The client then talks to:

   ```
   http://apace.tail1234.ts.net:1808
   ```

   and reaches Bedrock on `apace.tail1234.ts.net:19132`.

Because the Tailscale address works at home *and* away, a client patched this way no
longer cares which network the phone is on.

Nothing else to forward: no router ports, no firewall holes on the internet side. Inside
the tailnet the only thing to check is the ACL.

### ACLs

By default a tailnet only contains devices you approved, so access is already limited to
your own machines. To tighten it further (this example allows only named devices to reach
the game ports), edit the ACL in the Tailscale admin console:

```jsonc
{
  "acls": [
    {
      // Only the phone may talk to the Apace server, and only on the game ports.
      "action": "accept",
      "src":    ["pixel-8"],
      "dst":    ["apace:1808,19132"]
    }
  ]
}
```

Device names are the ones shown by `tailscale status`.

## Option B — Bare-metal install (no Docker)

Same idea, no container in between — Tailscale runs natively next to the launcher.

- **Linux:** `curl -fsSL https://tailscale.com/install.sh | sh && sudo tailscale up`
- **Windows:** install the [Tailscale Windows client](https://tailscale.com/download)
  (or `winget install Tailscale.Tailscale`) and connect from the tray icon.
- **macOS:** Tailscale from the App Store.

Then follow steps 2–5 above. One extra note for Linux bare-metal: the panel is only
guaranteed to listen on all interfaces when `ASPNETCORE_URLS=http://0.0.0.0:5000` is set
(the Docker image and compose file set it for you). If the panel is unreachable over
Tailscale but the game works, export that variable before starting the launcher — the
game API itself already binds all interfaces.

## Option C — Tailscale as a compose sidecar (optional, advanced)

Instead of installing anything on the host you can run Tailscale as a second container
and put Apace behind it. Only worth it if you cannot touch the host (managed Docker
hosts, NAS containers) — otherwise Option A is simpler and keeps the panel reachable the
normal way while you're home.

```yaml
services:
  tailscale:
    image: tailscale/tailscale:latest
    hostname: apace
    environment:
      - TS_AUTHKEY=tskey-auth-...   # from the admin console → Keys
    volumes:
      - ./tailscale-state:/var/lib/tailscale
    devices:
      - /dev/net/tun:/dev/net/tun
    cap_add:
      - NET_ADMIN

  apace:
    image: ghcr.io/kotpasztet/apace:main
    network_mode: "service:tailscale"   # Apace shares the Tailscale network stack
```

With `network_mode: "service:tailscale"` the port mappings move to the Tailscale
container (drop them from `apace`), and the server is reachable at the sidecar's
Tailscale name on the same ports. Note the auth key is a secret — keep it out of git.

## Troubleshooting

| Problem | Fix |
|---------|-----|
| Client stuck on "Cannot connect to the network" | Is the phone actually in the tailnet? Run `tailscale status` on the server — the phone must be listed. On mobile data the Tailscale app must be running (iOS can suspend VPNs; toggle it or open the app once). |
| Panel or API reachable, joining a buildplate fails | Bedrock (UDP 19132, or your **Buildplate Bridge Port**) is being blocked. The API is TCP; the bridge is UDP — ACLs and `tailscale up` guard both, but some "VPN kill switch" phone settings block UDP. |
| Address the client uses doesn't match | **Server Options → PC Ipv4 Address or Hostname** must be the Tailscale name/IP, and the client must be re-patched after changing it (the address is baked in at patch time). Auto mode in the Patcher always uses the current value. |
| Wrong port | The API port in the URL must equal **API Port** in Server Options (1808 on fresh installs). If you changed it in Docker, the compose mapping must match too. |
| MagicDNS name doesn't resolve from the phone | Enable MagicDNS in the Tailscale admin console (DNS tab), or just use the `100.x.y.z` address. |
| Works at home, not away | The phone is dropping off the tailnet (see first row), or the server's Tailscale expired its login — `sudo tailscale up` to re-authenticate. |
