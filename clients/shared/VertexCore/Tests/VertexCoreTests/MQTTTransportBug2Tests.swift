import Foundation
import Testing
@testable import VertexCore

/// Regression coverage for Bug #2 in `clients/PENDING_TRANSPORT_FIXES.md`:
/// subscriptions used to be `[(pattern, handler)]` (a list of pairs),
/// so calling `subscribe(pattern:handler:)` for an already-registered
/// pattern *appended* a duplicate. Realistic call paths (JoinManager
/// retry, ControlChannel.reattach) re-register on every reconnect, so
/// after N reconnects one matching PUBLISH fired the handler N times —
/// on the join control channel this manifested as N parallel DH
/// handshake replies per client and exit log floods like
/// "Assigning 10.9.0.5 to client iphone" repeated N times.
///
/// The fix replaces the list with a `[String: handler]` map; subscribe
/// becomes idempotent (same pattern → handler is *replaced*).
@Suite("MQTTTransport Bug #2 — subscription handler stacking")
struct MQTTTransportBug2Tests {

    private static let yc = BrokerURL(string: "mqtts://yc.example:8883")!

    @Test("Re-subscribing the same pattern replaces the handler — only the latest fires")
    func resubscribeReplacesHandler() {
        let transport = MQTTTransport(
            brokers: [Self.yc],
            username: "user",
            password: "pass",
            clientID: "vtx-test-bug2-replace"
        )

        let aHits = Counter()
        let bHits = Counter()

        transport.subscribe(pattern: "vpn/aws/iphone/in") { _, _ in aHits.inc() }
        transport.subscribe(pattern: "vpn/aws/iphone/in") { _, _ in bHits.inc() }

        transport._testInjectPublish(topic: "vpn/aws/iphone/in", payload: Data())

        #expect(aHits.value == 0,
                "old handler must NOT fire after re-subscribe (with the bug it would still fire)")
        #expect(bHits.value == 1,
                "new handler must fire exactly once (with the bug it would fire twice — old + new)")
    }

    @Test("Many resubscribes for the same pattern still produce a single dispatch")
    func tenResubscribesStillSingleDispatch() {
        let transport = MQTTTransport(
            brokers: [Self.yc],
            username: "user",
            password: "pass",
            clientID: "vtx-test-bug2-stack"
        )

        let hits = Counter()
        // Simulate 10 reconnect-driven re-registrations of the same pattern.
        for _ in 0..<10 {
            transport.subscribe(pattern: "vpn/aws/iphone/in") { _, _ in hits.inc() }
        }

        transport._testInjectPublish(topic: "vpn/aws/iphone/in", payload: Data())

        #expect(hits.value == 1,
                "10 re-registrations must collapse to 1 handler invocation; with the bug it would be 10")
    }

    @Test("Wildcard pattern re-registration also collapses to one handler")
    func wildcardPatternIsAlsoIdempotent() {
        let transport = MQTTTransport(
            brokers: [Self.yc],
            username: "user",
            password: "pass",
            clientID: "vtx-test-bug2-wildcard"
        )

        let hits = Counter()
        // Same wildcard pattern subscribed three times — each call would
        // append in the buggy implementation. With the map fix the
        // pattern is keyed verbatim and only the last handler survives.
        // JoinManager / ExitDiscovery don't use wildcards today but
        // could (e.g. `discovery/exits/+`), so we lock the contract in.
        transport.subscribe(pattern: "discovery/exits/+") { _, _ in /* old A */ }
        transport.subscribe(pattern: "discovery/exits/+") { _, _ in /* old B */ }
        transport.subscribe(pattern: "discovery/exits/+") { _, _ in hits.inc() }

        transport._testInjectPublish(topic: "discovery/exits/aws", payload: Data())
        transport._testInjectPublish(topic: "discovery/exits/sto", payload: Data())

        #expect(hits.value == 2,
                "wildcard pattern with 3 re-subscribes must dispatch exactly 2 PUBLISHes through the latest handler — with the bug it would be 6")
    }

    @Test("Distinct patterns each route to their own handler")
    func distinctPatternsRouteSeparately() {
        let transport = MQTTTransport(
            brokers: [Self.yc],
            username: "user",
            password: "pass",
            clientID: "vtx-test-bug2-distinct"
        )

        let inHits  = Counter()
        let ctlHits = Counter()

        transport.subscribe(pattern: "vpn/aws/iphone/in")     { _, _ in inHits.inc() }
        transport.subscribe(pattern: "vpn/aws/iphone/control") { _, _ in ctlHits.inc() }

        transport._testInjectPublish(topic: "vpn/aws/iphone/in",      payload: Data())
        transport._testInjectPublish(topic: "vpn/aws/iphone/control", payload: Data())
        transport._testInjectPublish(topic: "vpn/aws/iphone/in",      payload: Data())

        #expect(inHits.value  == 2, "in-topic dispatched twice")
        #expect(ctlHits.value == 1, "control-topic dispatched once; in-topic must not bleed into it")
    }
}

// MARK: - Test helpers

/// Tiny thread-safe counter — `_testInjectPublish` does `queue.sync`
/// so handler runs on the transport queue while the test thread is
/// blocked, but reading `value` afterwards still warrants a barrier.
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var _value = 0
    func inc() { lock.lock(); _value += 1; lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return _value }
}
