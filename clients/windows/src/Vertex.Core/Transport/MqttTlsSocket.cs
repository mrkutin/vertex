using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Vertex.Core.Config;

namespace Vertex.Core.Transport;

/// <summary>
/// TCP socket with optional TLS for MQTT brokers reachable as <c>mqtt://</c>
/// or <c>mqtts://</c>. Equivalent of the Swift NWConnection + NWProtocolTLS
/// pairing minus the iOS / NEPacketTunnelProvider trust-evaluation hack
/// — on Windows, the default <see cref="SslStream"/> validation works
/// inside the Windows Service sandbox (LocalSystem can read the OS cert
/// store for already-rooted CA chains).
/// </summary>
public sealed class MqttTlsSocket : IMqttSocket
{
    /// <summary>
    /// Defensive TCP connect timeout for callers who don't pass their own
    /// cancellation. Without it, <see cref="TcpClient.ConnectAsync"/> would
    /// wait the full Windows SYN-retry budget (≈21 s) on a dead broker.
    /// Phase 1.5's <c>MqttTransport</c> passes a tighter token and this
    /// is a no-op there.
    /// </summary>
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);

    public BrokerUrl Broker { get; }

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;

    public MqttTlsSocket(BrokerUrl broker)
    {
        if (broker.IsWebSocket)
        {
            throw new ArgumentException("MqttTlsSocket only handles mqtt:// and mqtts:// URLs.", nameof(broker));
        }
        Broker = broker;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(DefaultConnectTimeout);

        var tcp = new TcpClient { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(Broker.Host, Broker.Port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        _tcp = tcp;

        Stream stream = tcp.GetStream();

        if (Broker.IsTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            try
            {
                // .NET 8 default trust evaluation is correct inside a
                // LocalSystem Windows Service (full read access to the OS
                // trust store, unlike the iOS sandbox where Swift had to
                // override sec_protocol_options_set_verify_block). TODO:
                // when production CA story stabilises, add cert pinning
                // (RemoteCertificateValidationCallback comparing the
                // server's chain against a known SPKI hash).
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = Broker.Host,                                 // SNI
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, connectCts.Token).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                tcp.Dispose();
                _tcp = null;
                throw;
            }
            stream = ssl;
        }

        _stream = stream;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        var s = _stream ?? throw new InvalidOperationException("Socket not connected.");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await s.WriteAsync(packet, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
    {
        // NB: half-closed link detection (broker silent but no FIN) is the
        // responsibility of MqttConnection's PINGRESP watchdog — the TLS
        // ReadAsync below will hang here indefinitely on a stale path.
        var s = _stream ?? throw new InvalidOperationException("Socket not connected.");
        return await s.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public Task CloseAsync()
    {
        // Hard close — drop the TCP socket without waiting for a TLS
        // close_notify. An unreachable peer can't ack, and we'd block
        // here while the kernel times out the shutdown.
        try { _tcp?.Client.Close(0); } catch { /* swallow */ }
        try { _stream?.Dispose(); } catch { /* swallow */ }
        try { _tcp?.Dispose(); }    catch { /* swallow */ }
        _stream = null;
        _tcp = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
