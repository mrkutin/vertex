using System.Text.Json.Serialization;

namespace Vertex.Shared;

/// <summary>Cumulative tunnel byte/packet counters since the last connect.</summary>
public sealed record TunnelStats(
    [property: JsonPropertyName("bytesUp")]     long BytesUp     = 0,
    [property: JsonPropertyName("bytesDown")]   long BytesDown   = 0,
    [property: JsonPropertyName("packetsUp")]   long PacketsUp   = 0,
    [property: JsonPropertyName("packetsDown")] long PacketsDown = 0)
{
    public static readonly TunnelStats Zero = new();
}
