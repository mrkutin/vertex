using System.Net;
using System.Net.Sockets;

namespace Vertex.Core.Config;

/// <summary>
/// Parsed MQTT broker URL. Supports four schemes byte-for-byte equivalent
/// to Swift <c>BrokerURL</c> and Kotlin <c>BrokerUrl</c>:
/// <list type="bullet">
///   <item><c>mqtt://host[:port]</c>  — plain TCP MQTT (default port 1883).</item>
///   <item><c>mqtts://host[:port]</c> — TCP + TLS MQTT (default port 8883).</item>
///   <item><c>ws://host[:port]/[path]</c>   — plain WebSocket MQTT (default port 80).</item>
///   <item><c>wss://host[:port]/[path]</c>  — TLS WebSocket MQTT (default port 443).</item>
/// </list>
/// </summary>
public sealed record BrokerUrl(string Scheme, string Host, int Port, string Path)
{
    public bool IsTls       => Scheme == "mqtts" || Scheme == "wss";
    public bool IsWebSocket => Scheme == "ws"    || Scheme == "wss";

    public static BrokerUrl Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("Broker URL must be non-empty.", nameof(url));
        }

        int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            throw new FormatException($"Broker URL '{url}' is missing a scheme.");
        }

        string scheme = url[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("mqtt" or "mqtts" or "ws" or "wss"))
        {
            throw new FormatException($"Broker URL '{url}' uses unsupported scheme '{scheme}'.");
        }

        string rest = url[(schemeEnd + 3)..];

        // Split on first '/' to peel host:port off the path.
        int pathStart = rest.IndexOf('/');
        string authority = pathStart < 0 ? rest : rest[..pathStart];
        string path      = pathStart < 0 ? "/"  : rest[pathStart..];

        if (authority.Length == 0)
        {
            throw new FormatException($"Broker URL '{url}' is missing a host.");
        }

        string host;
        int port;

        // Bracketed IPv6 literal? `[::1]:8883`
        if (authority.StartsWith('['))
        {
            int closing = authority.IndexOf(']');
            if (closing < 0) throw new FormatException($"Broker URL '{url}' has unbalanced IPv6 brackets.");
            host = authority[1..closing];
            string after = authority[(closing + 1)..];
            port = after.StartsWith(':') ? int.Parse(after[1..]) : DefaultPort(scheme);
        }
        else
        {
            int colon = authority.LastIndexOf(':');
            if (colon < 0)
            {
                host = authority;
                port = DefaultPort(scheme);
            }
            else
            {
                host = authority[..colon];
                port = int.Parse(authority[(colon + 1)..]);
            }
        }

        if (port is < 1 or > 65535)
        {
            throw new FormatException($"Broker URL '{url}' has out-of-range port {port}.");
        }

        return new BrokerUrl(scheme, host, port, path);
    }

    /// <summary>Non-throwing variant for UI bindings where invalid input is expected to be common.</summary>
    public static bool TryParse(string url, out BrokerUrl result)
    {
        try { result = Parse(url); return true; }
        catch { result = null!; return false; }
    }

    private static int DefaultPort(string scheme) => scheme switch
    {
        "mqtt"  => 1883,
        "mqtts" => 8883,
        "ws"    => 80,
        "wss"   => 443,
        _       => throw new ArgumentException($"Unknown scheme '{scheme}'."),
    };

    public override string ToString() => $"{Scheme}://{Host}:{Port}{Path}";

    /// <summary>
    /// Default per-call deadline for <see cref="ResolveIpsAsync"/>.
    /// Caps the synchronous <c>getaddrinfo</c> call that
    /// <see cref="Dns.GetHostAddressesAsync"/> wraps so a misconfigured
    /// resolver / captive portal can't stall Connect by 30s × N
    /// brokers. Same magnitude as <c>SrvResolver._requestTimeout</c>.
    /// </summary>
    public static readonly TimeSpan DefaultResolveTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Resolve the broker hostname to IPv4 addresses for /32 route
    /// exclusion. Mirror of Swift <c>BrokerURL.resolveIPs()</c> — both
    /// platforms feed the result into broker-bypass routes so the long-
    /// lived MQTT TCP socket survives the moment the default route flips
    /// to TUN. If the host already parses as a literal IP, returns it
    /// unchanged; on DNS failure / non-IPv4-only zones, returns empty.
    /// </summary>
    public async Task<IReadOnlyList<IPAddress>> ResolveIpsAsync(
        CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        // Literal IP — no DNS round-trip needed.
        if (IPAddress.TryParse(Host, out var literal))
        {
            return literal.AddressFamily == AddressFamily.InterNetwork
                ? new[] { literal }
                : Array.Empty<IPAddress>();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultResolveTimeout);

        try
        {
            var addrs = await Dns.GetHostAddressesAsync(Host, AddressFamily.InterNetwork, cts.Token).ConfigureAwait(false);
            return addrs.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                        .Distinct()
                        .ToArray();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller's CT — surface as cancellation, don't swallow into empty.
            throw;
        }
        catch (Exception)
        {
            // Caller falls back to skipping bypass for this broker; same
            // shape as Swift's empty-array return on getaddrinfo failure.
            // Includes the per-call timeout (cts fired) and SocketException
            // (NXDOMAIN / no path / refused).
            return Array.Empty<IPAddress>();
        }
    }
}
