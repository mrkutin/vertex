import Foundation

/// MQTT 5.0 binary packet encoder/decoder for the minimal VPN subset.
///
/// Supports: CONNECT, CONNACK, PUBLISH (QoS 0), SUBSCRIBE (QoS 0),
/// SUBACK, PINGREQ, PINGRESP, DISCONNECT.
enum MQTTPacketCodec {

    // MARK: - Encode

    /// Encode a CONNECT packet (MQTT 5.0).
    static func encodeConnect(_ pkt: MQTTConnectPacket) -> Data {
        // Variable header
        var vh = Data()
        // Protocol Name: "MQTT"
        appendUTF8String("MQTT", to: &vh)
        // Protocol Version: 5
        vh.append(5)
        // Connect Flags
        var flags: UInt8 = 0
        if pkt.cleanStart { flags |= 0x02 }
        if pkt.username != nil { flags |= 0x80 }
        if pkt.password != nil { flags |= 0x40 }
        vh.append(flags)
        // Keep Alive
        appendUInt16(pkt.keepAlive, to: &vh)

        // Properties
        var props = Data()
        if let sei = pkt.sessionExpiryInterval {
            props.append(MQTTPropertyID.sessionExpiryInterval.rawValue)
            appendUInt32(sei, to: &props)
        }
        appendVariableInt(UInt32(props.count), to: &vh)
        vh.append(props)

        // Payload
        var payload = Data()
        appendUTF8String(pkt.clientID, to: &payload)
        if let u = pkt.username { appendUTF8String(u, to: &payload) }
        if let p = pkt.password { appendUTF8String(p, to: &payload) }

        return buildPacket(type: .connect, flags: 0, variableHeader: vh, payload: payload)
    }

    /// Encode a PUBLISH packet (QoS 0, no packet ID).
    static func encodePublish(_ pkt: MQTTPublishPacket) -> Data {
        var vh = Data()
        // Topic
        appendUTF8String(pkt.topic, to: &vh)
        // No packet ID for QoS 0

        // Properties
        var props = Data()
        if let expiry = pkt.messageExpiry {
            props.append(MQTTPropertyID.messageExpiryInterval.rawValue)
            appendUInt32(expiry, to: &props)
        }
        appendVariableInt(UInt32(props.count), to: &vh)
        vh.append(props)

        let flags: UInt8 = pkt.retain ? 0x01 : 0x00
        return buildPacket(type: .publish, flags: flags, variableHeader: vh, payload: pkt.payload)
    }

    /// Encode a SUBSCRIBE packet (QoS 0).
    static func encodeSubscribe(_ pkt: MQTTSubscribePacket) -> Data {
        var vh = Data()
        // Packet ID
        appendUInt16(pkt.packetID, to: &vh)
        // Properties (empty)
        vh.append(0) // property length = 0

        // Payload: topic filters with QoS 0
        var payload = Data()
        for topic in pkt.topics {
            appendUTF8String(topic, to: &payload)
            payload.append(0x00) // QoS 0, no options
        }

        return buildPacket(type: .subscribe, flags: 0x02, variableHeader: vh, payload: payload)
    }

    /// Encode a PINGREQ packet (2 bytes).
    static func encodePingReq() -> Data {
        Data([0xC0, 0x00]) // type=12, flags=0, remaining=0
    }

    /// Encode a DISCONNECT packet (reason=normal).
    static func encodeDisconnect() -> Data {
        // Type + flags, remaining length, reason code, property length
        Data([0xE0, 0x02, 0x00, 0x00])
    }

    // MARK: - Decode

    /// Try to decode one complete packet from the buffer.
    /// Returns (packet type, decoded data, bytes consumed) or nil if incomplete.
    static func tryDecode(_ buffer: Data) -> (MQTTPacketType, Data, Int)? {
        guard buffer.count >= 2 else { return nil }

        let firstByte = buffer[buffer.startIndex]
        let typeRaw = firstByte >> 4

        // Decode remaining length (variable-length integer)
        var multiplier: UInt32 = 1
        var remainingLength: UInt32 = 0
        var offset = buffer.startIndex + 1

        while offset < buffer.endIndex {
            let byte = buffer[offset]
            remainingLength += UInt32(byte & 0x7F) * multiplier
            offset += 1
            if byte & 0x80 == 0 { break }
            multiplier *= 128
            if multiplier > 128 * 128 * 128 { return nil } // max 4 bytes
        }

        let headerSize = offset - buffer.startIndex
        let totalSize = headerSize + Int(remainingLength)
        guard buffer.count >= totalSize else { return nil } // incomplete

        guard let packetType = MQTTPacketType(rawValue: typeRaw) else {
            return nil
        }

        let packetData = buffer[buffer.startIndex..<(buffer.startIndex + totalSize)]
        return (packetType, Data(packetData), totalSize)
    }

    /// Decode a CONNACK packet.
    static func decodeConnack(_ data: Data) throws -> MQTTConnackPacket {
        // Skip fixed header
        let (_, bodyOffset) = try parseFixedHeader(data)
        var offset = bodyOffset

        guard data.count >= offset + 2 else { throw MQTTCodecError.incompletePacket }

        // Acknowledge Flags
        let ackFlags = data[offset]
        let sessionPresent = (ackFlags & 0x01) != 0
        offset += 1

        // Reason Code
        let reasonCode = data[offset]
        offset += 1

        // Properties
        var serverKeepAlive: UInt16?
        if offset < data.count {
            let (propLen, propOffset) = try readVariableInt(data, from: offset)
            offset = propOffset
            let propEnd = offset + Int(propLen)

            while offset < propEnd && offset < data.count {
                let propID = data[offset]
                offset += 1
                switch propID {
                case MQTTPropertyID.serverKeepAlive.rawValue:
                    guard offset + 2 <= data.count else { break }
                    serverKeepAlive = readUInt16(data, at: offset)
                    offset += 2
                default:
                    // Skip unknown property — read its value based on type
                    offset = skipProperty(propID, in: data, at: offset)
                }
            }
        }

        return MQTTConnackPacket(
            sessionPresent: sessionPresent,
            reasonCode: reasonCode,
            serverKeepAlive: serverKeepAlive
        )
    }

    /// Decode a PUBLISH packet (incoming, QoS 0).
    static func decodePublish(_ data: Data) throws -> MQTTPublishReceived {
        let (_, bodyOffset) = try parseFixedHeader(data)
        var offset = bodyOffset

        // Topic
        let (topic, topicEnd) = try readUTF8String(data, from: offset)
        offset = topicEnd

        // No packet ID for QoS 0 (QoS is in fixed header flags bits 1-2)

        // Properties
        if offset < data.count {
            let (propLen, propOffset) = try readVariableInt(data, from: offset)
            offset = propOffset + Int(propLen) // skip all properties
        }

        // Payload = rest of packet
        let payload = data[offset...]
        return MQTTPublishReceived(topic: topic, payload: Data(payload))
    }

    /// Decode a SUBACK packet.
    static func decodeSuback(_ data: Data) throws -> MQTTSubackPacket {
        let (_, bodyOffset) = try parseFixedHeader(data)
        var offset = bodyOffset

        guard data.count >= offset + 2 else { throw MQTTCodecError.incompletePacket }

        // Packet ID
        let packetID = readUInt16(data, at: offset)
        offset += 2

        // Properties
        if offset < data.count {
            let (propLen, propOffset) = try readVariableInt(data, from: offset)
            offset = propOffset + Int(propLen)
        }

        // Reason codes (one per topic)
        let reasonCodes = Array(data[offset...])
        return MQTTSubackPacket(packetID: packetID, reasonCodes: reasonCodes)
    }

    // MARK: - Helpers (private)

    private static func buildPacket(
        type: MQTTPacketType,
        flags: UInt8,
        variableHeader: Data,
        payload: Data
    ) -> Data {
        let remaining = variableHeader.count + payload.count
        var packet = Data()
        packet.append((type.rawValue << 4) | (flags & 0x0F))
        appendVariableInt(UInt32(remaining), to: &packet)
        packet.append(variableHeader)
        packet.append(payload)
        return packet
    }

    private static func parseFixedHeader(_ data: Data) throws -> (remainingLength: UInt32, bodyOffset: Int) {
        guard data.count >= 2 else { throw MQTTCodecError.incompletePacket }
        let (remainingLength, bodyOffset) = try readVariableInt(data, from: data.startIndex + 1)
        return (remainingLength, bodyOffset)
    }

    // MARK: - Variable-length integer

    static func appendVariableInt(_ value: UInt32, to data: inout Data) {
        var v = value
        repeat {
            var byte = UInt8(v & 0x7F)
            v >>= 7
            if v > 0 { byte |= 0x80 }
            data.append(byte)
        } while v > 0
    }

    static func readVariableInt(_ data: Data, from start: Int) throws -> (UInt32, Int) {
        var multiplier: UInt32 = 1
        var value: UInt32 = 0
        var offset = start

        while offset < data.count {
            let byte = data[offset]
            value += UInt32(byte & 0x7F) * multiplier
            offset += 1
            if byte & 0x80 == 0 { return (value, offset) }
            multiplier *= 128
            if multiplier > 128 * 128 * 128 {
                throw MQTTCodecError.malformedRemainingLength
            }
        }
        throw MQTTCodecError.incompletePacket
    }

    // MARK: - UTF-8 strings

    private static func appendUTF8String(_ string: String, to data: inout Data) {
        let utf8 = Data(string.utf8)
        appendUInt16(UInt16(utf8.count), to: &data)
        data.append(utf8)
    }

    private static func readUTF8String(_ data: Data, from start: Int) throws -> (String, Int) {
        guard start + 2 <= data.count else { throw MQTTCodecError.incompletePacket }
        let length = Int(readUInt16(data, at: start))
        let strStart = start + 2
        guard strStart + length <= data.count else { throw MQTTCodecError.incompletePacket }
        guard let string = String(data: data[strStart..<(strStart + length)], encoding: .utf8) else {
            throw MQTTCodecError.malformedUTF8String
        }
        return (string, strStart + length)
    }

    // MARK: - Integer helpers

    private static func appendUInt16(_ value: UInt16, to data: inout Data) {
        data.append(UInt8(value >> 8))
        data.append(UInt8(value & 0xFF))
    }

    private static func appendUInt32(_ value: UInt32, to data: inout Data) {
        data.append(UInt8((value >> 24) & 0xFF))
        data.append(UInt8((value >> 16) & 0xFF))
        data.append(UInt8((value >> 8) & 0xFF))
        data.append(UInt8(value & 0xFF))
    }

    private static func readUInt16(_ data: Data, at offset: Int) -> UInt16 {
        UInt16(data[offset]) << 8 | UInt16(data[offset + 1])
    }

    /// Skip an unknown MQTT 5.0 property value based on property ID.
    private static func skipProperty(_ propID: UInt8, in data: Data, at offset: Int) -> Int {
        var off = offset
        switch propID {
        // 1-byte properties
        case 0x01, 0x17, 0x19, 0x24, 0x25, 0x28, 0x29, 0x2A:
            off += 1
        // 2-byte properties
        case 0x13, 0x21, 0x22, 0x23:
            off += 2
        // 4-byte properties
        case 0x02, 0x11, 0x18, 0x27:
            off += 4
        // UTF-8 string properties
        case 0x03, 0x08, 0x12, 0x15, 0x1A, 0x1C, 0x1F:
            if off + 2 <= data.count {
                let len = Int(readUInt16(data, at: off))
                off += 2 + len
            }
        // Binary data properties
        case 0x09, 0x16:
            if off + 2 <= data.count {
                let len = Int(readUInt16(data, at: off))
                off += 2 + len
            }
        // Variable byte integer properties
        case 0x0B:
            while off < data.count {
                let b = data[off]
                off += 1
                if b & 0x80 == 0 { break }
            }
        // User property (string pair)
        case 0x26:
            for _ in 0..<2 {
                if off + 2 <= data.count {
                    let len = Int(readUInt16(data, at: off))
                    off += 2 + len
                }
            }
        default:
            // Unknown property — can't determine size, bail
            off = data.count
        }
        return off
    }
}
