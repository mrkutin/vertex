using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Vertex.Service.Net;

/// <summary>
/// Manages the IPv4 routing table on the local Windows machine for the
/// duration of a Vertex VPN session. Mirrors what
/// <c>pkg/routing/routing_macos.go</c> does on macOS via <c>route -n add</c>:
/// install /32 host routes for each broker IP through the current
/// physical interface so MQTT keeps working when the default route flips
/// to TUN, then install <c>0/1</c> + <c>128/1</c> sub-range default-tunnel
/// routes through the TUN adapter (Phase 1 — full-tunnel mode; Phase 3
/// adds the RU split via top-1500 RU CIDRs).
///
/// All routes go through <c>iphlpapi!CreateIpForwardEntry2</c> with
/// <see cref="IpHelperInterop.NlRouteOriginManual"/>; <see cref="Cleanup"/>
/// removes them via <c>DeleteIpForwardEntry2</c>. Idempotent: <c>Cleanup</c>
/// is called by <c>TunnelEngine</c> before a fresh session and at
/// service shutdown — duplicate removes are silently swallowed.
/// </summary>
public sealed class RouteManager
{
    private readonly ILogger _log;
    private readonly List<IpHelperInterop.MibIpForwardRow2> _installed = new();
    private readonly List<IpHelperInterop.MibUnicastIpAddressRow> _addresses = new();

    public RouteManager(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    /// <summary>
    /// Bind <paramref name="ip"/> with the given <paramref name="prefixLength"/>
    /// to the adapter identified by <paramref name="tunLuid"/>. MUST be
    /// called before <see cref="SetTunMtu"/> and the route adds — without
    /// it, Windows source-address selection picks the physical NIC for
    /// tunnel-bound traffic (Phase 1.9 review CRITICAL-1).
    /// </summary>
    public void SetTunAddress(ulong tunLuid, IPAddress ip, byte prefixLength)
    {
        var row = default(IpHelperInterop.MibUnicastIpAddressRow);
        IpHelperInterop.InitializeUnicastIpAddressEntry(ref row);

        row.Address            = IpHelperInterop.ToSockaddr(ip);
        row.InterfaceLuid      = tunLuid;
        row.OnLinkPrefixLength = prefixLength;
        row.PrefixOrigin       = IpHelperInterop.IpPrefixOriginManual;
        row.SuffixOrigin       = IpHelperInterop.IpSuffixOriginManual;
        row.DadState           = IpHelperInterop.NldsPreferred;
        row.ValidLifetime      = 0xFFFFFFFF; // infinite
        row.PreferredLifetime  = 0xFFFFFFFF;

        int err = IpHelperInterop.CreateUnicastIpAddressEntry(ref row);
        if (err == 0 || err == 5010 /* ERROR_OBJECT_ALREADY_EXISTS */)
        {
            _addresses.Add(row);
            _log.LogInformation("Bound {Ip}/{Prefix} to TUN luid={Luid}", ip, prefixLength, tunLuid);
        }
        else
        {
            _log.LogWarning("CreateUnicastIpAddressEntry({Ip}/{Prefix}) failed with Win32 error {Err}",
                ip, prefixLength, err);
        }
    }

    /// <summary>
    /// Install /32 host routes for each broker IP via the OS-chosen
    /// physical interface. Without this, the moment we add the
    /// 0/1 + 128/1 catch-all default-via-TUN routes the MQTT socket
    /// itself would loop back through the TUN that depends on it.
    /// </summary>
    public void AddBrokerBypass(IEnumerable<IPAddress> brokerIps)
    {
        foreach (var ip in brokerIps)
        {
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                _log.LogWarning("Skipping non-IPv4 broker address {Ip}", ip);
                continue;
            }

            // Ask the kernel which next-hop it would pick if we routed
            // to this destination today. That gives us the LAN gateway
            // (e.g., 10.0.0.1) — using IPAddress.Any here would mark
            // the /32 as "on-link", and Windows would try to ARP-resolve
            // the broker IP directly on the local Ethernet. ARP fails →
            // packets dropped → MQTT TCP socket dies a few seconds in.
            // GetBestRoute2 returns the active default route's next-hop.
            var destSockaddr = IpHelperInterop.ToSockaddr(ip);
            int br = IpHelperInterop.GetBestRoute2(
                IntPtr.Zero, 0, IntPtr.Zero, ref destSockaddr, 0,
                out var bestRow, out _);
            if (br != 0)
            {
                _log.LogWarning("GetBestRoute2({Ip}) failed with Win32 error {Err} — skipping bypass", ip, br);
                continue;
            }
            uint physicalIfIndex = bestRow.InterfaceIndex;
            IPAddress nextHop = SockaddrToIp(bestRow.NextHop);

            var row = MakeRoute(
                destination: ip,
                prefixLength: 32,
                nextHop: nextHop,
                interfaceLuid: 0,
                interfaceIndex: physicalIfIndex,
                metric: 1);

            int err = IpHelperInterop.CreateIpForwardEntry2(ref row);
            if (err == 0 || err == 5010 /* ERROR_OBJECT_ALREADY_EXISTS */)
            {
                _installed.Add(row);
                _log.LogInformation("Installed broker-bypass /32 route to {Ip} via ifIndex={Idx}", ip, physicalIfIndex);
            }
            else
            {
                _log.LogWarning("CreateIpForwardEntry2(broker={Ip}) failed with Win32 error {Err}", ip, err);
            }
        }
    }

    /// <summary>
    /// Install the two default-via-TUN sub-range routes. We use
    /// <c>0/1</c> + <c>128/1</c> instead of <c>0/0</c> so the OS
    /// can keep its existing default route as a fallback (paritет с
    /// macOS sub-range trick from <c>pkg/routing/routing_macos.go</c>).
    /// </summary>
    public void AddDefaultViaTun(ulong tunLuid, IPAddress tunGateway)
    {
        AddRoute(IPAddress.Parse("0.0.0.0"),     1,  tunGateway, tunLuid, 0);
        AddRoute(IPAddress.Parse("128.0.0.0"),   1,  tunGateway, tunLuid, 0);
    }

    /// <summary>Install one explicit IPv4 route via the TUN adapter (used by Phase 3 split routing).</summary>
    public void AddRoute(IPAddress destination, byte prefixLength, IPAddress nextHop, ulong interfaceLuid, uint interfaceIndex)
    {
        var row = MakeRoute(destination, prefixLength, nextHop, interfaceLuid, interfaceIndex, metric: 1);
        int err = IpHelperInterop.CreateIpForwardEntry2(ref row);
        if (err == 0 || err == 5010)
        {
            _installed.Add(row);
        }
        else
        {
            _log.LogWarning("CreateIpForwardEntry2({Dest}/{Prefix}) failed with Win32 error {Err}",
                destination, prefixLength, err);
        }
    }

    /// <summary>
    /// Set the IPv4 NlMtu of the TUN interface. WinTUN's default is
    /// 0xFFFF — without clamping, Windows TCP MSS = 65495 immediately
    /// black-holes any path that drops ICMP "frag needed" (most RU
    /// ISPs). 1300 leaves &gt;180 B headroom for ChaCha+MQTT+TLS+TCP
    /// over a 1500 B wire MTU.
    /// </summary>
    public void SetTunMtu(ulong tunLuid, uint mtu)
    {
        var row = new IpHelperInterop.MibIpInterfaceRow
        {
            Family        = IpHelperInterop.AF_INET,
            InterfaceLuid = tunLuid,
        };

        int rc = IpHelperInterop.GetIpInterfaceEntry(ref row);
        if (rc != 0)
        {
            _log.LogWarning("GetIpInterfaceEntry(tun luid={Luid}) failed with Win32 error {Err}", tunLuid, rc);
            return;
        }

        row.NlMtu = mtu;
        // SetIpInterfaceEntry rejects rows whose read-only fields differ
        // from what the kernel last wrote; GetIpInterfaceEntry filled
        // those in. Zero them all so the kernel treats the row as
        // "change only writable fields I touched". Mirror of
        // wireguard-windows winipcfg behaviour. Phase 1.8 review MAJOR-2.
        row.SitePrefixLength          = 0;
        row.SupportsWakeUpPatterns    = false;
        row.SupportsNeighborDiscovery = false;
        row.SupportsRouterDiscovery   = false;
        row.Connected                 = false;
        row.TransmitNlMtu             = 0;
        row.ReachableTime             = 0;

        rc = IpHelperInterop.SetIpInterfaceEntry(ref row);
        if (rc != 0)
        {
            _log.LogWarning("SetIpInterfaceEntry(NlMtu={Mtu}) failed with Win32 error {Err}", mtu, rc);
            return;
        }
        _log.LogInformation("TUN MTU set to {Mtu}", mtu);
    }

    /// <summary>Roll back every route + address installed by this manager. Idempotent.</summary>
    public void Cleanup()
    {
        foreach (var row in _installed)
        {
            var copy = row;
            int err = IpHelperInterop.DeleteIpForwardEntry2(ref copy);
            if (err != 0 && err != 1168 /* ERROR_NOT_FOUND */)
            {
                _log.LogDebug("DeleteIpForwardEntry2 (cleanup) returned {Err}", err);
            }
        }
        _installed.Clear();

        foreach (var row in _addresses)
        {
            var copy = row;
            int err = IpHelperInterop.DeleteUnicastIpAddressEntry(ref copy);
            if (err != 0 && err != 1168)
            {
                _log.LogDebug("DeleteUnicastIpAddressEntry (cleanup) returned {Err}", err);
            }
        }
        _addresses.Clear();
    }

    /// <summary>Convert a SockaddrInet IPv4 back to <see cref="IPAddress"/>.</summary>
    private static IPAddress SockaddrToIp(IpHelperInterop.SockaddrInet sa)
    {
        var bytes = BitConverter.GetBytes(sa.Ipv4Address);
        return new IPAddress(bytes);
    }

    private static IpHelperInterop.MibIpForwardRow2 MakeRoute(
        IPAddress destination,
        byte prefixLength,
        IPAddress nextHop,
        ulong interfaceLuid,
        uint interfaceIndex,
        uint metric)
    {
        var row = default(IpHelperInterop.MibIpForwardRow2);
        IpHelperInterop.InitializeIpForwardEntry(ref row);

        row.InterfaceLuid       = interfaceLuid;
        row.InterfaceIndex      = interfaceIndex;
        row.DestinationPrefix   = new IpHelperInterop.IpAddressPrefix
        {
            Prefix       = IpHelperInterop.ToSockaddr(destination),
            PrefixLength = prefixLength,
        };
        row.NextHop  = IpHelperInterop.ToSockaddr(nextHop);
        row.Metric   = metric;
        row.Protocol = IpHelperInterop.MIB_IPPROTO_NETMGMT;
        row.Origin   = IpHelperInterop.NlRouteOriginManual;
        return row;
    }
}
