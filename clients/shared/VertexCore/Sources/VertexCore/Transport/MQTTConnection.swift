import Foundation
import Network
import Security
import os

/// A single MQTT 5.0 connection over NWConnection (TLS or WSS).
///
/// Manages the lifecycle: TCP/TLS connect → MQTT CONNECT → CONNACK → ready.
/// Provides publish/subscribe/ping. Reports state changes via callback.
/// Does NOT handle reconnection — that's MQTTTransport's job.
final class MQTTConnection: @unchecked Sendable {
    private let queue: DispatchQueue
    private let logger: Logger
    /// Visible in Instruments → Points of Interest. Same subsystem as the
    /// extension's PacketTunnelProvider signposter so all wake-ups appear
    /// on a single timeline.
    private static let signposter = OSSignposter(subsystem: "ru.vertices.tunnel", category: .pointsOfInterest)
    private var connection: NWConnection?

    // MQTT state
    private(set) var isConnected = false
    /// Set true once we receive a successful CONNACK. After that, an
    /// `NWConnection .waiting` means the network path actually broke
    /// (interface flap, sleep, etc.) — not the normal "waiting for path"
    /// that happens before the first connection succeeds.
    private var hasBeenReady = false
    private var receiveBuffer = Data()
    private var nextPacketID: UInt16 = 1
    private var pingTimer: DispatchSourceTimer?
    private var pingResponseTimer: DispatchSourceTimer?
    private var pingResponsePending = false

    // WebSocket mode
    private var isWebSocket = false

    // Configuration
    private let clientID: String
    private let username: String
    private let password: String
    private let keepAlive: UInt16

    // Callbacks
    var onPublish: ((String, Data) -> Void)?
    var onStateChange: ((ConnectionEvent) -> Void)?

    enum ConnectionEvent: Sendable {
        case connected
        /// Disconnect event. `linkDead = true` means the underlying physical
        /// link is gone (PINGRESP timed out, OR NWConnection went .failed
        /// while the system path is unsatisfied, OR went .waiting after
        /// having been ready). The transport promotes this to a fatal
        /// extension restart — NEPacketTunnelProvider is scoped to its
        /// startup interface and only `cancelTunnelWithError` + an on-demand
        /// rule can re-scope it. Broker-only failures (TCP RST while the
        /// path is still satisfied, broker DISCONNECT) leave it false.
        /// `connackReason` is set when the broker rejected our CONNECT
        /// (any non-zero CONNACK reason code — typically 0x86 "Bad
        /// username or password" or 0x87 "Not authorized"). The transport
        /// uses this to short-circuit the retry loop: auth failures don't
        /// recover by reconnecting with the same credentials, so we
        /// escalate to the host as a user-fixable error.
        case disconnected(error: Error?, linkDead: Bool, connackReason: UInt8?)
    }

    /// Hard upper bound on how long we'll wait for PINGRESP after sending
    /// PINGREQ before declaring the link dead. Combined with the PINGREQ
    /// cadence (`keepAlive - 5` seconds, see `startPingTimer`) this caps
    /// dead-connection detection at roughly `keepAlive` seconds:
    /// e.g. with keepAlive=20, we ping every 15s and bail in <=5s if no
    /// reply, so a stuck-but-not-yet-evicted TCP socket is reclaimed in
    /// ≈20s rather than the legacy ≈50s (= two PINGREQ cycles).
    private static let pingResponseTimeout: DispatchTimeInterval = .seconds(5)

    init(
        clientID: String,
        username: String,
        password: String,
        keepAlive: UInt16 = 20,
        queue: DispatchQueue
    ) {
        self.clientID = clientID
        self.username = username
        self.password = password
        self.keepAlive = keepAlive
        self.queue = queue
        self.logger = Logger(subsystem: "ru.vertices", category: "mqtt-conn")
    }

    // MARK: - Connect

    /// Connect to a broker URL (mqtt(s):// or ws(s)://).
    ///
    /// We do NOT use `NWParameters.requiredInterface` to attempt cross-
    /// interface socket rebind: empirically, `requiredInterface` inside a
    /// scoped NEPacketTunnelProvider doesn't reliably bypass extension
    /// scoping, and the resulting forceReconnect raced with the (much
    /// faster) linkDead/cancelTunnelWithError + on-demand path. Recovery
    /// is now uniform — extension restart on any interface event — so
    /// individual sockets only need plain TCP/TLS parameters.
    func connect(to broker: BrokerURL) {
        let params: NWParameters

        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true

        if broker.isTLS {
            let tls = NWProtocolTLS.Options()
            // Inside an macOS NEPacketTunnelProvider sandbox, Network.framework's
            // default trust evaluation compares the server cert against the
            // wrong "input" (empty/IP/whatever) — fails with -67602
            // "certificate name does not match name(s) in certificate" even
            // though the cert clearly contains the host as a SAN. Setting
            // sec_protocol_options_set_tls_server_name alone wasn't enough —
            // that fixes SNI but doesn't fix the verifier's hostname source.
            //
            // The fix is to take trust evaluation into our own hands: build an
            // SSL policy with the correct hostname, attach it to the trust
            // object Network.framework hands us, and call SecTrustEvaluate
            // explicitly. This works inside the extension sandbox because
            // SecTrustEvaluateWithError doesn't require keychain access for
            // already-rooted CA chains (Let's Encrypt → ISRG Root X1 ships
            // in the OS trust store which Security.framework can reach
            // read-only from a sandboxed extension).
            //
            // Set SNI too — costs nothing and is correct.
            sec_protocol_options_set_tls_server_name(tls.securityProtocolOptions, broker.host)
            let host = broker.host
            let logger = self.logger
            sec_protocol_options_set_verify_block(
                tls.securityProtocolOptions,
                { _, secTrustRef, completion in
                    let trust = sec_trust_copy_ref(secTrustRef).takeRetainedValue()
                    let policy = SecPolicyCreateSSL(true, host as CFString)
                    SecTrustSetPolicies(trust, policy)
                    var error: CFError?
                    let ok = SecTrustEvaluateWithError(trust, &error)
                    if !ok, let error {
                        let desc = CFErrorCopyDescription(error) as String? ?? "?"
                        let domain = CFErrorGetDomain(error) as String? ?? "?"
                        let code = CFErrorGetCode(error)
                        logger.error("SecTrust eval fail for \(host, privacy: .public): \(domain, privacy: .public) \(code) — \(desc, privacy: .public)")
                    }
                    completion(ok)
                },
                queue
            )
            params = NWParameters(tls: tls, tcp: tcp)
        } else {
            params = NWParameters(tls: nil, tcp: tcp)
        }

        if broker.isWebSocket {
            let ws = NWProtocolWebSocket.Options()
            ws.autoReplyPing = true
            ws.setSubprotocols(["mqtt"])
            params.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        }

        let host = NWEndpoint.Host(broker.host)
        let port = NWEndpoint.Port(rawValue: UInt16(broker.port))!
        let conn = NWConnection(host: host, port: port, using: params)

        conn.stateUpdateHandler = { [weak self] state in
            self?.handleNWState(state)
        }

        self.connection = conn
        self.receiveBuffer = Data()
        self.isWebSocket = broker.isWebSocket
        conn.start(queue: queue)
    }

    /// Detach the NWConnection state handler BEFORE we cancel — Apple's
    /// pattern for "abort and start fresh" (Apple Forums #108903).
    /// Without this, `cancel()` schedules a final `.cancelled` callback
    /// that would arrive after MQTTTransport has already moved on,
    /// triggering a phantom reconnect on the new connection's behalf.
    private func clearNWHandlers() {
        connection?.stateUpdateHandler = nil
    }

    // MARK: - Disconnect

    /// Graceful MQTT disconnect + cancel NWConnection. Detaches NWConnection
    /// AND MQTTTransport-facing handlers first so late callbacks (from a
    /// pending `receive(...)` or `.cancelled`) don't fire on MQTTTransport
    /// after we've already moved on.
    func disconnect() {
        stopPingTimer()
        if isConnected {
            sendMQTTData(MQTTPacketCodec.encodeDisconnect()) { _ in }
        }
        isConnected = false
        onStateChange = nil
        onPublish = nil
        clearNWHandlers()
        connection?.forceCancel()
        connection = nil
    }

    // MARK: - Publish

    /// Publish QoS 0 with optional message expiry. Drops silently if not connected.
    func publish(topic: String, payload: Data, retain: Bool = false, messageExpiry: UInt32? = 10) {
        guard isConnected else { return }
        let pkt = MQTTPublishPacket(
            topic: topic,
            payload: payload,
            retain: retain,
            messageExpiry: messageExpiry
        )
        sendMQTTData(MQTTPacketCodec.encodePublish(pkt)) { [weak self] error in
            if let error {
                self?.logger.error("Publish send error: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: - Subscribe

    /// Subscribe to topic patterns (QoS 0).
    func subscribe(topics: [String]) {
        guard isConnected else { return }
        let packetID = nextPacketID
        nextPacketID &+= 1
        if nextPacketID == 0 { nextPacketID = 1 }

        let pkt = MQTTSubscribePacket(packetID: packetID, topics: topics)
        sendMQTTData(MQTTPacketCodec.encodeSubscribe(pkt)) { [weak self] error in
            if let error {
                self?.logger.error("Subscribe send error: \(error.localizedDescription, privacy: .public)")
            }
        }
    }

    // MARK: - Send helper

    /// Send data over NWConnection. For WebSocket, wraps in binary frame metadata.
    private func sendMQTTData(_ data: Data, completion: @escaping (NWError?) -> Void) {
        guard let conn = connection else { return }
        if isWebSocket {
            let metadata = NWProtocolWebSocket.Metadata(opcode: .binary)
            let context = NWConnection.ContentContext(identifier: "mqtt", metadata: [metadata])
            conn.send(content: data, contentContext: context, isComplete: true, completion: .contentProcessed { completion($0) })
        } else {
            conn.send(content: data, completion: .contentProcessed { completion($0) })
        }
    }

    // MARK: - NWConnection State

    private func handleNWState(_ state: NWConnection.State) {
        switch state {
        case .ready:
            logger.info("TLS ready, sending MQTT CONNECT")
            sendMQTTConnect()
            startReceiveLoop()

        case .failed(let error):
            // If we were already ready and the system path is no longer
            // satisfied, this is a dead link (e.g. wifi turned off — TCP
            // gets ENOTCONN within 100ms before any PINGRESP cycle could
            // fire). Same recovery as PINGRESP timeout: ask the host to
            // restart the extension. If the path is still satisfied, the
            // failure is broker-side (RST, TLS bounce) — let the transport
            // retry without escalating.
            let pathStatus = connection?.currentPath?.status
            let linkDead = hasBeenReady && pathStatus != .satisfied
            logger.error("Connection failed: \(error.localizedDescription, privacy: .public) hasBeenReady=\(self.hasBeenReady) pathStatus=\(String(describing: pathStatus), privacy: .public) linkDead=\(linkDead)")
            handleDisconnect(error: error, linkDead: linkDead)

        case .cancelled:
            handleDisconnect(error: nil, linkDead: false)

        case .waiting(let error):
            // Before first .ready: normal — we're waiting for the path to
            // become available. After: the path broke and the existing TCP
            // socket is now bound to a dead interface. Treat as link-dead
            // so the host restarts the extension via the on-demand rule.
            if hasBeenReady {
                logger.warning("Connection waiting after ready (link dead): \(error.localizedDescription, privacy: .public)")
                handleDisconnect(error: error, linkDead: true)
            } else {
                logger.warning("Connection waiting: \(error.localizedDescription, privacy: .public)")
            }

        default:
            break
        }
    }

    // MARK: - MQTT CONNECT

    private func sendMQTTConnect() {
        let pkt = MQTTConnectPacket(
            clientID: clientID,
            username: username.isEmpty ? nil : username,
            password: password.isEmpty ? nil : password,
            keepAlive: keepAlive,
            cleanStart: true,
            sessionExpiryInterval: 0
        )
        sendMQTTData(MQTTPacketCodec.encodeConnect(pkt)) { [weak self] error in
            if let error {
                self?.logger.error("CONNECT send error: \(error.localizedDescription, privacy: .public)")
                self?.handleDisconnect(error: error, linkDead: false)
            }
        }
    }

    // MARK: - Receive Loop

    private func startReceiveLoop() {
        guard let conn = connection else { return }

        // 8KB read window is enough for one full MQTT packet (max payload 1700 + headers
        // for our broker config) and keeps the per-receive heap pressure low — important
        // for the iOS NEPacketTunnelProvider memory budget (~50MB).
        conn.receive(minimumIncompleteLength: 1, maximumLength: 8192) { [weak self] data, _, isComplete, error in
            guard let self else { return }

            if let error {
                let pathStatus = self.connection?.currentPath?.status
                let linkDead = self.hasBeenReady && pathStatus != .satisfied
                self.handleDisconnect(error: error, linkDead: linkDead)
                return
            }

            if let data, !data.isEmpty {
                self.receiveBuffer.append(data)
                self.processReceiveBuffer()
                // Continue receiving (for WebSocket, isComplete=true per message, not per connection)
                self.startReceiveLoop()
            } else if isComplete {
                // Empty data + isComplete = connection closed (or WebSocket close frame)
                self.handleDisconnect(error: nil, linkDead: false)
            } else {
                // No data yet, continue
                self.startReceiveLoop()
            }
        }
    }

    private func processReceiveBuffer() {
        while true {
            guard let (packetType, packetData, consumed) = MQTTPacketCodec.tryDecode(receiveBuffer) else {
                break // incomplete packet, wait for more data
            }

            receiveBuffer.removeFirst(consumed)
            handlePacket(type: packetType, data: packetData)
        }
        // After draining, if the buffer is empty, replace with a freshly-allocated empty
        // Data so the underlying capacity (which can grow up to a few hundred KB during
        // a burst) is released back to the allocator instead of being held forever.
        if receiveBuffer.isEmpty {
            receiveBuffer = Data()
        }
    }

    private func handlePacket(type: MQTTPacketType, data: Data) {
        switch type {
        case .connack:
            do {
                let connack = try MQTTPacketCodec.decodeConnack(data)
                if connack.isSuccess {
                    logger.info("CONNACK success (session=\(connack.sessionPresent))")
                    isConnected = true
                    hasBeenReady = true
                    startPingTimer()
                    onStateChange?(.connected)
                } else {
                    logger.error("CONNACK rejected: \(connack.reasonString, privacy: .public) (code=\(connack.reasonCode))")
                    handleDisconnect(error: MQTTCodecError.connackFailed(connack.reasonString), linkDead: false, connackReason: connack.reasonCode)
                }
            } catch {
                logger.error("CONNACK decode error: \(String(describing: error), privacy: .public)")
                handleDisconnect(error: error, linkDead: false)
            }

        case .publish:
            do {
                let pub = try MQTTPacketCodec.decodePublish(data)
                onPublish?(pub.topic, pub.payload)
            } catch {
                logger.error("PUBLISH decode error: \(String(describing: error), privacy: .public)")
            }

        case .suback:
            if let suback = try? MQTTPacketCodec.decodeSuback(data) {
                if !suback.allSuccess {
                    logger.warning("SUBACK partial failure: \(suback.reasonCodes)")
                }
            }

        case .pingresp:
            pingResponsePending = false
            pingResponseTimer?.cancel()
            pingResponseTimer = nil

        case .disconnect:
            logger.info("Received DISCONNECT from broker")
            handleDisconnect(error: nil, linkDead: false)

        default:
            break
        }
    }

    // MARK: - Ping Timer

    private func startPingTimer() {
        stopPingTimer()
        // Send PINGREQ slightly before keepAlive to stay within window
        let interval = max(Double(keepAlive) - 5, 5)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + interval, repeating: interval)
        timer.setEventHandler { [weak self] in
            self?.sendPing()
        }
        timer.resume()
        pingTimer = timer
    }

    private func stopPingTimer() {
        pingTimer?.cancel()
        pingTimer = nil
        pingResponseTimer?.cancel()
        pingResponseTimer = nil
        pingResponsePending = false
    }

    /// Force a fresh liveness probe ahead of the regular ping cadence. Used
    /// when an external signal (system wake, path-monitor event) suggests
    /// the existing socket may be stale. If a ping is already in flight,
    /// no-op — the in-flight ping's deadline will surface the dead link
    /// at least as fast as a fresh probe would. After sending, the regular
    /// ping cadence is reset so we don't double-ping moments later.
    func pingNow(reason: String) {
        guard isConnected else { return }
        if pingResponsePending {
            logger.info("pingNow(\(reason, privacy: .public)): ping already pending — skipping")
            return
        }
        logger.info("pingNow(\(reason, privacy: .public)): forcing fresh PINGREQ")
        sendPing()
        startPingTimer()
    }

    private func sendPing() {
        guard isConnected, let conn = connection else { return }

        if pingResponsePending {
            // Defence-in-depth: this branch is now reached only if the
            // explicit `pingResponseTimer` was somehow missed. The timer
            // is the primary signal — see startPingTimer / sendPing.
            logger.warning("PINGRESP still pending at next PINGREQ — disconnecting")
            handleDisconnect(error: MQTTCodecError.connackFailed("PINGRESP timeout"), linkDead: true)
            return
        }

        Self.signposter.emitEvent("mqtt-pingreq")
        pingResponsePending = true
        sendMQTTData(MQTTPacketCodec.encodePingReq()) { _ in }

        // Explicit PINGRESP-deadline timer. If the broker doesn't echo
        // back within `pingResponseTimeout`, the link is dead — even if
        // NWConnection viability/path handlers haven't noticed (which
        // happens reliably for Control-Center Wi-Fi disconnect on iOS,
        // where the Wi-Fi radio stays on but our socket loses its
        // route). Triggers handleDisconnect → MQTTTransport
        // scheduleReconnect → fresh NWConnection on the current best
        // path. Worst-case detection time is one ping interval plus
        // pingResponseTimeout.
        pingResponseTimer?.cancel()
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + Self.pingResponseTimeout)
        t.setEventHandler { [weak self] in
            guard let self else { return }
            guard self.pingResponsePending else { return }
            self.logger.warning("PINGRESP not received within timeout — link dead")
            self.handleDisconnect(
                error: MQTTCodecError.connackFailed("PINGRESP timeout"),
                linkDead: true
            )
        }
        t.resume()
        pingResponseTimer = t
    }

    // MARK: - Disconnect handling

    /// Tear down. We always use `forceCancel()` — the connection is
    /// being discarded so there's no value in a graceful TLS close_notify
    /// that an unreachable peer can't ack (and that would block our
    /// queue waiting for the kernel to time the TLS shutdown out).
    ///
    /// `linkDead = true` tells the transport that the underlying physical
    /// path is gone (PINGRESP timeout, .failed with unsatisfied path, or
    /// .waiting after ready). The transport escalates to a full extension
    /// restart instead of a same-process reconnect that iOS would just
    /// rescope to the same dead interface.
    private func handleDisconnect(error: Error?, linkDead: Bool, connackReason: UInt8? = nil) {
        let wasConnected = isConnected
        isConnected = false
        stopPingTimer()
        // Capture and nil the handler atomically before forceCancel, so a
        // late callback (pingResponseTimer firing between schedule and
        // cancel, or NWConnection's terminal .cancelled state) cannot
        // re-enter handleDisconnect and emit a phantom second event to
        // MQTTTransport. Same defensive pattern as `disconnect()`.
        let handler = onStateChange
        onStateChange = nil
        onPublish = nil
        clearNWHandlers()
        connection?.forceCancel()
        connection = nil

        if wasConnected || error != nil {
            handler?(.disconnected(error: error, linkDead: linkDead, connackReason: connackReason))
        }
    }
}
