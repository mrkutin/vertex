using System.Text.Json.Serialization;

namespace Vertex.Core.Protocol;

/// <summary>
/// Join handshake message published by the client to <c>vpn/{exit}/control/join</c>.
/// Field names match Go <c>cmd/exit</c>, Swift <c>JoinMessage</c>, and Kotlin
/// <c>JoinMessage</c> byte-for-byte — <c>id_sig</c> is intentionally snake_case
/// on the wire.
/// </summary>
public sealed record JoinMessage(
    [property: JsonPropertyName("name")]   string  Name,
    /// <summary>Base64 of the client's ephemeral X25519 public key (32 bytes raw).</summary>
    [property: JsonPropertyName("dh")]     string  Dh,
    /// <summary>Base64 of the client's persistent X25519 identity public key (32 bytes raw). Optional for backward compat.</summary>
    [property: JsonPropertyName("id"),     JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Id    = null,
    /// <summary>Base64 of the HMAC-SHA256 identity proof. See <c>IdentityKey.Proof</c>.</summary>
    [property: JsonPropertyName("id_sig"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IdSig = null);
