package liveness

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/mrkutin/vertex/pkg/protocol"
	"github.com/mrkutin/vertex/pkg/transport"
)

// fakeTransport is a minimal in-memory transport that implements both
// transport.Transport and transport.Retainer. Plain pub/sub is enough to
// exercise the Feed contract without spinning up a real broker.
type fakeTransport struct {
	mu        sync.Mutex
	subs      []func(topic string, data []byte)
	published []publishedMsg
	retained  []publishedMsg
}

type publishedMsg struct {
	topic   string
	payload []byte
	retain  bool
}

func (f *fakeTransport) Publish(_ context.Context, topic string, data []byte) error {
	f.mu.Lock()
	f.published = append(f.published, publishedMsg{topic: topic, payload: data})
	f.mu.Unlock()
	return nil
}

func (f *fakeTransport) PublishRetained(_ context.Context, topic string, data []byte) error {
	f.mu.Lock()
	f.retained = append(f.retained, publishedMsg{topic: topic, payload: data, retain: true})
	subs := append([]func(string, []byte){}, f.subs...)
	f.mu.Unlock()
	// Loopback so a Watch on the same fakeTransport receives the value.
	for _, h := range subs {
		h(topic, data)
	}
	return nil
}

func (f *fakeTransport) Subscribe(_ context.Context, _ string, h func(topic string, data []byte)) error {
	f.mu.Lock()
	f.subs = append(f.subs, h)
	f.mu.Unlock()
	return nil
}

func (f *fakeTransport) Ready() bool                         { return true }
func (f *fakeTransport) WaitReady(_ context.Context) error   { return nil }
func (f *fakeTransport) Close(_ context.Context) error       { return nil }

// Verifies the fake satisfies the interfaces — caught at build time, not
// runtime, so this is structural insurance.
var (
	_ transport.Transport = (*fakeTransport)(nil)
	_ transport.Retainer  = (*fakeTransport)(nil)
)

func TestAnnouncePublishesRetained(t *testing.T) {
	tr := &fakeTransport{}
	feed, err := NewMQTTFeed(tr)
	if err != nil {
		t.Fatalf("NewMQTTFeed: %v", err)
	}

	info := NodeInfo{ID: "aws", Country: "CA", Clients: 1, MaxClients: 50, Uptime: 100, TS: 1234}
	if err := feed.Announce(context.Background(), info); err != nil {
		t.Fatalf("Announce: %v", err)
	}

	if len(tr.retained) != 1 {
		t.Fatalf("expected 1 retained message, got %d", len(tr.retained))
	}
	got := tr.retained[0]
	wantTopic := protocol.DiscoveryHeartbeat("aws").MQTTTopic()
	if got.topic != wantTopic {
		t.Errorf("topic: want %q, got %q", wantTopic, got.topic)
	}
	if !got.retain {
		t.Errorf("expected retain=true")
	}
	var decoded NodeInfo
	if err := json.Unmarshal(got.payload, &decoded); err != nil {
		t.Fatalf("payload not JSON NodeInfo: %v", err)
	}
	if decoded.ID != "aws" || decoded.Country != "CA" {
		t.Errorf("payload mismatch: %+v", decoded)
	}
}

func TestWatchEmitsUpdatedAndRemoved(t *testing.T) {
	tr := &fakeTransport{}
	feed, _ := NewMQTTFeed(tr)

	events, err := feed.Watch(context.Background())
	if err != nil {
		t.Fatalf("Watch: %v", err)
	}

	// Announce → Watch should observe EventUpdated.
	go func() {
		_ = feed.Announce(context.Background(), NodeInfo{ID: "aws", Country: "CA"})
	}()
	select {
	case evt := <-events:
		if evt.Type != EventUpdated {
			t.Errorf("type: want EventUpdated, got %v", evt.Type)
		}
		if evt.Info.ID != "aws" {
			t.Errorf("ID: want aws, got %s", evt.Info.ID)
		}
	case <-time.After(time.Second):
		t.Fatal("no event after Announce")
	}

	// Empty payload on the heartbeat topic = LWT-style removal.
	topic := protocol.DiscoveryHeartbeat("aws").MQTTTopic()
	if err := tr.PublishRetained(context.Background(), topic, nil); err != nil {
		t.Fatalf("simulate LWT: %v", err)
	}
	select {
	case evt := <-events:
		if evt.Type != EventRemoved {
			t.Errorf("type: want EventRemoved, got %v", evt.Type)
		}
		if evt.Info.ID != "aws" {
			t.Errorf("ID: want aws, got %s", evt.Info.ID)
		}
	case <-time.After(time.Second):
		t.Fatal("no removal event after empty payload")
	}
}

// Transports without the Retainer capability cannot announce — the Feed
// constructor must reject them rather than fail later inside Announce.
func TestNewMQTTFeedRejectsNonRetainer(t *testing.T) {
	type plain struct{ fakeTransport }
	// Trick: embed fakeTransport but override PublishRetained out of the
	// method set by hiding the embedded type... actually fakeTransport
	// has the method on a pointer receiver, so to "remove" the capability
	// we use a separate type without it.
	_, err := NewMQTTFeed(&onlyTransport{})
	if err == nil {
		t.Fatal("expected error for non-Retainer transport")
	}
	if !errors.Is(err, err) {
		// trivially true; just here so 'errors' import is referenced
	}
}

// TestNodeInfoJSONShape pins the exact wire format of the heartbeat
// payload. Swift clients (iOS, macOS) parse this via a struct with
// snake_case CodingKeys; an accidental rename here (e.g. Go default
// CamelCase serialization) would silently drop fields on decode and
// the iPhone would join WITHOUT the exit's DH pubkey, leaving id_sig
// empty — the exit then rejects with "invalid identity proof". Asks
// for an exact match on each field name.
func TestNodeInfoJSONShape(t *testing.T) {
	tr := &fakeTransport{}
	feed, _ := NewMQTTFeed(tr)

	info := NodeInfo{
		ID:         "sto",
		Country:    "SE",
		Clients:    3,
		MaxClients: 50,
		BrokerRTTs: map[string]int64{"broker": 7},
		Uptime:     42,
		DHPubKey:   "abc",
		TS:         1700000000,
	}
	if err := feed.Announce(context.Background(), info); err != nil {
		t.Fatalf("Announce: %v", err)
	}
	if len(tr.retained) != 1 {
		t.Fatalf("expected 1 retained, got %d", len(tr.retained))
	}
	var raw map[string]any
	if err := json.Unmarshal(tr.retained[0].payload, &raw); err != nil {
		t.Fatalf("not JSON: %v", err)
	}
	for _, key := range []string{"id", "country", "clients", "max_clients", "broker_rtt_ms", "uptime", "dh_pubkey", "ts"} {
		if _, ok := raw[key]; !ok {
			t.Errorf("missing required JSON field %q in heartbeat: %s", key, string(tr.retained[0].payload))
		}
	}
	// Reject CamelCase variants: the bug we caught was
	// `{"ID":"sto","DHPubKey":"abc"}` slipping out when JSON tags were
	// missing. Make that fail loudly.
	for _, key := range []string{"ID", "Country", "DHPubKey", "MaxClients", "BrokerRTTs"} {
		if _, ok := raw[key]; ok {
			t.Errorf("unexpected CamelCase field %q in heartbeat — JSON tags missing?", key)
		}
	}
}

// onlyTransport implements Transport but NOT Retainer.
type onlyTransport struct{}

func (onlyTransport) Publish(_ context.Context, _ string, _ []byte) error { return nil }
func (onlyTransport) Subscribe(_ context.Context, _ string, _ func(string, []byte)) error {
	return nil
}
func (onlyTransport) Ready() bool                       { return false }
func (onlyTransport) WaitReady(_ context.Context) error { return nil }
func (onlyTransport) Close(_ context.Context) error     { return nil }
