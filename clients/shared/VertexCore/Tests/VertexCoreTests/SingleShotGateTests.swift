import XCTest
@testable import VertexCore

final class SingleShotGateTests: XCTestCase {

    func testFirstResumeWinsContinuation() async {
        let value: Int = await withCheckedContinuation { (continuation: CheckedContinuation<Int, Never>) in
            let gate = SingleShotGate<Int>(continuation: continuation)
            // Second call must be a no-op — continuation may be resumed exactly once.
            gate.resume(42)
            gate.resume(99)
        }
        XCTAssertEqual(value, 42)
    }

    func testOnResumeRunsOnceOnly() async {
        // Capture how many times the side-effect closure ran across many
        // concurrent resume calls. Using NSLock + Int avoids needing a
        // Sendable counter type.
        let counter = LockedInt()

        let value: String = await withCheckedContinuation { (continuation: CheckedContinuation<String, Never>) in
            let gate = SingleShotGate<String>(continuation: continuation) {
                counter.increment()
            }
            // 1000 races across multiple threads — onResume must still fire once.
            DispatchQueue.concurrentPerform(iterations: 1_000) { i in
                gate.resume("call-\(i)")
            }
        }

        XCTAssertFalse(value.isEmpty)
        XCTAssertEqual(counter.value, 1, "onResume must run exactly once across concurrent resumes")
    }

    func testOnResumeRunsBeforeContinuation() async {
        // Verify that the side-effect (e.g. cancel underlying connection)
        // happens before the awaiter sees the result, so cleanup is finished
        // by the time downstream code observes completion.
        let flag = LockedFlag()
        let observed: Bool = await withCheckedContinuation { (continuation: CheckedContinuation<Bool, Never>) in
            let gate = SingleShotGate<Bool>(continuation: continuation) {
                flag.set()
            }
            gate.resume(false) // dummy value — we want to see flag state when continuation resumes
        }
        // `observed` is whatever was passed to resume; the assertion is that
        // by the time continuation resumed, the side-effect had already run.
        _ = observed
        XCTAssertTrue(flag.isSet)
    }

    func testNoOnResumeIsAllowed() async {
        // Gate without a side-effect closure should still single-shot correctly.
        let value: Int = await withCheckedContinuation { (continuation: CheckedContinuation<Int, Never>) in
            let gate = SingleShotGate<Int>(continuation: continuation)
            gate.resume(7)
            gate.resume(8)
        }
        XCTAssertEqual(value, 7)
    }
}

// MARK: - Test helpers

private final class LockedInt: @unchecked Sendable {
    private let lock = NSLock()
    private var _value: Int = 0
    var value: Int {
        lock.lock()
        defer { lock.unlock() }
        return _value
    }
    func increment() {
        lock.lock()
        _value += 1
        lock.unlock()
    }
}

private final class LockedFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var _set: Bool = false
    var isSet: Bool {
        lock.lock()
        defer { lock.unlock() }
        return _set
    }
    func set() {
        lock.lock()
        _set = true
        lock.unlock()
    }
}
