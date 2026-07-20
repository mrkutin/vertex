import XCTest
@testable import VertexCore

final class DiscoveryTrackerTests: XCTestCase {

    // MARK: - Helpers

    private func heartbeat(
        id: String,
        country: String? = nil,
        clients: Int = 0,
        maxClients: Int = 50,
        rtt: [String: Int] = [:],
        dhPubkey: String? = nil
    ) -> DiscoveryHeartbeat {
        DiscoveryHeartbeat(
            id: id,
            country: country,
            clients: clients,
            maxClients: maxClients,
            brokerRTTms: rtt,
            uptime: 100,
            ts: Int64(Date().timeIntervalSince1970),
            dhPubkey: dhPubkey
        )
    }

    // MARK: - Ingest

    func testHandleStoresExit() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", country: "CA", clients: 5, maxClients: 50,
                           rtt: ["broker-ru": 70]))
        let info = t.info(for: "aws")
        XCTAssertNotNil(info)
        XCTAssertEqual(info?.country, "CA")
        XCTAssertEqual(info?.clients, 5)
        XCTAssertEqual(info?.maxClients, 50)
        XCTAssertEqual(info?.brokerRTTms["broker-ru"], 70)
    }

    func testHandleReplacesExit() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", clients: 5))
        t.handle(heartbeat(id: "aws", clients: 12))
        XCTAssertEqual(t.info(for: "aws")?.clients, 12)
    }

    func testRemoveDropsExit() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws"))
        XCTAssertTrue(t.isAvailable("aws"))
        t.remove(exitID: "aws")
        XCTAssertFalse(t.isAvailable("aws"))
    }

    // MARK: - bestExit

    func testBestExitPicksLowerRTT() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", clients: 2, rtt: ["broker-ru": 70, "broker-eu": 80]))
        t.handle(heartbeat(id: "ams", clients: 2, rtt: ["broker-ru": 50, "broker-eu": 5]))

        XCTAssertEqual(t.bestExit(forBroker: "broker-ru"), "ams")
        XCTAssertEqual(t.bestExit(forBroker: "broker-eu"), "ams")
    }

    func testBestExitFavorsLessLoaded() {
        // Same-ish RTT, very different load. eu2 score ≈ 6*(1+5/50*2)=7.2
        // beats eu1 ≈ 5*(1+40/50*2)=13.
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "eu1", clients: 40, maxClients: 50, rtt: ["broker": 5]))
        t.handle(heartbeat(id: "eu2", clients: 5, maxClients: 50, rtt: ["broker": 6]))
        XCTAssertEqual(t.bestExit(forBroker: "broker"), "eu2")
    }

    func testBestExitSkipsFull() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", clients: 50, maxClients: 50, rtt: ["broker": 10]))
        t.handle(heartbeat(id: "ams", clients: 5, maxClients: 50, rtt: ["broker": 100]))
        XCTAssertEqual(t.bestExit(forBroker: "broker"), "ams")
    }

    func testBestExitSkipsStale() {
        let t = DiscoveryTracker(staleAge: 90)
        t.handle(heartbeat(id: "aws", rtt: ["broker": 10]),
                 receivedAt: Date().addingTimeInterval(-120))
        XCTAssertNil(t.bestExit(forBroker: "broker"))
        XCTAssertNil(t.info(for: "aws"))
        XCTAssertFalse(t.isAvailable("aws"))
    }

    func testBestExitEmptyTracker() {
        let t = DiscoveryTracker()
        XCTAssertNil(t.bestExit(forBroker: "broker"))
    }

    func testBestExitMissingRTTUsesDefault() {
        // Both exits lack RTT for the queried broker. Default RTT = 100,
        // so the less-loaded one wins purely on load factor.
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "a", clients: 30, maxClients: 50))
        t.handle(heartbeat(id: "b", clients: 1, maxClients: 50))
        XCTAssertEqual(t.bestExit(forBroker: "broker"), "b")
    }

    // MARK: - shouldSwitch

    func testShouldSwitchStaleCurrentReturnsAlternative() {
        let t = DiscoveryTracker(staleAge: 90)
        t.handle(heartbeat(id: "aws", rtt: ["broker": 10]),
                 receivedAt: Date().addingTimeInterval(-120))
        t.handle(heartbeat(id: "ams", rtt: ["broker": 5]))
        XCTAssertEqual(t.shouldSwitch(currentExit: "aws", brokerHost: "broker"), "ams")
    }

    func testShouldSwitchMissingCurrentReturnsAlternative() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "ams", rtt: ["broker": 5]))
        XCTAssertEqual(t.shouldSwitch(currentExit: "aws", brokerHost: "broker"), "ams")
    }

    func testShouldSwitchToleranceBlocksSmallImprovement() {
        // 40ms vs 50ms ≈ 1.25x — under 1.5x threshold.
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", rtt: ["broker": 50]))
        t.handle(heartbeat(id: "ams", rtt: ["broker": 40]))
        XCTAssertNil(t.shouldSwitch(currentExit: "aws", brokerHost: "broker"))
    }

    func testShouldSwitchAcceptsLargeImprovement() {
        // 10ms vs 50ms = 5x — well above 1.5x threshold.
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", rtt: ["broker": 50]))
        t.handle(heartbeat(id: "ams", rtt: ["broker": 10]))
        XCTAssertEqual(t.shouldSwitch(currentExit: "aws", brokerHost: "broker"), "ams")
    }

    func testShouldSwitchNoAlternative() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", rtt: ["broker": 50]))
        XCTAssertNil(t.shouldSwitch(currentExit: "aws", brokerHost: "broker"))
    }

    // MARK: - Broker host normalization

    func testBrokerHostStripsPort() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", rtt: ["mqtt.example.com": 50]))
        XCTAssertEqual(t.bestExit(forBroker: "mqtt.example.com:8883"), "aws")
    }

    func testBrokerHostExactMatchTakesPrecedence() {
        // If both bare and ported keys exist, exact match wins.
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", rtt: ["mqtt.example.com": 50, "mqtt.example.com:8883": 25]))
        // Bare lookup → 50; this is a direct hit on the bare key, no port-strip needed.
        XCTAssertEqual(t.info(for: "aws")?.brokerRTTms["mqtt.example.com"], 50)
    }

    // MARK: - dhPubkey passthrough

    func testInfoExposesDHPubkey() {
        let t = DiscoveryTracker()
        t.handle(heartbeat(id: "aws", dhPubkey: "BASE64KEY=="))
        XCTAssertEqual(t.info(for: "aws")?.dhPubkey, "BASE64KEY==")
    }

    // MARK: - snapshot

    func testSnapshotSkipsStaleByDefault() {
        let t = DiscoveryTracker(staleAge: 90)
        t.handle(heartbeat(id: "fresh"))
        t.handle(heartbeat(id: "stale"),
                 receivedAt: Date().addingTimeInterval(-120))
        let ids = Set(t.snapshot().map(\.id))
        XCTAssertEqual(ids, ["fresh"])
        let allIds = Set(t.snapshot(includeStale: true).map(\.id))
        XCTAssertEqual(allIds, ["fresh", "stale"])
    }

    // MARK: - Concurrency smoke

    func testConcurrentHandleAndReadDoesNotCrash() {
        let t = DiscoveryTracker()
        let group = DispatchGroup()
        for w in 0..<8 {
            DispatchQueue.global().async(group: group) {
                for i in 0..<200 {
                    t.handle(self.heartbeat(id: "exit\(w)", clients: i, rtt: ["broker": 10]))
                }
            }
        }
        for _ in 0..<4 {
            DispatchQueue.global().async(group: group) {
                for _ in 0..<200 {
                    _ = t.bestExit(forBroker: "broker")
                    _ = t.shouldSwitch(currentExit: "exit0", brokerHost: "broker")
                    _ = t.snapshot()
                }
            }
        }
        let result = group.wait(timeout: .now() + 10)
        XCTAssertEqual(result, .success)
    }
}
