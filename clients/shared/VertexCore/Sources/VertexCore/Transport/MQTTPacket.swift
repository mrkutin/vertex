import Foundation

// MARK: - Packet Types

/// MQTT 5.0 control packet types (4-bit, upper nibble of first byte).
public enum MQTTPacketType: UInt8, Sendable {
    case connect     = 1
    case connack     = 2
    case publish     = 3
    case subscribe   = 8
    case suback      = 9
    case pingreq     = 12
    case pingresp    = 13
    case disconnect  = 14
}

// MARK: - CONNECT

/// MQTT 5.0 CONNECT packet parameters.
struct MQTTConnectPacket: Sendable {
    let clientID: String
    let username: String?
    let password: String?
    let keepAlive: UInt16
    let cleanStart: Bool
    let sessionExpiryInterval: UInt32?
}

// MARK: - CONNACK

/// MQTT 5.0 CONNACK packet (decoded).
struct MQTTConnackPacket: Sendable {
    let sessionPresent: Bool
    let reasonCode: UInt8
    let serverKeepAlive: UInt16?

    var isSuccess: Bool { reasonCode == 0 }

    var reasonString: String {
        switch reasonCode {
        case 0: "Success"
        case 0x80: "Unspecified error"
        case 0x81: "Malformed packet"
        case 0x82: "Protocol error"
        case 0x83: "Implementation specific error"
        case 0x84: "Unsupported protocol version"
        case 0x85: "Client ID not valid"
        case 0x86: "Bad username or password"
        case 0x87: "Not authorized"
        case 0x88: "Server unavailable"
        case 0x89: "Server busy"
        case 0x8A: "Banned"
        case 0x8C: "Bad authentication method"
        case 0x90: "Topic name invalid"
        case 0x95: "Packet too large"
        case 0x97: "Quota exceeded"
        case 0x99: "Payload format invalid"
        case 0x9A: "Retain not supported"
        case 0x9B: "QoS not supported"
        case 0x9C: "Use another server"
        case 0x9D: "Server moved"
        case 0x9F: "Connection rate exceeded"
        default: "Unknown (\(reasonCode))"
        }
    }
}

// MARK: - PUBLISH

/// MQTT 5.0 PUBLISH packet parameters (QoS 0 only).
struct MQTTPublishPacket: Sendable {
    let topic: String
    let payload: Data
    let retain: Bool
    let messageExpiry: UInt32?
}

/// Decoded incoming PUBLISH packet.
struct MQTTPublishReceived: Sendable {
    let topic: String
    let payload: Data
}

// MARK: - SUBSCRIBE

/// MQTT 5.0 SUBSCRIBE packet (QoS 0 only).
struct MQTTSubscribePacket: Sendable {
    let packetID: UInt16
    let topics: [String]
}

/// Decoded SUBACK.
struct MQTTSubackPacket: Sendable {
    let packetID: UInt16
    let reasonCodes: [UInt8]

    var allSuccess: Bool {
        reasonCodes.allSatisfy { $0 <= 2 } // 0=QoS0, 1=QoS1, 2=QoS2 granted
    }
}

// MARK: - MQTT 5.0 Property IDs

enum MQTTPropertyID: UInt8 {
    case sessionExpiryInterval = 0x11   // 17
    case serverKeepAlive       = 0x13   // 19
    case messageExpiryInterval = 0x02   // 2
}

// MARK: - Errors

enum MQTTCodecError: Error, Sendable {
    case incompletePacket
    case invalidPacketType(UInt8)
    case malformedRemainingLength
    case malformedUTF8String
    case connackFailed(String)
    case unexpectedPacketType(expected: MQTTPacketType, got: MQTTPacketType)
}
