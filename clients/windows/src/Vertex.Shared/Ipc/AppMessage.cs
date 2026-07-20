using System.Text.Json.Serialization;

namespace Vertex.Shared.Ipc;

/// <summary>
/// Messages sent from the WinUI App to the Windows Service over the named
/// pipe. Each line on the pipe is one JSON object with a <c>type</c>
/// discriminator so the polymorphic deserializer can dispatch.
/// Wire-compatible across upgrades: never repurpose a <c>type</c> string.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", IgnoreUnrecognizedTypeDiscriminators = true)]
[JsonDerivedType(typeof(RequestStatus),     "requestStatus")]
[JsonDerivedType(typeof(RequestStats),      "requestStats")]
[JsonDerivedType(typeof(Connect),           "connect")]
[JsonDerivedType(typeof(Disconnect),        "disconnect")]
[JsonDerivedType(typeof(SetPassword),       "setPassword")]
[JsonDerivedType(typeof(SetSelectedExit),   "setSelectedExit")]
[JsonDerivedType(typeof(SetSelectedBroker), "setSelectedBroker")]
[JsonDerivedType(typeof(SetSplitTunnel),    "setSplitTunnel")]
[JsonDerivedType(typeof(SetClientName),     "setClientName")]
[JsonDerivedType(typeof(SetDiscoveryDomain),"setDiscoveryDomain")]
[JsonDerivedType(typeof(RequestIdentityInfo), "requestIdentityInfo")]
[JsonDerivedType(typeof(RefreshDiscovery),  "refreshDiscovery")]
[JsonDerivedType(typeof(ResetIdentity),     "resetIdentity")]
[JsonDerivedType(typeof(ExportDiagnostics), "exportDiagnostics")]
public abstract record AppMessage
{
    public sealed record RequestStatus : AppMessage;
    public sealed record RequestStats  : AppMessage;
    public sealed record Connect       : AppMessage;
    public sealed record Disconnect    : AppMessage;

    /// <summary>Push the broker password into Service-owned DPAPI storage. Never logged.</summary>
    public sealed record SetPassword(
        [property: JsonPropertyName("password")] string Password) : AppMessage;

    /// <summary>Choose an exit (specific id like "aws"/"sto", or "auto" for tracker-driven).</summary>
    public sealed record SetSelectedExit(
        [property: JsonPropertyName("exitId")] string ExitId) : AppMessage;

    /// <summary>Choose a broker (specific id, or "auto" for RTT-probe-driven).</summary>
    public sealed record SetSelectedBroker(
        [property: JsonPropertyName("brokerId")] string BrokerId) : AppMessage;

    /// <summary>Toggle RU-bypass split tunnel (Phase 3).</summary>
    public sealed record SetSplitTunnel(
        [property: JsonPropertyName("enabled")] bool Enabled) : AppMessage;

    /// <summary>
    /// Override the device's client name (== Mosquitto user suffix, "vtx-client-{name}").
    /// Empty / whitespace falls back to the default "windows". Persisted to state.json
    /// and used on the next Connect.
    /// </summary>
    public sealed record SetClientName(
        [property: JsonPropertyName("clientName")] string ClientName) : AppMessage;

    /// <summary>
    /// Override the SRV discovery domain (default "vertices.ru"). Persisted to
    /// state.json. Service re-resolves SRV on the next RefreshDiscovery / Connect.
    /// </summary>
    public sealed record SetDiscoveryDomain(
        [property: JsonPropertyName("domain")] string Domain) : AppMessage;

    /// <summary>
    /// Ask the Service to push the current <c>IdentityInfo</c> envelope
    /// (client name, identity pubkey hex, discovery domain). Used by the
    /// Settings window on open so the UI doesn't have to wait for the next
    /// status push. Idempotent.
    /// </summary>
    public sealed record RequestIdentityInfo : AppMessage;

    /// <summary>Force a fresh SRV lookup + broker probe sweep.</summary>
    public sealed record RefreshDiscovery : AppMessage;

    /// <summary>Wipe persistent identity key — next connect re-TOFUs to all exits.</summary>
    public sealed record ResetIdentity : AppMessage;

    /// <summary>Bundle logs + state into a ZIP at the given absolute path.</summary>
    public sealed record ExportDiagnostics(
        [property: JsonPropertyName("targetPath")] string TargetPath) : AppMessage;
}
