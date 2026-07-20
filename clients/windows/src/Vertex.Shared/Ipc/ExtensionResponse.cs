using System.Text.Json.Serialization;

namespace Vertex.Shared.Ipc;

/// <summary>
/// Messages sent from the Windows Service back to the App over the named
/// pipe. Three classes:
///   <list type="bullet">
///     <item>Replies to App requests (<c>status</c>, <c>stats</c>).</item>
///     <item>Push events (<c>error</c>, <c>discoveryUpdate</c>, <c>brokerUpdate</c>).</item>
///     <item>Acknowledgements (<c>ack</c>) for fire-and-forget commands.</item>
///   </list>
/// JSON line-delimited; one object per newline.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(StatusEnvelope),    "status")]
[JsonDerivedType(typeof(StatsEnvelope),     "stats")]
[JsonDerivedType(typeof(ErrorEnvelope),     "error")]
[JsonDerivedType(typeof(DiscoveryUpdate),   "discoveryUpdate")]
[JsonDerivedType(typeof(BrokerUpdate),      "brokerUpdate")]
[JsonDerivedType(typeof(IdentityInfo),      "identityInfo")]
[JsonDerivedType(typeof(Ack),               "ack")]
public abstract record ExtensionResponse
{
    public sealed record StatusEnvelope(
        [property: JsonPropertyName("status")] ConnectionStatus Status) : ExtensionResponse;

    public sealed record StatsEnvelope(
        [property: JsonPropertyName("stats")] TunnelStats Stats) : ExtensionResponse;

    public sealed record ErrorEnvelope(
        [property: JsonPropertyName("error")] TunnelErrorReport Error) : ExtensionResponse;

    /// <summary>Snapshot of currently known exits with score/RTT for the picker UI.</summary>
    public sealed record DiscoveryUpdate(
        [property: JsonPropertyName("exits")] IReadOnlyList<ExitInfo> Exits) : ExtensionResponse;

    /// <summary>Snapshot of currently known brokers with measured TCP RTT.</summary>
    public sealed record BrokerUpdate(
        [property: JsonPropertyName("brokers")] IReadOnlyList<BrokerInfo> Brokers) : ExtensionResponse;

    /// <summary>
    /// Device-bound identity snapshot — pushed by the Service on App attach,
    /// after <c>SetClientName</c> / <c>SetDiscoveryDomain</c> / <c>ResetIdentity</c>,
    /// or on explicit <c>RequestIdentityInfo</c>. Lets the Settings window
    /// render the editable client-name field, the SRV domain, and the
    /// identity public-key fingerprint without polling identity.bin from
    /// the App side. <c>PubkeyHex</c> is the lowercase 64-char hex of the
    /// X25519 public key — same value the exit registers in TOFU.
    /// </summary>
    public sealed record IdentityInfo(
        [property: JsonPropertyName("clientName")] string ClientName,
        [property: JsonPropertyName("pubkeyHex")]  string PubkeyHex,
        [property: JsonPropertyName("domain")]     string Domain) : ExtensionResponse;

    /// <summary>Acknowledgement of a fire-and-forget App command (e.g. <c>setSelectedExit</c>).</summary>
    public sealed record Ack(
        [property: JsonPropertyName("forType")] string ForType,
        [property: JsonPropertyName("ok")]      bool   Ok,
        [property: JsonPropertyName("detail")]  string Detail = "") : ExtensionResponse;
}

public sealed record ExitInfo(
    [property: JsonPropertyName("id")]            string  Id,
    [property: JsonPropertyName("country")]       string? Country,
    [property: JsonPropertyName("clients")]       int     Clients,
    [property: JsonPropertyName("capacity")]      int     Capacity,
    [property: JsonPropertyName("brokerRttMs")]   int     BrokerRttMs,
    [property: JsonPropertyName("score")]         double  Score,
    [property: JsonPropertyName("staleSeconds")]  double  StaleSeconds,
    /// <summary>
    /// Display string from the SRV TXT record on the exit's target host
    /// (e.g. "Toronto, Canada"). Optional — null when the TXT record is
    /// absent or unparseable, in which case the App falls back to the
    /// uppercased ID via <c>NodeLabels.EdgeLabel</c>. Defaulted for
    /// backward-compat with older Service builds that didn't populate
    /// the field.
    /// </summary>
    [property: JsonPropertyName("displayName"),  JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string?                                                       DisplayName = null);

public sealed record BrokerInfo(
    [property: JsonPropertyName("id")]      string  Id,
    [property: JsonPropertyName("url")]     string  Url,
    [property: JsonPropertyName("rttMs")]   int?    RttMs,
    [property: JsonPropertyName("ok")]      bool    Ok,
    [property: JsonPropertyName("detail")]  string? Detail);
