package liveness

import (
	"context"
	"encoding/json"
	"fmt"

	"github.com/mrkutin/vertex/pkg/protocol"
	"github.com/mrkutin/vertex/pkg/transport"
)

// MQTTLWTConfig builds the Last-Will configuration that an exit must
// register at MQTT connect time. The will publishes an empty payload to
// the node's heartbeat topic with retain=true on ungraceful disconnect,
// which clears the retained announcement and is observed by watchers as
// `EventRemoved`. Used by callers like:
//
//	lwt := liveness.MQTTLWTConfig(cfg.ID)
//	tr, _ := mqtt.New(url, user, pass, id, mqtt.WithLWT(lwt))
//
// We do not bury this inside `Feed` because LWT must be set at transport
// connect time — before any Feed exists.
func MQTTLWTConfig(nodeID string) transport.LWTConfig {
	return transport.LWTConfig{
		Topic:   protocol.DiscoveryHeartbeat(nodeID).MQTTTopic(),
		Payload: nil,
		Retain:  true,
	}
}

// mqttFeed is the MQTT-flavored implementation of Feed: announcements are
// retained publishes on `discovery/exits/{id}`, watch is a wildcard
// subscribe on `discovery/exits/+`.
type mqttFeed struct {
	tr transport.Transport
	r  transport.Retainer
}

// NewMQTTFeed constructs a Feed backed by the given transport. The
// transport must implement Retainer (otherwise heartbeats can't be
// announced). On the watcher side, callers should also have configured
// `MQTTLWTConfig` via `mqtt.WithLWT` at transport construction so that
// removal is observed automatically on ungraceful disconnect.
func NewMQTTFeed(tr transport.Transport) (Feed, error) {
	r, ok := tr.(transport.Retainer)
	if !ok {
		return nil, fmt.Errorf("transport does not implement Retainer — cannot announce liveness")
	}
	return &mqttFeed{tr: tr, r: r}, nil
}

func (f *mqttFeed) Announce(ctx context.Context, info NodeInfo) error {
	data, err := json.Marshal(info)
	if err != nil {
		return fmt.Errorf("encode NodeInfo: %w", err)
	}
	topic := protocol.DiscoveryHeartbeat(info.ID).MQTTTopic()
	return f.r.PublishRetained(ctx, topic, data)
}

func (f *mqttFeed) Watch(ctx context.Context) (<-chan NodeEvent, error) {
	// Buffered enough to absorb the burst of retained messages that
	// arrives right after subscribe (one per known exit). Capacity
	// chosen large enough that consumer queues never see backpressure
	// in practice; if we ever have hundreds of exits, this becomes a
	// configuration parameter.
	ch := make(chan NodeEvent, 64)
	err := f.tr.Subscribe(ctx, protocol.MQTTDiscoveryAll, func(topic string, data []byte) {
		addr := protocol.ParseMQTTTopic(topic)
		if addr.Kind != protocol.KindDiscoveryHeartbeat {
			return
		}
		if len(data) == 0 {
			// Empty retained payload = LWT cleared this exit's announcement.
			select {
			case ch <- NodeEvent{Type: EventRemoved, Info: NodeInfo{ID: addr.ExitID}}:
			default:
			}
			return
		}
		var info NodeInfo
		if err := json.Unmarshal(data, &info); err != nil {
			return
		}
		// Trust the topic, not the payload — defensive against a misbehaving
		// publisher claiming a different ID inside the JSON.
		info.ID = addr.ExitID
		select {
		case ch <- NodeEvent{Type: EventUpdated, Info: info}:
		default:
		}
	})
	if err != nil {
		close(ch)
		return nil, fmt.Errorf("subscribe to discovery: %w", err)
	}
	return ch, nil
}

func (f *mqttFeed) Close() error {
	// Transport-level subscriptions are owned by the transport; the Feed
	// has no resources to release. Kept here so future implementations
	// (etcd lease, gRPC stream) have a hook for shutdown.
	return nil
}
