import Foundation
import os

/// MQTT 5.0 transport for VPN tunnel data.
///
/// Runs on NWConnection (Network.framework) — safe for Network Extension use.
/// Handles broker failover, automatic reconnection, and resubscription.
/// Conforms to `Transport` (the abstract pub/sub contract used by
/// PacketTunnelProvider) so callers can swap the implementation later
/// without touching the data-plane code.
///
/// **Liveness model.** A long iteration through several iOS network APIs
/// (NEProvider.defaultPath KVO; process-wide NWPathMonitor; per-connection
/// viability/betterPath/pathUpdate handlers) showed each of them either
/// silent or noisy on at least one real-device scenario. The single
/// signal that consistently reflects reality on iPhone is application-
/// level keepalive: the broker is unreachable when MQTT PINGRESP doesn't
/// come back. Everything in this file is built around that one signal.
public final class MQTTTransport: Transport, @unchecked Sendable {
    private let queue = DispatchQueue(label: "ru.vertices.mqtt")
    private let logger = Logger(subsystem: "ru.vertices", category: "mqtt")

    // Configuration
    private var brokers: [BrokerURL]
    private let username: String
    private let password: String
    private let clientID: String

    // Tunables — see comments at use sites for the rationale.
    private static let connectTimeout: DispatchTimeInterval = .seconds(15)

    // State
    private var connection: MQTTConnection?
    // Pattern → handler map. Map (not list of pairs) so a second
    // subscribe(pattern:handler:) for the same pattern *replaces* the
    // handler instead of stacking it. See clients/PENDING_TRANSPORT_FIXES.md
    // "Bug #2": with a list of pairs, every reconnect-resubscribe path
    // (JoinManager retry, ControlChannel.reattach) appended yet another
    // copy of the same (pattern, handler) — N reconnects ⇒ N handler
    // calls per matching PUBLISH ⇒ exit log floods with N parallel
    // "Assigning IP" replies for one client.
    //
    // Insertion order is not preserved by Dictionary; if dispatch
    // ordering becomes load-bearing later, switch to OrderedDictionary
    // from swift-collections. Today no consumer relies on order —
    // JoinManager, ControlChannel, and ExitDiscovery each subscribe a
    // distinct pattern.
    private var subscriptions: [String: @Sendable (String, Data) -> Void] = [:]
    private var _isReady = false
    private var shouldReconnect = false
    private var currentBrokerIndex = 0
    private var reconnectAttempt = 0
    private var reconnectTimer: DispatchSourceTimer?
    private var connectTimeoutTimer: DispatchSourceTimer?
    /// Invoked when the link is declared dead (PINGRESP timeout, .failed
    /// on unsatisfied path, .waiting after ready) or after persistent
    /// connect-failure. The host's PacketTunnelProvider responds with
    /// `cancelTunnelWithError`, and the on-demand rule restarts the
    /// extension scoped to whatever interface iOS now considers best.
    /// Stored as a private var, only accessed under `queue` — the
    /// callback is supplied at init time so there's no cross-thread
    /// publish hazard on the var itself.
    private var onFatalError: (@Sendable (String) -> Void)?

    /// Invoked when the broker rejects our CONNECT (any non-zero CONNACK
    /// reason — bad creds, banned, server unavailable). Auth failures
    /// don't recover by retrying with the same credentials, so the
    /// transport stops reconnecting and lets the host surface a
    /// user-fixable error. Caller gets `(reasonCode, reasonString)`.
    private var onAuthFailure: (@Sendable (UInt8, String) -> Void)?

    /// Counts consecutive connect-attempt timeouts since the last CONNACK.
    /// After several in a row, escalate to `onFatalError` as a safety net
    /// when the immediate linkDead path didn't fire.
    private var consecutiveConnectFailures = 0

    // State stream for UI
    private var stateContinuation: AsyncStream<TransportState>.Continuation?
    private let _stateUpdates: AsyncStream<TransportState>

    // Continuation for waitReady
    private var readyContinuation: CheckedContinuation<Void, any Error>?

    /// True when connected and CONNACK received.
    public var isReady: Bool { queue.sync { _isReady } }

    /// Current broker host (or nil if disconnected).
    public var currentBroker: String? {
        queue.sync { _isReady ? brokers[currentBrokerIndex].host : nil }
    }

    /// Async stream of connection state changes.
    public var stateUpdates: AsyncStream<TransportState> { _stateUpdates }

    public init(
        brokers: [BrokerURL],
        username: String,
        password: String,
        clientID: String,
        onFatalError: (@Sendable (String) -> Void)? = nil,
        onAuthFailure: (@Sendable (UInt8, String) -> Void)? = nil
    ) {
        self.brokers = brokers
        self.username = username
        self.password = password
        self.clientID = clientID
        self.onFatalError = onFatalError
        self.onAuthFailure = onAuthFailure

        var cont: AsyncStream<TransportState>.Continuation!
        _stateUpdates = AsyncStream { cont = $0 }
        stateContinuation = cont
    }

    deinit {
        stateContinuation?.finish()
    }

    // MARK: - Lifecycle

    /// Connect to the first available broker. Returns when CONNACK received.
    public func start() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, any Error>) in
            queue.async { [weak self] in
                guard let self else {
                    cont.resume(throwing: CancellationError())
                    return
                }
                self.shouldReconnect = true
                self.readyContinuation = cont
                self.connectToCurrentBroker()
            }
        }
    }

    /// Graceful disconnect. Cancels reconnection. Resumes any pending
    /// `start()` continuation with `CancellationError` — leaking a
    /// `CheckedContinuation` is a Swift-runtime fatal error.
    public func stop() {
        queue.async { [weak self] in
            guard let self else { return }
            self.shouldReconnect = false
            self.cancelAllTimers()
            self.connection?.disconnect()
            self.connection = nil
            self._isReady = false
            self.readyContinuation?.resume(throwing: CancellationError())
            self.readyContinuation = nil
            self.emitState(.disconnected)
        }
    }

    /// Tear down the current connection and reconnect immediately. Right
    /// now this is only invoked internally from `handleConnectionEvent`
    /// when the underlying connection's `.disconnected` event fires
    /// (e.g. broker DISCONNECT, PINGRESP timeout). The Transport
    /// protocol exposes it for symmetry with future implementations.
    public func forceReconnect(reason: String) {
        queue.async { [weak self] in
            guard let self else { return }
            guard self.shouldReconnect else { return }
            self.logger.info("Force reconnect: \(reason, privacy: .public)")
            self._isReady = false
            self.connection?.disconnect()
            self.connection = nil
            self.reconnectAttempt = 0
            self.consecutiveConnectFailures = 0
            self.reconnectTimer?.cancel()
            self.reconnectTimer = nil
            self.cancelConnectTimeout()
            self.emitState(.reconnecting(broker: self.brokers[self.currentBrokerIndex].host, attempt: 0))
            self.connectToCurrentBroker()
        }
    }

    // MARK: - Test seams

    /// **Test-only.** Snapshot of the broker order in queue-safe form.
    /// Used by VertexCoreTests to assert that the Bug #1 demotion
    /// reordered the failover list after a CONNACK auth rejection.
    /// Production callers should observe broker selection through
    /// `currentBroker` / `state`.
    internal var _testBrokerHosts: [String] {
        queue.sync { brokers.map(\.host) }
    }

    /// **Test-only.** Synthesise a `.disconnected(connackReason:)` event
    /// on the transport's serial queue so a regression test for the
    /// sticky-vs-auth-failure interaction (PENDING_TRANSPORT_FIXES.md
    /// Bug #1) doesn't need a real broker. The synthesised event takes
    /// the same `handleConnectionEvent` path as a real CONNACK 0x86 from
    /// the broker.
    internal func _testFireAuthFailureDisconnect(connackReason: UInt8 = 0x86) {
        queue.sync {
            handleConnectionEvent(.disconnected(error: nil, linkDead: false, connackReason: connackReason))
        }
    }

    /// **Test-only.** Re-arm `onAuthFailure` between synthesised events.
    /// The production path nulls it out as part of the escalation
    /// shutdown, so the consecutive-demotion regression test needs a
    /// way to put it back without going through the public init.
    internal func _testReinstallAuthFailureCallback(_ cb: @escaping @Sendable (UInt8, String) -> Void) {
        queue.sync { onAuthFailure = cb }
    }

    /// **Test-only.** Synthesise an inbound PUBLISH and run the same
    /// dispatch path the wire reader would. Used by Bug #2 regression
    /// tests to assert that re-registering the same pattern replaces
    /// the handler instead of stacking.
    internal func _testInjectPublish(topic: String, payload: Data) {
        queue.sync {
            dispatchPublish(topic: topic, payload: payload)
        }
    }

    // MARK: - Publish

    /// Publish QoS 0. Drops silently when disconnected (fire-and-forget).
    /// retain=false is the only sensible value for VPN data plane traffic
    /// (a retained packet would replay on every new subscriber). Retained
    /// publishes belong on `Retainer` and are not used by current Swift
    /// callers — the exit (Go) is the only producer of retained messages.
    public func publish(topic: String, payload: Data) {
        queue.async { [weak self] in
            self?.connection?.publish(topic: topic, payload: payload, retain: false)
        }
    }

    // MARK: - Subscribe

    /// Register a subscription. Sends SUBSCRIBE immediately if connected
    /// **and** this is the first time this pattern is being registered;
    /// re-registering the same pattern just replaces the handler in
    /// place (no extra wire SUBSCRIBE — the broker already has the
    /// subscription, and the new handler will see the next PUBLISH).
    /// Patterns registered before connection are queued for the first
    /// connect's resubscribe.
    public func subscribe(
        pattern: String,
        handler: @escaping @Sendable (String, Data) -> Void
    ) {
        queue.async { [weak self] in
            guard let self else { return }
            let isNew = self.subscriptions[pattern] == nil
            self.subscriptions[pattern] = handler
            if self._isReady, isNew {
                self.connection?.subscribe(topics: [pattern])
            }
        }
    }

    /// Trigger an immediate liveness probe (PINGREQ) outside the regular
    /// keepalive cadence. Called when an external signal — system wake or
    /// a network path event — suggests the existing socket may be stale.
    /// On a dead link the probe's PINGRESP deadline (~5s) fires
    /// `linkDead`, escalating to extension restart far sooner than the
    /// next scheduled ping (~15s) would. No-op if not yet ready.
    public func checkLiveness(reason: String) {
        queue.async { [weak self] in
            guard let self else { return }
            guard self._isReady else {
                self.logger.info("checkLiveness(\(reason, privacy: .public)): not ready, skipping")
                return
            }
            self.connection?.pingNow(reason: reason)
        }
    }

    /// Wait until connected (with timeout).
    public func waitReady(timeout: Duration) async throws {
        if isReady { return }
        try await withThrowingTaskGroup(of: Void.self) { group in
            group.addTask {
                for await state in self.stateUpdates {
                    if case .connected = state { return }
                }
            }
            group.addTask {
                try await Task.sleep(for: timeout)
                throw TransportError.connectTimeout
            }
            try await group.next()
            group.cancelAll()
        }
    }

    // MARK: - Connection Management (private, runs on queue)

    private func connectToCurrentBroker() {
        guard shouldReconnect, !brokers.isEmpty else { return }

        let broker = brokers[currentBrokerIndex]
        logger.info("Connecting to \(broker.host, privacy: .public):\(broker.port, privacy: .public) (\(broker.scheme.rawValue, privacy: .public))")
        emitState(.connecting(broker: broker.host))

        let conn = MQTTConnection(
            clientID: clientID,
            username: username,
            password: password,
            queue: queue
        )

        conn.onStateChange = { [weak self] event in
            self?.handleConnectionEvent(event)
        }

        conn.onPublish = { [weak self] topic, payload in
            self?.dispatchPublish(topic: topic, payload: payload)
        }

        self.connection = conn
        scheduleConnectTimeout()
        conn.connect(to: broker)
    }

    /// Schedule a 15s deadline on reaching CONNACK. NWConnection by itself
    /// can sit in `.waiting` indefinitely (Apple Forums #768666 — known iOS
    /// 18 stick-in-waiting bug); without an application-side deadline we
    /// rely on the OS giving up, which it sometimes never does. On expiry
    /// we abort and let scheduleReconnect's backoff take over.
    private func scheduleConnectTimeout() {
        cancelConnectTimeout()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + Self.connectTimeout)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            self.connectTimeoutTimer = nil
            guard !self._isReady, self.shouldReconnect else { return }
            self.consecutiveConnectFailures += 1
            self.logger.warning("Connect timeout — aborting (\(self.consecutiveConnectFailures, privacy: .public) consecutive failures)")
            self.connection?.disconnect()
            self.connection = nil
            // Fallback to extension restart only after persistent failure
            // — gives the path-monitor / requiredInterface mechanism time
            // to migrate naturally first.
            if self.consecutiveConnectFailures >= 3, let onFatal = self.onFatalError {
                self.logger.warning("Persistent connect failures — escalating to extension restart")
                self.shouldReconnect = false
                self.cancelAllTimers()
                self.onFatalError = nil
                onFatal("Persistent connect failures (\(self.consecutiveConnectFailures))")
                return
            }
            self.scheduleReconnect()
        }
        t.resume()
        connectTimeoutTimer = t
    }

    private func cancelConnectTimeout() {
        connectTimeoutTimer?.cancel()
        connectTimeoutTimer = nil
    }

    private func cancelAllTimers() {
        reconnectTimer?.cancel()
        reconnectTimer = nil
        cancelConnectTimeout()
    }

    private func handleConnectionEvent(_ event: MQTTConnection.ConnectionEvent) {
        // Already on queue (MQTTConnection callbacks run on our queue)
        switch event {
        case .connected:
            cancelConnectTimeout()
            logger.info("MQTT connected to \(self.brokers[self.currentBrokerIndex].host, privacy: .public)")
            _isReady = true
            reconnectAttempt = 0
            consecutiveConnectFailures = 0

            // Sticky reconnect: move successful broker to front
            if currentBrokerIndex > 0 {
                let winner = brokers.remove(at: currentBrokerIndex)
                brokers.insert(winner, at: 0)
                currentBrokerIndex = 0
            }

            // Resubscribe all patterns
            let topics = Array(subscriptions.keys)
            if !topics.isEmpty {
                connection?.subscribe(topics: topics)
                logger.info("Resubscribed \(topics.count, privacy: .public) topics")
            }

            emitState(.connected(broker: brokers[0].host))

            // Resume waitReady / start continuation
            readyContinuation?.resume()
            readyContinuation = nil

        case .disconnected(let error, let linkDead, let connackReason):
            cancelConnectTimeout()
            let wasReady = _isReady
            _isReady = false

            if let error {
                logger.error("MQTT disconnected: \(error.localizedDescription, privacy: .public) linkDead=\(linkDead, privacy: .public) connackReason=\(connackReason.map(String.init) ?? "nil", privacy: .public)")
            } else {
                logger.info("MQTT disconnected linkDead=\(linkDead, privacy: .public)")
            }

            // CONNACK rejection is a config issue (wrong creds, banned,
            // unsupported protocol). Retrying with the same credentials
            // will keep failing — demote the stickied broker, surface
            // to the host (if subscribed), and stop the loop. The
            // earlier `let onAuth = onAuthFailure` guard accidentally
            // gated the demotion on host subscription too — paritет с
            // Kotlin port, where demotion is pure routing state and
            // fires regardless of whether the host wants to be told.
            if let connackReason, connackReason != 0 {
                let reasonStr = (error as? MQTTCodecError).flatMap { e -> String? in
                    if case .connackFailed(let s) = e { return s } else { return nil }
                } ?? "code=\(connackReason)"
                logger.warning("CONNACK rejected (code=\(connackReason, privacy: .public)) — escalating, no retry")

                // Un-sticky: a previous successful connect promoted this broker
                // to index 0 (sticky reconnect). Auth-rejecting now means the
                // stickied broker is misconfigured (creds rotated, ACL
                // tightened, …). Demote it to the tail so a future start()
                // tries the original primary first — the user should not be
                // locked out by one bad broker once another is healthy.
                if brokers.count > 1, currentBrokerIndex == 0 {
                    let loser = brokers.remove(at: 0)
                    brokers.append(loser)
                    // Fresh start counters so the next start() begins its
                    // backoff cycle from 0 instead of resuming the old
                    // ladder against a different broker. Cosmetic — Windows
                    // doesn't reset either, but cleaner here.
                    reconnectAttempt = 0
                    consecutiveConnectFailures = 0
                }

                shouldReconnect = false
                cancelAllTimers()
                let cb = onAuthFailure
                onAuthFailure = nil
                onFatalError = nil
                cb?(connackReason, reasonStr)
                return
            }

            // Link-dead = physical interface we were bound to is gone
            // (PINGRESP timeout, .failed on unsatisfied path, .waiting
            // after ready). Same-process retries can't escape the
            // extension's interface scoping (verified empirically:
            // NWConnection rebind via NWParameters.requiredInterface
            // only takes the new path AFTER the path monitor has seen
            // the new default — which can be 5–10s). Escalate to a
            // full extension restart immediately so the on-demand rule
            // re-scopes to the new best path within ~1s.
            if linkDead, let onFatal = onFatalError {
                logger.warning("Link dead — escalating to extension restart")
                shouldReconnect = false
                cancelAllTimers()
                onFatalError = nil
                onFatal("Link dead")
                return
            }

            if wasReady {
                emitState(.reconnecting(broker: brokers[currentBrokerIndex].host, attempt: reconnectAttempt))
            }

            scheduleReconnect()
        }
    }

    private func scheduleReconnect() {
        guard shouldReconnect else { return }

        reconnectTimer?.cancel()

        // Advance to next broker
        reconnectAttempt += 1
        currentBrokerIndex = reconnectAttempt % brokers.count

        // Backoff: 0, 0.5, 1, 2, 5, 5, 5...
        let delays: [Double] = [0, 0.5, 1, 2, 5]
        let cycleIndex = min(reconnectAttempt / brokers.count, delays.count - 1)
        let delay = delays[cycleIndex]

        if delay == 0 {
            connectToCurrentBroker()
            return
        }

        logger.info("Reconnecting in \(delay, privacy: .public)s (attempt \(self.reconnectAttempt, privacy: .public), broker \(self.brokers[self.currentBrokerIndex].host, privacy: .public))")
        emitState(.reconnecting(broker: brokers[currentBrokerIndex].host, attempt: reconnectAttempt))

        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + delay)
        timer.setEventHandler { [weak self] in
            self?.connectToCurrentBroker()
        }
        timer.resume()
        reconnectTimer = timer
    }

    // MARK: - Dispatch incoming publishes

    private func dispatchPublish(topic: String, payload: Data) {
        for (pattern, handler) in subscriptions {
            if topicMatches(topic: topic, pattern: pattern) {
                handler(topic, payload)
            }
        }
    }

    /// MQTT topic matching: + = single level, # = multi-level (end only).
    private func topicMatches(topic: String, pattern: String) -> Bool {
        let topicParts = topic.split(separator: "/", omittingEmptySubsequences: false)
        let patternParts = pattern.split(separator: "/", omittingEmptySubsequences: false)

        var ti = 0, pi = 0
        while pi < patternParts.count {
            let pp = patternParts[pi]
            if pp == "#" { return true } // matches rest
            guard ti < topicParts.count else { return false }
            if pp != "+" && pp != topicParts[ti] { return false }
            ti += 1
            pi += 1
        }
        return ti == topicParts.count
    }

    // MARK: - State emission

    private func emitState(_ state: TransportState) {
        stateContinuation?.yield(state)
    }
}
