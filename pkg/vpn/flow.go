package vpn

import "encoding/binary"

// FlowHash computes a session index for an IP packet based on its 5-tuple.
// All packets of the same TCP/UDP flow always map to the same session.
func FlowHash(pkt []byte, numSessions int) int {
	if numSessions <= 1 || len(pkt) < 20 {
		return 0
	}

	ver := pkt[0] >> 4
	if ver != 4 {
		return 0
	}

	ihl := int(pkt[0]&0x0f) * 4
	proto := pkt[9]

	var h uint32
	h = fnv32(pkt[12:16])  // src IP
	h ^= fnv32(pkt[16:20]) // dst IP
	h ^= uint32(proto)

	// Add ports for TCP/UDP
	if (proto == 6 || proto == 17) && len(pkt) >= ihl+4 {
		srcPort := binary.BigEndian.Uint16(pkt[ihl : ihl+2])
		dstPort := binary.BigEndian.Uint16(pkt[ihl+2 : ihl+4])
		h ^= uint32(srcPort)<<16 | uint32(dstPort)
	}

	return int(h % uint32(numSessions))
}

func fnv32(data []byte) uint32 {
	h := uint32(2166136261)
	for _, b := range data {
		h ^= uint32(b)
		h *= 16777619
	}
	return h
}
