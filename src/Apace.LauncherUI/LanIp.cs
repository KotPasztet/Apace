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
/// </summary>
internal static class LanIp
{
    // The docker daemon always creates this marker inside containers; the
    // panel's SelfUpdateService uses the same file to detect docker mode.
    private const string DockerEnvMarker = "/.dockerenv";

    // Successful detections are cached for the process lifetime; failures are
    // not, so a later call can still succeed once the network comes up.
    private static string? _cached;

    /// <summary>
    /// The detected LAN IPv4 (e.g. "192.168.1.42"), or null when none could
    /// be found.
    /// </summary>
    public static string? DetectLanIPv4()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        string? detected = null;
        if (!IsRunningInContainer())
        {
            detected = DetectViaUdpConnect() ?? DetectViaNetworkInterfaces();
        }

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
