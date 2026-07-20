using Vertex.Core.Config;

namespace Vertex.Service;

/// <summary>
/// Effective configuration for one tunnel session. Materialised by
/// <see cref="TunnelEngine"/> from the <see cref="Storage.ServiceState"/>
/// + identity / password stores at the moment the host App calls
/// <c>connect</c>. Mirror of Swift <c>TunnelConfig</c> and Kotlin
/// <c>TunnelConfig</c> (the protocol-level one, not the wire-format
/// records of the same name).
/// </summary>
public sealed record TunnelConfig(
    /// <summary>Client name on the broker (must match Mosquitto's <c>vtx-client-{name}</c>).</summary>
    string                ClientName,
    /// <summary>Username used for the MQTT CONNECT handshake (Phase 1: <c>vtx-client-{name}</c>).</summary>
    string                MqttUsername,
    /// <summary>Ordered broker URL list — first entry is the preferred broker.</summary>
    IReadOnlyList<BrokerUrl> Brokers,
    /// <summary>Selected exit id, or <c>"auto"</c> for tracker-driven selection.</summary>
    string                SelectedExit,
    /// <summary>SRV discovery domain (default "vertices.ru"); used by <see cref="Core.Discovery.SrvResolver"/>.</summary>
    string                DiscoveryDomain = "vertices.ru",
    /// <summary>WinTUN adapter name visible in Device Manager.</summary>
    string                AdapterName = "Vertex",
    /// <summary>Conservative MTU — see <c>PacketPipeline.DefaultMtu</c> reasoning.</summary>
    ushort                Mtu = 1300,
    /// <summary>Wait window for at least one discovery heartbeat before the join handshake gives up.</summary>
    TimeSpan?             DiscoveryWait = null,
    /// <summary>Wait window for the exit's assign reply after publishing JoinMessage.</summary>
    TimeSpan?             JoinTimeout = null);
