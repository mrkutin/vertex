using System.Text.Json;
using FluentAssertions;
using Vertex.Shared;
using Vertex.Shared.Ipc;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Phase 0 smoke: lock the wire-format strings of the IPC types so future
/// edits can't silently drift away from Swift / Kotlin field names. Real
/// cross-platform fixtures land with Phase 1's crypto vectors.
/// </summary>
public class IpcContractTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    [Fact]
    public void ConnectionStatus_RoundTrip_KeepsLowerCaseStateName()
    {
        var status = new ConnectionStatus(
            State: ConnectionState.Connected,
            AssignedIp: "10.9.0.5",
            CurrentBroker: "yc",
            CurrentExit: "aws",
            ConnectedSinceEpochMs: 1_700_000_000_000L,
            PingMs: 42,
            LastError: null);

        var json = JsonSerializer.Serialize(status, Json);

        json.Should().Contain("\"state\":\"connected\"");
        json.Should().Contain("\"assignedIp\":\"10.9.0.5\"");
        json.Should().Contain("\"currentBroker\":\"yc\"");
        json.Should().Contain("\"currentExit\":\"aws\"");
        json.Should().Contain("\"connectedSinceEpochMs\":1700000000000");
        json.Should().Contain("\"pingMs\":42");

        var roundTripped = JsonSerializer.Deserialize<ConnectionStatus>(json, Json);
        roundTripped.Should().Be(status);
    }

    /// <summary>
    /// pingMs is omitted from the wire when null so older Apps (built before
    /// the field existed) parse new Service messages cleanly. Anchors the
    /// JsonIgnoreCondition.WhenWritingNull behavior across all optional
    /// status fields.
    /// </summary>
    [Fact]
    public void ConnectionStatus_NullPingMs_OmittedFromWire()
    {
        var status = new ConnectionStatus(ConnectionState.Connected, AssignedIp: "10.9.0.5");

        var json = JsonSerializer.Serialize(status, Json);

        json.Should().NotContain("pingMs");
        var back = JsonSerializer.Deserialize<ConnectionStatus>(json, Json);
        back!.PingMs.Should().BeNull();
    }

    /// <summary>
    /// Forward-compat: a Service still emitting old-shape status (no pingMs)
    /// must deserialize without error; the App falls back to "no ping
    /// known" rather than failing the whole status update.
    /// </summary>
    [Fact]
    public void ConnectionStatus_LegacyWireWithoutPingMs_ParsesAsNull()
    {
        var legacy = """{"state":"connected","assignedIp":"10.9.0.5"}""";

        var back = JsonSerializer.Deserialize<ConnectionStatus>(legacy, Json);

        back!.PingMs.Should().BeNull();
        back.State.Should().Be(ConnectionState.Connected);
        back.AssignedIp.Should().Be("10.9.0.5");
    }

    [Fact]
    public void TunnelErrorReport_KindUsesUnderscoreVariant()
    {
        var report = new TunnelErrorReport(TunnelErrorKind.IdentityRejected, "android", 42);
        var json = JsonSerializer.Serialize(report, Json);
        json.Should().Contain("\"kind\":\"identity_rejected\"");

        var back = JsonSerializer.Deserialize<TunnelErrorReport>(json, Json);
        back!.Kind.Should().Be(TunnelErrorKind.IdentityRejected);
    }

    [Fact]
    public void AppMessage_PolymorphicDispatch_PicksByTypeDiscriminator()
    {
        AppMessage cmd = new AppMessage.SetSelectedExit("aws");

        var json = JsonSerializer.Serialize(cmd, Json);
        json.Should().Contain("\"type\":\"setSelectedExit\"");
        json.Should().Contain("\"exitId\":\"aws\"");

        var back = JsonSerializer.Deserialize<AppMessage>(json, Json);
        back.Should().BeOfType<AppMessage.SetSelectedExit>()
            .Which.ExitId.Should().Be("aws");
    }

    [Fact]
    public void ExtensionResponse_Status_NestsConnectionStatus()
    {
        ExtensionResponse env = new ExtensionResponse.StatusEnvelope(
            new ConnectionStatus(ConnectionState.Reconnecting));

        var json = JsonSerializer.Serialize(env, Json);
        json.Should().Contain("\"type\":\"status\"");
        json.Should().Contain("\"state\":\"reconnecting\"");

        var back = JsonSerializer.Deserialize<ExtensionResponse>(json, Json);
        back.Should().BeOfType<ExtensionResponse.StatusEnvelope>()
            .Which.Status.State.Should().Be(ConnectionState.Reconnecting);
    }

    /// <summary>
    /// Forward-compat: an older Service receiving an unknown App→Service
    /// command (added by a newer App) must NOT throw a generic
    /// <see cref="JsonException"/> that the named-pipe handler would
    /// confuse with a malformed packet. With
    /// <c>IgnoreUnrecognizedTypeDiscriminators = true</c> on an abstract
    /// base type, STJ falls through to a constructor-less base and throws
    /// <see cref="NotSupportedException"/>. The pipe handler treats this
    /// as a soft "unknown message" signal (logs + skips) rather than
    /// disconnecting; an unknown discriminator does NOT crash the
    /// Service worker.
    /// </summary>
    [Fact]
    public void AppMessage_UnknownDiscriminator_RaisesIgnorableException()
    {
        var futureCommand = """{"type":"setLogLevel","level":"trace"}""";
        Action act = () => JsonSerializer.Deserialize<AppMessage>(futureCommand, Json);
        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ExtensionResponse_UnknownDiscriminator_RaisesIgnorableException()
    {
        var futureEvent = """{"type":"throughputSnapshot","mbps":42}""";
        Action act = () => JsonSerializer.Deserialize<ExtensionResponse>(futureEvent, Json);
        act.Should().Throw<NotSupportedException>();
    }

    /// <summary>
    /// A genuinely malformed packet — missing discriminator or wrong-type
    /// discriminator value — also produces an exception (same
    /// <see cref="NotSupportedException"/> from STJ's fallback-to-base
    /// path). The pipe handler treats every parse exception as
    /// "unprocessable line, log + skip" — there's no semantic difference
    /// between unknown-command and broken-JSON for the handler's
    /// dispatcher loop.
    /// </summary>
    [Fact]
    public void AppMessage_MissingDiscriminator_RaisesIgnorableException()
    {
        var noType = """{"foo":"bar"}""";
        Action act = () => JsonSerializer.Deserialize<AppMessage>(noType, Json);
        act.Should().Throw<Exception>().Which.Should().Match<Exception>(e =>
            e is NotSupportedException || e is JsonException);
    }

    /// <summary>
    /// `JsonNumberHandling.AllowReadingFromString` is set on
    /// <c>ConnectedSinceEpochMs</c> and <c>TimestampEpochMs</c> for
    /// resilience against JS-bridge stringification of long integers.
    /// </summary>
    [Fact]
    public void ConnectionStatus_AcceptsStringifiedEpochMs()
    {
        var json = """{"state":"connected","connectedSinceEpochMs":"1700000000000"}""";
        var back = JsonSerializer.Deserialize<ConnectionStatus>(json, Json);
        back!.ConnectedSinceEpochMs.Should().Be(1_700_000_000_000L);
    }
}
