import Foundation
import Testing
@testable import VertexCore

@Suite("MQTT Packet Codec")
struct MQTTCodecTests {

    // MARK: - Variable-length integer

    @Test("Variable-length int encoding")
    func variableLengthEncode() {
        var data = Data()
        MQTTPacketCodec.appendVariableInt(0, to: &data)
        #expect(data == Data([0x00]))

        data = Data()
        MQTTPacketCodec.appendVariableInt(127, to: &data)
        #expect(data == Data([0x7F]))

        data = Data()
        MQTTPacketCodec.appendVariableInt(128, to: &data)
        #expect(data == Data([0x80, 0x01]))

        data = Data()
        MQTTPacketCodec.appendVariableInt(16383, to: &data)
        #expect(data == Data([0xFF, 0x7F]))

        data = Data()
        MQTTPacketCodec.appendVariableInt(16384, to: &data)
        #expect(data == Data([0x80, 0x80, 0x01]))
    }

    @Test("Variable-length int decoding")
    func variableLengthDecode() throws {
        let (v1, o1) = try MQTTPacketCodec.readVariableInt(Data([0x00]), from: 0)
        #expect(v1 == 0)
        #expect(o1 == 1)

        let (v2, o2) = try MQTTPacketCodec.readVariableInt(Data([0x7F]), from: 0)
        #expect(v2 == 127)
        #expect(o2 == 1)

        let (v3, o3) = try MQTTPacketCodec.readVariableInt(Data([0x80, 0x01]), from: 0)
        #expect(v3 == 128)
        #expect(o3 == 2)
    }

    // MARK: - CONNECT

    @Test("CONNECT packet encoding")
    func connectEncode() {
        let pkt = MQTTConnectPacket(
            clientID: "test",
            username: "user",
            password: "pass",
            keepAlive: 30,
            cleanStart: true,
            sessionExpiryInterval: 0
        )
        let data = MQTTPacketCodec.encodeConnect(pkt)

        // First byte: type=1 (CONNECT), flags=0
        #expect(data[0] == 0x10)

        // Protocol name "MQTT" starts at offset after remaining length
        let mqttStr = Data([0x00, 0x04, 0x4D, 0x51, 0x54, 0x54]) // len=4, "MQTT"
        let headerStart = data.index(data.startIndex, offsetBy: 2) // skip fixed header (1 byte type + 1 byte remaining)
        let slice = data[headerStart..<(headerStart + 6)]
        #expect(Data(slice) == mqttStr)

        // Protocol version = 5
        #expect(data[headerStart + 6] == 5)
    }

    // MARK: - PUBLISH

    @Test("PUBLISH packet encode/decode roundtrip")
    func publishRoundtrip() throws {
        let original = MQTTPublishPacket(
            topic: "vpn/aws/test/out",
            payload: Data([0x45, 0x00, 0x00, 0x3C]), // fake IP header
            retain: false,
            messageExpiry: 10
        )
        let encoded = MQTTPacketCodec.encodePublish(original)

        // First byte: type=3 (PUBLISH), QoS=0, retain=0
        #expect(encoded[0] == 0x30)

        // Decode it back
        let decoded = try MQTTPacketCodec.decodePublish(encoded)
        #expect(decoded.topic == "vpn/aws/test/out")
        #expect(decoded.payload == Data([0x45, 0x00, 0x00, 0x3C]))
    }

    @Test("PUBLISH with retain flag")
    func publishRetain() {
        let pkt = MQTTPublishPacket(
            topic: "test",
            payload: Data([0x01]),
            retain: true,
            messageExpiry: nil
        )
        let encoded = MQTTPacketCodec.encodePublish(pkt)
        // retain bit is bit 0 of first byte
        #expect(encoded[0] & 0x01 == 0x01)
    }

    // MARK: - SUBSCRIBE

    @Test("SUBSCRIBE packet encoding")
    func subscribeEncode() {
        let pkt = MQTTSubscribePacket(
            packetID: 1,
            topics: ["vpn/+/test/in", "vpn/+/test/control"]
        )
        let encoded = MQTTPacketCodec.encodeSubscribe(pkt)

        // First byte: type=8 (SUBSCRIBE), flags=0x02 (required by spec)
        #expect(encoded[0] == 0x82)
    }

    // MARK: - PINGREQ

    @Test("PINGREQ is exactly 2 bytes")
    func pingReq() {
        let data = MQTTPacketCodec.encodePingReq()
        #expect(data.count == 2)
        #expect(data[0] == 0xC0) // type=12
        #expect(data[1] == 0x00) // remaining=0
    }

    // MARK: - DISCONNECT

    @Test("DISCONNECT encoding")
    func disconnectEncode() {
        let data = MQTTPacketCodec.encodeDisconnect()
        #expect(data[0] == 0xE0) // type=14
    }

    // MARK: - tryDecode framing

    @Test("tryDecode returns nil for incomplete packet")
    func tryDecodeIncomplete() {
        // Just one byte — incomplete
        let result = MQTTPacketCodec.tryDecode(Data([0x30]))
        #expect(result == nil)
    }

    @Test("tryDecode parses PINGRESP")
    func tryDecodePingresp() {
        let data = Data([0xD0, 0x00]) // PINGRESP
        let result = MQTTPacketCodec.tryDecode(data)
        #expect(result != nil)
        #expect(result!.0 == .pingresp)
        #expect(result!.2 == 2)
    }

    @Test("tryDecode handles multiple packets in buffer")
    func tryDecodeMultiple() {
        // Two PINGRESPs concatenated
        var buffer = Data([0xD0, 0x00, 0xD0, 0x00])

        let first = MQTTPacketCodec.tryDecode(buffer)
        #expect(first != nil)
        #expect(first!.2 == 2)

        buffer.removeFirst(first!.2)
        let second = MQTTPacketCodec.tryDecode(buffer)
        #expect(second != nil)
        #expect(second!.0 == .pingresp)
    }

    // MARK: - CONNACK

    @Test("CONNACK success decode")
    func connackSuccess() throws {
        // Minimal CONNACK: type=0x20, remaining=3, ack_flags=0, reason=0, props_len=0
        let data = Data([0x20, 0x03, 0x00, 0x00, 0x00])
        let connack = try MQTTPacketCodec.decodeConnack(data)
        #expect(connack.isSuccess)
        #expect(!connack.sessionPresent)
    }

    @Test("CONNACK failure decode")
    func connackFailure() throws {
        // reason=0x86 = "Bad username or password"
        let data = Data([0x20, 0x03, 0x00, 0x86, 0x00])
        let connack = try MQTTPacketCodec.decodeConnack(data)
        #expect(!connack.isSuccess)
        #expect(connack.reasonCode == 0x86)
    }

    // MARK: - BrokerURL

    @Test("BrokerURL parses all schemes")
    func brokerURLParsing() {
        let mqtt = BrokerURL(string: "mqtt://localhost:1883")
        #expect(mqtt != nil)
        #expect(mqtt!.scheme == .mqtt)
        #expect(mqtt!.port == 1883)
        #expect(!mqtt!.isTLS)
        #expect(!mqtt!.isWebSocket)

        let mqtts = BrokerURL(string: "mqtts://broker.example.com")
        #expect(mqtts != nil)
        #expect(mqtts!.port == 8883)
        #expect(mqtts!.isTLS)

        let ws = BrokerURL(string: "ws://localhost:9001")
        #expect(ws != nil)
        #expect(ws!.isWebSocket)
        #expect(!ws!.isTLS)

        let wss = BrokerURL(string: "wss://broker.example.com:443")
        #expect(wss != nil)
        #expect(wss!.isWebSocket)
        #expect(wss!.isTLS)
    }

    // MARK: - Topics

    @Test("Topic builders match Go implementation")
    func topicBuilders() {
        #expect(Topics.upload(exit: "aws", name: "mac") == "vpn/aws/mac/out")
        #expect(Topics.download(exit: "aws", name: "mac") == "vpn/aws/mac/in")
        #expect(Topics.downloadAny(name: "mac") == "vpn/+/mac/in")
        #expect(Topics.join(exit: "aws") == "vpn/aws/control/join")
        #expect(Topics.control(exit: "aws", name: "mac") == "vpn/aws/mac/control")
        #expect(Topics.controlAny(name: "mac") == "vpn/+/mac/control")
        #expect(Topics.discovery(exit: "aws") == "discovery/exits/aws")
        #expect(Topics.discoveryAll == "discovery/exits/+")
    }
}
