using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Vertex.Service.Net;

/// <summary>
/// P/Invoke surface for the routing / interface APIs in
/// <c>iphlpapi.dll</c>. Used by <see cref="RouteManager"/> and
/// <see cref="DnsLeakGuard"/>. Only the minimum we need today —
/// future expansion via <c>Microsoft.Windows.CsWin32</c> source generator.
/// </summary>
internal static class IpHelperInterop
{
    private const string Lib = "iphlpapi.dll";

    public const uint AF_INET   = 2;  // IPv4
    public const uint AF_INET6  = 23; // IPv6

    /// <summary>Standard route protocol id used by user-mode routing apps (per <c>NL_ROUTE_PROTOCOL</c>).</summary>
    public const uint MIB_IPPROTO_NETMGMT = 3;

    /// <summary><c>NL_ROUTE_ORIGIN_MANUAL</c>.</summary>
    public const uint NlRouteOriginManual = 0;

    /// <summary>SOCKADDR_INET stub: room for IPv4 / IPv6 / scope.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 28)]
    public struct SockaddrInet
    {
        [FieldOffset(0)] public ushort Family;
        // IPv4
        [FieldOffset(2)] public ushort PortV4;
        [FieldOffset(4)] public uint   Ipv4Address;
        // IPv6 — laid out at the same physical offsets the Windows
        // SOCKADDR_IN6 uses; we only fill IPv4 for now.
    }

    /// <summary>
    /// IP_ADDRESS_PREFIX (CIDR pair). Default alignment matches the C SDK:
    /// SOCKADDR_INET (28 B) + UCHAR PrefixLength + 3 B tail-pad = 32 B.
    /// Marshaller adds the tail-pad automatically.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct IpAddressPrefix
    {
        public SockaddrInet Prefix;
        public byte         PrefixLength;
    }

    /// <summary>
    /// Mirror of <c>MIB_IPFORWARD_ROW2</c> from <c>netioapi.h</c>.
    ///
    /// LayoutKind.Sequential WITHOUT explicit Pack — the C SDK uses
    /// default (natural) alignment, and an earlier <c>Pack=4</c> would
    /// have cut 4 bytes of pad around the 8-byte-aligned <c>InterfaceLuid</c>,
    /// leaving the kernel writing past our struct's end (Phase 1.8
    /// review CRITICAL-1). Size is pinned at 104 bytes on x64/ARM64 by
    /// <c>MibIpForwardRow2_Size_MatchesNativeOnX64Arm64</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibIpForwardRow2
    {
        public ulong          InterfaceLuid;
        public uint           InterfaceIndex;
        public IpAddressPrefix DestinationPrefix;
        public SockaddrInet   NextHop;

        public byte           SitePrefixLength;
        public uint           ValidLifetime;
        public uint           PreferredLifetime;
        public uint           Metric;
        public uint           Protocol;       // NL_ROUTE_PROTOCOL
        [MarshalAs(UnmanagedType.U1)] public bool Loopback;
        [MarshalAs(UnmanagedType.U1)] public bool AutoconfigureAddress;
        [MarshalAs(UnmanagedType.U1)] public bool Publish;
        [MarshalAs(UnmanagedType.U1)] public bool Immortal;

        public uint           Age;
        public uint           Origin;          // NL_ROUTE_ORIGIN
    }

    /// <summary>MIB_IPINTERFACE_ROW — used for setting NlMtu (Phase 1.7 follow-up).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibIpInterfaceRow
    {
        public uint   Family;            // AF_INET / AF_INET6
        public ulong  InterfaceLuid;
        public uint   InterfaceIndex;

        public uint   MaxReassemblySize;
        public ulong  InterfaceIdentifier;
        public uint   MinRouterAdvertisementInterval;
        public uint   MaxRouterAdvertisementInterval;
        [MarshalAs(UnmanagedType.U1)] public bool AdvertisingEnabled;
        [MarshalAs(UnmanagedType.U1)] public bool ForwardingEnabled;
        [MarshalAs(UnmanagedType.U1)] public bool WeakHostSend;
        [MarshalAs(UnmanagedType.U1)] public bool WeakHostReceive;
        [MarshalAs(UnmanagedType.U1)] public bool UseAutomaticMetric;
        [MarshalAs(UnmanagedType.U1)] public bool UseNeighborUnreachabilityDetection;
        [MarshalAs(UnmanagedType.U1)] public bool ManagedAddressConfigurationSupported;
        [MarshalAs(UnmanagedType.U1)] public bool OtherStatefulConfigurationSupported;
        [MarshalAs(UnmanagedType.U1)] public bool AdvertiseDefaultRoute;

        public uint   RouterDiscoveryBehavior;
        public uint   DadTransmits;
        public uint   BaseReachableTime;
        public uint   RetransmitTime;
        public uint   PathMtuDiscoveryTimeout;
        public uint   LinkLocalAddressBehavior;
        public uint   LinkLocalAddressTimeout;
        public uint   ZoneIndicesIPv4_0;        // (real array; we don't index it)
        public uint   ZoneIndicesIPv4_1;
        public uint   ZoneIndicesIPv4_2;
        public uint   ZoneIndicesIPv4_3;
        public uint   ZoneIndicesIPv4_4;
        public uint   ZoneIndicesIPv4_5;
        public uint   ZoneIndicesIPv4_6;
        public uint   ZoneIndicesIPv4_7;
        public uint   ZoneIndicesIPv4_8;
        public uint   ZoneIndicesIPv4_9;
        public uint   ZoneIndicesIPv4_10;
        public uint   ZoneIndicesIPv4_11;
        public uint   ZoneIndicesIPv4_12;
        public uint   ZoneIndicesIPv4_13;
        public uint   ZoneIndicesIPv4_14;
        public uint   ZoneIndicesIPv4_15;

        public uint   SitePrefixLength;
        public uint   Metric;
        public uint   NlMtu;
        [MarshalAs(UnmanagedType.U1)] public bool Connected;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsWakeUpPatterns;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsNeighborDiscovery;
        [MarshalAs(UnmanagedType.U1)] public bool SupportsRouterDiscovery;

        public uint   ReachableTime;
        public uint   TransmitNlMtu;
        public uint   InterfaceMetric;
        public uint   DisableDefaultRoutes;
        public uint   NlMtuOverride;
        public uint   ConnectedSubnetPrefixLength;
        public uint   InterfaceIdentifierLength;
    }

    [DllImport(Lib, ExactSpelling = true)]
    public static extern void InitializeIpForwardEntry(ref MibIpForwardRow2 row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int CreateIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int DeleteIpForwardEntry2(ref MibIpForwardRow2 row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int GetIpInterfaceEntry(ref MibIpInterfaceRow row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int SetIpInterfaceEntry(ref MibIpInterfaceRow row);

    /// <summary>Find the interface index that the OS would use to reach <paramref name="destination"/>.</summary>
    [DllImport(Lib, ExactSpelling = true, EntryPoint = "GetBestInterface")]
    public static extern int GetBestInterface(uint destAddrV4, out uint bestIfIndex);

    /// <summary>
    /// Resolve the routing decision (best matching MIB_IPFORWARD_ROW2) for
    /// a destination — used to pull the kernel-current default-gateway
    /// next-hop so our /32 broker-bypass routes don't claim "on-link"
    /// when the broker actually lives over the WAN.
    /// </summary>
    [DllImport(Lib, ExactSpelling = true)]
    public static extern int GetBestRoute2(
        IntPtr interfaceLuid,        // optional, pass NULL
        uint   interfaceIndex,       // 0 = let kernel pick
        IntPtr sourceAddress,        // optional, pass NULL
        ref SockaddrInet destinationAddress,
        uint   addressSortOptions,   // 0 = default
        out MibIpForwardRow2 bestRoute,
        out SockaddrInet bestSourceAddress);

    public static SockaddrInet ToSockaddr(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 supported in this minimal interop surface.");
        }

        var raw = ip.GetAddressBytes();
        return new SockaddrInet
        {
            Family      = (ushort)AF_INET,
            Ipv4Address = BitConverter.ToUInt32(raw, 0),
        };
    }

    // ---- MIB_UNICASTIPADDRESS_ROW + helpers (Phase 1.9 CRITICAL-1) ----

    /// <summary>
    /// MIB_UNICASTIPADDRESS_ROW — binds an IP to an adapter via
    /// <see cref="CreateUnicastIpAddressEntry"/>. Without it, Windows
    /// source-address selection picks the physical NIC for tunnel
    /// outbound traffic and the exit's stateful NAT keys mismatch
    /// (Phase 1.9 review CRITICAL-1).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MibUnicastIpAddressRow
    {
        public SockaddrInet Address;
        public ulong        InterfaceLuid;
        public uint         InterfaceIndex;
        public uint         PrefixOrigin;       // NL_PREFIX_ORIGIN
        public uint         SuffixOrigin;       // NL_SUFFIX_ORIGIN
        public uint         ValidLifetime;
        public uint         PreferredLifetime;
        public byte         OnLinkPrefixLength;
        [MarshalAs(UnmanagedType.U1)] public bool SkipAsSource;
        public uint         DadState;            // NL_DAD_STATE
        public uint         ScopeId;
        public long         CreationTimeStamp;
    }

    public const uint IpPrefixOriginManual = 0;
    public const uint IpSuffixOriginManual = 0;
    public const uint NldsPreferred         = 4; // NlDadStatePreferred

    [DllImport(Lib, ExactSpelling = true)]
    public static extern void InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    [DllImport(Lib, ExactSpelling = true)]
    public static extern int DeleteUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

    /// <summary>Convert a dotted-quad subnet mask (e.g. <c>"255.255.255.0"</c>) to a CIDR prefix length.</summary>
    public static byte MaskToPrefix(IPAddress mask)
    {
        if (mask.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 masks supported.");
        }
        uint bits = BitConverter.ToUInt32(mask.GetAddressBytes(), 0);
        // Mask is network byte order; count contiguous high bits.
        bits = (uint)System.Net.IPAddress.NetworkToHostOrder((int)bits);
        byte count = 0;
        while (bits != 0)
        {
            count += (byte)(bits & 1u);
            bits >>= 1;
        }
        return count;
    }
}
