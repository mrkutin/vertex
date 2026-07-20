using System.Text.Json.Serialization;

namespace Vertex.Core.Protocol;

/// <summary>
/// Exit discovery heartbeat published as retained on <c>discovery/exits/{id}</c>.
/// Field names match Go implementation byte-for-byte (note <c>max_clients</c>,
/// <c>broker_rtt_ms</c>, <c>dh_pubkey</c> are snake_case on the wire).
/// </summary>
public sealed record DiscoveryHeartbeat(
    /// <summary>Exit identifier (e.g. <c>aws</c>, <c>sto</c>).</summary>
    [property: JsonPropertyName("id")]                                                                          string                            Id,
    /// <summary>ISO country code (e.g. <c>CA</c>, <c>SE</c>).</summary>
    [property: JsonPropertyName("country"),       JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string?                            Country,
    /// <summary>Number of currently connected clients.</summary>
    [property: JsonPropertyName("clients"),       JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?                               Clients,
    /// <summary>Maximum client capacity advertised by the exit.</summary>
    [property: JsonPropertyName("max_clients"),   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int?                               MaxClients,
    /// <summary>Broker RTT measurements: <c>hostname → milliseconds</c>.</summary>
    [property: JsonPropertyName("broker_rtt_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string,int>?   BrokerRttMs,
    /// <summary>Uptime in seconds.</summary>
    [property: JsonPropertyName("uptime"),        JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long?                              Uptime,
    /// <summary>Unix timestamp the heartbeat was emitted.</summary>
    [property: JsonPropertyName("ts"),            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long?                              Ts,
    /// <summary>Exit's static X25519 DH public key (base64).</summary>
    [property: JsonPropertyName("dh_pubkey"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string?                            DhPubkey);
