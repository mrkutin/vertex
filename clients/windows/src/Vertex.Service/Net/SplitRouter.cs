using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Net;

namespace Vertex.Service.Net;

/// <summary>
/// Installs RU CIDR routes through the physical NIC so RU traffic
/// bypasses the tunnel. Counterpart to <see cref="RouteManager.AddDefaultViaTun"/>:
/// when split tunnel is on, the catch-all <c>0.0.0.0/0</c> still goes
/// through TUN, but a list of RU prefixes overrides at higher
/// specificity (longer prefix = more specific) to leave the tunnel.
/// Mirror of macOS <c>excludedRoutes</c> in
/// <c>NEPacketTunnelNetworkSettings</c> — same intent expressed via
/// the Windows route-table model.
/// </summary>
public sealed class SplitRouter
{
    /// <summary>
    /// Cap on the number of CIDRs installed. The bundled zone has ~8585
    /// entries; installing them all works on Windows but adds noticeable
    /// time to Connect / Disconnect (each CreateIpForwardEntry2 is
    /// ~100µs amortized). 1500 is the macOS / Android plan-level cap and
    /// covers ≥99% of RU traffic when sorted by prefix-length ascending
    /// (broadest first).
    /// </summary>
    public const int DefaultMaxRoutes = 1500;

    private readonly ILogger _log;
    private readonly List<IpHelperInterop.MibIpForwardRow2> _installed = new();

    public SplitRouter(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public int InstalledCount => _installed.Count;

    /// <summary>
    /// Install up to <paramref name="maxRoutes"/> RU CIDRs as routes
    /// through the physical interface (chosen by GetBestInterface for
    /// each destination). Returns the number of routes actually
    /// installed; partial success is logged but not thrown — split
    /// tunnel that excludes 1499/1500 networks is still useful.
    /// </summary>
    public int Apply(IEnumerable<string> cidrs, int maxRoutes = DefaultMaxRoutes)
    {
        var sorted = cidrs
            .Select(line =>
            {
                if (RuNetsLoader.TryParseCidr(line, out var ip, out var prefix))
                    return (Ok: true, Network: ip, Prefix: prefix);
                return (Ok: false, Network: IPAddress.None, Prefix: (byte)0);
            })
            .Where(t => t.Ok)
            .OrderBy(t => t.Prefix)        // shortest prefix first (broadest coverage)
            .ThenBy(t => t.Network.GetHashCode())
            .Take(maxRoutes)
            .ToList();

        int ok = 0;
        foreach (var (_, network, prefix) in sorted)
        {
            // GetBestRoute2 (not GetBestInterface) so we get BOTH the
            // physical interface AND its next-hop (LAN gateway) in one
            // call. Using IPAddress.Any as next-hop would mark the
            // route as "on-link" — Windows would then ARP-resolve the
            // RU destination directly on the LAN segment, fail, and
            // drop the packet. AddBrokerBypass uses the same trick;
            // staying consistent here.
            var destSockaddr = IpHelperInterop.ToSockaddr(network);
            int br = IpHelperInterop.GetBestRoute2(
                IntPtr.Zero, 0, IntPtr.Zero, ref destSockaddr, 0,
                out var bestRow, out _);
            if (br != 0)
            {
                _log.LogDebug("GetBestRoute2({Net}/{P}) failed {Err}", network, prefix, br);
                continue;
            }
            uint physicalIfIndex = bestRow.InterfaceIndex;
            IPAddress nextHop    = SockaddrToIp(bestRow.NextHop);

            var row = MakeRoute(
                destination: network,
                prefixLength: prefix,
                nextHop: nextHop,
                interfaceLuid: 0,
                interfaceIndex: physicalIfIndex,
                metric: 1);

            int err = IpHelperInterop.CreateIpForwardEntry2(ref row);
            if (err == 0 || err == 5010 /* ERROR_OBJECT_ALREADY_EXISTS */)
            {
                _installed.Add(row);
                ok++;
            }
            else
            {
                _log.LogDebug("CreateIpForwardEntry2({Net}/{P}) failed {Err}", network, prefix, err);
            }
        }
        _log.LogInformation("SplitRouter installed {Ok}/{Total} RU bypass routes", ok, sorted.Count);
        return ok;
    }

    /// <summary>
    /// Decode a Windows <c>SOCKADDR_INET</c> back into a managed IPv4
    /// address. Mirror of RouteManager's helper — kept private here so
    /// SplitRouter doesn't have to expose IpHelper internals or take a
    /// dependency on RouteManager.
    /// </summary>
    private static IPAddress SockaddrToIp(IpHelperInterop.SockaddrInet sa) =>
        new IPAddress(BitConverter.GetBytes(sa.Ipv4Address));

    /// <summary>Roll back every route installed by this instance. Idempotent — already-deleted routes don't error.</summary>
    public void Cleanup()
    {
        int rolled = 0;
        foreach (var row in _installed)
        {
            var copy = row;
            int err = IpHelperInterop.DeleteIpForwardEntry2(ref copy);
            if (err == 0 || err == 1168 /* ERROR_NOT_FOUND */) rolled++;
            else _log.LogDebug("DeleteIpForwardEntry2 (split cleanup) returned {Err}", err);
        }
        _installed.Clear();
        if (rolled > 0) _log.LogInformation("SplitRouter rolled back {N} RU bypass routes", rolled);
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
