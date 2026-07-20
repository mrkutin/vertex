using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vertex.Shared;

/// <summary>Coarse connection state. Mirror of Swift <c>ConnectionState</c> and Kotlin <c>ConnectionState</c>.</summary>
[JsonConverter(typeof(ConnectionStateJsonConverter))]
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Handshaking,
    Connected,
    Reconnecting,
}

/// <summary>
/// Detailed status pushed from the Service to the App over the named pipe.
/// Wire-compatible with the Kotlin and Swift <c>ConnectionStatus</c> for
/// fields they share; <c>PingMs</c> is Windows-IPC-only because the probe
/// lives Service-side here, whereas iOS/macOS measure it host-side on the
/// ViewModel and Android has no IPC boundary. Explicit JsonPropertyName
/// tags are load-bearing for cross-platform fixture tests.
/// </summary>
/// <remarks>
/// <para><c>PingMs</c> is the TCP RTT to a public anchor (1.1.1.1:443)
/// measured by <c>PingProbe</c> in the Service. The value is
/// <em>sticky</em>: only an <see cref="ConnectionState.Disconnected"/>
/// transition clears it; transient probe failures (timeout, network blip,
/// reconnect) keep the last successful value so the SpeedPill UI does not
/// flicker. Producers MUST omit the field (write null) on disconnect; on
/// transient failures they MUST repeat the previous value. On Service
/// restart the field is null until the first successful probe (~60s);
/// consumers should treat null as "unknown" and either hide the icon or
/// keep the App-side last-known value, never display "0 ms".</para>
/// </remarks>
public sealed record ConnectionStatus(
    [property: JsonPropertyName("state")]                                                                                ConnectionState State,
    [property: JsonPropertyName("assignedIp"),            JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]   string?         AssignedIp            = null,
    [property: JsonPropertyName("currentBroker"),         JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]   string?         CurrentBroker         = null,
    [property: JsonPropertyName("currentExit"),           JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]   string?         CurrentExit           = null,
    [property: JsonPropertyName("connectedSinceEpochMs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull),
                                                          JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long?           ConnectedSinceEpochMs = null,
    [property: JsonPropertyName("pingMs"),                JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]   int?            PingMs                = null,
    [property: JsonPropertyName("lastError"),             JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]   string?         LastError             = null)
{
    public static readonly ConnectionStatus Disconnected = new(ConnectionState.Disconnected);
}

internal sealed class ConnectionStateJsonConverter : JsonConverter<ConnectionState>
{
    public override ConnectionState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() switch
        {
            "disconnected" => ConnectionState.Disconnected,
            "connecting"   => ConnectionState.Connecting,
            "handshaking"  => ConnectionState.Handshaking,
            "connected"    => ConnectionState.Connected,
            "reconnecting" => ConnectionState.Reconnecting,
            var s => throw new JsonException($"Unknown ConnectionState '{s}'"),
        };

    public override void Write(Utf8JsonWriter writer, ConnectionState value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            ConnectionState.Disconnected => "disconnected",
            ConnectionState.Connecting   => "connecting",
            ConnectionState.Handshaking  => "handshaking",
            ConnectionState.Connected    => "connected",
            ConnectionState.Reconnecting => "reconnecting",
            _ => throw new JsonException($"Unhandled ConnectionState {value}"),
        });
}
