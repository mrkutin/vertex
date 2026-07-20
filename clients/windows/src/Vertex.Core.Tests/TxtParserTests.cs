using FluentAssertions;
using Vertex.Core.Util;
using Xunit;

namespace Vertex.Core.Tests;

/// <summary>
/// Anchors RFC 1035 TXT character-string decoding parity with Swift
/// <c>TXTParser.parse</c>. Inputs mirror the DoH JSON shape we observe
/// from Cloudflare/Google for the SRV exit TXT records (e.g.
/// <c>aws.exit.vertices.ru. IN TXT "Toronto, Canada"</c>).
/// </summary>
public class TxtParserTests
{
    [Fact]
    public void Parse_SingleQuotedSegment_StripsQuotes()
    {
        TxtParser.Parse("\"Toronto, Canada\"").Should().Be("Toronto, Canada");
    }

    [Fact]
    public void Parse_TwoSegments_ConcatenatedWithoutSeparator()
    {
        // Two RFC 1035 character-strings come back joined with adjacent quoted
        // segments; DNS treats them as one logical string at the wire level.
        TxtParser.Parse("\"Foo\" \"Bar\"").Should().Be("FooBar");
    }

    [Fact]
    public void Parse_EscapedQuote_PreservedLiterally()
    {
        TxtParser.Parse("\"He said \\\"hi\\\"\"").Should().Be("He said \"hi\"");
    }

    [Fact]
    public void Parse_EscapedBackslash_PreservedLiterally()
    {
        TxtParser.Parse("\"path\\\\to\"").Should().Be("path\\to");
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsEmpty()
    {
        TxtParser.Parse("").Should().Be("");
        TxtParser.Parse(null!).Should().Be("");
    }

    [Fact]
    public void Parse_NoQuotes_ReturnsEmpty()
    {
        // Unquoted bytes are treated as the joining whitespace and dropped —
        // matches Swift behavior. A future TXT value lacking quotes is
        // malformed; "" is the safe signal so callers fall back to the
        // uppercased ID via NodeLabels.EdgeLabel.
        TxtParser.Parse("Toronto").Should().Be("");
    }

    [Fact]
    public void Parse_UnclosedSegment_ReturnsAccumulatedContent()
    {
        // No closing quote: parser keeps appending until EOS. Lossless, no
        // exceptions; intentional — DoH never produces this shape, so the
        // permissive read is "safer than throwing."
        TxtParser.Parse("\"trailing").Should().Be("trailing");
    }
}
