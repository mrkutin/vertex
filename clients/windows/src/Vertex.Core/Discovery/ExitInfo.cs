namespace Vertex.Core.Discovery;

/// <summary>
/// Snapshot of one exit's most recent discovery heartbeat plus the
/// wall-clock time the tracker observed it. Mirror of Swift
/// <c>VertexCore.ExitInfo</c>, Kotlin <c>ExitInfo</c>, and Go
/// <c>pkg/discovery.ExitInfo</c>.
/// </summary>
public sealed record ExitInfo(
    string  Id,
    string? Country,
    int     Clients,
    int     MaxClients,
    IReadOnlyDictionary<string, int> BrokerRttMs,
    string? DhPubkey,
    DateTime ReceivedAt);
