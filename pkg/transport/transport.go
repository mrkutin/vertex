// Package transport defines the abstract message transport used by Vertex
// data and control planes. Concrete implementations (MQTT today; NATS,
// Kafka, gRPC stream, QUIC datagram in the future) live in subpackages.
//
// The base `Transport` interface is intentionally minimal — only the
// universally-supported pub/sub primitives. Features that vary by
// transport (retained messages, last-will, message-expiry, schema
// validation) are exposed through optional capability interfaces.
// Consumers that need a capability should type-assert it explicitly,
// so the requirement is visible at the call site:
//
//	if r, ok := tr.(transport.Retainer); ok {
//	    r.PublishRetained(ctx, topic, data)
//	}
package transport

import (
	"context"
	"time"
)

// Transport is a bidirectional message transport with topic-based routing.
// Implementations: MQTT (pkg/transport/mqtt). NATS, gRPC, QUIC are future.
//
// All blocking operations take a context.Context so callers can bound
// latency; this is essential for stream-based transports (gRPC/QUIC) where
// internal timeouts are not natural.
type Transport interface {
	// Publish sends data to the specified topic.
	Publish(ctx context.Context, topic string, data []byte) error

	// Subscribe registers a handler for messages matching the topic pattern.
	// Pattern may include wildcards (e.g. "vpn/aws/+/out" for MQTT).
	// Handler receives the actual topic the message arrived on and the
	// payload. Can be called multiple times for different patterns.
	Subscribe(ctx context.Context, pattern string, handler func(topic string, data []byte)) error

	// Ready returns true when the transport is connected and operational.
	Ready() bool

	// WaitReady blocks until the transport finishes its first connect, or
	// ctx is cancelled.
	WaitReady(ctx context.Context) error

	// Close shuts down the transport gracefully.
	Close(ctx context.Context) error
}

// Retainer is implemented by transports that support retained messages —
// state-style messages where a new subscriber receives the last value
// published on the topic. Used for liveness/discovery heartbeats.
//
// MQTT supports it natively. NATS Core does not (use JetStream KV).
// Kafka does (via log compaction). gRPC/QUIC have no equivalent —
// callers should provide the heartbeat semantics themselves.
type Retainer interface {
	PublishRetained(ctx context.Context, topic string, data []byte) error
}

// RTTProbe is implemented by transports that can measure round-trip time
// to the underlying broker. Used by exit nodes to populate
// `broker_rtt_ms` in discovery heartbeats so watchers can score exits
// from a network-latency perspective: lower RTT = closer broker.
//
// Implementations should publish a small acknowledged message (MQTT
// QoS 1, NATS request/no-reply, etc.) and time the round-trip from
// before-send to ack. The topic should be a write-only one with no
// expected consumer; brokers that PUBACK on QoS 1 will deliver the
// timing signal regardless. ctx bounds the wait — callers treat a
// context-deadline error as "broker slow / unreachable" and either
// skip this RTT entry or fall back to a stale value.
type RTTProbe interface {
	ProbeRTT(ctx context.Context, topic string) (time.Duration, error)
}

// LWTConfig describes an automatic offline-notification message published
// by the broker when the transport disconnects ungracefully. Configured
// at construction time (not at runtime), since the broker needs to know
// the will at connect time.
type LWTConfig struct {
	Topic   string // topic to publish on ungraceful disconnect
	Payload []byte // payload (empty = clear retained)
	Retain  bool   // whether LWT should be retained
}
