using System.Text.Json.Serialization;

namespace Vertex.Core.Discovery;

/// <summary>
/// Single SRV record (RFC 2782). Lower priority is preferred; among equal
/// priorities, higher weight wins. Mirror of Swift <c>SRVRecord</c> and
/// Kotlin <c>SrvRecord</c> with byte-for-byte JSON parity for cached
/// SRV-discovery results.
/// </summary>
public sealed record SrvRecord(
    [property: JsonPropertyName("priority")] int    Priority,
    [property: JsonPropertyName("weight")]   int    Weight,
    [property: JsonPropertyName("port")]     int    Port,
    [property: JsonPropertyName("target")]   string Target) : IComparable<SrvRecord>
{
    public int CompareTo(SrvRecord? other)
    {
        if (other is null) return 1;
        if (Priority != other.Priority) return Priority.CompareTo(other.Priority);
        return other.Weight.CompareTo(Weight);
    }
}
