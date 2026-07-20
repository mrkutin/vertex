import Foundation
import Testing
@testable import VertexCore

/// Integration tests against a Docker Mosquitto broker.
/// Requires: docker compose up -d mosquitto-1
/// Mosquitto at localhost:1883 with user vtx-client-mac / test123
@Suite("MQTT Integration", .disabled(if: !isMosquittoAvailable(), "Docker Mosquitto not running"))
struct MQTTIntegrationTests {

    @Test("Connect and disconnect")
    func connectDisconnect() async throws {
        let transport = MQTTTransport(
            brokers: [BrokerURL(string: "mqtt://localhost:1883")!],
            username: "vtx-client-mac",
            password: "test123",
            clientID: "vtx-test-\(UUID().uuidString.prefix(8))"
        )

        try await transport.start()
        #expect(transport.isReady)

        transport.stop()
        // Give time for disconnect
        try await Task.sleep(for: .milliseconds(200))
        #expect(!transport.isReady)
    }

    @Test("Subscribe and publish work without errors")
    func subscribeAndPublish() async throws {
        let transport = MQTTTransport(
            brokers: [BrokerURL(string: "mqtt://localhost:1883")!],
            username: "vtx-client-mac",
            password: "test123",
            clientID: "vtx-test-\(UUID().uuidString.prefix(8))"
        )

        // Subscribe to download topic (read-only per ACL)
        transport.subscribe(pattern: "vpn/aws/mac/in") { _, _ in }

        try await transport.start()
        #expect(transport.isReady)

        // Wait for SUBSCRIBE
        try await Task.sleep(for: .milliseconds(300))

        // Publish to upload topic (write-only per ACL) — should not error
        let testPayload = Data("hello-mqtt".utf8)
        transport.publish(topic: "vpn/aws/mac/out", payload: testPayload)

        // Publish join (write ACL)
        let join = JoinMessage(name: "mac", dh: String(repeating: "ab", count: 32))
        let joinData = try JSONEncoder().encode(join)
        transport.publish(topic: Topics.join(exit: "aws"), payload: joinData)

        // Give broker time to process
        try await Task.sleep(for: .milliseconds(300))

        // Still connected = no ACL rejections crashed the connection
        #expect(transport.isReady, "Transport should stay connected after publish")

        transport.stop()
    }

    @Test("Join handshake with exit-aws", .disabled("Requires exit-aws on same broker network"))
    func joinHandshake() async throws {
        let transport = MQTTTransport(
            brokers: [BrokerURL(string: "mqtt://localhost:1883")!],
            username: "vtx-client-mac",
            password: "test123",
            clientID: "vtx-client-aws-mac-test"
        )

        let assignReceived = Mutex<AssignMessage?>(nil)

        // Subscribe to control topic for assign response
        transport.subscribe(pattern: Topics.controlAny(name: "mac")) { topic, data in
            if let assign = try? JSONDecoder().decode(AssignMessage.self, from: data) {
                assignReceived.withLock { $0 = assign }
            }
        }

        try await transport.start()

        // Send join message (with dummy DH key)
        let join = JoinMessage(
            name: "mac",
            dh: String(repeating: "aa", count: 32), // 32-byte dummy key hex
            id: nil,
            idSig: nil
        )
        let joinData = try JSONEncoder().encode(join)
        transport.publish(topic: Topics.join(exit: "aws"), payload: joinData)

        // Wait for assign response from exit-aws
        for _ in 0..<50 {
            try await Task.sleep(for: .milliseconds(100))
            if assignReceived.withLock({ $0 }) != nil { break }
        }

        let assign = assignReceived.withLock { $0 }
        #expect(assign != nil, "Should receive AssignMessage from exit-aws")
        if let assign {
            #expect(assign.ip.hasPrefix("10.9.0."), "IP should be in 10.9.0.0/24 subnet")
            #expect(assign.gw == "10.9.0.1", "Gateway should be 10.9.0.1")
            #expect(!(assign.dh ?? "").isEmpty, "Exit should return DH pubkey")
        }

        transport.stop()
    }
}

// Helper: check if Mosquitto is listening on localhost:1883
private func isMosquittoAvailable() -> Bool {
    let sock = socket(AF_INET, SOCK_STREAM, 0)
    guard sock >= 0 else { return false }
    defer { close(sock) }

    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = UInt16(1883).bigEndian
    addr.sin_addr.s_addr = inet_addr("127.0.0.1")

    let result = withUnsafePointer(to: &addr) { ptr in
        ptr.withMemoryRebound(to: sockaddr.self, capacity: 1) { sockPtr in
            connect(sock, sockPtr, socklen_t(MemoryLayout<sockaddr_in>.size))
        }
    }
    return result == 0
}

/// Simple mutex for test synchronization.
final class Mutex<Value>: @unchecked Sendable {
    private var value: Value
    private let lock = NSLock()

    init(_ value: Value) { self.value = value }

    func withLock<T>(_ body: (inout Value) -> T) -> T {
        lock.lock()
        defer { lock.unlock() }
        return body(&value)
    }
}
