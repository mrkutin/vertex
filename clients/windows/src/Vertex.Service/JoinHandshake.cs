using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vertex.Core.Crypto;
using Vertex.Core.Discovery;
using Vertex.Core.Protocol;
using Vertex.Core.Transport;

namespace Vertex.Service;

/// <summary>
/// Drives the post-CONNACK MQTT join sequence:
/// <list type="number">
///   <item>Subscribe to the assign topic (<c>vpn/+/{name}/control</c>).</item>
///   <item>Wait for at least one fresh discovery heartbeat for the
///   selected exit so we can read its DH public key + LUID.</item>
///   <item>Publish a <see cref="JoinMessage"/> with our identity proof
///   on <c>vpn/{exit}/control/join</c>.</item>
///   <item>Await the corresponding <see cref="AssignMessage"/> with our
///   TUN IP + the exit's session DH pubkey.</item>
///   <item>Derive the per-session <see cref="SessionCrypto"/>.</item>
/// </list>
///
/// Returns the materialised <see cref="JoinResult"/> (or throws on
/// timeout / decode error). Caller (TunnelEngine) plugs the result into
/// <see cref="Tun.PacketPipeline.SetSession"/> and uses
/// <see cref="JoinResult.AssignedIp"/> to configure the WinTUN adapter.
/// </summary>
public sealed class JoinHandshake
{
    public sealed record JoinResult(
        string Exit,
        string AssignedIp,
        string AssignedMask,
        string AssignedGateway,
        SessionCrypto Session);

    private readonly IMqttTransport _transport;
    private readonly DiscoveryTracker _discovery;
    private readonly ILogger _log;

    public JoinHandshake(
        IMqttTransport transport,
        DiscoveryTracker discovery,
        ILogger? log = null)
    {
        _transport = transport;
        _discovery = discovery;
        _log = log ?? NullLogger.Instance;
    }

    public async Task<JoinResult> RunAsync(
        TunnelConfig config,
        IdentityKey identity,
        TimeSpan discoveryWait,
        TimeSpan joinTimeout,
        CancellationToken ct = default)
    {
        // 1. Subscribe to discovery + control topics.
        var assignTcs = new TaskCompletionSource<AssignMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        _transport.Subscribe(Topics.DiscoveryAll, (topic, payload) =>
        {
            try
            {
                var hb = JsonSerializer.Deserialize<DiscoveryHeartbeat>(payload);
                if (hb is not null) _discovery.Handle(hb);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Discovery heartbeat decode failed"); }
        });

        _transport.Subscribe(Topics.ControlAny(config.ClientName), (topic, payload) =>
        {
            try
            {
                var assign = JsonSerializer.Deserialize<AssignMessage>(payload);
                if (assign is not null) assignTcs.TrySetResult(assign);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Assign decode failed"); }
        });

        // 2. Wait for the selected exit's heartbeat.
        ExitInfo? exitInfo = await WaitForExitAsync(config.SelectedExit, discoveryWait, ct).ConfigureAwait(false);
        if (exitInfo is null)
        {
            throw new TransportException(
                $"Exit \"{config.SelectedExit}\" did not announce itself within {discoveryWait.TotalSeconds:F1}s.");
        }

        if (string.IsNullOrEmpty(exitInfo.DhPubkey))
        {
            throw new TransportException(
                $"Exit \"{exitInfo.Id}\" heartbeat is missing dh_pubkey — cannot derive session key.");
        }

        byte[] exitDhPub = Convert.FromBase64String(exitInfo.DhPubkey);

        // 3. Build ephemeral DH keypair + identity proof.
        using var ephemeral = X25519KeyPair.Generate();
        byte[] proof = identity.Proof(exitDhPub, config.ClientName);

        var join = new JoinMessage(
            Name:  config.ClientName,
            Dh:    Convert.ToBase64String(ephemeral.PublicKey),
            Id:    Convert.ToBase64String(identity.PublicKey),
            IdSig: Convert.ToBase64String(proof));

        _log.LogInformation("Publishing JOIN to exit={Exit}", exitInfo.Id);
        _transport.Publish(Topics.Join(exitInfo.Id), JsonSerializer.SerializeToUtf8Bytes(join));

        // 4. Await ASSIGN with timeout.
        AssignMessage assignMsg;
        try
        {
            assignMsg = await assignTcs.Task.WaitAsync(joinTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TransportException(
                $"Exit \"{exitInfo.Id}\" did not respond to JOIN within {joinTimeout.TotalSeconds:F1}s.");
        }

        if (string.IsNullOrEmpty(assignMsg.Dh))
        {
            throw new TransportException("Assign message missing dh — cannot derive session key.");
        }

        // 5. Derive per-session crypto.
        byte[] assignExitDh = Convert.FromBase64String(assignMsg.Dh);
        var session = SessionCrypto.FromDH(
            myPrivate: ephemeral,
            peerPublicKey: assignExitDh,
            clientPublicKey: ephemeral.PublicKey,
            exitPublicKey: assignExitDh);

        _log.LogInformation("ASSIGN received: ip={Ip} gw={Gw}", assignMsg.Ip, assignMsg.Gw);

        return new JoinResult(
            Exit:            exitInfo.Id,
            AssignedIp:      assignMsg.Ip,
            AssignedMask:    assignMsg.Mask ?? "255.255.255.0",
            AssignedGateway: assignMsg.Gw,
            Session:         session);
    }

    /// <summary>
    /// Initial collection window before the first <c>BestExit</c> call.
    /// Retained MQTT heartbeats arrive sequentially per topic
    /// (<c>discovery/exits/aws</c>, <c>discovery/exits/sto</c>, …) and
    /// without a warm-up the first call would race the first heartbeat
    /// in and pick that exit as the only candidate. Mirror of macOS
    /// <c>PacketTunnelProvider.resolveAutoExit:gatherWindow</c> and
    /// Android <c>TunnelEngine.kt</c>.
    /// </summary>
    internal static readonly TimeSpan AutoGatherWindow = TimeSpan.FromMilliseconds(1500);

    private async Task<ExitInfo?> WaitForExitAsync(string selected, TimeSpan timeout, CancellationToken ct)
    {
        // BestExit scores by broker-RTT keyed on the actual broker host
        // (heartbeats publish per-broker RTT maps). Passing literal "auto"
        // would always fall back to DefaultRttMs and reduce the choice to
        // load-only — broken parity with Swift PacketTunnelProvider, which
        // resolves auto-exit against the live broker host. Take the
        // current broker from the transport; null only happens if the
        // transport is between sticky reconnect windows, in which case
        // we wait for the next poll.
        var startedAt = DateTime.UtcNow;
        var deadline = startedAt + timeout;

        // Auto: wait for the gather-window before scoring, but don't
        // wait if the user picked an explicit exit — that path returns
        // as soon as the heartbeat for the selected exit lands.
        if (selected == "auto")
        {
            var warmup = AutoGatherWindow;
            if (warmup > timeout) warmup = timeout;
            try { await Task.Delay(warmup, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
        }

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (selected == "auto")
            {
                var snap = _discovery.Snapshot();
                var brokerHost = _transport.CurrentBroker;
                if (snap.Count > 0 && !string.IsNullOrEmpty(brokerHost))
                {
                    string? best = _discovery.BestExit(brokerHost);
                    if (best is not null) return _discovery.Info(best);
                }
            }
            else
            {
                var info = _discovery.Info(selected);
                if (info is not null) return info;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return null;
    }
}
