import XCTest
@testable import VertexCore

final class NodeLabelsTests: XCTestCase {

    // MARK: - vertexLabel

    func testVertexLabel_stripsMqttPrefix_yc() {
        let result = NodeLabels.vertexLabel(host: "mqtt-yc.vertices.ru", index: 0)
        XCTAssertEqual(result.shortName, "YC")
        XCTAssertEqual(result.code, "V\u{2080} \u{00B7} YC")
    }

    func testVertexLabel_stripsMqttPrefix_sber() {
        let result = NodeLabels.vertexLabel(host: "mqtt-sber.vertices.ru", index: 1)
        XCTAssertEqual(result.shortName, "SBER")
        XCTAssertEqual(result.code, "V\u{2081} \u{00B7} SBER")
    }

    func testVertexLabel_stripsMqttPrefix_other() {
        let result = NodeLabels.vertexLabel(host: "mqtt-fra.example.com", index: 2)
        XCTAssertEqual(result.shortName, "FRA")
        XCTAssertEqual(result.code, "V\u{2082} \u{00B7} FRA")
    }

    func testVertexLabel_unknownNoMqttPrefix() {
        let result = NodeLabels.vertexLabel(host: "broker.example.com", index: 3)
        XCTAssertEqual(result.shortName, "BROKER")
    }

    func testVertexLabel_indexBeyondSubscriptRange() {
        // Index ≥ 10 falls back to plain decimal digits.
        let result = NodeLabels.vertexLabel(host: "mqtt-yc.vertices.ru", index: 12)
        XCTAssertEqual(result.code, "V12 \u{00B7} YC")
    }

    // MARK: - edgeLabel

    func testEdgeLabel_displayOverride() {
        // City/country comes from a TXT record on the SRV target. The
        // override wins regardless of caller's casing on the ID.
        let result = NodeLabels.edgeLabel("aws", index: 0, displayOverride: "Toronto, Canada")
        XCTAssertEqual(result.display, "Toronto, Canada")
        XCTAssertEqual(result.code, "E\u{2080} \u{00B7} AWS")
    }

    func testEdgeLabel_displayOverride_preservesCodeCase() {
        let result = NodeLabels.edgeLabel("AWS", index: 0, displayOverride: "Toronto, Canada")
        XCTAssertEqual(result.display, "Toronto, Canada")
        XCTAssertEqual(result.code, "E\u{2080} \u{00B7} AWS")
    }

    func testEdgeLabel_noOverride_fallsBackToUppercase() {
        let result = NodeLabels.edgeLabel("aws", index: 0)
        XCTAssertEqual(result.display, "AWS")
        XCTAssertEqual(result.code, "E\u{2080} \u{00B7} AWS")
    }

    func testEdgeLabel_emptyOverride_fallsBackToUppercase() {
        let result = NodeLabels.edgeLabel("sto", index: 1, displayOverride: "")
        XCTAssertEqual(result.display, "STO")
        XCTAssertEqual(result.code, "E\u{2081} \u{00B7} STO")
    }

    func testEdgeLabel_unknownIDUppercased() {
        let result = NodeLabels.edgeLabel("fra", index: 2)
        XCTAssertEqual(result.display, "FRA")
        XCTAssertEqual(result.code, "E\u{2082} \u{00B7} FRA")
    }

    // MARK: - host(of:)

    func testHostOf_validURL() {
        XCTAssertEqual(NodeLabels.host(of: "mqtts://mqtt-yc.vertices.ru:8883"), "mqtt-yc.vertices.ru")
        XCTAssertEqual(NodeLabels.host(of: "wss://mqtt-sber.vertices.ru:443"), "mqtt-sber.vertices.ru")
    }

    func testHostOf_noScheme() {
        XCTAssertNil(NodeLabels.host(of: "mqtt-yc.vertices.ru"))
    }

    func testHostOf_garbage() {
        XCTAssertNil(NodeLabels.host(of: "not a url at all"))
    }

    // MARK: - uniqueHosts

    func testUniqueHosts_dedupsAcrossSchemes() {
        let urls = [
            "mqtts://mqtt-yc.vertices.ru:8883",
            "wss://mqtt-yc.vertices.ru:443",
            "mqtts://mqtt-sber.vertices.ru:8883",
            "wss://mqtt-sber.vertices.ru:443",
        ]
        XCTAssertEqual(
            NodeLabels.uniqueHosts(in: urls),
            ["mqtt-yc.vertices.ru", "mqtt-sber.vertices.ru"]
        )
    }

    func testUniqueHosts_preservesEncounterOrder() {
        // Sber appears first → it must come first in the result.
        let urls = [
            "mqtts://mqtt-sber.vertices.ru:8883",
            "mqtts://mqtt-yc.vertices.ru:8883",
            "wss://mqtt-sber.vertices.ru:443",
        ]
        XCTAssertEqual(
            NodeLabels.uniqueHosts(in: urls),
            ["mqtt-sber.vertices.ru", "mqtt-yc.vertices.ru"]
        )
    }

    func testUniqueHosts_emptyInput() {
        XCTAssertEqual(NodeLabels.uniqueHosts(in: []), [])
    }

    func testUniqueHosts_skipsUnparseable() {
        let urls = ["not-a-url", "mqtts://mqtt-yc.vertices.ru:8883"]
        XCTAssertEqual(NodeLabels.uniqueHosts(in: urls), ["mqtt-yc.vertices.ru"])
    }

    // MARK: - protocols(forHost:in:)

    func testProtocols_orderedAndUppercased() {
        let urls = [
            "mqtts://mqtt-yc.vertices.ru:8883",
            "wss://mqtt-yc.vertices.ru:443",
        ]
        XCTAssertEqual(
            NodeLabels.protocols(forHost: "mqtt-yc.vertices.ru", in: urls),
            ["MQTTS", "WSS"]
        )
    }

    func testProtocols_dedupsRepeatedScheme() {
        let urls = [
            "mqtts://mqtt-yc.vertices.ru:8883",
            "mqtts://mqtt-yc.vertices.ru:8884",
        ]
        XCTAssertEqual(
            NodeLabels.protocols(forHost: "mqtt-yc.vertices.ru", in: urls),
            ["MQTTS"]
        )
    }

    func testProtocols_filtersByHost() {
        let urls = [
            "mqtts://mqtt-yc.vertices.ru:8883",
            "wss://mqtt-sber.vertices.ru:443",
        ]
        XCTAssertEqual(
            NodeLabels.protocols(forHost: "mqtt-yc.vertices.ru", in: urls),
            ["MQTTS"]
        )
        XCTAssertEqual(
            NodeLabels.protocols(forHost: "mqtt-sber.vertices.ru", in: urls),
            ["WSS"]
        )
    }

    func testProtocols_unknownHostReturnsEmpty() {
        let urls = ["mqtts://mqtt-yc.vertices.ru:8883"]
        XCTAssertEqual(
            NodeLabels.protocols(forHost: "missing.vertices.ru", in: urls),
            []
        )
    }
}
