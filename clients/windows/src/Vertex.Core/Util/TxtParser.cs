namespace Vertex.Core.Util;

/// <summary>
/// RFC 1035 TXT record character-string decoder.
///
/// DoH JSON returns the <c>data</c> field with TXT character-strings escaped
/// as a space-separated sequence of double-quoted segments:
/// <list type="bullet">
///   <item><c>"Toronto, Canada"</c> — single character-string.</item>
///   <item><c>"Foo" "Bar"</c> — two character-strings concatenated.</item>
///   <item><c>"He said \"hi\""</c> — embedded literal quote.</item>
/// </list>
///
/// Mirror of Swift <c>TXTParser.parse</c> — semantics MUST match because
/// both clients read the same SRV TXT records (e.g.
/// <c>aws.exit.vertices.ru. IN TXT "Toronto, Canada"</c>) for exit display
/// names. The parser is intentionally lossless: embedded quotes and
/// backslashes survive un-escaped, so future TXT values with branding,
/// localization, or vendor strings work without revisiting the parser.
///
/// Note: Kotlin's port (<c>SrvDiscovery.queryDohTxt</c>) collapses with a
/// naive <c>.replace("\"", "")</c>; that is good enough for the current
/// "City, Country" payload but loses <c>\"</c> semantics. We follow the
/// stricter Swift implementation here per the iOS-is-reference rule.
/// </summary>
public static class TxtParser
{
    /// <summary>
    /// Decode the DoH <c>data</c> field. Returns the concatenated content
    /// of each <c>"..."</c> segment, with <c>\\</c> and <c>\"</c>
    /// un-escaped. Bytes outside any segment (in practice the joining
    /// whitespace) are skipped — a TXT with multiple character-strings
    /// round-trips as a single string with no separator, matching how DNS
    /// treats them at the wire level.
    /// </summary>
    public static string Parse(string data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;

        var result = new System.Text.StringBuilder(data.Length);
        bool inSegment = false;
        bool escape = false;

        foreach (var ch in data)
        {
            if (escape)
            {
                result.Append(ch);
                escape = false;
                continue;
            }
            if (inSegment)
            {
                if (ch == '\\') { escape = true; continue; }
                if (ch == '"')  { inSegment = false; continue; }
                result.Append(ch);
            }
            else
            {
                if (ch == '"') inSegment = true;
                // bytes outside segments (the joining whitespace) are skipped
            }
        }

        return result.ToString();
    }
}
