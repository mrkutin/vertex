package vpn

import "encoding/binary"

// FixChecksums recalculates TCP/UDP checksums for a raw IP packet.
// This is needed because the kernel may use checksum offloading when writing
// packets to TUN interfaces, resulting in incorrect checksums.
func FixChecksums(pkt []byte) {
	if len(pkt) < 20 {
		return
	}
	ver := pkt[0] >> 4
	if ver != 4 {
		return
	}
	ihl := int(pkt[0]&0x0f) * 4
	if ihl < 20 || len(pkt) < ihl {
		return
	}

	proto := pkt[9]
	switch proto {
	case 6: // TCP
		if len(pkt) < ihl+18 {
			return
		}
		pkt[ihl+16] = 0
		pkt[ihl+17] = 0
		cksum := transportChecksum(pkt, ihl, proto)
		binary.BigEndian.PutUint16(pkt[ihl+16:ihl+18], cksum)

	case 17: // UDP
		if len(pkt) < ihl+8 {
			return
		}
		pkt[ihl+6] = 0
		pkt[ihl+7] = 0
		cksum := transportChecksum(pkt, ihl, proto)
		if cksum == 0 {
			cksum = 0xffff
		}
		binary.BigEndian.PutUint16(pkt[ihl+6:ihl+8], cksum)

	case 1: // ICMP
		if len(pkt) < ihl+4 {
			return
		}
		pkt[ihl+2] = 0
		pkt[ihl+3] = 0
		cksum := icmpChecksum(pkt[ihl:])
		binary.BigEndian.PutUint16(pkt[ihl+2:ihl+4], cksum)
	}
}

func icmpChecksum(data []byte) uint16 {
	return ipChecksum(data)
}

func ipChecksum(header []byte) uint16 {
	var sum uint32
	for i := 0; i < len(header)-1; i += 2 {
		sum += uint32(header[i])<<8 | uint32(header[i+1])
	}
	if len(header)%2 != 0 {
		sum += uint32(header[len(header)-1]) << 8
	}
	for sum > 0xffff {
		sum = (sum >> 16) + (sum & 0xffff)
	}
	return ^uint16(sum)
}

func transportChecksum(pkt []byte, ihl int, proto byte) uint16 {
	var sum uint32

	// Pseudo header
	sum += uint32(pkt[12])<<8 | uint32(pkt[13]) // src IP
	sum += uint32(pkt[14])<<8 | uint32(pkt[15])
	sum += uint32(pkt[16])<<8 | uint32(pkt[17]) // dst IP
	sum += uint32(pkt[18])<<8 | uint32(pkt[19])
	sum += uint32(proto)
	transportLen := len(pkt) - ihl
	sum += uint32(transportLen)

	// Sum transport header + data
	for i := ihl; i < len(pkt)-1; i += 2 {
		sum += uint32(pkt[i])<<8 | uint32(pkt[i+1])
	}
	if transportLen%2 != 0 {
		sum += uint32(pkt[len(pkt)-1]) << 8
	}

	for sum > 0xffff {
		sum = (sum >> 16) + (sum & 0xffff)
	}
	return ^uint16(sum)
}
