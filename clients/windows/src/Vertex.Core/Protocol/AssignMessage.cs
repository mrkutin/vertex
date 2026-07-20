using System.Text.Json.Serialization;

namespace Vertex.Core.Protocol;

/// <summary>
/// IP assignment response from exit, received on <c>vpn/{exit}/{name}/control</c>.
/// Field names match Go and Swift exactly.
/// </summary>
public sealed record AssignMessage(
    /// <summary>Assigned TUN IP address (e.g. <c>10.9.0.5</c>).</summary>
    [property: JsonPropertyName("ip")]   string  Ip,
    /// <summary>Subnet mask (e.g. <c>255.255.255.0</c>).</summary>
    [property: JsonPropertyName("mask"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Mask,
    /// <summary>Gateway / exit TUN IP (e.g. <c>10.9.0.1</c>).</summary>
    [property: JsonPropertyName("gw")]   string  Gw,
    /// <summary>Exit's X25519 DH public key (base64) for session key derivation.</summary>
    [property: JsonPropertyName("dh"),   JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Dh);
