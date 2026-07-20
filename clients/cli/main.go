package main

import (
	"context"
	"crypto/ecdh"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	urlpkg "net/url"
	"os"
	"os/signal"
	"sort"
	"strconv"
	"os/user"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/mrkutin/vertex/pkg/config"
	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
	"github.com/mrkutin/vertex/pkg/discovery"
	"github.com/mrkutin/vertex/pkg/events"
	"github.com/mrkutin/vertex/pkg/identity"
	"github.com/mrkutin/vertex/pkg/liveness"
	"github.com/mrkutin/vertex/pkg/pipeline"
	"github.com/mrkutin/vertex/pkg/probe"
	"github.com/mrkutin/vertex/pkg/protocol"
	"github.com/mrkutin/vertex/pkg/routing"
	"github.com/mrkutin/vertex/pkg/transport"
	"github.com/mrkutin/vertex/pkg/transport/mqtt"
	"github.com/mrkutin/vertex/pkg/vpn"
)

type joinResponse struct {
	IP      string `json:"ip"`
	Mask    string `json:"mask"`
	Gateway string `json:"gw"`
	DH      string `json:"dh,omitempty"` // exit's X25519 pubkey (base64)
}

// vpnSession holds the mutable VPN state that changes on exit switch.
// Read atomically by data-path goroutines via atomic.Pointer.
type vpnSession struct {
	exitID       string
	pubTopic     string
	subTopic     string
	controlTopic string
	joinTopic    string
	cipher *btcrypto.Cipher
}

// Version is set via -ldflags "-X main.Version=..." at build time.
var Version = "dev"

func main() {
	flag.Usage = func() {
		fmt.Fprintf(os.Stderr, `vtx-client — VPN client over MQTT broker tunnel

Usage:
  vtx-client --config /path/to/config.yaml
  vtx-client --brokers mqtts://broker:8883 --name mac --pass secret

The client connects to MQTT broker(s), joins an exit node, creates a TUN
interface, and routes all traffic through the encrypted tunnel.

Exit selection:
  By default the client auto-selects the best exit based on discovery
  heartbeats and broker RTT. Use --exit to pin a specific exit node.

E2E encryption:
  X25519 DH key exchange is automatic — no keys needed in config.
  Session keys are derived per-connection via DH key exchange (PFS).

Invite URL (one-time setup):
  vtx-client --invite "vtx://join?domain=vertices.ru&name=mac&pass=secret"

Config file (YAML):
  domain: vertices.ru        # SRV discovery (resolves brokers, exits, backup domain)
  brokers:                   # optional fallback if DNS fails
    - mqtts://mqtt-yc.vertices.ru:8883
    - mqtts://mqtt-sber.vertices.ru:8883
  name: mac
  pass: secret
  exit: sto                  # optional, omit for auto-select
  verbose: false
  json: false

CLI flags override config file values.

Flags:
`)
		flag.VisitAll(func(f *flag.Flag) {
			typ := "string"
			switch f.DefValue {
			case "true", "false":
				typ = ""
			}
			if typ != "" {
				fmt.Fprintf(os.Stderr, "  --%s %s\n    \t%s\n", f.Name, typ, f.Usage)
			} else {
				fmt.Fprintf(os.Stderr, "  --%s\n    \t%s\n", f.Name, f.Usage)
			}
		})
	}

	configFile := flag.String("config", "", "YAML config file path")
	invite := flag.String("invite", "", "Invite URL (bt://join?domain=...&name=...&pass=...)")
	broker := flag.String("broker", "", "MQTT broker URL (single broker, backward compat)")
	brokers := flag.String("brokers", "", "Comma-separated MQTT broker URLs for failover")
	name := flag.String("name", "", "Client name (required, e.g. mac, laptop)")
	exit := flag.String("exit", "", "Exit node ID (empty = auto-select)")
	pass := flag.String("pass", "", "MQTT password")
	domain := flag.String("domain", "", "Discovery domain for SRV lookup")
	jsonMode := flag.Bool("json", false, "JSON event output on stdout (for native app wrappers)")
	verbose := flag.Bool("verbose", false, "Verbose packet logging")
	noRouting := flag.Bool("no-routing", false, "Skip routing setup (for Docker or external routing)")
	showVersion := flag.Bool("version", false, "Print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(Version)
		return
	}

	// Load config: invite URL → YAML file → CLI overrides
	var cfg config.Config

	if *invite != "" {
		// Parse invite URL: bt://join?domain=...&name=...&pass=...
		ic, err := config.ParseInviteURL(*invite)
		if err != nil {
			log.Fatalf("Invalid invite: %v", err)
		}
		cfg = ic.ToConfig()
		// Save config for future use
		configDir := defaultConfigDir()
		savePath := filepath.Join(configDir, "config.yaml")
		if err := cfg.SaveYAML(savePath); err != nil {
			log.Printf("Warning: could not save config to %s: %v", savePath, err)
		} else {
			log.Printf("Config saved to %s", savePath)
		}
	} else if *configFile != "" {
		loaded, err := config.LoadConfig(*configFile)
		if err != nil {
			log.Fatalf("Config: %v", err)
		}
		cfg = *loaded
	}

	// CLI overrides
	if *brokers != "" || *broker != "" {
		cfg.Brokers = config.ParseBrokerList(*brokers, *broker)
	}
	if *name != "" {
		cfg.Name = *name
	}
	if *exit != "" {
		cfg.Exit = *exit
	}
	if *pass != "" {
		cfg.Pass = *pass
	}
	if *domain != "" {
		cfg.Domain = *domain
	}
	if *jsonMode {
		cfg.JSON = true
	}
	if *verbose {
		cfg.Verbose = true
	}

	// DNS SRV discovery: resolve brokers from domain
	if cfg.Domain != "" {
		cachePath := filepath.Join(defaultConfigDir(), "discovery-cache.json")
		dns := discovery.NewDNSDiscovery(cachePath)
		cache, err := dns.ResolveWithFallback(cfg.Domain)
		if err != nil {
			log.Printf("DNS discovery failed: %v (using config brokers)", err)
		} else {
			dnsURLs := cache.BrokerURLs()
			cfg.Brokers = config.MergeBrokerURLs(dnsURLs, cfg.Brokers)
			log.Printf("DNS discovery: %d broker(s) via %s", len(dnsURLs), cache.Domain)
			if cache.BackupDomain != "" {
				log.Printf("DNS discovery: backup domain = %s", cache.BackupDomain)
			}
			if len(cache.ExitIDs()) > 0 {
				log.Printf("DNS discovery: exits = %v", cache.ExitIDs())
			}
		}
	}

	brokerURLs := config.JoinBrokerURLs(cfg.Brokers)
	if brokerURLs == "" {
		log.Fatalf("No broker URLs: use -brokers, -broker, -domain, or -config")
	}

	if cfg.Name == "" {
		fmt.Fprintln(os.Stderr, "required: -name or name in config")
		flag.Usage()
		os.Exit(1)
	}

	log.SetFlags(log.LstdFlags | log.Lmicroseconds)
	log.SetOutput(os.Stderr)

	ev := events.New(cfg.JSON)

	// Load or generate persistent device identity key
	identityKeyPath := cfg.IdentityKey
	if identityKeyPath == "" {
		if u, err := user.Current(); err == nil {
			identityKeyPath = filepath.Join(u.HomeDir, ".config", "vertex", "identity.key")
		} else {
			identityKeyPath = "identity.key"
		}
	}
	identityKey, err := identity.LoadOrGenerateKey(identityKeyPath)
	if err != nil {
		log.Fatalf("Identity key: %v", err)
	}
	log.Printf("Device identity loaded (pubkey: %s...)", btcrypto.EncodePubKey(identityKey.PublicKey())[:16])

	mqttUser := fmt.Sprintf("vtx-client-%s", cfg.Name)

	// Discovery tracker for auto-select
	tracker := discovery.NewTracker()
	autoMode := cfg.Exit == "" // auto-select mode if no explicit exit

	// Resolve initial exit
	activeExit := cfg.Exit
	if activeExit == "" {
		activeExit = "aws" // temporary default until discovery selects
	}

	// DH key exchange: pending ephemeral private key for current join.
	// Control handler is the SOLE consumer — it calls deriveDHCipher on every
	// join response (initial or switch) and updates the session cipher.
	// No other code path should call deriveDHCipher.
	var pendingDHKey atomic.Pointer[ecdh.PrivateKey]

	// deriveDHCipher derives a session cipher from the pending DH key and exit's pubkey.
	// Clears pendingDHKey after use to limit ephemeral key lifetime (PFS).
	// MUST only be called from the control handler — single consumer guarantees no races.
	deriveDHCipher := func(exitDHPubB64 string) *btcrypto.Cipher {
		if exitDHPubB64 == "" {
			return nil
		}
		dhKey := pendingDHKey.Swap(nil)
		if dhKey == nil {
			return nil
		}
		exitPub, err := btcrypto.DecodePubKey(exitDHPubB64)
		if err != nil {
			log.Printf("Invalid exit DH pubkey: %v", err)
			return nil
		}
		c, err := btcrypto.DeriveSessionCipher(dhKey, exitPub, dhKey.PublicKey().Bytes(), exitPub.Bytes())
		if err != nil {
			log.Printf("DH derive error: %v", err)
			return nil
		}
		return c
	}

	// Topic builder. Address values are protocol-level, the MQTT encoding
	// happens once per call. clientID is MQTT-specific (no Address kind for
	// it) — it goes on the wire as the MQTT client identifier, not a topic.
	buildTopics := func(exitID string) (pub, sub, control, join, clientID string) {
		return protocol.DataOut(exitID, cfg.Name).MQTTTopic(),
			protocol.DataIn(exitID, cfg.Name).MQTTTopic(),
			protocol.Control(exitID, cfg.Name).MQTTTopic(),
			protocol.Join(exitID).MQTTTopic(),
			fmt.Sprintf("vtx-client-%s-%s", exitID, cfg.Name)
	}

	// Atomic session state — read by data-path goroutines
	var session atomic.Pointer[vpnSession]
	initSession := func(exitID string, cipher *btcrypto.Cipher) {
		pub, sub, ctrl, join, _ := buildTopics(exitID)
		session.Store(&vpnSession{
			exitID:       exitID,
			pubTopic:     pub,
			subTopic:     sub,
			controlTopic: ctrl,
			joinTopic:    join,
			cipher:       cipher,
		})
	}
	initSession(activeExit, nil)

	// generateJoinMsg creates a join request with an ephemeral DH pubkey.
	generateJoinMsg := func() []byte {
		dhKey, err := btcrypto.GenerateKeyPair()
		if err != nil {
			log.Printf("DH keygen error: %v", err)
			msg, _ := json.Marshal(map[string]string{"name": cfg.Name})
			return msg
		}
		pendingDHKey.Store(dhKey)

		joinData := map[string]string{
			"name": cfg.Name,
			"dh":   btcrypto.EncodePubKey(dhKey.PublicKey()),
		}

		// Add device identity proof if exit's static pubkey is known
		s := session.Load()
		if s != nil {
			if info := tracker.GetExitInfo(s.exitID); info != nil && info.DHPubKey != "" {
				exitPub, err := btcrypto.DecodePubKey(info.DHPubKey)
				if err == nil {
					proof, err := identity.ComputeIdentityProof(identityKey, exitPub, cfg.Name)
					if err == nil {
						joinData["id"] = btcrypto.EncodePubKey(identityKey.PublicKey())
						joinData["id_sig"] = base64.StdEncoding.EncodeToString(proof)
					}
				}
			}
		}

		msg, _ := json.Marshal(joinData)
		return msg
	}

	// Extract all broker hosts for routing bypass
	brokerHosts := config.ExtractBrokerHosts(brokerURLs)

	// Reorder broker URLs by client→broker TCP-connect RTT — the
	// autopaho ServerUrls list is tried strictly in order on first
	// connect, so an unfavourable order means the user pays an extra
	// failover-and-retry penalty even when the second broker is much
	// faster. Probe is parallel and capped at 1.5s; failed probes keep
	// their original relative position at the tail.
	brokerURLs = reorderBrokersByRTT(brokerURLs, ev)

	// Connect to MQTT (autopaho handles failover across ServerUrls)
	_, _, _, _, clientID := buildTopics(activeExit)
	ev.Emit("connecting", "broker", brokerURLs)
	mqttTr, err := mqtt.New(brokerURLs, mqttUser, cfg.Pass, clientID)
	if err != nil {
		ev.Emit("error", "message", fmt.Sprintf("MQTT: %v", err))
		log.Fatalf("MQTT: %v", err)
	}
	// Hold as the abstract Transport — keeps the call sites independent of MQTT.
	var tr transport.Transport = mqttTr
	defer func() {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		tr.Close(ctx)
		cancel()
	}()

	// Per-call timeouts: 5s for publish (matches the policy that prevented
	// PINGREQ starvation), 10s for subscribe round-trips.
	const pubTimeout = 5 * time.Second
	const subTimeout = 10 * time.Second
	pub := func(t transport.Transport, topic string, data []byte) error {
		ctx, cancel := context.WithTimeout(context.Background(), pubTimeout)
		defer cancel()
		return t.Publish(ctx, topic, data)
	}
	sub := func(t transport.Transport, pattern string, h func(string, []byte)) error {
		ctx, cancel := context.WithTimeout(context.Background(), subTimeout)
		defer cancel()
		return t.Subscribe(ctx, pattern, h)
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	if err := tr.WaitReady(ctx); err != nil {
		cancel()
		ev.Emit("error", "message", fmt.Sprintf("MQTT connect timeout: %v", err))
		log.Fatalf("MQTT connect timeout: %v", err)
	}
	cancel()
	ev.Emit("connected")

	// Subscribe to liveness events from peers and pump them into the tracker.
	// The Feed hides the MQTT-specific encoding (retained heartbeats + LWT
	// for removal); the tracker just receives EventUpdated/EventRemoved.
	feed, err := liveness.NewMQTTFeed(tr)
	if err != nil {
		log.Fatalf("liveness feed: %v", err)
	}
	feedCtx, cancelFeed := context.WithCancel(context.Background())
	defer cancelFeed()
	feedEvents, err := feed.Watch(feedCtx)
	if err != nil {
		log.Fatalf("liveness watch: %v", err)
	}
	tracker.Pump(feedEvents)

	// In auto mode, wait for discovery before proceeding
	if autoMode {
		log.Printf("Auto-select mode: waiting for exit discovery...")
		discoveryDeadline := time.After(10 * time.Second)
		discoveryTick := time.NewTicker(500 * time.Millisecond)
		defer discoveryTick.Stop()
	waitDiscovery:
		for {
			select {
			case <-discoveryTick.C:
				brokerHosts := config.ExtractBrokerHosts(brokerURLs)
				brokerHost := ""
				if len(brokerHosts) > 0 {
					brokerHost = brokerHosts[0]
				}
				if best, ok := tracker.BestExit(brokerHost); ok {
					activeExit = best
					initSession(activeExit, nil)
					log.Printf("Auto-selected exit: %s", activeExit)
					ev.Emit("exit_selected", "exit", activeExit)
					break waitDiscovery
				}
			case <-discoveryDeadline:
				log.Printf("Discovery timeout, using default exit: %s", activeExit)
				break waitDiscovery
			}
		}
		discoveryTick.Stop()
	}

	// joinExit performs the join handshake with the current or target exit.
	joinResp := make(chan joinResponse, 1)
	joinErr := make(chan string, 1)

	// switchTarget: during a switch, only accept responses from this exit ID.
	// nil = accept from current session's exit only.
	var switchTarget atomic.Pointer[string]

	// Control topic handler — uses wildcard to handle any exit's control responses.
	// Filters by exit ID to prevent stale responses from wrong exit.
	controlPattern := protocol.MQTTControlAny(cfg.Name)
	sub(tr, controlPattern, func(topic string, data []byte) {
		addr := protocol.ParseMQTTTopic(topic)
		if addr.Kind != protocol.KindControl {
			return
		}
		topicExit := addr.ExitID

		var msg map[string]string
		if err := json.Unmarshal(data, &msg); err != nil {
			return
		}

		// Diagnostic logging BEFORE the routing filter so error/alert from
		// the old exit during a switch are still visible (see gateway/main.go
		// for the 2026-05-19 incident that motivated this split).
		if errMsg, ok := msg["error"]; ok {
			log.Printf("Exit %s error: %s", topicExit, errMsg)
		} else if alertMsg, ok := msg["alert"]; ok {
			log.Printf("Exit %s ALERT: %s", topicExit, alertMsg)
		}

		s := session.Load()
		target := ""
		if t := switchTarget.Load(); t != nil {
			target = *t
		}
		// Routing filter — see pkg/protocol.ShouldAcceptControl. Load order is
		// safe because the filter is monotone: worst case is an extra discard.
		if !protocol.ShouldAcceptControl(topicExit, s.exitID, target) {
			return
		}

		if _, ok := msg["error"]; ok {
			select {
			case joinErr <- msg["error"]:
			default:
			}
			return
		}
		if _, ok := msg["alert"]; ok {
			return
		}
		if ip, ok := msg["ip"]; ok && ip != "" {
			resp := joinResponse{IP: ip, Mask: msg["mask"], Gateway: msg["gw"], DH: msg["dh"]}
			// Derive DH cipher from join response
			if resp.DH != "" {
				if c := deriveDHCipher(resp.DH); c != nil {
					s := session.Load()
					initSession(s.exitID, c)
				}
			}
			select {
			case joinResp <- resp:
			default:
				log.Printf("Duplicate join response for IP %s (discarded)", ip)
			}
		}
	})

	// lastJoinMsg stores the most recent join message (with DH key) for keepalive reuse.
	// Updated by joinExit on every successful join (initial or switch).
	var lastJoinMsg atomic.Value // []byte

	joinExit := func(jTopic string, timeout time.Duration) (joinResponse, error) {
		// Drain any stale responses
		for len(joinResp) > 0 {
			<-joinResp
		}
		for len(joinErr) > 0 {
			<-joinErr
		}

		// Generate ephemeral DH keypair for this join attempt
		joinMsg := generateJoinMsg()
		lastJoinMsg.Store(joinMsg)

		pub(tr, jTopic, joinMsg)
		log.Printf("Requesting IP from exit (topic %s)...", jTopic)

		retryTick := time.NewTicker(2 * time.Second)
		defer retryTick.Stop()
		deadline := time.After(timeout)
		for {
			select {
			case resp := <-joinResp:
				return resp, nil
			case errMsg := <-joinErr:
				return joinResponse{}, fmt.Errorf("join rejected: %s", errMsg)
			case <-retryTick.C:
				// Resend same join message (same DH key) — never regenerate
				// on retry to avoid pendingDHKey/response mismatch
				pub(tr, jTopic, joinMsg)
			case <-deadline:
				return joinResponse{}, fmt.Errorf("timeout waiting for IP (%v)", timeout)
			}
		}
	}

	s := session.Load()
	resp, err := joinExit(s.joinTopic, 30*time.Second)
	if err != nil {
		ev.Emit("error", "message", err.Error())
		log.Fatalf("Join: %v", err)
	}
	// DH cipher already derived by control handler (sole owner of deriveDHCipher).
	// Read from session — control handler set it before routing response to joinResp.
	if session.Load().cipher != nil {
		log.Printf("DH key exchange complete (PFS)")
	}
	log.Printf("Received IP: %s/%s gw %s", resp.IP, resp.Mask, resp.Gateway)
	ev.Emit("ip_assigned", "ip", resp.IP, "gw", resp.Gateway)

	// Create TUN with assigned IP
	tunCIDR := resp.IP + "/" + config.MaskToCIDR(resp.Mask)
	tun, err := vpn.NewTUN(tunCIDR, "")
	if err != nil {
		ev.Emit("error", "message", fmt.Sprintf("TUN: %v", err))
		log.Fatalf("TUN: %v", err)
	}
	defer tun.Close()
	ev.Emit("tun_created", "name", tun.Name(), "ip", resp.IP)

	// Setup routing: all broker hosts bypassed + default via TUN
	if !*noRouting {
		routeState, err := routing.Setup(routing.Config{
			BrokerHosts: brokerHosts,
			TunGW:       resp.Gateway,
			TunName:     tun.Name(),
		})
		if err != nil {
			ev.Emit("error", "message", fmt.Sprintf("routing: %v", err))
			log.Fatalf("Routing setup: %v", err)
		}
		defer routeState.Cleanup()
	}
	ev.Emit("routes_configured")

	log.Printf("VPN ready: %s via %s, %d broker(s) bypassed", resp.IP, tun.Name(), len(brokerHosts))
	ev.Emit("ready")

	// Packet counters for periodic stats
	var tunReadPkts, tunReadBytes, tunWritePkts, tunWriteBytes atomic.Uint64
	var mqttPubPkts, mqttPubErrs, mqttRecvPkts atomic.Uint64
	debug := cfg.Verbose

	// Stats emitter for GUI wrappers (JSON mode only)
	if cfg.JSON {
		go func() {
			ticker := time.NewTicker(2 * time.Second)
			defer ticker.Stop()
			var lastIn, lastOut uint64
			for range ticker.C {
				curIn := tunWriteBytes.Load()  // bytes written to TUN = download
				curOut := tunReadBytes.Load()   // bytes read from TUN = upload
				speedIn := (curIn - lastIn) / 2 // bytes per second
				speedOut := (curOut - lastOut) / 2
				lastIn, lastOut = curIn, curOut
				ev.Emit("stats",
					"bytes_in", fmt.Sprint(curIn),
					"bytes_out", fmt.Sprint(curOut),
					"speed_in", fmt.Sprint(speedIn),
					"speed_out", fmt.Sprint(speedOut))
			}
		}()
	}

	// Download pipeline: MQTT → channel → TUN writer goroutine (no mutex needed)
	downPipeline := pipeline.NewDownload(pipeline.DownloadConfig{
		WriteFunc: func(data []byte) (int, error) {
			n, err := tun.Write(data)
			if err != nil {
				log.Printf("TUN write error: %v (len=%d)", err, len(data))
			} else {
				tunWritePkts.Add(1)
				tunWriteBytes.Add(uint64(n))
			}
			return n, err
		},
	})
	defer downPipeline.Stop()

	// Subscribe: MQTT → download pipeline
	var dataSubPattern string
	if !autoMode {
		dataSubPattern = protocol.DataIn(activeExit, cfg.Name).MQTTTopic()
	} else {
		dataSubPattern = protocol.MQTTDataAny(cfg.Name)
	}
	sub(tr, dataSubPattern, func(topic string, data []byte) {
		mqttRecvPkts.Add(1)
		s := session.Load()
		// Filter on the address topic — discards stale packets from a
		// previous exit during a switch.
		if addr := protocol.ParseMQTTTopic(topic); addr.Kind == protocol.KindDataIn && addr.ExitID != s.exitID {
			if debug {
				log.Printf("Dropped stale packet from exit %s (active: %s)", addr.ExitID, s.exitID)
			}
			return
		}
		if s.cipher != nil {
			var err error
			data, err = s.cipher.Open(data)
			if err != nil {
				log.Printf("Decrypt error: %v", err)
				return
			}
		}
		if debug && len(data) >= 20 {
			logPacket("MQTT→TUN", data)
		}
		downPipeline.Enqueue(data)
	})

	// Signal handler
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	// Periodic keepalive: resend the same join message (with DH key) to
	// update brokerIdx/lastSeen on exit and re-establish cipher after exit restart.
	// No new DH key is generated — pendingDHKey is already consumed, so
	// deriveDHCipher returns nil on keepalive response → cipher unchanged on client.
	// On exit: same DH pubkey → same derived cipher (deterministic ECDH).
	go func() {
		ticker := time.NewTicker(60 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			s := session.Load()
			if msg, ok := lastJoinMsg.Load().([]byte); ok {
				pub(tr, s.joinTopic, msg)
			}
		}
	}()

	// switchExit performs a runtime exit switch: join new exit, reconfigure TUN IP,
	// then atomically swap session. Old session stays active until switch completes.
	var switchMu sync.Mutex // prevents concurrent switches
	switchExit := func(newExitID string) {
		switchMu.Lock()
		defer switchMu.Unlock()

		oldExit := session.Load().exitID
		if newExitID == oldExit {
			return
		}

		log.Printf("[switch] %s → %s: starting", oldExit, newExitID)
		ev.Emit("exit_switching", "from", oldExit, "to", newExitID)

		// 1. Join new exit FIRST (old session still active, data flows through old exit)
		switchJoinTopic := protocol.Join(newExitID).MQTTTopic()

		// Tell control handler to accept responses from the new exit
		switchTarget.Store(&newExitID)
		defer switchTarget.Store(nil)

		newResp, err := joinExit(switchJoinTopic, 15*time.Second)
		if err != nil {
			log.Printf("[switch] %s join failed: %v — staying on %s", newExitID, err, oldExit)
			ev.Emit("exit_switch_failed", "exit", newExitID, "error", err.Error())
			return
		}

		// 2. Reconfigure TUN with new IP
		newCIDR := newResp.IP + "/" + config.MaskToCIDR(newResp.Mask)
		if err := reconfigureTUN(tun.Name(), newCIDR, newResp.Gateway); err != nil {
			log.Printf("[switch] TUN reconfig failed: %v — staying on %s", err, oldExit)
			ev.Emit("exit_switch_failed", "exit", newExitID, "error", err.Error())
			return
		}

		// 3. DH cipher already derived by control handler (sole owner of deriveDHCipher).
		//    Read it from session — control handler set it before routing resp to joinResp.
		newCipher := session.Load().cipher
		if newCipher != nil {
			log.Printf("[switch] DH key exchange with %s complete (PFS)", newExitID)
		}

		// 4. NOW swap session atomically — data-path instantly uses new exit
		initSession(newExitID, newCipher)

		log.Printf("[switch] %s → %s: done (IP %s)", oldExit, newExitID, newResp.IP)
		ev.Emit("exit_switched", "from", oldExit, "to", newExitID, "ip", newResp.IP)
	}

	// Periodic exit health check + rebalance (auto mode only)
	if autoMode {
		go func() {
			healthTicker := time.NewTicker(15 * time.Second)
			rebalanceInterval := 5 * time.Minute
			lastRebalance := time.Now()
			defer healthTicker.Stop()
			for range healthTicker.C {
				brokerHost := ""
				if len(brokerHosts) > 0 {
					brokerHost = brokerHosts[0]
				}

				curExit := session.Load().exitID

				// Fast health check (every 15s): is current exit alive?
				if !tracker.ExitAvailable(curExit) {
					if best, ok := tracker.BestExit(brokerHost); ok && best != curExit {
						log.Printf("[rebalance] exit %s offline, switching to %s", curExit, best)
						switchExit(best)
						lastRebalance = time.Now()
					}
					continue
				}

				// Slow rebalance (every 5 min): is there a significantly better exit?
				if time.Since(lastRebalance) >= rebalanceInterval {
					if better, shouldSwitch := tracker.ShouldSwitch(curExit, brokerHost); shouldSwitch {
						log.Printf("[rebalance] exit %s significantly better than %s, switching", better, curExit)
						switchExit(better)
					}
					lastRebalance = time.Now()
				}
			}
		}()
	}

	// Periodic stats (verbose only)
	if debug {
		go func() {
			ticker := time.NewTicker(5 * time.Second)
			defer ticker.Stop()
			for range ticker.C {
				tr := tunReadPkts.Load()
				tb := tunReadBytes.Load()
				tw := tunWritePkts.Load()
				twb := tunWriteBytes.Load()
				mp := mqttPubPkts.Load()
				me := mqttPubErrs.Load()
				mr := mqttRecvPkts.Load()
				if tr > 0 || tw > 0 {
					log.Printf("[stats] TUN read=%d/%dB write=%d/%dB | MQTT pub=%d err=%d recv=%d",
						tr, tb, tw, twb, mp, me, mr)
				}
			}
		}()
	}

	// Upload pipeline: TUN reader → channel → publisher goroutine
	upPipeline := pipeline.NewUpload(pipeline.UploadConfig{
		ReadFunc: tun.Read,
		Closer:   tun,
		OnRead: func(n int) {
			tunReadPkts.Add(1)
			tunReadBytes.Add(uint64(n))
		},
		ProcessFunc: func(pkt []byte) {
			if debug && len(pkt) >= 20 {
				logPacket("TUN→MQTT", pkt)
			}
			vpn.FixChecksums(pkt)
			s := session.Load()
			if s.cipher != nil {
				var err error
				pkt, err = s.cipher.Seal(pkt)
				if err != nil {
					log.Printf("Encrypt error: %v", err)
					return
				}
			}
			mqttPubPkts.Add(1)
			if err := pub(tr, s.pubTopic, pkt); err != nil {
				mqttPubErrs.Add(1)
				log.Printf("Publish error: %v (pkt %d bytes)", err, len(pkt))
			}
		},
	})
	defer upPipeline.Stop()

	sig := <-sigCh
	log.Printf("Received %v, shutting down", sig)
	ev.Emit("disconnected")
}

// logPacket logs first 50 packets per direction, then every 100th.
var (
	logTunToMQTT atomic.Uint64
	logMQTTToTUN atomic.Uint64
)

func logPacket(dir string, pkt []byte) {
	var counter *atomic.Uint64
	if dir == "TUN→MQTT" {
		counter = &logTunToMQTT
	} else {
		counter = &logMQTTToTUN
	}
	n := counter.Add(1)
	if n > 50 && n%100 != 0 {
		return
	}

	srcIP := net.IP(pkt[12:16])
	dstIP := net.IP(pkt[16:20])
	proto := pkt[9]
	ihl := int(pkt[0]&0x0f) * 4

	var detail string
	switch proto {
	case 6: // TCP
		if len(pkt) >= ihl+14 {
			srcPort := binary.BigEndian.Uint16(pkt[ihl : ihl+2])
			dstPort := binary.BigEndian.Uint16(pkt[ihl+2 : ihl+4])
			flags := pkt[ihl+13]
			flagStr := tcpFlags(flags)
			detail = fmt.Sprintf("TCP %s:%d→%s:%d [%s]", srcIP, srcPort, dstIP, dstPort, flagStr)
		}
	case 17: // UDP
		if len(pkt) >= ihl+4 {
			srcPort := binary.BigEndian.Uint16(pkt[ihl : ihl+2])
			dstPort := binary.BigEndian.Uint16(pkt[ihl+2 : ihl+4])
			detail = fmt.Sprintf("UDP %s:%d→%s:%d", srcIP, srcPort, dstIP, dstPort)
		}
	case 1: // ICMP
		detail = fmt.Sprintf("ICMP %s→%s", srcIP, dstIP)
	default:
		detail = fmt.Sprintf("proto=%d %s→%s", proto, srcIP, dstIP)
	}

	log.Printf("[%s] #%d len=%d %s", dir, n, len(pkt), detail)
}

func defaultConfigDir() string {
	if u, err := user.Current(); err == nil {
		return filepath.Join(u.HomeDir, ".config", "vertex")
	}
	return "."
}

func tcpFlags(f byte) string {
	var flags []string
	if f&0x02 != 0 {
		flags = append(flags, "SYN")
	}
	if f&0x10 != 0 {
		flags = append(flags, "ACK")
	}
	if f&0x01 != 0 {
		flags = append(flags, "FIN")
	}
	if f&0x04 != 0 {
		flags = append(flags, "RST")
	}
	if f&0x08 != 0 {
		flags = append(flags, "PSH")
	}
	if len(flags) == 0 {
		return fmt.Sprintf("0x%02x", f)
	}
	return strings.Join(flags, ",")
}

// reorderBrokersByRTT probes every broker URL in `brokerURLsCSV` in
// parallel via a TCP-connect RTT measurement and returns the same CSV
// reordered by ascending RTT. Failed probes are kept at the tail in
// original order so they remain available as a last-resort fallback.
//
// The result is also emitted as a structured `broker_probe` event so
// JSON consumers (Swift/Kotlin native UIs) can surface the latency
// numbers in diagnostics. On any parse failure the original CSV is
// returned unchanged — probe is best-effort, never fatal.
func reorderBrokersByRTT(brokerURLsCSV string, ev *events.Emitter) string {
	parts := strings.Split(brokerURLsCSV, ",")
	urls := make([]*urlpkg.URL, 0, len(parts))
	for _, raw := range parts {
		raw = strings.TrimSpace(raw)
		if raw == "" {
			continue
		}
		u, err := urlpkg.Parse(raw)
		if err != nil {
			log.Printf("[probe] cannot parse broker URL %q: %v — skipping reorder", raw, err)
			return brokerURLsCSV
		}
		urls = append(urls, u)
	}
	if len(urls) <= 1 {
		return brokerURLsCSV
	}
	sorted, rtts := probe.MeasureMap(urls, 1500*time.Millisecond)
	log.Printf("[probe] broker order: %s", probe.FormatOrder(sorted, rtts))
	hosts := make([]string, 0, len(rtts))
	for h := range rtts {
		hosts = append(hosts, h)
	}
	sort.Strings(hosts)
	rttFields := make([]string, 0, 2*len(hosts))
	for _, h := range hosts {
		rttFields = append(rttFields, h, strconv.Itoa(int(rtts[h].Milliseconds())))
	}
	ev.Emit("broker_probe", rttFields...)
	out := make([]string, len(sorted))
	for i, u := range sorted {
		out[i] = u.String()
	}
	return strings.Join(out, ",")
}



