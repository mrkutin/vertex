import XCTest
@testable import VertexCore

final class TXTParserTests: XCTestCase {

    func testSingleSegment() {
        XCTAssertEqual(TXTParser.parse("\"Toronto, Canada\""), "Toronto, Canada")
    }

    func testTwoAdjacentSegments_concatNoSeparator() {
        // "Toronto" "Canada" → TorontoCanada (no separator). Matches RFC 1035
        // semantics: a TXT record's value is the concatenation of all its
        // character-strings; ops who want a comma should put it inside one
        // segment.
        XCTAssertEqual(TXTParser.parse("\"Toronto\" \"Canada\""), "TorontoCanada")
    }

    func testEscapedQuote_preserved() {
        // Wire form: `"He said \"hi\""` → `He said "hi"`.
        XCTAssertEqual(TXTParser.parse("\"He said \\\"hi\\\"\""), "He said \"hi\"")
    }

    func testEscapedBackslash_preserved() {
        XCTAssertEqual(TXTParser.parse("\"a\\\\b\""), "a\\b")
    }

    func testEmptyInput() {
        XCTAssertEqual(TXTParser.parse(""), "")
    }

    func testEmptyQuotedSegment() {
        XCTAssertEqual(TXTParser.parse("\"\""), "")
    }

    func testUnquotedGarbage_dropped() {
        // Anything outside `"..."` segments is treated as inter-segment
        // whitespace and dropped.
        XCTAssertEqual(TXTParser.parse("foo \"hello\" bar"), "hello")
    }
}
