import Foundation

/// RFC 1035 TXT record character-string decoder.
///
/// DoH JSON returns the `data` field with TXT character-strings escaped as a
/// space-separated sequence of double-quoted segments:
///
/// ```
/// "Toronto, Canada"          // single character-string
/// "Foo" "Bar"                // two character-strings concatenated
/// "He said \"hi\""           // embedded literal quote
/// ```
///
/// Used by both iOS and macOS clients to read display strings from TXT
/// records on SRV exit targets (e.g. `aws.exit.vertices.ru. IN TXT
/// "Toronto, Canada"`). The parser is intentionally lossless — embedded
/// quotes and backslashes survive — so future TXT values with branding,
/// localization quirks, or vendor strings Just Work without revisiting the
/// parsing code.
public enum TXTParser {
    /// Decode the DoH `data` field. Returns the concatenated content of
    /// each `"..."` segment, with `\\` and `\"` un-escaped. Bytes that
    /// fall outside any segment (in practice the joining whitespace) are
    /// skipped — a TXT with multiple character-strings round-trips as a
    /// single string with no separator, matching how DNS treats them
    /// at the wire level.
    public static func parse(_ data: String) -> String {
        var result = ""
        var inSegment = false
        var escape = false
        for ch in data {
            if escape {
                result.append(ch)
                escape = false
                continue
            }
            if inSegment {
                if ch == "\\" { escape = true; continue }
                if ch == "\"" { inSegment = false; continue }
                result.append(ch)
            } else {
                if ch == "\"" { inSegment = true }
                // bytes outside segments (the joining whitespace) are skipped
            }
        }
        return result
    }
}
