// Package protocol describes the Vertex application protocol — the names of
// the participants and the kinds of messages they exchange — without
// reference to any particular transport.
//
// Today the only transport-encoding is MQTT (hierarchical topics with
// `+`/`#` wildcards), but a NATS, Kafka, or gRPC implementation would
// reuse `Address` and `Kind` and supply its own encoder/decoder.
//
// The motivation for promoting these from string-formatting in callers is
// that previously every consumer parsed topics by hand with strings.Split,
// hard-coding both the segment count and segment positions. That pattern
// works only for MQTT-style hierarchical topics — Kafka has no wildcards,
// gRPC has no topics at all — and it scattered three different parsers
// across the codebase that had to agree on the layout.
package protocol

import (
	"fmt"
	"strings"
)

// Kind identifies the role of an address — what kind of message flows on it.
type Kind int

const (
	// KindUnknown is the zero value, used by parsers when no pattern matches.
	KindUnknown Kind = iota
	// KindDataOut: data plane, client → exit (encrypted IP packet uplink).
	KindDataOut
	// KindDataIn: data plane, exit → client (encrypted IP packet downlink).
	KindDataIn
	// KindControl: control plane, exit → client (assign responses, errors).
	KindControl
	// KindJoin: control plane, client → exit (join requests with DH key).
	KindJoin
	// KindDiscoveryHeartbeat: discovery, exit → all (retained liveness/load).
	KindDiscoveryHeartbeat
	// KindDiscoveryPing: discovery, exit → broker (RTT probe; no consumer).
	KindDiscoveryPing
)

func (k Kind) String() string {
	switch k {
	case KindDataOut:
		return "data-out"
	case KindDataIn:
		return "data-in"
	case KindControl:
		return "control"
	case KindJoin:
		return "join"
	case KindDiscoveryHeartbeat:
		return "discovery-heartbeat"
	case KindDiscoveryPing:
		return "discovery-ping"
	default:
		return "unknown"
	}
}

// Address identifies a participant on the Vertex control/data plane.
// Different Kinds use different fields — see the constructors below for
// which fields each Kind requires; zero-valued fields are simply unused.
type Address struct {
	Kind       Kind
	ExitID     string
	ClientName string
	Index      int // for KindDiscoveryPing
}

// DataOut builds an address for a client→exit data packet.
func DataOut(exit, name string) Address {
	return Address{Kind: KindDataOut, ExitID: exit, ClientName: name}
}

// DataIn builds an address for an exit→client data packet.
func DataIn(exit, name string) Address {
	return Address{Kind: KindDataIn, ExitID: exit, ClientName: name}
}

// Control builds an address for an exit→client control message addressed
// to a specific client name.
func Control(exit, name string) Address {
	return Address{Kind: KindControl, ExitID: exit, ClientName: name}
}

// Join builds an address for a client→exit join request.
func Join(exit string) Address {
	return Address{Kind: KindJoin, ExitID: exit}
}

// DiscoveryHeartbeat builds an address for a retained discovery announcement.
func DiscoveryHeartbeat(exitID string) Address {
	return Address{Kind: KindDiscoveryHeartbeat, ExitID: exitID}
}

// DiscoveryPing builds an address for an RTT-measurement ping (one-shot,
// not consumed by any subscriber — its only purpose is to time the publish
// round-trip).
func DiscoveryPing(exitID string, brokerIdx int) Address {
	return Address{Kind: KindDiscoveryPing, ExitID: exitID, Index: brokerIdx}
}

// MQTTTopic encodes the address as an MQTT topic per the Vertex
// convention. The mapping is:
//
//	data-out:               vpn/{exit}/{name}/out
//	data-in:                vpn/{exit}/{name}/in
//	control:                vpn/{exit}/{name}/control
//	join:                   vpn/{exit}/control/join
//	discovery-heartbeat:    discovery/exits/{exit}
//	discovery-ping:         discovery/ping/{exit}/{idx}
//
// Returns "" for KindUnknown or otherwise invalid addresses.
func (a Address) MQTTTopic() string {
	switch a.Kind {
	case KindDataOut:
		return fmt.Sprintf("vpn/%s/%s/out", a.ExitID, a.ClientName)
	case KindDataIn:
		return fmt.Sprintf("vpn/%s/%s/in", a.ExitID, a.ClientName)
	case KindControl:
		return fmt.Sprintf("vpn/%s/%s/control", a.ExitID, a.ClientName)
	case KindJoin:
		return fmt.Sprintf("vpn/%s/control/join", a.ExitID)
	case KindDiscoveryHeartbeat:
		return fmt.Sprintf("discovery/exits/%s", a.ExitID)
	case KindDiscoveryPing:
		return fmt.Sprintf("discovery/ping/%s/%d", a.ExitID, a.Index)
	default:
		return ""
	}
}

// MQTT subscription patterns. These are not Address values because
// patterns include MQTT-specific wildcards (`+`); they are produced by
// helpers so the wildcard appears in exactly one place.

// MQTTDataInbound is the pattern an exit subscribes to in order to receive
// uplink packets from any client of that exit (vpn/{exit}/+/out).
func MQTTDataInbound(exit string) string {
	return fmt.Sprintf("vpn/%s/+/out", exit)
}

// MQTTControlAny is the pattern a client subscribes to in order to receive
// control responses from any exit addressed to its own name
// (vpn/+/{name}/control). The "+" lets the client survive runtime exit
// switches without re-subscribing.
func MQTTControlAny(name string) string {
	return fmt.Sprintf("vpn/+/%s/control", name)
}

// MQTTDataAny is the pattern a client uses in auto-select mode to receive
// downlink packets from whichever exit the broker routes through
// (vpn/+/{name}/in).
func MQTTDataAny(name string) string {
	return fmt.Sprintf("vpn/+/%s/in", name)
}

// MQTTJoinPattern is the pattern an exit subscribes to in order to receive
// join requests from any client (vpn/{exit}/control/join — no wildcard,
// concrete topic, but listed here for symmetry).
func MQTTJoinPattern(exit string) string {
	return fmt.Sprintf("vpn/%s/control/join", exit)
}

// MQTTDiscoveryAll is the pattern subscribers use to receive heartbeats
// from every exit (discovery/exits/+).
const MQTTDiscoveryAll = "discovery/exits/+"

// ParseMQTTTopic decodes an Address from an MQTT topic. Returns
// `Kind == KindUnknown` if the topic doesn't match any known pattern.
//
// Recognized layouts (segment count, fixed segments, variable parts):
//
//	vpn/{exit}/{name}/out                 → data-out
//	vpn/{exit}/{name}/in                  → data-in
//	vpn/{exit}/{name}/control             → control      (or join if name=="control" && segment=="join")
//	vpn/{exit}/control/join               → join
//	discovery/exits/{exit}                → discovery-heartbeat
//	discovery/ping/{exit}/{idx}           → discovery-ping (idx parse error → KindUnknown)
func ParseMQTTTopic(topic string) Address {
	parts := strings.Split(topic, "/")

	switch len(parts) {
	case 3:
		// discovery/exits/{exit}
		if parts[0] == "discovery" && parts[1] == "exits" {
			return DiscoveryHeartbeat(parts[2])
		}
	case 4:
		// vpn/{exit}/control/join — must come before vpn/{exit}/{name}/control
		// because the third segment is the literal "control" in both layouts
		// but the fourth distinguishes them.
		if parts[0] == "vpn" && parts[2] == "control" && parts[3] == "join" {
			return Join(parts[1])
		}
		if parts[0] == "vpn" {
			switch parts[3] {
			case "out":
				return DataOut(parts[1], parts[2])
			case "in":
				return DataIn(parts[1], parts[2])
			case "control":
				return Control(parts[1], parts[2])
			}
		}
		// discovery/ping/{exit}/{idx}
		if parts[0] == "discovery" && parts[1] == "ping" {
			var idx int
			if _, err := fmt.Sscanf(parts[3], "%d", &idx); err == nil {
				return DiscoveryPing(parts[2], idx)
			}
		}
	}
	return Address{Kind: KindUnknown}
}
