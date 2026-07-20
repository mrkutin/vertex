import VertexCore
import CryptoKit
import Network
import NetworkExtension
import os

/// NEPacketTunnelProvider — the VPN engine running in the Network Extension process.
///
/// Flow: MQTT connect → join handshake → DH key exchange → TUN setup → packet pipeline.
///
/// macOS adaptations vs the iOS provider:
///  - `notifyPathChanged` IPC (raw value 4) instead of `notifyWifiAvailable`.
///  - Path monitor restarts the extension on **any** default-interface type
///    flip (Ethernet ↔ Wi-Fi). On Mac both interfaces are first-class, so
///    we don't have an iOS-style "always prefer wifi" rule.
class PacketTunnelProvider: NEPacketTunnelProvider {
    private let logger = Logger(subsystem: "ru.vertices.tunnel", category: "provider")
    /// Visible in Instruments → Points of Interest.
    private static let signposter = OSSignposter(subsystem: "ru.vertices.tunnel", category: .pointsOfInterest)

    /// Shared App Group container — used to hand `TunnelErrorReport`s to
    /// the host app on fatal connect failures (so the user sees "wrong
    /// password" / "TOFU rejected" instead of an opaque disconnect).
    fileprivate static let appGroupID = "group.ru.vertices"

    /// App Group key for the last successfully-connected exit ID. Surface
    /// channel for "Auto" UX (host app reads this to display "Auto · STO"
    /// even before the next IPC roundtrip) and the auto-resolve fallback
    /// chain when the discovery tracker is empty after the gather window.
    private static let lastGoodExitKey = "lastGoodExit"

    /// Hardcoded last-resort exit when auto-resolve has nothing — empty
    /// tracker, no `lastGoodExit`, no stale entries.
    private static let lastResortExitID = "sto"

    /// Per-broker TCP-connect probe deadline. Long enough to cover a
    /// worst-case RTT to any of our brokers, short enough that connect
    /// doesn't feel sluggish on a clean network. Failed probes still go
    /// to the tail so a degraded broker stays available as a fallback.
    private static let brokerProbeTimeout: TimeInterval = 1.5

    /// Persist a fatal connect failure to the App Group so the host app
    /// can pick it up on the next status-change-to-disconnected event.
    /// Logged at error level too — kind makes the cause searchable in
    /// Console.app / `log show` output.
    fileprivate func reportFatal(_ kind: TunnelErrorKind, detail: String = "") {
        let report = TunnelErrorReport(kind: kind, detail: detail)
        TunnelErrorReport.write(report, appGroupID: Self.appGroupID)
        logger.error("Fatal connect failure: kind=\(kind.rawValue, privacy: .public) detail=\(detail, privacy: .public)")
    }

    // MQTT
    private var transport: (any Transport)?

    // Discovery — accumulates retained heartbeats from `discovery/exits/+`
    // and runs the same scoring logic as Go's `pkg/discovery`. Used by
    // auto-resolve when `tunnelConfig.selectedExit == "auto"`, and for
    // pre-warming the join handshake's identity proof.
    private let discoveryTracker = DiscoveryTracker()

    // Crypto
    private var sessionCrypto: SessionCrypto?
    private var dhPrivateKey: Curve25519.KeyAgreement.PrivateKey?

    // Config
    private var config: TunnelConfig?

    // State
    private var connectionStatus = ConnectionStatus(state: .disconnected)
    private var stats = TunnelStats()
    private var uploadTask: Task<Void, Never>?
    private var keepaliveTimer: DispatchSourceTimer?
    private var statsLogTimer: DispatchSourceTimer?
    private var lastJoinData: Data?

    // Path monitor (any type-flip). Lives for the whole extension lifetime.
    private var pathMonitor: NWPathMonitor?
    private let pathMonitorQueue = DispatchQueue(label: "ru.vertices.tunnel.path-monitor")
    private var currentDefaultInterface: NWInterface?
    private var pathMonitorInitialized = false
    /// Re-entry guard for `cancelTunnelWithError`.
    private var isCancelling = false

    // Stats counters
    private var _bytesUp: UInt64 = 0
    private var _bytesDown: UInt64 = 0
    private var _packetsUp: UInt64 = 0
    private var _packetsDown: UInt64 = 0
    private var _decryptErrors: UInt64 = 0
    private var _maxBatchUp: Int = 0
    private var _maxBatchDown: Int = 0

    // MARK: - Tunnel Lifecycle

    override func startTunnel(options: [String: NSObject]?) async throws {
        logger.info("Starting tunnel... memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public)")

        // Drop any stale error from the previous attempt so the host
        // doesn't surface yesterday's failure on a fresh successful connect.
        TunnelErrorReport.clear(appGroupID: Self.appGroupID)

        // 1. Parse configuration
        let tunnelConfig: TunnelConfig
        do {
            tunnelConfig = try TunnelConfig(
                providerConfiguration: (protocolConfiguration as? NETunnelProviderProtocol)?.providerConfiguration
            )
        } catch {
            self.reportFatal(.configuration, detail: error.localizedDescription)
            throw error
        }
        self.config = tunnelConfig
        connectionStatus = ConnectionStatus(state: .connecting)
        logger.info("Config: exit=\(tunnelConfig.selectedExit, privacy: .public), name=\(tunnelConfig.clientName, privacy: .public), brokers=\(tunnelConfig.brokerURLs.count, privacy: .public)")

        // 2. Load identity key. Mirrors iOS strict path: a locked keychain
        // (Mac asleep / not yet logged in / NE woken up before user
        // unlock) must NOT mint a fresh key — that would silently break
        // TOFU on the exit. Fail the connect and let the user retry
        // after unlocking; on-demand will pick it up.
        let identityKey: IdentityKey
        do {
            identityKey = try loadOrCreateIdentityKey()
        } catch KeychainError.locked {
            self.reportFatal(.keychainLocked, detail: "Unlock the Mac, then reconnect")
            throw NEVPNError(.connectionFailed)
        } catch KeychainError.loadFailed(let status) {
            self.reportFatal(.unknown, detail: "Keychain unreadable (OSStatus \(status)). Try reinstalling the app.")
            throw NEVPNError(.connectionFailed)
        } catch {
            self.reportFatal(.unknown, detail: "identity key: \(error.localizedDescription)")
            throw error
        }
        logger.info("Identity: \(identityKey.publicKeyHex.prefix(16))...")

        // 3. Generate ephemeral DH keypair
        let dhKey = Curve25519.KeyAgreement.PrivateKey()
        self.dhPrivateKey = dhKey

        // 4. Load password from Keychain (or providerConfiguration override)
        let password: String
        if let providerPass = (protocolConfiguration as? NETunnelProviderProtocol)?.providerConfiguration?["password"] as? String {
            password = providerPass
        } else {
            password = (try? KeychainStore.loadPassword()) ?? ""
        }

        // 5. If the user picked Auto, probe TCP-connect RTT to every
        // broker in parallel and reorder by ascending RTT — failed
        // probes go to the tail in original order so they remain
        // available as fallback. For an explicit pick the host app has
        // already pinned the chosen broker first; we keep that order
        // and only log so the user's choice is honoured.
        let probedBrokers: [BrokerURL]
        let probeRTTs: [String: Int]
        if tunnelConfig.selectedBroker == "auto" {
            (probedBrokers, probeRTTs) = await BrokerProbe.reorderWithRTTs(
                tunnelConfig.brokerURLs,
                timeout: Self.brokerProbeTimeout
            )
            logger.info("Broker probe (auto, \(probedBrokers.count, privacy: .public)): \(BrokerProbe.formatOrder(probedBrokers, rttMs: probeRTTs), privacy: .public)")
        } else {
            probedBrokers = tunnelConfig.brokerURLs
            probeRTTs = [:]
            logger.info("Broker pinned by user: \(tunnelConfig.selectedBroker, privacy: .public)")
        }

        // 6. Create MQTT transport
        // ClientID gets an epoch suffix so two Macs with the same client
        // name in Auto mode don't collide on `vtx-client-auto-mac` and
        // ping-pong each other off the broker.
        let clientIDSuffix = Int(Date().timeIntervalSince1970)
        let clientID = "vtx-client-\(tunnelConfig.selectedExit)-\(tunnelConfig.clientName)-\(clientIDSuffix)"
        let mqttUser = "vtx-client-\(tunnelConfig.clientName)"
        logger.info("MQTT clientID=\(clientID, privacy: .public), user=\(mqttUser, privacy: .public)")
        let mqttTransport = MQTTTransport(
            brokers: probedBrokers,
            username: mqttUser,
            password: password,
            clientID: clientID,
            onFatalError: { [weak self] reason in
                self?.requestCancel(reason: reason, code: -1)
            },
            onAuthFailure: { [weak self] code, reason in
                // Broker said no — almost always wrong creds. Persist a
                // user-fixable error before tearing down.
                self?.reportFatal(.authentication, detail: "\(reason) (code=\(code))")
                self?.requestCancel(reason: "Auth failed: \(reason)", code: -10)
            }
        )
        let transport: any Transport = mqttTransport
        self.transport = transport

        // 6. Subscribe to discovery heartbeats — must precede transport.start()
        // so retained heartbeats arrive before we need them for auto-resolve.
        // Each heartbeat is fed into `discoveryTracker` (for score-based
        // selection and dhPubkey pre-warm) and forwarded to a stream the
        // join handshake uses if the tracker hasn't seen the chosen exit yet.
        var discoveryContinuation: AsyncStream<DiscoveryHeartbeat>.Continuation!
        let discoveryStream = AsyncStream<DiscoveryHeartbeat> { discoveryContinuation = $0 }
        transport.subscribe(pattern: Topics.discoveryAll) { [weak self] _, data in
            do {
                let hb = try JSONDecoder().decode(DiscoveryHeartbeat.self, from: data)
                self?.discoveryTracker.handle(hb)
                self?.logger.info("Discovery heartbeat: exit=\(hb.id, privacy: .public), dhPubkey=\(hb.dhPubkey?.prefix(16) ?? "nil")")
                discoveryContinuation.yield(hb)
            } catch {
                self?.logger.error("Discovery decode error: \(String(describing: error), privacy: .public)")
            }
        }

        // 7. Connect to broker
        connectionStatus = ConnectionStatus(state: .connecting)
        try await transport.start()
        logger.info("MQTT connected, memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public)")
        connectionStatus = ConnectionStatus(state: .handshaking)

        // 7b. Auto-resolve exit if user picked "Auto". The control / data
        // subscriptions below capture `resolvedExit`, so the wire-protocol
        // exit ID is locked before the join handshake fires.
        let brokerHostForResolve = transport.currentBroker
            ?? tunnelConfig.brokerURLs.first?.host
            ?? ""
        let resolvedExit: String
        if tunnelConfig.selectedExit == "auto" {
            resolvedExit = await resolveAutoExit(
                brokerHost: brokerHostForResolve,
                timeoutSeconds: 10
            )
            logger.info("Auto-resolved exit: \(resolvedExit, privacy: .public)")
        } else {
            resolvedExit = tunnelConfig.selectedExit
        }

        // 7c. Subscribe to control topic AFTER resolve, so the exit-ID
        // filter inside the handler captures the resolved value.
        var assignContinuation: AsyncStream<AssignMessage>.Continuation!
        let assignStream = AsyncStream<AssignMessage> { assignContinuation = $0 }
        transport.subscribe(pattern: Topics.controlAny(name: tunnelConfig.clientName)) { [weak self] topic, data in
            guard let self else { return }
            let dataStr = String(data: data, encoding: .utf8) ?? "<binary \(data.count)B>"
            self.logger.info("Control message on \(topic, privacy: .public): \(dataStr, privacy: .public)")

            let parts = topic.split(separator: "/")
            guard parts.count >= 3 else { return }
            let exitID = String(parts[1])
            guard exitID == resolvedExit else {
                self.logger.info("Ignoring control from \(exitID, privacy: .public), want \(resolvedExit, privacy: .public)")
                return
            }

            if let json = try? JSONDecoder().decode([String: String].self, from: data),
               let errorMsg = json["error"] {
                self.logger.error("Exit error: \(errorMsg, privacy: .public), topic: \(topic, privacy: .public)")
                // Exit explicitly rejected the join. Most common cause is
                // a TOFU mismatch — different identity pubkey for the
                // same client name. Surface and tear down so the user
                // doesn't sit through the 30s join retry loop.
                self.reportFatal(.identityRejected, detail: errorMsg)
                self.requestCancel(reason: "Exit rejected: \(errorMsg)", code: -11)
                return
            }

            do {
                let assign = try JSONDecoder().decode(AssignMessage.self, from: data)
                self.logger.info("Assign received: ip=\(assign.ip, privacy: .public), gw=\(assign.gw, privacy: .public), dh=\(assign.dh != nil, privacy: .public)")
                assignContinuation.yield(assign)
            } catch {
                self.logger.error("Assign decode error: \(String(describing: error), privacy: .public)")
            }
        }

        // 8. Get exit's DH pubkey for identity proof. Prefer the tracker —
        // when the heartbeat already arrived, this is instant and saves the
        // 10s wait below. Otherwise fall back to the discovery stream.
        var exitDHPubB64: String? = discoveryTracker.info(for: resolvedExit)?.dhPubkey
        if exitDHPubB64 == nil {
            logger.info("Waiting for discovery heartbeat from \(resolvedExit, privacy: .public)...")
            do {
                try await withThrowingTaskGroup(of: String?.self) { group in
                    group.addTask {
                        for await hb in discoveryStream {
                            if hb.id == resolvedExit {
                                return hb.dhPubkey
                            }
                        }
                        return nil
                    }
                    group.addTask {
                        try await Task.sleep(for: .seconds(10))
                        throw NSError(domain: "Vertex", code: 3,
                                      userInfo: [NSLocalizedDescriptionKey: "Discovery timeout: no heartbeat from \(resolvedExit) in 10s"])
                    }
                    if let result = try await group.next() {
                        exitDHPubB64 = result
                    }
                    group.cancelAll()
                }
            } catch {
                logger.warning("Discovery failed: \(error.localizedDescription, privacy: .public). Proceeding without identity proof.")
            }
        }

        // 9. Build join message with identity proof
        let joinMsg: Data
        if let exitDHB64 = exitDHPubB64,
           let exitDHData = Data(base64Encoded: exitDHB64),
           let exitPubForProof = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: exitDHData) {
            let proof = try identityKey.proof(exitPublicKey: exitPubForProof, name: tunnelConfig.clientName)
            let join = JoinMessage(
                name: tunnelConfig.clientName,
                dh: dhKey.publicKey.rawRepresentation.base64EncodedString(),
                id: identityKey.privateKey.publicKey.rawRepresentation.base64EncodedString(),
                idSig: proof.base64EncodedString()
            )
            joinMsg = try JSONEncoder().encode(join)
            logger.info("Join built WITH identity proof")
        } else {
            let join = JoinMessage(
                name: tunnelConfig.clientName,
                dh: dhKey.publicKey.rawRepresentation.base64EncodedString(),
                id: identityKey.privateKey.publicKey.rawRepresentation.base64EncodedString(),
                idSig: nil
            )
            joinMsg = try JSONEncoder().encode(join)
            logger.warning("Join built WITHOUT identity proof, json=\(String(data: joinMsg, encoding: .utf8) ?? "")")
        }
        self.lastJoinData = joinMsg

        // 10. Wait for assign. If the exit doesn't reply within 30s after
        // multiple join retries, we treat it as joinTimeout and surface a
        // user-actionable error before throwing.
        let assign: AssignMessage
        do {
            assign = try await waitForAssign(
                joinMsg: joinMsg,
                transport: transport,
                effectiveExit: resolvedExit,
                stream: assignStream,
                timeout: 30
            )
        } catch {
            self.reportFatal(.joinTimeout, detail: resolvedExit)
            throw error
        }

        // 11. Derive session key
        guard let dhStr = assign.dh,
              let exitPubData = Data(base64Encoded: dhStr),
              let exitPub = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: exitPubData) else {
            throw NSError(domain: "Vertex", code: 2, userInfo: [NSLocalizedDescriptionKey: "Invalid exit DH pubkey"])
        }
        let crypto = try SessionCrypto.fromDH(privateKey: dhKey, peerPublicKey: exitPub)
        self.sessionCrypto = crypto
        logger.info("Session key derived")

        // 12. Subscribe to data topic
        transport.subscribe(pattern: Topics.download(exit: resolvedExit, name: tunnelConfig.clientName)) { [weak self] _, payload in
            self?.handleDownloadPacket(payload)
        }

        // 13. Configure TUN
        let settings = NEPacketTunnelNetworkSettings(tunnelRemoteAddress: assign.gw)
        settings.mtu = 1500 as NSNumber

        let ipv4 = NEIPv4Settings(
            addresses: [assign.ip],
            subnetMasks: ["255.255.255.0"]
        )
        ipv4.includedRoutes = [
            NEIPv4Route(destinationAddress: "0.0.0.0", subnetMask: "128.0.0.0"),
            NEIPv4Route(destinationAddress: "128.0.0.0", subnetMask: "128.0.0.0"),
        ]
        var excluded: [NEIPv4Route] = tunnelConfig.brokerIPs.map { ip in
            NEIPv4Route(destinationAddress: ip, subnetMask: "255.255.255.255")
        }
        if tunnelConfig.splitTunnelEnabled, let path = tunnelConfig.ruNetsPath {
            let ruRoutes = Self.loadRUNets(path: path)
            excluded.append(contentsOf: ruRoutes)
            logger.info("excludedRoutes: split=on brokers=\(tunnelConfig.brokerIPs.count, privacy: .public) ru=\(ruRoutes.count, privacy: .public) total=\(excluded.count, privacy: .public)")
        } else {
            logger.info("excludedRoutes: split=off brokers=\(tunnelConfig.brokerIPs.count, privacy: .public)")
        }
        ipv4.excludedRoutes = excluded
        settings.ipv4Settings = ipv4

        // DNS goes through the tunnel for ALL domains. Same rationale as iOS:
        // split DNS via matchDomains breaks any service whose system DNS lives
        // on a non-routable network from the exit (LAN router, carrier
        // resolver, etc.).
        let dns = NEDNSSettings(servers: ["1.1.1.1", "8.8.8.8"])
        dns.matchDomains = [""]
        settings.dnsSettings = dns

        try await setTunnelNetworkSettings(settings)
        logger.info("TUN configured: \(assign.ip, privacy: .public), memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public)")

        // 13b. Persist resolved exit so the host app shows the correct
        // edge immediately on relaunch (Auto · STO) and the auto-resolve
        // fallback chain can pick it up on the next connect when the
        // discovery tracker is empty.
        UserDefaults(suiteName: Self.appGroupID)?.set(resolvedExit, forKey: Self.lastGoodExitKey)

        // 14. Start upload pipeline
        let uploadTopic = Topics.upload(exit: resolvedExit, name: tunnelConfig.clientName)
        startUploadPipeline(topic: uploadTopic)

        // 15. Keepalive
        startKeepalive(transport: transport, effectiveExit: resolvedExit)

        // 16. Stats logger
        startStatsLogger()

        connectionStatus = ConnectionStatus(
            state: .connected,
            assignedIP: assign.ip,
            currentBroker: transport.currentBroker,
            currentExit: resolvedExit,
            connectedSince: Date()
        )

        // 17. Path monitor (any type-flip → restart).
        startPathMonitor(transport: mqttTransport)

        logger.info("Tunnel ready")
    }

    /// macOS calls wake() when the system resumes from sleep. After a long
    /// sleep the broker may have evicted our session — fire an immediate
    /// PINGREQ so the PINGRESP-timeout path detects death within seconds
    /// rather than after the next scheduled interval.
    override func wake() {
        super.wake()
        logger.info("wake() — probing liveness")
        if let mqtt = transport as? MQTTTransport {
            mqtt.checkLiveness(reason: "wake")
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {
        logger.info("Stopping tunnel, reason: \(String(describing: reason)) memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public) packets up=\(self._packetsUp) down=\(self._packetsDown) bytes up=\(self._bytesUp) down=\(self._bytesDown) decryptErrors=\(self._decryptErrors)")

        pathMonitorQueue.async { [weak self] in self?.isCancelling = true }

        keepaliveTimer?.cancel()
        keepaliveTimer = nil
        statsLogTimer?.cancel()
        statsLogTimer = nil
        uploadTask?.cancel()
        uploadTask = nil
        pathMonitor?.cancel()
        pathMonitor = nil
        transport?.stop()
        transport = nil
        sessionCrypto = nil
        dhPrivateKey = nil
        connectionStatus = ConnectionStatus(state: .disconnected)
        logger.info("Tunnel stopped")
    }

    // MARK: - Auto-resolve

    /// Resolve "auto" to a concrete exit ID using `discoveryTracker`'s
    /// scoring logic.
    ///
    /// Always waits a minimum collection window (`gatherWindow`) before the
    /// first decision: retained MQTT heartbeats arrive sequentially per
    /// topic (`discovery/exits/sto`, `discovery/exits/ams`, …), and a
    /// no-wait `bestExit` would race the first heartbeat in and pick that
    /// exit as the only candidate. After the window, score-based selection
    /// runs against the full snapshot. If the tracker is still empty, the
    /// loop polls every 500ms up to `timeoutSeconds` total.
    ///
    /// Fallbacks when the tracker stays empty: (1) App Group
    /// `lastGoodExit`, (2) any stale tracker entry by minimum RTT,
    /// (3) the hardcoded last-resort literal. Always returns a non-empty
    /// exit ID.
    private func resolveAutoExit(brokerHost: String, timeoutSeconds: Int) async -> String {
        let gatherWindow: Duration = .milliseconds(1500)
        try? await Task.sleep(for: gatherWindow)

        if let best = discoveryTracker.bestExit(forBroker: brokerHost) {
            logSnapshot(brokerHost: brokerHost, picked: best, source: "fast")
            return best
        }
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(timeoutSeconds))
        while clock.now < deadline, !Task.isCancelled {
            do {
                try await Task.sleep(for: .milliseconds(500))
            } catch {
                break
            }
            if let best = discoveryTracker.bestExit(forBroker: brokerHost) {
                logSnapshot(brokerHost: brokerHost, picked: best, source: "poll")
                return best
            }
        }
        if let lastGood = UserDefaults(suiteName: Self.appGroupID)?.string(forKey: Self.lastGoodExitKey),
           !lastGood.isEmpty, lastGood != "auto" {
            logger.warning("Auto-resolve fallback to lastGoodExit: \(lastGood, privacy: .public)")
            return lastGood
        }
        let stale = discoveryTracker.snapshot(includeStale: true)
            .min(by: {
                ($0.brokerRTTms[brokerHost] ?? Int.max) <
                ($1.brokerRTTms[brokerHost] ?? Int.max)
            })
        if let staleID = stale?.id {
            logger.warning("Auto-resolve fallback to stale exit: \(staleID, privacy: .public)")
            return staleID
        }
        logger.warning("Auto-resolve fallback to last-resort: \(Self.lastResortExitID, privacy: .public)")
        return Self.lastResortExitID
    }

    /// Emit a one-line summary of every known exit's (rtt, clients,
    /// computed score) for the given broker host plus the picked ID. Lets
    /// us debug "why did Auto pick X" from device logs without re-running
    /// the formula by hand.
    private func logSnapshot(brokerHost: String, picked: String, source: String) {
        let snapshot = discoveryTracker.snapshot(includeStale: true)
        let entries = snapshot.map { info -> String in
            let rtt = info.brokerRTTms[brokerHost]
                ?? info.brokerRTTms.first(where: { $0.key.split(separator: ":").first.map(String.init) == brokerHost })?.value
                ?? -1
            let cap = info.maxClients > 0 ? info.maxClients : 253
            let rttD = rtt > 0 ? Double(rtt) : 100.0
            let score = rttD * (1.0 + Double(info.clients) / Double(cap) * 2.0)
            let scoreStr = String(format: "%.1f", score)
            let age = Int(Date().timeIntervalSince(info.receivedAt))
            return "\(info.id)(rtt=\(rtt) clients=\(info.clients)/\(info.maxClients) age=\(age)s score=\(scoreStr))"
        }.joined(separator: " ")
        logger.info("Auto-resolve [\(source, privacy: .public)] broker=\(brokerHost, privacy: .public) picked=\(picked, privacy: .public) | \(entries, privacy: .public)")
    }

    // MARK: - Join Handshake

    private func waitForAssign(
        joinMsg: Data,
        transport: any Transport,
        effectiveExit: String,
        stream: AsyncStream<AssignMessage>,
        timeout: Int
    ) async throws -> AssignMessage {
        let joinTopic = Topics.join(exit: effectiveExit)

        transport.publish(topic: joinTopic, payload: joinMsg)
        logger.info("Join published to topic=\(joinTopic, privacy: .public), exit=\(effectiveExit, privacy: .public)")

        return try await withThrowingTaskGroup(of: AssignMessage.self) { group in
            group.addTask {
                for await assign in stream {
                    return assign
                }
                throw NSError(domain: "Vertex", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Assign stream ended"])
            }

            group.addTask {
                for i in 1...((timeout / 2) - 1) {
                    try await Task.sleep(for: .seconds(2))
                    transport.publish(topic: joinTopic, payload: joinMsg)
                    self.logger.info("Join retry \(i)")
                }
                try await Task.sleep(for: .seconds(2))
                throw NSError(domain: "Vertex", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Join timeout after \(timeout)s"])
            }

            let result = try await group.next()!
            group.cancelAll()
            return result
        }
    }

    // MARK: - Packet Pipeline

    private func startUploadPipeline(topic: String) {
        uploadTask = Task { [weak self] in
            guard let self else { return }
            while !Task.isCancelled {
                let (packets, _) = await self.packetFlow.readPackets()
                Self.signposter.emitEvent("upload-batch", "n=\(packets.count, privacy: .public)")
                guard let transport = self.transport, transport.isReady,
                      let crypto = self.sessionCrypto else { continue }

                if packets.count > self._maxBatchUp {
                    self._maxBatchUp = packets.count
                    self.logger.info("Upload batch new max=\(packets.count, privacy: .public)")
                }

                for packet in packets {
                    do {
                        let encrypted = try crypto.seal(packet)
                        transport.publish(topic: topic, payload: encrypted)
                        self._packetsUp += 1
                        self._bytesUp += UInt64(packet.count)
                    } catch {
                        self.logger.error("Seal error: \(String(describing: error), privacy: .public)")
                    }
                }
            }
        }
    }

    private func handleDownloadPacket(_ payload: Data) {
        guard let crypto = sessionCrypto else { return }
        do {
            let decrypted = try crypto.open(payload)
            let proto: NSNumber = (decrypted.first.map { $0 >> 4 } == 6) ? AF_INET6 as NSNumber : AF_INET as NSNumber
            packetFlow.writePackets([decrypted], withProtocols: [proto])
            _packetsDown += 1
            _bytesDown += UInt64(decrypted.count)
        } catch {
            _decryptErrors += 1
            if _decryptErrors <= 5 {
                logger.error("Open error: \(error.localizedDescription, privacy: .public) (count=\(self._decryptErrors))")
            }
        }
    }

    // MARK: - Stats logging

    private func startStatsLogger() {
        let timer = DispatchSource.makeTimerSource()
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in
            guard let self else { return }
            Self.signposter.emitEvent("stats-log")
            let mem = Self.currentMemoryFootprint()
            let memStr = Self.formatBytes(mem)
            let ready = self.transport?.isReady == true
            self.logger.info("stats: mem=\(memStr, privacy: .public) up=\(self._bytesUp) down=\(self._bytesDown) pkt up=\(self._packetsUp) down=\(self._packetsDown) maxBatch up=\(self._maxBatchUp) decErr=\(self._decryptErrors) mqttReady=\(ready)")
        }
        timer.resume()
        statsLogTimer = timer
    }

    private static func currentMemoryFootprint() -> UInt64 {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let kr = withUnsafeMutablePointer(to: &info) { ptr -> kern_return_t in
            ptr.withMemoryRebound(to: integer_t.self, capacity: Int(count)) { intPtr in
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), intPtr, &count)
            }
        }
        guard kr == KERN_SUCCESS else { return 0 }
        return UInt64(info.phys_footprint)
    }

    private static func formatBytes(_ bytes: UInt64) -> String {
        let mb = Double(bytes) / 1_048_576.0
        return String(format: "%.1fMB", mb)
    }

    // MARK: - Keepalive

    private func startKeepalive(transport: any Transport, effectiveExit: String) {
        let timer = DispatchSource.makeTimerSource()
        timer.schedule(deadline: .now() + 60, repeating: 60)
        timer.setEventHandler { [weak self] in
            guard let self, let joinData = self.lastJoinData else { return }
            Self.signposter.emitEvent("keepalive-join")
            let topic = Topics.join(exit: effectiveExit)
            transport.publish(topic: topic, payload: joinData)
            self.logger.debug("Keepalive join re-sent")
        }
        timer.resume()
        keepaliveTimer = timer
    }

    // MARK: - Path monitor (macOS — symmetric Ethernet ↔ Wi-Fi)

    /// On macOS, both Ethernet and Wi-Fi are first-class citizens. A type
    /// flip in either direction (kabel out → wifi, wifi off → ethernet,
    /// hotspot dock → ethernet, etc.) means the existing socket is bound
    /// to a stale path. Restart the extension so iOS rebuilds it on the
    /// new best path. Wi-Fi → Wi-Fi roams (same type, different name)
    /// only fire `checkLiveness` — if the link is alive on the new BSSID,
    /// MQTTConnection's `requiredInterface` rebind handles it; if dead,
    /// PINGRESP timeout takes over.
    private func startPathMonitor(transport: MQTTTransport) {
        pathMonitor?.cancel()

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let newDefault = (path.status == .satisfied) ? path.availableInterfaces.first : nil
            let oldDesc = self.currentDefaultInterface.map { "\($0.name)[\($0.index)]/\(String(describing: $0.type))" } ?? "nil"
            let newDesc = newDefault.map { "\($0.name)[\($0.index)]/\(String(describing: $0.type))" } ?? "nil"
            self.logger.info("Path: status=\(String(describing: path.status), privacy: .public) ifaces=\(path.availableInterfaces.map { $0.name }, privacy: .public) wifi=\(path.usesInterfaceType(.wifi)) wired=\(path.usesInterfaceType(.wiredEthernet)) default: \(oldDesc, privacy: .public) → \(newDesc, privacy: .public)")

            let oldType = self.currentDefaultInterface?.type
            let oldName = self.currentDefaultInterface?.name
            let newType = newDefault?.type
            let newName = newDefault?.name
            self.currentDefaultInterface = newDefault

            if !self.pathMonitorInitialized {
                self.pathMonitorInitialized = true
                self.logger.info("Path monitor initial scope: \(newDesc, privacy: .public)")
                return
            }

            // Any change in either type or name is a hint the socket is
            // bound to a stale path → force a fresh PINGREQ.
            if oldType != newType || oldName != newName {
                transport.checkLiveness(reason: "path-change")
            }

            // Type flip (Ethernet ↔ Wi-Fi, or anything ↔ nil/cell) →
            // restart so iOS rebinds. Wi-Fi roam (same type, different
            // name) does not restart; checkLiveness is enough.
            if oldType != newType {
                self.logger.warning("Interface type changed (\(String(describing: oldType), privacy: .public) → \(String(describing: newType), privacy: .public)); restarting extension")
                self.requestCancel(reason: "Switching interface", code: -3)
            }
        }
        monitor.start(queue: pathMonitorQueue)
        pathMonitor = monitor
    }

    /// Re-entrant-safe wrapper around cancelTunnelWithError.
    private func requestCancel(reason: String, code: Int) {
        pathMonitorQueue.async { [weak self] in
            guard let self else { return }
            guard !self.isCancelling else {
                self.logger.info("Cancel ignored (already cancelling): \(reason, privacy: .public)")
                return
            }
            self.isCancelling = true
            self.logger.warning("Cancelling tunnel: \(reason, privacy: .public)")
            self.connectionStatus = ConnectionStatus(
                state: .reconnecting,
                assignedIP: self.connectionStatus.assignedIP,
                currentBroker: self.connectionStatus.currentBroker,
                currentExit: self.connectionStatus.currentExit,
                connectedSince: self.connectionStatus.connectedSince,
                lastError: reason
            )
            self.cancelTunnelWithError(NSError(
                domain: "ru.vertices.tunnel",
                code: code,
                userInfo: [NSLocalizedDescriptionKey: reason]
            ))
        }
    }

    // MARK: - App IPC

    override func handleAppMessage(_ messageData: Data) async -> Data? {
        guard let byte = messageData.first,
              let message = AppMessage(rawValue: byte) else {
            return nil
        }

        let response: ExtensionResponse
        switch message {
        case .requestStatus:
            response = .status(connectionStatus)
        case .requestStats:
            let s = TunnelStats(
                bytesUp: _bytesUp,
                bytesDown: _bytesDown,
                packetsUp: _packetsUp,
                packetsDown: _packetsDown
            )
            response = .stats(s)
        case .notifyWifiAvailable:
            // iOS-only signal. macOS uses notifyPathChanged; treat any
            // received notifyWifiAvailable as a generic liveness probe so
            // a mixed-version IPC contract still does the right thing.
            logger.info("Received notifyWifiAvailable (treating as path probe)")
            if let mqtt = transport as? MQTTTransport {
                mqtt.checkLiveness(reason: "host-wifi-ipc")
            }
            return nil
        case .notifyPathChanged:
            logger.info("Received notifyPathChanged from host app")
            if let mqtt = transport as? MQTTTransport {
                mqtt.checkLiveness(reason: "host-path-change-ipc")
            }
            return nil
        }
        return try? JSONEncoder().encode(response)
    }

    // MARK: - RU CIDR loader

    private static func loadRUNets(path: String) -> [NEIPv4Route] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return [] }
        return CIDRParser.parseAll(text).map { route in
            NEIPv4Route(destinationAddress: route.address, subnetMask: route.mask)
        }
    }

    // MARK: - Identity Key

    private func loadOrCreateIdentityKey() throws -> IdentityKey {
        do {
            let data = try KeychainStore.loadIdentityKey()
            return try IdentityKey(rawRepresentation: data)
        } catch KeychainError.notFound {
            let key = IdentityKey()
            do {
                try KeychainStore.saveIdentityKey(key.rawRepresentation)
            } catch KeychainError.saveFailed(errSecInteractionNotAllowed) {
                // Keychain went from "not-found" to "locked" between
                // load and save — same window iOS hits, treat as locked.
                throw KeychainError.locked
            }
            logger.info("Generated new identity key (first launch)")
            return key
        }
    }
}
