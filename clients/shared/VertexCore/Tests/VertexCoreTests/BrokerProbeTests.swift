import XCTest
import Network
@testable import VertexCore

final class BrokerProbeTests: XCTestCase {

    /// Spin up a one-shot TCP listener on `127.0.0.1:0` and return the
    /// allocated port. The listener accepts and immediately drops any
    /// client (we only care about the SYN→SYN+ACK round trip in the
    /// kernel). Caller closes via the returned closure.
    ///
    /// `NWListener.port` only returns a valid value after the listener
    /// transitions to `.ready` — synchronously reading it right after
    /// `start()` returns 0. We block until `.ready` (typically <10ms)
    /// or fail the test.
    /// Box for the single-resume flag — Swift 6 strict-concurrency
    /// rejects mutating a captured `var` from a `@Sendable` closure.
    private final class ResumedFlag: @unchecked Sendable {
        private let lock = NSLock()
        private var done = false
        func setIfFirst() -> Bool {
            lock.lock(); defer { lock.unlock() }
            if done { return false }
            done = true
            return true
        }
    }

    private func startListener() async throws -> (UInt16, () -> Void) {
        let listener = try NWListener(using: .tcp, on: .any)
        listener.newConnectionHandler = { conn in
            conn.start(queue: .global(qos: .utility))
            conn.cancel()
        }
        let flag = ResumedFlag()
        let port: UInt16 = try await withCheckedThrowingContinuation { (cont: CheckedContinuation<UInt16, Error>) in
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    guard flag.setIfFirst() else { return }
                    if let p = listener.port {
                        cont.resume(returning: p.rawValue)
                    } else {
                        cont.resume(throwing: NSError(domain: "BrokerProbeTests", code: 1))
                    }
                case .failed(let err):
                    guard flag.setIfFirst() else { return }
                    cont.resume(throwing: err)
                default:
                    break
                }
            }
            listener.start(queue: .global(qos: .utility))
        }
        return (port, { listener.cancel() })
    }

    private func brokerURL(host: String, port: Int) -> BrokerURL {
        BrokerURL(scheme: .mqtt, host: host, port: port)
    }

    // MARK: - reorderByRTT

    func testReorderByRTT_singleURLNoOp() async {
        let only = brokerURL(host: "127.0.0.1", port: 1)
        let out = await BrokerProbe.reorderByRTT([only])
        XCTAssertEqual(out, [only])
    }

    func testReorderByRTT_emptyNoOp() async {
        let out = await BrokerProbe.reorderByRTT([])
        XCTAssertTrue(out.isEmpty)
    }

    func testReorderByRTT_failedAtTail() async throws {
        let (livePort, stopLive) = try await startListener()
        defer { stopLive() }

        // 192.0.2.0/24 is RFC 5737 TEST-NET-1 — guaranteed unrouteable,
        // probe will hit the wall-clock deadline.
        let dead = brokerURL(host: "192.0.2.1", port: 1883)
        let live = brokerURL(host: "127.0.0.1", port: Int(livePort))

        let out = await BrokerProbe.reorderByRTT([dead, live], timeout: 0.3)
        XCTAssertEqual(out.count, 2)
        XCTAssertEqual(out[0], live, "live broker should come first")
        XCTAssertEqual(out[1], dead, "failed broker should be at the tail")
    }

    func testReorderByRTT_allFailedKeepsOrder() async {
        let a = brokerURL(host: "192.0.2.1", port: 1883)
        let b = brokerURL(host: "192.0.2.2", port: 1883)

        let out = await BrokerProbe.reorderByRTT([a, b], timeout: 0.2)
        XCTAssertEqual(out, [a, b], "all-failed input must preserve original order")
    }

    // MARK: - reorderWithRTTs

    func testReorderWithRTTs_returnsRttMap() async throws {
        let (port, stop) = try await startListener()
        defer { stop() }

        let dead = brokerURL(host: "192.0.2.1", port: 1883)
        let live = brokerURL(host: "127.0.0.1", port: Int(port))

        let (sorted, rtts) = await BrokerProbe.reorderWithRTTs([dead, live], timeout: 0.3)

        XCTAssertEqual(sorted[0], live)
        XCTAssertEqual(sorted[1], dead)
        XCTAssertNotNil(rtts["127.0.0.1"], "live broker missing from rtt map")
        XCTAssertNil(rtts["192.0.2.1"], "failed broker should not appear in rtt map")
    }

    func testReorderWithRTTs_singleMeasured() async throws {
        let (port, stop) = try await startListener()
        defer { stop() }

        let only = brokerURL(host: "127.0.0.1", port: Int(port))
        let (sorted, rtts) = await BrokerProbe.reorderWithRTTs([only], timeout: 0.3)

        XCTAssertEqual(sorted, [only])
        XCTAssertNotNil(rtts["127.0.0.1"])
    }

    // MARK: - formatOrder

    func testFormatOrder_marksFailures() {
        let a = BrokerURL(scheme: .mqtt, host: "a.example", port: 1883)
        let b = BrokerURL(scheme: .mqtt, host: "b.example", port: 1883)
        let s = BrokerProbe.formatOrder([a, b], rttMs: ["a.example": 25])
        XCTAssertTrue(s.contains("a.example=25ms"))
        XCTAssertTrue(s.contains("b.example=fail"))
    }

    // MARK: - TCPRTT direct

    // Note: TCPRTT.ProbeError.invalidPort is reachable only if
    // `NWEndpoint.Port(rawValue:)` returns nil for the given UInt16,
    // which never happens (any UInt16 — including 0 — is a valid Port).
    // Port 0 is rejected by the kernel at connect time as
    // EADDRNOTAVAIL, surfaced as POSIXError, not ProbeError. The enum
    // case stays for completeness; no unit test needed.

    func testTCPRTT_timeout() async {
        let r = await TCPRTT.measure(host: "192.0.2.1", port: 1883, timeout: 0.15)
        guard case .failure = r else {
            XCTFail("expected failure on unrouteable host")
            return
        }
    }

    func testTCPRTT_success() async throws {
        let (port, stop) = try await startListener()
        defer { stop() }
        let r = await TCPRTT.measure(host: "127.0.0.1", port: port, timeout: 0.3)
        guard case .success(let ms) = r else {
            XCTFail("expected success, got \(r)")
            return
        }
        XCTAssertGreaterThanOrEqual(ms, 0)
        XCTAssertLessThan(ms, 300)
    }
}
