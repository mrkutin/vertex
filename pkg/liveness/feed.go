// Package liveness defines a transport-neutral interface for the
// announce/watch pattern used by Vertex exit-node discovery.
//
// Currently the data plane and the liveness plane both ride on MQTT —
// liveness uses retained heartbeats and Last-Will to signal removal.
// Splitting `Feed` out of the transport contract is the second half of
// the abstraction work: a future setup might run data-plane on QUIC or
// WebRTC (neither of which has retained-message semantics) while
// liveness keeps using MQTT, etcd lease, mDNS, or a static config.
//
// The Feed itself does not store state; it only emits events. The
// downstream `discovery.Tracker` accumulates them, handles staleness,
// and runs scoring/selection.
package liveness

import "context"

// NodeInfo describes a node's current state — published by the node
// itself in periodic announcements.
//
// JSON tags are load-bearing: the Swift clients (iOS, macOS) decode this
// payload via `DiscoveryHeartbeat` with snake_case CodingKeys. Changing
// any tag here is a wire-protocol break — tested clients in the field
// will silently miss fields. Bump a wire version before changing.
type NodeInfo struct {
	ID         string           `json:"id"`                 // unique node identifier (exit ID)
	Country    string           `json:"country,omitempty"`  // ISO country code, optional
	Clients    int              `json:"clients"`            // currently connected clients
	MaxClients int              `json:"max_clients"`        // soft cap for load balancing
	BrokerRTTs map[string]int64 `json:"broker_rtt_ms"`      // RTT in ms to each broker host (exit only)
	Uptime     int64            `json:"uptime"`             // seconds since process start
	DHPubKey   string           `json:"dh_pubkey,omitempty"` // base64 X25519 public key (PFS handshake)
	TS         int64            `json:"ts"`                 // Unix timestamp of this announcement
}

// EventType describes what happened to a node from the watcher's view.
type EventType int

const (
	// EventUnknown is the zero value — not emitted by Feed implementations.
	EventUnknown EventType = iota
	// EventUpdated: a new or refreshed announcement arrived. Carries
	// full NodeInfo. Watchers may treat the first observation as "added".
	EventUpdated
	// EventRemoved: the node went offline (explicit removal or
	// transport-level last-will signal). Only Info.ID is meaningful.
	EventRemoved
)

// NodeEvent is one item delivered on the Watch channel.
type NodeEvent struct {
	Type EventType
	Info NodeInfo
}

// Feed is the announce/watch contract.
//
// `Announce` and `Watch` are independent: a node may announce without
// watching (a producer-only exit), watch without announcing (a
// consumer-only client), or do both. Caller controls the announcement
// cadence — typically every 30s for an exit. Feed implementations are
// stateless beyond what they need to dispatch incoming events; cumulative
// state (last-seen timestamps, scoring, staleness) belongs to consumers.
type Feed interface {
	// Announce publishes the given NodeInfo so peers' Watch streams
	// observe it. Returns when the underlying transport has accepted the
	// message (or ctx is cancelled).
	Announce(ctx context.Context, info NodeInfo) error

	// Watch returns a channel of events from peers. The channel closes
	// when ctx is cancelled or Close is called.
	Watch(ctx context.Context) (<-chan NodeEvent, error)

	// Close releases watcher subscriptions; the transport is the
	// caller's to close separately.
	Close() error
}
