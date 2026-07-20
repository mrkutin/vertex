import Foundation

/// Lock-protected single-resume gate around a `CheckedContinuation`.
///
/// Use this when multiple producers race to provide the result of an async
/// operation — e.g. an `NWConnection.stateUpdateHandler` competing with a
/// timeout block. Only the first call to `resume(_:)` is honored; subsequent
/// calls are silently ignored. An optional `onResume` side-effect runs once,
/// outside the lock, before the continuation is resumed (typical use:
/// nullify a state callback and cancel the underlying connection so the
/// callback chain stops feeding events back at us).
///
/// Thread-safety: `NSLock` + a `done` flag. `onResume` and the continuation
/// resume are both invoked outside the lock to avoid re-entrancy when the
/// side-effect itself triggers further state callbacks (which then race
/// back into `resume(_:)`).
public final class SingleShotGate<T>: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    private let continuation: CheckedContinuation<T, Never>
    private let onResume: (@Sendable () -> Void)?

    public init(continuation: CheckedContinuation<T, Never>, onResume: (@Sendable () -> Void)? = nil) {
        self.continuation = continuation
        self.onResume = onResume
    }

    public func resume(_ value: sending T) {
        lock.lock()
        if done {
            lock.unlock()
            return
        }
        done = true
        lock.unlock()
        onResume?()
        continuation.resume(returning: value)
    }
}
