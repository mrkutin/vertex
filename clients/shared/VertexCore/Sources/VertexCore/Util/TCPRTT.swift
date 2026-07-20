import Foundation
import Network

/// `TCPRTT` measures the time from issuing `NWConnection.start()` to
/// observing `.ready` (~ one TCP round-trip — SYN → SYN+ACK).
///
/// Originally lived in iOS `TunnelViewModel.measurePing`; lifted to
/// shared so the Network Extension can reuse it for broker-probe
/// reordering without duplicating the NWConnection plumbing on both
/// platforms.
///
/// Returns `Result<Int, Error>` where the success payload is elapsed
/// milliseconds. Cancels the underlying `NWConnection` on resolution
/// (success, failure, or timeout) so we never leak a half-open socket.
public enum TCPRTT {
    public enum ProbeError: Error, Sendable {
        /// Reachable only if `NWEndpoint.Port(rawValue:)` returns nil.
        /// In practice every UInt16 (including 0) maps to a valid Port,
        /// so this case is dead-by-construction today — kept so a future
        /// API change (string ports? IANA lookup?) has a slot ready.
        case invalidPort
        case timeout
        case cancelled
    }

    /// Probe `host:port` once. The continuation resumes exactly once
    /// (guarded by `SingleShotGate`) — `.ready` succeeds with elapsed
    /// milliseconds, `.failed` / `.waiting` fail immediately, and a
    /// fallback `timeout` deadline backstops both. The connection is
    /// always cancelled before resolving so a slow broker doesn't leave
    /// a stale socket sitting on the path.
    ///
    /// The timeout is scheduled as a `DispatchWorkItem` and cancelled
    /// the moment any earlier outcome (`.ready` / `.failed` / `.waiting`)
    /// fires. Without this cancel the timeout block could still execute
    /// just after a late-but-successful `.ready` and race the gate;
    /// while `SingleShotGate` would still pick a deterministic winner,
    /// scheduler ordering on the global queue could occasionally award
    /// it to the timeout block — a successful handshake then visible
    /// to the caller as `.failure(.timeout)`. Cancelling the work item
    /// closes that window.
    public static func measure(host: String, port: UInt16, timeout: TimeInterval) async -> Result<Int, Error> {
        await withCheckedContinuation { (continuation: CheckedContinuation<Result<Int, Error>, Never>) in
            guard let nwPort = NWEndpoint.Port(rawValue: port) else {
                continuation.resume(returning: .failure(ProbeError.invalidPort))
                return
            }
            let connection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: .tcp)
            let queue = DispatchQueue.global(qos: .utility)
            let start = CFAbsoluteTimeGetCurrent()

            // Box the timeout work-item so the gate's onResume closure
            // (which is `@Sendable` under Swift 6 strict concurrency)
            // can cancel it without capturing a `var`.
            let timeoutBox = TimeoutBox()

            // Nullify stateUpdateHandler before cancel to break the
            // retain cycle (handler captures the gate strongly, the
            // gate doesn't need to outlive the resolve), silence the
            // trailing `.cancelled` callback, and cancel the pending
            // timeout block so it can't race a late `.ready`.
            let gate = SingleShotGate<Result<Int, Error>>(continuation: continuation) { [weak connection] in
                timeoutBox.item?.cancel()
                connection?.stateUpdateHandler = nil
                connection?.cancel()
            }

            let item = DispatchWorkItem {
                gate.resume(.failure(ProbeError.timeout))
            }
            timeoutBox.item = item

            connection.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    let elapsedMs = Int((CFAbsoluteTimeGetCurrent() - start) * 1000)
                    gate.resume(.success(elapsedMs))
                case .failed(let error):
                    gate.resume(.failure(error))
                case .waiting(let error):
                    // No path available (airplane mode, no interface).
                    // Surface the failure immediately rather than
                    // sitting through the full timeout.
                    gate.resume(.failure(error))
                case .cancelled:
                    gate.resume(.failure(ProbeError.cancelled))
                default:
                    break
                }
            }

            queue.asyncAfter(deadline: .now() + timeout, execute: item)

            connection.start(queue: queue)
        }
    }

    /// Holds a pending `DispatchWorkItem` so a `@Sendable` closure can
    /// cancel it without capturing a mutable local. Mutation happens on
    /// the calling thread before any concurrent reader exists; the
    /// `@unchecked Sendable` is correct under that one-write-then-read
    /// discipline.
    private final class TimeoutBox: @unchecked Sendable {
        var item: DispatchWorkItem?
    }
}
