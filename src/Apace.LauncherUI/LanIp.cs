using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Apace.LauncherUI;

/// <summary>
/// Best-effort detection of this machine's LAN IPv4 address, used only for
/// defaults: the prefilled <see cref="Settings.IPv4"/> of fresh installs and
/// the one-time upgrade of the legacy "192.168.x.x" placeholder in
/// <see cref="Settings.LoadAsync"/>. A user-set address always wins — this
/// class never overwrites a configured value.
///
/// Detection order:
///  1. UDP "connect" to a public address: the OS picks the interface it would
///     route through without sending any packet — the most reliable pick on
///     desktops and inside the Android PRoot launcher alike.
///  2. First IPv4 unicast address of an Up, non-loopback, non-virtual
///     interface (docker/veth/br-/vEthernet/tun/tap are skipped — container
///     bridges and VPN adapters are never the address clients should use).
///  3. null when nothing was found (the caller falls back to a safe default).
///
/// Inside a docker container there is no meaningful "LAN IP" at all (the
/// container's own address is unreachable from client devices — the stock
/// compose file uses bridge networking with published ports), so detection
/// returns null there and the panel expects the address to be set manually.
///
/// Mobile mode: when the panel runs inside the Android launcher app
/// (<see cref="IsMobileLauncher"/>), the patched MCE client runs on the SAME
/// device as the server, so 127.0.0.1 is the only correct default — the
/// phone's Wi-Fi address changes between networks and its cellular address
/// is useless to a client on the same phone. DetectLanIPv4() therefore
/// returns null there too, and the callers fall back to localhost instead
/// of a LAN address.
/// </summary>
internal static class LanIp
{
    // The docker daemon always creates this marker inside containers; the
    // panel's SelfUpdateService uses the same file to detect docker mode.
    private const string DockerEnvMarker = "/.dockerenv";

    // Set to "1" by the launcher's run-inside.sh since launcher v1.1.12 —
    // the explicit "this is the mobile app" marker.
    private const string MobileEnvMarker = "APACE_MOBILE";

    // Successful detections are cached for the process lifetime; failures are
    // not, so a later call can still succeed once the network comes up.
    private static string? _cached;

    // Computed once on first access (double-checked lock); null = not yet.
    private static bool? _isMobileLauncher;
    private static readonly object MobileLock = new();

    /// <summary>
    /// True when the panel runs inside the Android launcher app (PRoot on the
    /// phone). Detection: the explicit <c>APACE_MOBILE=1</c> env marker set by
    /// the launcher's entrypoint (run-inside.sh, launcher v1.1.12 onward),
    /// falling back to a non-zero <c>TracerPid</c> in /proc/self/status — PRoot
    /// is ptrace-based and traces every process it runs. Unreadable or
    /// unparseable means false (desktop/docker never match); the result is
    /// computed once and cached.
    /// </summary>
    public static bool IsMobileLauncher
    {
        get
        {
            if (_isMobileLauncher is { } cached)
            {
                return cached;
            }

            lock (MobileLock)
            {
                if (_isMobileLauncher is { } value)
                {
                    return value;
                }

                _isMobileLauncher = DetectMobileLauncher();
                return _isMobileLauncher.Value;
            }
        }
    }

    private static bool DetectMobileLauncher()
    {
        try
        {
            // Authoritative marker from the launcher entrypoint — cheapest
            // check first.
            if (Environment.GetEnvironmentVariable(MobileEnvMarker) == "1")
            {
                return true;
            }

            // Older launcher builds set no marker: PRoot (the ptrace-based
            // Ubuntu emulator the launcher runs the panel under) shows up as
            // a non-zero TracerPid. A missing/unparseable file means "not
            // mobile" (plain desktop or docker).
            foreach (string line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("TracerPid:", StringComparison.Ordinal) &&
                    int.TryParse(line["TracerPid:".Length..].Trim(), out int tracerPid))
                {
                    return tracerPid != 0;
                }
            }

            return false;
        }
        catch
        {
            // No env marker, unreadable /proc (non-Linux, permissions) — treat
            // any failure as "not mobile".
            return false;
        }
    }

    /// <summary>
    /// The detected LAN IPv4 (e.g. "192.168.1.42"), or null when none could
    /// be found (mobile launcher, docker container, no usable interface).
    /// </summary>
    public static string? DetectLanIPv4()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        // Mobile: a phone's Wi-Fi/cellular address is never the right default
        // (the client runs on the same device) — skip the probes, the callers
        // fall back to localhost. Docker: same, but the address must be set
        // manually there.
        if (IsMobileLauncher || IsRunningInContainer())
        {
            return null;
        }

        string? detected = DetectViaUdpConnect() ?? DetectViaNetworkInterfaces();

        if (detected is not null)
        {
            _cached = detected;
        }

        return detected;
    }

    private static bool IsRunningInContainer()
        => File.Exists(DockerEnvMarker);

    private static string? DetectViaUdpConnect()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            // Connect on a UDP socket only makes the OS choose the outgoing
            // route — no packet is ever sent.
            socket.Connect("8.8.8.8", 80);

            if (socket.LocalEndPoint is IPEndPoint endpoint &&
                endpoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(endpoint.Address))
            {
                return endpoint.Address.ToString();
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // No route to the outside (isolated LAN, network not up yet) — the
            // interface enumeration below may still find something.
        }

        return null;
    }

    private static string? DetectViaNetworkInterfaces()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                // Skip virtualized/container/VPN adapters: docker bridges,
                // veth pairs (the check also covers "vEthernet"), Linux bridge
                // names ("br-0123abcd") and tun/tap devices are never the
                // address LAN clients should connect to.
                string name = $"{nic.Name} {nic.Description}".ToLowerInvariant();
                if (name.Contains("docker") || name.Contains("veth") || name.Contains("br-") ||
                    name.Contains("tun") || name.Contains("tap"))
                {
                    continue;
                }

                foreach (var address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(address.Address))
                    {
                        return address.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or InvalidOperationException)
        {
            // Enumeration can fail in sandboxed environments — no address then.
        }

        return null;
    }
}
