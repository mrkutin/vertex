import VertexCore
import CryptoKit
import Network
import NetworkExtension
import os

/// NEPacketTunnelProvider — the VPN engine running in the Network Extension process.
///
/// Flow: MQTT connect → join handshake → DH key exchange → TUN setup → packet pipeline.
class PacketTunnelProvider: NEPacketTunnelProvider {
    private let logger = Logger(subsystem: "ru.vertices.tunnel", category: "provider")
    /// Visible in Instruments → Points of Interest. Lets us see whether wake-ups
    /// (keepalive, stats log, MQTT PING) coalesce or scatter across time —
    /// scattered wakes hold the cellular radio in a high-power state longer.
    private static let signposter = OSSignposter(subsystem: "ru.vertices.tunnel", category: .pointsOfInterest)

    /// Shared App Group container — used to hand `TunnelErrorReport`s to
    /// the host app on fatal connect failures (auth/identity/joinTimeout/etc.)
    /// so the user sees a fixable message instead of an opaque disconnect.
    fileprivate static let appGroupID = "group.ru.vertices"

    /// App Group key for the last successfully-connected exit ID. Surface
    /// channel for "Auto" UX (host app reads this to display "Auto · STO"
    /// even before the next IPC roundtrip) and the auto-resolve fallback
    /// chain when the discovery tracker is empty after the 10s window.
    private static let lastGoodExitKey = "lastGoodExit"

    /// Hardcoded last-resort exit when auto-resolve has nothing — empty
    /// tracker, no `lastGoodExit`, no stale entries. Kept as a single
    /// literal here (not in `DiscoveryTracker`) to keep deployment topology
    /// out of the shared Swift package.
    private static let lastResortExitID = "sto"

    /// Per-broker TCP-connect probe deadline. Long enough to cover a
    /// worst-case cellular RTT to any of our brokers (≈400ms one-way
    /// from Russia + safety margin), short enough that connect doesn't
    /// feel sluggish on a clean network. Failed probes still go to the
    /// tail so a degraded broker stays available as a fallback.
    private static let brokerProbeTimeout: TimeInterval = 1.5

    /// Persist a fatal connect failure to the App Group. Host app picks
    /// it up on the next NEVPNStatus → .disconnected event.
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

    // Path monitor (cell→wifi only, see startPathMonitor). Lives for the
    // whole extension lifetime, never cancelled.
    private var pathMonitor: NWPathMonitor?
    private let pathMonitorQueue = DispatchQueue(label: "ru.vertices.tunnel.path-monitor")
    private var currentDefaultInterface: NWInterface?
    /// True after the first satisfied path event — first event just
    /// records the current interface, never triggers a restart, otherwise
    /// every cold start would self-cancel.
    private var pathMonitorInitialized = false
    /// Re-entry guard for `cancelTunnelWithError`. Three independent
    /// triggers (linkDead, path monitor, host-app IPC) can fire close in
    /// time; without this guard iOS occasionally gets stuck in
    /// `disconnecting` state after a flap (NEProvider isn't reentrant).
    /// Read/written only on `pathMonitorQueue`.
    private var isCancelling = false

    // Dead-link detection: PINGRESP timeout in MQTTConnection (see
    // MQTTTransport's class comment). All NWConnection-level signals
    // (viability, betterPath, per-connection pathUpdate, defaultPath
    // KVO) were tried in earlier versions and proven unreliable on at
    // least one real-iPhone scenario. The wifi watcher above is a
    // narrow exception: it only fires the extension-restart path
    // (cancelTunnelWithError + on-demand rule), which is the only
    // documented way to re-scope an NEPacketTunnelProvider after
    // startup.

    // Stats counters (accessed from multiple queues)
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

        // Drop any stale error from the previous attempt so the host app
        // doesn't surface yesterday's failure on a fresh connect.
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
        logger.info("Config: exit=\(tunnelConfig.selectedExit), name=\(tunnelConfig.clientName), brokers=\(tunnelConfig.brokerURLs.count)")

        // 2. Load identity key. Failure is fatal — we MUST NOT mint a fresh
        // key when Keychain is locked (boot-to-first-unlock window), or
        // we'd permanently overwrite the device's identity and trip TOFU
        // on every exit.
        let identityKey: IdentityKey
        do {
            identityKey = try loadOrCreateIdentityKey()
        } catch KeychainError.locked {
            self.reportFatal(.keychainLocked, detail: "Unlock the iPhone, then reconnect")
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

        // 4. Load password from Keychain
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
        // and only log the probe results for diagnostics, so the user's
        // choice is honoured.
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
        // ClientID gets an epoch suffix so two iPhones owned by the same
        // user (clientName="iphone") and both in Auto mode don't collide
        // on `vtx-client-auto-iphone` and ping-pong each other off the
        // broker. Mirrors `feedback_android_unique_clientid`.
        let clientIDSuffix = Int(Date().timeIntervalSince1970)
        let clientID = "vtx-client-\(tunnelConfig.selectedExit)-\(tunnelConfig.clientName)-\(clientIDSuffix)"
        let mqttUser = "vtx-client-\(tunnelConfig.clientName)"
        logger.info("MQTT clientID=\(clientID), user=\(mqttUser)")
        // Concrete type is MQTTTransport, but PacketTunnelProvider only depends
        // on the abstract Transport surface — keeps the data-plane code free
        // of MQTT specifics for the day we add a second implementation.
        // onFatalError is the linkDead escalation path (PINGRESP timeout,
        // .failed on unsatisfied path, .waiting after ready, persistent
        // connect timeouts). Pass via init so MQTTTransport's queue
        // ordering is guaranteed by construction.
        let mqttTransport = MQTTTransport(
            brokers: probedBrokers,
            username: mqttUser,
            password: password,
            clientID: clientID,
            onFatalError: { [weak self] reason in
                self?.requestCancel(reason: reason, code: -1)
            },
            onAuthFailure: { [weak self] code, reason in
                // Broker rejected our CONNECT — typically wrong creds.
                // Persist a user-fixable error before tearing down so
                // the host app shows "check Settings → Identity".
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
                self?.logger.info("Discovery heartbeat: exit=\(hb.id), dhPubkey=\(hb.dhPubkey?.prefix(16) ?? "nil")")
                discoveryContinuation.yield(hb)
            } catch {
                self?.logger.error("Discovery decode error: \(error)")
            }
        }

        // 7. Connect to broker
        connectionStatus = ConnectionStatus(state: .connecting)
        try await transport.start()
        logger.info("MQTT connected, memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public)")
        connectionStatus = ConnectionStatus(state: .handshaking)

        // 7b. Auto-resolve exit if user picked "Auto". When the tracker
        // already has a winner (retained heartbeats arrive within ~1s of
        // subscribe), this returns instantly. The control / data
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
            self.logger.info("Control message on \(topic): \(dataStr)")

            let parts = topic.split(separator: "/")
            guard parts.count >= 3 else { return }
            let exitID = String(parts[1])
            guard exitID == resolvedExit else {
                self.logger.info("Ignoring control from \(exitID), want \(resolvedExit)")
                return
            }

            // Check for error response from exit
            if let json = try? JSONDecoder().decode([String: String].self, from: data),
               let errorMsg = json["error"] {
                self.logger.error("Exit error: \(errorMsg, privacy: .public), topic: \(topic, privacy: .public)")
                // Exit explicitly rejected the join — most often a TOFU
                // mismatch. Surface and tear down so the user doesn't sit
                // through the 30s join retry loop.
                self.reportFatal(.identityRejected, detail: errorMsg)
                self.requestCancel(reason: "Exit rejected: \(errorMsg)", code: -11)
                return
            }

            do {
                let assign = try JSONDecoder().decode(AssignMessage.self, from: data)
                self.logger.info("Assign received: ip=\(assign.ip), gw=\(assign.gw), dh=\(assign.dh != nil)")
                assignContinuation.yield(assign)
            } catch {
                self.logger.error("Assign decode error: \(error)")
            }
        }

        // 8. Get exit's DH pubkey for identity proof. Prefer the tracker —
        // when the heartbeat already arrived, this is instant and saves the
        // 10s wait below. Otherwise fall back to the discovery stream.
        var exitDHPubB64: String? = discoveryTracker.info(for: resolvedExit)?.dhPubkey
        if exitDHPubB64 == nil {
            logger.info("Waiting for discovery heartbeat from \(resolvedExit)...")
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
                logger.warning("Discovery failed: \(error.localizedDescription). Proceeding without identity proof.")
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

        // 10. Wait for assign response (retry join every 2s, timeout 30s).
        // If the exit doesn't reply, it's down or the user isn't authorized.
        // Surface a user-actionable error before throwing so the host app
        // shows it on disconnect.
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

        // 10. Derive session key from DH exchange
        guard let dhStr = assign.dh,
              let exitPubData = Data(base64Encoded: dhStr),
              let exitPub = try? Curve25519.KeyAgreement.PublicKey(rawRepresentation: exitPubData) else {
            throw NSError(domain: "Vertex", code: 2, userInfo: [NSLocalizedDescriptionKey: "Invalid exit DH pubkey"])
        }
        let crypto = try SessionCrypto.fromDH(privateKey: dhKey, peerPublicKey: exitPub)
        self.sessionCrypto = crypto
        logger.info("Session key derived")

        // 11. Subscribe to data topic (download)
        transport.subscribe(pattern: Topics.download(exit: resolvedExit, name: tunnelConfig.clientName)) { [weak self] _, payload in
            self?.handleDownloadPacket(payload)
        }

        // 12. Configure TUN with assigned IP
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
        // Operator-owned RU services that must ALWAYS bypass the tunnel — even
        // with split-tunnel off. Otherwise a RU-hosted service on our own infra
        // is tunnelled abroad and back and fails (the home firewall rejects the
        // foreign exit IP), so the app can't reach its server with the VPN on.
        // Mirror of Android PriorityBypassNets. See PriorityBypass.
        excluded.append(contentsOf: PriorityBypass.routes.map {
            NEIPv4Route(destinationAddress: $0.address, subnetMask: $0.mask)
        })
        if tunnelConfig.splitTunnelEnabled, let path = tunnelConfig.ruNetsPath {
            let ruRoutes = Self.loadRUNets(path: path)
            excluded.append(contentsOf: ruRoutes)
            logger.info("excludedRoutes: split=on brokers=\(tunnelConfig.brokerIPs.count) ru=\(ruRoutes.count) total=\(excluded.count)")
        } else {
            logger.info("excludedRoutes: split=off brokers=\(tunnelConfig.brokerIPs.count)")
        }
        ipv4.excludedRoutes = excluded
        settings.ipv4Settings = ipv4

        // DNS goes through the tunnel for ALL domains regardless of split mode.
        // Split DNS via matchDomains was tried in v2.9.2 but broke any service
        // resolved by the system DNS (e.g. speedtest.net): the system DNS is
        // typically the local LAN router (192.168.x.1) or the carrier's private
        // resolver, neither of which is reachable from the exit node — so DNS
        // queries time out and the host fails to resolve. iOS does not let one
        // NEDNSSettings configure different servers per domain, and there is
        // no second DNSSettings slot. Keep DNS centralized; rely on excludedRoutes
        // alone for the speed win on .ru/.рф traffic that resolves to RU IPs.
        let dns = NEDNSSettings(servers: ["1.1.1.1", "8.8.8.8"])
        dns.matchDomains = [""]
        settings.dnsSettings = dns

        try await setTunnelNetworkSettings(settings)
        logger.info("TUN configured: \(assign.ip), memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public)")

        // 13. Persist resolved exit so the host app shows the correct edge
        // immediately on relaunch (Auto · STO) and the auto-resolve fallback
        // chain can pick it up on the next connect when the discovery
        // tracker is empty.
        UserDefaults(suiteName: Self.appGroupID)?.set(resolvedExit, forKey: Self.lastGoodExitKey)

        // 14. Start upload pipeline
        let uploadTopic = Topics.upload(exit: resolvedExit, name: tunnelConfig.clientName)
        startUploadPipeline(topic: uploadTopic)

        // 15. Start keepalive timer (re-send join every 60s)
        startKeepalive(transport: transport, effectiveExit: resolvedExit)

        // 16. Start periodic stats logger (memory + traffic) — surfaces jetsam pressure.
        startStatsLogger()

        connectionStatus = ConnectionStatus(
            state: .connected,
            assignedIP: assign.ip,
            currentBroker: transport.currentBroker,
            currentExit: resolvedExit,
            connectedSince: Date()
        )

        // 16. Start persistent path monitor. On every default-interface
        // change (wifi→cell, cell→wifi), rebinds the MQTT socket to the
        // new interface via NWParameters.requiredInterface. No extension
        // restart needed for either direction.
        startPathMonitor(transport: mqttTransport)

        logger.info("Tunnel ready")
    }

    /// Called when the system wakes the extension after a sleep period.
    /// On long sleeps the broker often evicts our session while DispatchSource
    /// timers are frozen, so the next regular PINGREQ may be 15s away —
    /// firing one immediately compresses wake-to-reconnect from ~20s to ~5s.
    /// Apple's wake() signal isn't 100% reliable on iOS NetworkExtensions
    /// (the path-monitor probe is the real safety net), but it's nearly
    /// free and helps when it does fire.
    override func wake() {
        super.wake()
        logger.info("wake() — probing liveness")
        if let mqtt = transport as? MQTTTransport {
            mqtt.checkLiveness(reason: "wake")
        }
    }

    override func stopTunnel(with reason: NEProviderStopReason) async {
        logger.info("Stopping tunnel, reason: \(String(describing: reason)) memory=\(Self.formatBytes(Self.currentMemoryFootprint()), privacy: .public) packets up=\(self._packetsUp) down=\(self._packetsDown) bytes up=\(self._bytesUp) down=\(self._bytesDown) decryptErrors=\(self._decryptErrors)")

        // Block any late-firing cancel triggers (path monitor, MQTT
        // linkDead, IPC) from running cancelTunnelWithError after iOS
        // has already initiated tear-down.
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


    /// Resolve "auto" to a concrete exit ID using `discoveryTracker`'s
    /// scoring logic.
    ///
    /// Always waits a minimum collection window (`gatherWindow`) before the
    /// first decision: retained MQTT heartbeats arrive sequentially per
    /// topic (`discovery/exits/aws`, `discovery/exits/sto`, …), and a
    /// no-wait `bestExit` would race the first heartbeat in and pick that
    /// exit as the only candidate. After the window, score-based selection
    /// runs against the full snapshot. If the tracker is still empty, the
    /// loop polls every 500 ms up to `timeoutSeconds` total.
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
                // Task cancelled mid-sleep — fall through to fallback chain
                // so callers get a deterministic answer instead of hanging.
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

    // MARK: - Join Handshake

    private func waitForAssign(
        joinMsg: Data,
        transport: any Transport,
        effectiveExit: String,
        stream: AsyncStream<AssignMessage>,
        timeout: Int
    ) async throws -> AssignMessage {
        let joinTopic = Topics.join(exit: effectiveExit)

        // Publish first join
        transport.publish(topic: joinTopic, payload: joinMsg)
        logger.info("Join published to topic=\(joinTopic), exit=\(effectiveExit)")

        // Retry every 2 seconds, timeout after N seconds
        return try await withThrowingTaskGroup(of: AssignMessage.self) { group in
            // Wait for assign response
            group.addTask {
                for await assign in stream {
                    return assign
                }
                throw NSError(domain: "Vertex", code: 1,
                              userInfo: [NSLocalizedDescriptionKey: "Assign stream ended"])
            }

            // Retry timer
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
                Self.signposter.emitEvent("upload-batch", "n=\(packets.count)")
                guard let transport = self.transport, transport.isReady,
                      let crypto = self.sessionCrypto else { continue }

                if packets.count > self._maxBatchUp {
                    self._maxBatchUp = packets.count
                    self.logger.info("Upload batch new max=\(packets.count)")
                }

                for packet in packets {
                    do {
                        let encrypted = try crypto.seal(packet)
                        transport.publish(topic: topic, payload: encrypted)
                        self._packetsUp += 1
                        self._bytesUp += UInt64(packet.count)
                    } catch {
                        self.logger.error("Seal error: \(error)")
                    }
                }
            }
        }
    }

    private func handleDownloadPacket(_ payload: Data) {
        guard let crypto = sessionCrypto else { return }
        do {
            let decrypted = try crypto.open(payload)
            // Detect IP version from the version nibble of the packet header.
            let proto: NSNumber = (decrypted.first.map { $0 >> 4 } == 6) ? AF_INET6 as NSNumber : AF_INET as NSNumber
            packetFlow.writePackets([decrypted], withProtocols: [proto])
            _packetsDown += 1
            _bytesDown += UInt64(decrypted.count)
        } catch {
            _decryptErrors += 1
            // Log only the first few errors to avoid log flooding under attack scenarios.
            if _decryptErrors <= 5 {
                logger.error("Open error: \(error.localizedDescription, privacy: .public) (count=\(self._decryptErrors))")
            }
        }
    }

    // MARK: - Stats logging

    /// Periodic resource log (every 5s). Surfaces the iOS extension's memory
    /// footprint right before a jetsam kill — look for the last logged value.
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

    /// Resident memory footprint in bytes (Apple's recommended technique
    /// matches what jetsam evaluates, i.e. `phys_footprint`).
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

    // MARK: - Path monitor (Hiddify pattern)

    /// Persistent NWPathMonitor scoped to one specific job: catch
    /// any-non-wifi → wifi handover so we can switch onto the cheaper /
    /// faster path. For the opposite direction (wifi → anything) we
    /// rely on `linkDead` detection in MQTTTransport (.failed / .waiting
    /// after ready / PINGRESP timeout) — that fires within ~1s of the
    /// wifi socket dying, whereas the in-extension path monitor takes
    /// longer to flip its default and used to race with linkDead
    /// (double extension restart, several-second downtime).
    ///
    /// → wifi can NOT use linkDead because the existing cellular socket
    /// stays healthy when wifi associates — only the path monitor (or
    /// the host-app's IPC) can spot the new better path.
    ///
    /// First satisfied event is recorded but does NOT trigger — without
    /// this guard, a cold start on wifi would self-cancel (oldType=nil,
    /// newType=.wifi → match → cancel → restart → repeat).
    private func startPathMonitor(transport: MQTTTransport) {
        pathMonitor?.cancel()

        let monitor = NWPathMonitor()
        monitor.pathUpdateHandler = { [weak self] path in
            guard let self else { return }
            let newDefault = (path.status == .satisfied) ? path.availableInterfaces.first : nil
            let oldDesc = self.currentDefaultInterface.map { "\($0.name)[\($0.index)]/\(String(describing: $0.type))" } ?? "nil"
            let newDesc = newDefault.map { "\($0.name)[\($0.index)]/\(String(describing: $0.type))" } ?? "nil"
            self.logger.info("Path: status=\(String(describing: path.status), privacy: .public) ifaces=\(path.availableInterfaces.map { $0.name }, privacy: .public) wifi=\(path.usesInterfaceType(.wifi)) cell=\(path.usesInterfaceType(.cellular)) default: \(oldDesc, privacy: .public) → \(newDesc, privacy: .public)")

            let oldType = self.currentDefaultInterface?.type
            let newType = newDefault?.type
            self.currentDefaultInterface = newDefault

            if !self.pathMonitorInitialized {
                self.pathMonitorInitialized = true
                self.logger.info("Path monitor initial scope: \(newDesc, privacy: .public)")
                return
            }

            // Any default-interface change is a hint that the existing
            // socket may now be bound to a stale path (post-wake, AP
            // change, brief radio flap). Force a fresh PINGREQ — if the
            // link is dead, the 5s PINGRESP deadline fires linkDead far
            // sooner than the next scheduled ping (~15s). Wifi-roam
            // events (oldType==newType==.wifi) are excluded so we don't
            // spam pings on flaky enterprise wifi.
            if oldType != newType {
                transport.checkLiveness(reason: "path-change")
            }

            // Trigger on any non-wifi → wifi transition: cellular → wifi
            // (handover home), nil → wifi (cold start where cell came up
            // first then wifi associated). Wifi → wifi roams self-heal.
            guard oldType != .wifi, newType == .wifi else { return }

            self.logger.warning("→ WiFi: restarting extension to switch")
            self.requestCancel(reason: "Switching to WiFi", code: -3)
        }
        monitor.start(queue: pathMonitorQueue)
        pathMonitor = monitor
    }

    /// Re-entrant-safe wrapper around cancelTunnelWithError. All three
    /// trigger paths (linkDead, path monitor, host-app IPC) funnel here.
    /// First call wins; subsequent calls are logged and dropped.
    /// Updates connectionStatus to `.reconnecting` first so the host app
    /// doesn't show "Connected" during the brief tear-down window.
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
            logger.info("Received notifyWifiAvailable from host app")
            handleWifiAvailableSignal()
            return nil
        case .notifyPathChanged:
            // macOS-originated signal. The iOS host app does not send this
            // case; we still handle it for IPC-contract compatibility (a
            // shared VertexCore is built into both extensions).
            logger.info("Received notifyPathChanged (iOS no-op probe)")
            return nil
        }
        return try? JSONEncoder().encode(response)
    }

    /// Called when the host app's (unscoped) NWPathMonitor saw wifi
    /// associate. If we're not currently scoped to wifi, request a
    /// restart via the shared (re-entrant-safe) `requestCancel` so the
    /// on-demand rule rebuilds the extension on wifi.
    private func handleWifiAvailableSignal() {
        pathMonitorQueue.async { [weak self] in
            guard let self else { return }
            let currentType = self.currentDefaultInterface?.type
            guard currentType != .wifi else {
                self.logger.info("notifyWifiAvailable: already on wifi (\(self.currentDefaultInterface?.name ?? "?", privacy: .public)) — ignoring")
                return
            }
            self.logger.warning("notifyWifiAvailable: requesting switch onto wifi (current=\(String(describing: currentType), privacy: .public))")
            self.requestCancel(reason: "Switching to WiFi (IPC)", code: -2)
        }
    }


    // MARK: - RU CIDR loader

    /// Reads CIDR list from the App Group container, returns NEIPv4Routes.
    /// Returns empty array if file is missing or unreadable — extension stays
    /// usable as full-tunnel in that case.
    private static func loadRUNets(path: String) -> [NEIPv4Route] {
        guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { return [] }
        return CIDRParser.parseAll(text).map { route in
            NEIPv4Route(destinationAddress: route.address, subnetMask: route.mask)
        }
    }

    // MARK: - Identity Key

    /// Load the device's persistent identity key from Keychain, or create one
    /// on genuine first launch. Throws on `KeychainError.locked` so the caller
    /// can fail the connect attempt instead of silently minting a fresh key
    /// and overwriting the real one — that bug bricked TOFU on every reboot
    /// because on-demand starts the extension before first unlock.
    private func loadOrCreateIdentityKey() throws -> IdentityKey {
        do {
            let data = try KeychainStore.loadIdentityKey()
            return try IdentityKey(rawRepresentation: data)
        } catch KeychainError.notFound {
            // Genuine first launch — generate, persist, return.
            let key = IdentityKey()
            do {
                try KeychainStore.saveIdentityKey(key.rawRepresentation)
            } catch KeychainError.saveFailed(errSecInteractionNotAllowed) {
                // Keychain went locked between load (notFound) and save —
                // possible after reboot if the extension wins the race
                // before first unlock. Re-classify as .locked so on-demand
                // retries cleanly and the user sees the unlock prompt.
                throw KeychainError.locked
            }
            logger.info("Generated new identity key (first launch)")
            return key
        }
        // KeychainError.locked / loadFailed / IdentityKey decoding errors
        // propagate — caller turns them into a fatal connect failure.
    }
}
