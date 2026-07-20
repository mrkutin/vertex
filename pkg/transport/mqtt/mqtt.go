package mqtt

import (
	"context"
	"crypto/tls"
	"fmt"
	"log"
	"net"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/eclipse/paho.golang/autopaho"
	"github.com/eclipse/paho.golang/packets"
	"github.com/eclipse/paho.golang/paho"

	"github.com/mrkutin/vertex/pkg/transport"
)

type subscription struct {
	pattern string
	handler func(topic string, data []byte)
}

// Option configures optional Transport behavior.
type Option func(*options)

type options struct {
	lwt *transport.LWTConfig
}

// WithLWT sets the Last Will and Testament for this connection.
// Accepts the transport-neutral LWTConfig type so callers don't have to
// import the mqtt package just to construct one.
func WithLWT(lwt transport.LWTConfig) Option {
	return func(o *options) { o.lwt = &lwt }
}

// Transport implements transport.Transport over MQTT 5.0.
// It also implements transport.Retainer (PublishRetained for discovery
// heartbeats and other state-style messages).
type Transport struct {
	cm         *autopaho.ConnectionManager
	clientID   string
	subs       []subscription
	ready      atomic.Bool
	mu         sync.Mutex
	lastFailed atomic.Int32 // index of last failed URL (for sticky reconnect)
}

// Compile-time interface conformance checks.
var (
	_ transport.Transport = (*Transport)(nil)
	_ transport.Retainer  = (*Transport)(nil)
)

// New creates an MQTT transport.
// brokerURLs: comma-separated list of broker URLs for failover.
// Single URL: "mqtts://host:8883", multiple: "mqtt://host1:1883,mqtt://host2:1883"
// autopaho tries URLs in order on reconnect.
func New(brokerURLs, user, pass, clientID string, opts ...Option) (*Transport, error) {
	var o options
	for _, opt := range opts {
		opt(&o)
	}

	parts := strings.Split(brokerURLs, ",")
	urls := make([]*url.URL, 0, len(parts))
	for _, raw := range parts {
		raw = strings.TrimSpace(raw)
		if raw == "" {
			continue
		}
		u, err := url.Parse(raw)
		if err != nil {
			return nil, fmt.Errorf("parse broker URL %q: %w", raw, err)
		}
		urls = append(urls, u)
	}
	if len(urls) == 0 {
		return nil, fmt.Errorf("no broker URLs provided")
	}

	t := &Transport{
		clientID: clientID,
	}

	cfg := autopaho.ClientConfig{
		ServerUrls:                    urls,
		KeepAlive:                     30,
		CleanStartOnInitialConnection: true,
		SessionExpiryInterval:         0,
		// Custom connection: use our wsConn for WebSocket schemes.
		// paho's built-in websocketConnector sends each net.Buffers slice as a
		// separate WebSocket frame, but MQTT over WS requires one frame per packet.
		AttemptConnection: func(ctx context.Context, _ autopaho.ClientConfig, u *url.URL) (net.Conn, error) {
			switch strings.ToLower(u.Scheme) {
			case "ws":
				return dialWebSocket(ctx, nil, u)
			case "wss":
				return dialWebSocket(ctx, &tls.Config{}, u)
			default: // TCP-based: mqtt, mqtts, ssl, tls, etc.
				var conn net.Conn
				var err error
				switch strings.ToLower(u.Scheme) {
				case "mqtt", "tcp", "":
					conn, err = (&net.Dialer{}).DialContext(ctx, "tcp", u.Host)
				default:
					conn, err = (&tls.Dialer{Config: &tls.Config{}}).DialContext(ctx, "tcp", u.Host)
				}
				if err != nil {
					return nil, err
				}
				return packets.NewThreadSafeConn(conn), nil
			}
		},
		OnConnectionUp: func(cm *autopaho.ConnectionManager, connAck *paho.Connack) {
			// Sticky reconnect: autopaho tries URLs in order. If URL[0] failed,
			// it tries URL[1], etc. lastFailed tracks how many failed in this cycle.
			// The successful URL is at index (lastFailed) % len(urls).
			// Move it to front so next reconnect starts from last-known-good.
			if len(urls) > 1 {
				successIdx := int(t.lastFailed.Load()) % len(urls)
				if successIdx > 0 {
					winner := urls[successIdx]
					copy(urls[1:successIdx+1], urls[:successIdx])
					urls[0] = winner
					log.Printf("[mqtt] sticky reconnect: %s moved to front", winner.Host)
				}
				t.lastFailed.Store(0)
			}
			log.Printf("[mqtt] connected: %s", clientID)
			t.ready.Store(true)
			// Resubscribe on every connect (paho does NOT auto-resubscribe)
			t.resubscribe(cm)
		},
		OnConnectError: func(err error) {
			log.Printf("[mqtt] connect error: %s: %v", clientID, err)
			t.lastFailed.Add(1)
			t.ready.Store(false)
		},
		ClientConfig: paho.ClientConfig{
			ClientID: clientID,
			OnPublishReceived: []func(paho.PublishReceived) (bool, error){
				func(pr paho.PublishReceived) (bool, error) {
					topic := pr.Packet.Topic
					payload := pr.Packet.Payload
					t.mu.Lock()
					subs := make([]subscription, len(t.subs))
					copy(subs, t.subs)
					t.mu.Unlock()
					for _, s := range subs {
						if topicMatchesPattern(topic, s.pattern) {
							s.handler(topic, payload)
						}
					}
					return true, nil
				},
			},
			OnClientError: func(err error) {
				log.Printf("[mqtt] client error: %s: %v", clientID, err)
				t.ready.Store(false)
			},
			OnServerDisconnect: func(d *paho.Disconnect) {
				log.Printf("[mqtt] server disconnect: %s: reason=%d", clientID, d.ReasonCode)
				t.ready.Store(false)
			},
		},
	}

	if user != "" {
		cfg.ConnectUsername = user
		cfg.ConnectPassword = []byte(pass)
	}

	// Set Last Will and Testament if configured
	if o.lwt != nil {
		cfg.WillMessage = &paho.WillMessage{
			Topic:   o.lwt.Topic,
			Payload: o.lwt.Payload,
			Retain:  o.lwt.Retain,
			QoS:     0,
		}
	}

	cm, err := autopaho.NewConnection(context.Background(), cfg)
	if err != nil {
		return nil, fmt.Errorf("create MQTT connection: %w", err)
	}
	t.cm = cm

	return t, nil
}

func (t *Transport) resubscribe(cm *autopaho.ConnectionManager) {
	t.mu.Lock()
	subs := make([]subscription, len(t.subs))
	copy(subs, t.subs)
	t.mu.Unlock()

	for _, s := range subs {
		ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
		_, err := cm.Subscribe(ctx, &paho.Subscribe{
			Subscriptions: []paho.SubscribeOptions{
				{Topic: s.pattern, QoS: 0},
			},
		})
		cancel()
		if err != nil {
			log.Printf("[mqtt] subscribe error: %s pattern=%s: %v", t.clientID, s.pattern, err)
			continue
		}
		log.Printf("[mqtt] subscribed: %s pattern=%s", t.clientID, s.pattern)
	}
}

// Publish sends data to the specified topic with QoS 0.
// ctx bounds the publish latency — important when the TCP send buffer is
// backed up (otherwise PINGREQ starves and the broker disconnects). The
// caller is responsible for setting a sensible deadline; we recommend ≤5s
// for VPN data-plane traffic.
func (t *Transport) Publish(ctx context.Context, topic string, data []byte) error {
	if !t.ready.Load() {
		return nil // drop silently when not connected
	}

	expiry := uint32(10) // 10s message expiry (MQTT 5.0)
	_, err := t.cm.Publish(ctx, &paho.Publish{
		Topic:   topic,
		QoS:     0,
		Retain:  false, // NEVER retain VPN packets
		Payload: data,
		Properties: &paho.PublishProperties{
			MessageExpiry: &expiry,
		},
	})
	return err
}

// PublishRetained sends a retained message (for discovery heartbeats).
// Implements transport.Retainer.
func (t *Transport) PublishRetained(ctx context.Context, topic string, data []byte) error {
	if !t.ready.Load() {
		return nil
	}
	_, err := t.cm.Publish(ctx, &paho.Publish{
		Topic:   topic,
		QoS:     0,
		Retain:  true,
		Payload: data,
	})
	return err
}

// ProbeRTT publishes a QoS-1 message to `topic` and times the broker's
// PUBACK reply. Implements transport.RTTProbe. Used by exit nodes to
// populate broker_rtt_ms in discovery heartbeats.
//
// QoS 1 (not 0) is essential — paho's autopaho.ConnectionManager.Publish
// returns immediately at QoS 0 (after the local TX buffer accepts the
// frame) but blocks until the PUBACK round-trip completes at QoS 1.
// The timed window therefore reflects actual network latency to the
// broker, not the local syscall overhead.
//
// Payload is empty (just a small CONNECT-style ping). MessageExpiry=5s
// keeps any rare buffered copy from sitting on the broker forever
// when the network slowed mid-probe. Retain=false; this is a
// fire-and-forget probe with no surviving state.
//
// Returns context.Canceled / context.DeadlineExceeded if the PUBACK
// doesn't arrive in time — the caller treats that as "broker
// unreachable" and skips the entry rather than recording a 0ms RTT.
func (t *Transport) ProbeRTT(ctx context.Context, topic string) (time.Duration, error) {
	if !t.ready.Load() {
		return 0, fmt.Errorf("transport not ready")
	}
	expiry := uint32(5)
	start := time.Now()
	_, err := t.cm.Publish(ctx, &paho.Publish{
		Topic:  topic,
		QoS:    1,
		Retain: false,
		Properties: &paho.PublishProperties{
			MessageExpiry: &expiry,
		},
	})
	if err != nil {
		return 0, err
	}
	return time.Since(start), nil
}

// Subscribe registers a handler for messages matching the topic pattern.
// Pattern may use MQTT wildcards: + (single level), # (multi level).
// ctx bounds the SUBSCRIBE round-trip when the transport is already
// connected; before the first connect, ctx is unused (the subscription is
// queued and sent on OnConnectionUp).
func (t *Transport) Subscribe(ctx context.Context, pattern string, handler func(topic string, data []byte)) error {
	t.mu.Lock()
	t.subs = append(t.subs, subscription{pattern: pattern, handler: handler})
	t.mu.Unlock()

	// Subscribe immediately if already connected
	if t.ready.Load() {
		_, err := t.cm.Subscribe(ctx, &paho.Subscribe{
			Subscriptions: []paho.SubscribeOptions{
				{Topic: pattern, QoS: 0},
			},
		})
		if err != nil {
			return fmt.Errorf("subscribe %s: %w", pattern, err)
		}
		log.Printf("[mqtt] subscribed: %s pattern=%s", t.clientID, pattern)
	}
	return nil
}

// Ready returns true when connected to the broker.
func (t *Transport) Ready() bool {
	return t.ready.Load()
}

// Close disconnects from the broker.
func (t *Transport) Close(ctx context.Context) error {
	return t.cm.Disconnect(ctx)
}

// WaitReady blocks until the transport is connected and OnConnectionUp
// has fired (ready=true). AwaitConnection alone can return before
// OnConnectionUp sets ready, causing Subscribe/Publish to silently drop.
func (t *Transport) WaitReady(ctx context.Context) error {
	if err := t.cm.AwaitConnection(ctx); err != nil {
		return err
	}
	for !t.ready.Load() {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(5 * time.Millisecond):
		}
	}
	return nil
}

// topicMatchesPattern checks if a concrete topic matches an MQTT pattern.
// Supports + (single level wildcard) and # (multi level wildcard).
func topicMatchesPattern(topic, pattern string) bool {
	if pattern == "#" {
		return true
	}

	tParts := splitTopic(topic)
	pParts := splitTopic(pattern)

	for i, pp := range pParts {
		if pp == "#" {
			return true // # matches rest
		}
		if i >= len(tParts) {
			return false
		}
		if pp != "+" && pp != tParts[i] {
			return false
		}
	}
	return len(tParts) == len(pParts)
}

func splitTopic(t string) []string {
	parts := make([]string, 0, 8)
	start := 0
	for i := 0; i < len(t); i++ {
		if t[i] == '/' {
			parts = append(parts, t[start:i])
			start = i + 1
		}
	}
	parts = append(parts, t[start:])
	return parts
}
