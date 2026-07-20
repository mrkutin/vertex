import Foundation
import Testing
@testable import VertexCore

/// Regression coverage for the sticky-broker / auth-failure interaction
/// described in `clients/PENDING_TRANSPORT_FIXES.md` ("Bug #1").
///
/// Scenario: YC primary, Sber backup. After a successful day on YC, the
/// admin rotates the password on YC. Sticky reconnect has long since
/// promoted YC to `brokers[0]`. Without the fix, `start()` after a
/// reboot tries YC, hits `CONNACK 0x86 "Bad credentials"`, the loop
/// gives up — and Sber, which is healthy, is never tried again.
///
/// With the fix the offending broker is demoted to the tail before the
/// reconnect loop is killed, so the next `start()` tries the original
/// primary first.
///
/// We don't stand up a real broker here — `_testFireAuthFailureDisconnect`
/// synthesises the same `.disconnected(connackReason:)` event the
/// production code would observe.
@Suite("MQTTTransport Bug #1 — sticky+auth lock-out")
struct MQTTTransportBug1Tests {

    @Test("CONNACK auth-failure demotes the offending broker from index 0 to tail")
    func authFailureDemotesStickyBroker() async {
        let yc   = BrokerURL(string: "mqtts://yc.example:8883")!
        let sber = BrokerURL(string: "mqtts://sber.example:8883")!

        // The onAuthFailure callback is non-nil — the demotion path inside
        // handleConnectionEvent only fires if the host actually subscribed
        // to auth notifications (otherwise the disconnect falls through to
        // the regular reconnect loop).
        let authFired = ManagedAtomicBox(false)
        let transport = MQTTTransport(
            brokers: [yc, sber],
            username: "user",
            password: "stale",
            clientID: "vtx-test-bug1",
            onAuthFailure: { _, _ in authFired.set(true) }
        )

        #expect(transport._testBrokerHosts == ["yc.example", "sber.example"])

        transport._testFireAuthFailureDisconnect(connackReason: 0x86)

        #expect(authFired.get(), "onAuthFailure callback must fire")
        #expect(transport._testBrokerHosts == ["sber.example", "yc.example"],
                "broker that auth-rejected must be demoted to the tail")
    }

    @Test("Single broker is left alone (nothing to demote against)")
    func singleBrokerIsNotReordered() async {
        let yc = BrokerURL(string: "mqtts://yc.example:8883")!

        let transport = MQTTTransport(
            brokers: [yc],
            username: "user",
            password: "stale",
            clientID: "vtx-test-bug1-single",
            onAuthFailure: { _, _ in }
        )

        transport._testFireAuthFailureDisconnect(connackReason: 0x86)

        #expect(transport._testBrokerHosts == ["yc.example"],
                "single-broker setup has nowhere to demote — list must stay intact")
    }

    @Test("connackReason=0 (success-style disconnect) does NOT trigger demotion")
    func connackZeroIsNotAuthFailure() async {
        let yc   = BrokerURL(string: "mqtts://yc.example:8883")!
        let sber = BrokerURL(string: "mqtts://sber.example:8883")!
        let authFired = ManagedAtomicBox(false)

        let transport = MQTTTransport(
            brokers: [yc, sber],
            username: "user",
            password: "stale",
            clientID: "vtx-test-bug1-zero",
            onAuthFailure: { _, _ in authFired.set(true) }
        )

        transport._testFireAuthFailureDisconnect(connackReason: 0)

        #expect(!authFired.get(), "connackReason=0 must NOT escalate as auth failure")
        #expect(transport._testBrokerHosts == ["yc.example", "sber.example"],
                "non-auth disconnect must leave the list untouched")
    }

    @Test("Auth failure with no onAuthFailure subscriber STILL demotes (paritет с Kotlin)")
    func noSubscriberStillDemotes() async {
        let yc   = BrokerURL(string: "mqtts://yc.example:8883")!
        let sber = BrokerURL(string: "mqtts://sber.example:8883")!

        // Host didn't supply onAuthFailure. Demotion is pure routing
        // state and must fire regardless of whether anyone wants to be
        // told — earlier the gate `let onAuth = onAuthFailure`
        // accidentally tied the two together; reviewer caught the
        // asymmetry against Kotlin and we removed the guard.
        let transport = MQTTTransport(
            brokers: [yc, sber],
            username: "user",
            password: "stale",
            clientID: "vtx-test-bug1-nocb",
            onAuthFailure: nil
        )

        transport._testFireAuthFailureDisconnect(connackReason: 0x86)

        #expect(transport._testBrokerHosts == ["sber.example", "yc.example"],
                "demotion fires regardless of onAuthFailure subscriber")
    }

    @Test("Two consecutive auth failures rotate the list correctly")
    func twoConsecutiveDemotionsRotateProperly() async {
        let yc   = BrokerURL(string: "mqtts://yc.example:8883")!
        let sber = BrokerURL(string: "mqtts://sber.example:8883")!

        let transport = MQTTTransport(
            brokers: [yc, sber],
            username: "user",
            password: "stale",
            clientID: "vtx-test-bug1-double",
            onAuthFailure: { _, _ in }
        )

        // First auth-fail: YC at index 0 → demote to tail. Order: [Sber, YC].
        transport._testFireAuthFailureDisconnect(connackReason: 0x86)
        #expect(transport._testBrokerHosts == ["sber.example", "yc.example"])

        // Re-arm onAuthFailure (the previous fire nulled it) so the
        // second synthesised event hits the same demotion path.
        transport._testReinstallAuthFailureCallback { _, _ in }

        // Second auth-fail (now Sber is at index 0) → demote Sber too.
        // Order: [YC, Sber] — back to the original.
        transport._testFireAuthFailureDisconnect(connackReason: 0x86)
        #expect(transport._testBrokerHosts == ["yc.example", "sber.example"],
                "two demotions cycle through the list and bring the original primary back")
    }
}

// MARK: - Test helpers

/// Tiny atomic box. We don't pull in swift-atomics for one boolean — the
/// queue-bound dispatch is enough since the test thread only reads after
/// _testFireAuthFailureDisconnect's queue.sync has returned.
private final class ManagedAtomicBox<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Value
    init(_ initial: Value) { self.value = initial }
    func get() -> Value { lock.lock(); defer { lock.unlock() }; return value }
    func set(_ new: Value) { lock.lock(); value = new; lock.unlock() }
}
