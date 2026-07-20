using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vertex.Shared;

/// <summary>
/// Categories the tunnel surfaces on a fatal disconnect. Mirror of Swift
/// <c>TunnelErrorKind</c> and Kotlin <c>TunnelErrorKind</c>. Wire string
/// values must stay byte-identical across platforms.
/// </summary>
[JsonConverter(typeof(TunnelErrorKindJsonConverter))]
public enum TunnelErrorKind
{
    Authentication,
    IdentityRejected,
    DiscoveryTimeout,
    JoinTimeout,
    Configuration,
    Unknown,
}

/// <summary>
/// Last fatal error from the tunnel. Persisted by the Service and read by
/// the App when status flips back to Disconnected. Field names match the
/// Kotlin <c>TunnelErrorReport</c> wire format (Swift's variant adds a
/// <c>keychainLocked</c> kind that doesn't apply on Windows).
/// </summary>
public sealed record TunnelErrorReport(
    [property: JsonPropertyName("kind")]                                                                            TunnelErrorKind Kind,
    [property: JsonPropertyName("detail")]                                                                          string          Detail           = "",
    [property: JsonPropertyName("timestampEpochMs"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long            TimestampEpochMs = 0)
{
    public string UserMessage => Kind switch
    {
        TunnelErrorKind.Authentication =>
            $"Authentication failed. Check Client name and Password in Settings → Identity. ({Detail})",
        TunnelErrorKind.IdentityRejected =>
            $"The exit rejected this device's identity ({Detail}). Ask admin to reset TOFU for this device on the exit, then reconnect.",
        TunnelErrorKind.DiscoveryTimeout =>
            $"Exit \"{Detail}\" is unreachable. Check Edge selection in Settings or try a different exit.",
        TunnelErrorKind.JoinTimeout =>
            $"Exit \"{Detail}\" didn't respond to join. The exit may be down, or your Client name is not authorized.",
        TunnelErrorKind.Configuration =>
            $"Configuration error: {Detail}",
        TunnelErrorKind.Unknown =>
            string.IsNullOrEmpty(Detail) ? "Connection failed." : Detail,
        _ => "Connection failed.",
    };
}

internal sealed class TunnelErrorKindJsonConverter : JsonConverter<TunnelErrorKind>
{
    public override TunnelErrorKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "authentication"    => TunnelErrorKind.Authentication,
            "identity_rejected" => TunnelErrorKind.IdentityRejected,
            "discovery_timeout" => TunnelErrorKind.DiscoveryTimeout,
            "join_timeout"      => TunnelErrorKind.JoinTimeout,
            "configuration"     => TunnelErrorKind.Configuration,
            "unknown"           => TunnelErrorKind.Unknown,
            var s => throw new JsonException($"Unknown TunnelErrorKind '{s}'"),
        };

    public override void Write(Utf8JsonWriter writer, TunnelErrorKind value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            TunnelErrorKind.Authentication   => "authentication",
            TunnelErrorKind.IdentityRejected => "identity_rejected",
            TunnelErrorKind.DiscoveryTimeout => "discovery_timeout",
            TunnelErrorKind.JoinTimeout      => "join_timeout",
            TunnelErrorKind.Configuration    => "configuration",
            TunnelErrorKind.Unknown          => "unknown",
            _ => throw new JsonException($"Unhandled TunnelErrorKind {value}"),
        });
}
