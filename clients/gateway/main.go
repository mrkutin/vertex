package main

import (
	"context"
	"crypto/ecdh"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	urlpkg "net/url"
	"os"
	"os/signal"
	"os/user"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/mrkutin/vertex/pkg/config"
	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
	"github.com/mrkutin/vertex/pkg/discovery"
	"github.com/mrkutin/vertex/pkg/identity"
	"github.com/mrkutin/vertex/pkg/liveness"
	"github.com/mrkutin/vertex/pkg/pipeline"
	"github.com/mrkutin/vertex/pkg/probe"
	"github.com/mrkutin/vertex/pkg/protocol"
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

// vpnSession holds mutable VPN state that changes on exit switch.
// Read atomically by data-path goroutines via atomic.Pointer.
type vpnSession struct {
	exitID       string
	pubTopic     string
	controlTopic string
	joinTopic    string
	cipher  *btcrypto.Cipher
	tunCIDR string // current TUN IP/mask, e.g. "10.9.1.2/24"
	tunGW        string           // current TUN gateway, e.g. "10.9.1.1"
}

// Version is set via -ldflags "-X main.Version=..." at build time.
var Version = "dev"

func main() {
	configFile := flag.String("config", "", "YAML config file path")
	broker := flag.String("broker", "", "MQTT broker URL (single broker, backward compat)")
	brokers := flag.String("brokers", "", "Comma-separated MQTT broker URLs for failover")
	name := flag.String("name", "", "Client name (required, e.g. r3s, mac)")
	exit := flag.String("exit", "", "Exit node ID (empty = auto-select)")
	pass := flag.String("pass", "", "MQTT password")
	domain := flag.String("domain", "", "Discovery domain for SRV lookup")
	showVersion := flag.Bool("version", false, "Print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Println(Version)
		return
	}

	// Load YAML config if provided, CLI flags override
	var cfg config.Config
	if *configFile != "" {
		loaded, err := config.LoadConfig(*configFile)
		if err != nil {
			log.Fatalf("Config: %v", err)
		}
		cfg = *loaded
	}
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

	// DNS SRV discovery: resolve brokers from domain
	if cfg.Domain != "" {
		var cachePath string
		if runtime.GOOS == "linux" {
			cachePath = "/etc/vertex/discovery-cache.json"
		} else {
			if u, err := user.Current(); err == nil {
				cachePath = filepath.Join(u.HomeDir, ".config", "vertex", "discovery-cache.json")
			} else {
				cachePath = "discovery-cache.json"
			}
		}
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

	// Load or generate persistent device identity key
	identityKeyPath := cfg.IdentityKey
	if identityKeyPath == "" {
		if runtime.GOOS == "linux" {
			identityKeyPath = "/etc/vertex/identity.key"
		} else {
			if u, err := user.Current(); err == nil {
				identityKeyPath = filepath.Join(u.HomeDir, ".config", "vertex", "identity.key")
			} else {
				identityKeyPath = "identity.key"
			}
		}
	}
	identityKey, err := identity.LoadOrGenerateKey(identityKeyPath)
	if err != nil {
		log.Fatalf("Identity key: %v", err)
	}
	log.Printf("Device identity loaded (pubkey: %s...)", btcrypto.EncodePubKey(identityKey.PublicKey())[:16])

	// Discovery tracker for auto-select and rebalance
	tracker := discovery.NewTracker()
	autoMode := cfg.Exit == ""

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

	// Atomic session state
	var session atomic.Pointer[vpnSession]
	initSession := func(exitID, tunCIDR, tunGW string, cipher *btcrypto.Cipher) {
		session.Store(&vpnSession{
			exitID:       exitID,
			pubTopic:     protocol.DataOut(exitID, cfg.Name).MQTTTopic(),
			controlTopic: protocol.Control(exitID, cfg.Name).MQTTTopic(),
			joinTopic:    protocol.Join(exitID).MQTTTopic(),
			cipher:       cipher,
			tunCIDR:      tunCIDR,
			tunGW:        tunGW,
		})
	}
	initSession(activeExit, "", "", nil)

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

	mqttUser := fmt.Sprintf("vtx-client-%s", cfg.Name)
	clientID := fmt.Sprintf("vtx-client-%s-%s", activeExit, cfg.Name)

	// Reorder broker URLs by client→broker TCP-connect RTT — same
	// rationale as in `clients/cli/main.go`: autopaho's ServerUrls list
	// is tried strictly in order on first connect, so an unfavourable
	// order means an extra failover-and-retry penalty even when the
	// second broker is much faster.
	brokerURLs = reorderBrokersByRTT(brokerURLs)

	// Connect to MQTT (autopaho handles failover across ServerUrls)
	mqttTr, err := mqtt.New(brokerURLs, mqttUser, cfg.Pass, clientID)
	if err != nil {
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
		log.Fatalf("MQTT connect timeout: %v", err)
	}
	cancel()

	// Liveness watch: Feed hides MQTT-specific encoding (retained
	// heartbeats + LWT). Pump events into the tracker.
	brokerHosts := config.ExtractBrokerHosts(brokerURLs)
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
	waitDiscovery:
		for {
			select {
			case <-discoveryTick.C:
				brokerHost := ""
				if len(brokerHosts) > 0 {
					brokerHost = brokerHosts[0]
				}
				if best, ok := tracker.BestExit(brokerHost); ok {
					activeExit = best
					initSession(activeExit, "", "", nil)
					log.Printf("Auto-selected exit: %s", activeExit)
					break waitDiscovery
				}
			case <-discoveryDeadline:
				log.Printf("Discovery timeout, using default exit: %s", activeExit)
				break waitDiscovery
			}
		}
		discoveryTick.Stop()
	}

	// Join handshake: reusable for initial join and exit switching
	joinResp := make(chan joinResponse, 1)
	joinErr := make(chan string, 1)
	var switchTarget atomic.Pointer[string]

	// Control topic handler — wildcard to handle any exit's responses.
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

		// Diagnostic logging happens BEFORE the routing filter so error/alert
		// from the old exit during a switch are still visible in journals
		// (helped diagnose the 2026-05-19 identity-proof cascade — without
		// this log line the secondary failure mode would be invisible).
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
		// Routing filter — only the message from the right exit gets pushed to
		// the handshake channels. See pkg/protocol.ShouldAcceptControl for the
		// rationale and the regression test.
		// Note: loading session+target non-atomically is safe here because the
		// filter is monotone — worst case is an extra discard, never a
		// wrong-exit accept.
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
					initSession(s.exitID, s.tunCIDR, s.tunGW, c)
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
	var lastJoinMsg atomic.Value // []byte

	joinExit := func(jTopic string, timeout time.Duration) (joinResponse, error) {
		for len(joinResp) > 0 {
			<-joinResp
		}
		for len(joinErr) > 0 {
			<-joinErr
		}

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

	// Initial join
	s := session.Load()
	resp, err := joinExit(s.joinTopic, 30*time.Second)
	if err != nil {
		log.Fatalf("Join: %v", err)
	}
	// DH cipher already set by control handler via deriveDHCipher
	dhCipher := session.Load().cipher
	if dhCipher != nil {
		log.Printf("DH key exchange complete (PFS)")
	}
	log.Printf("Received IP: %s/%s gw %s", resp.IP, resp.Mask, resp.Gateway)

	// Create TUN with assigned IP
	tunCIDR := resp.IP + "/" + config.MaskToCIDR(resp.Mask)
	tun, err := vpn.NewTUN(tunCIDR, "vtx0")
	if err != nil {
		log.Fatalf("TUN: %v", err)
	}
	defer tun.Close()

	// Update session with TUN info, keep DH cipher from control handler
	initSession(activeExit, tunCIDR, resp.Gateway, dhCipher)

	log.Printf("Transport ready, starting VPN")

	// Download pipeline: MQTT → channel → TUN writer goroutine (no mutex needed)
	downPipeline := pipeline.NewDownload(pipeline.DownloadConfig{
		WriteFunc: func(data []byte) (int, error) {
			n, err := tun.Write(data)
			if err != nil {
				log.Printf("TUN write error: %v (len=%d)", err, len(data))
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
		s := session.Load()
		if addr := protocol.ParseMQTTTopic(topic); addr.Kind == protocol.KindDataIn && addr.ExitID != s.exitID {
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
		downPipeline.Enqueue(data)
	})

	// Signal handler
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	// switchExit performs runtime exit switching with TUN reconfig and routing update.
	var switchMu sync.Mutex
	switchExit := func(newExitID string) {
		switchMu.Lock()
		defer switchMu.Unlock()

		old := session.Load()
		if newExitID == old.exitID {
			return
		}

		log.Printf("[switch] %s → %s: starting", old.exitID, newExitID)

		// 1. Join new exit (old session still active, data flows through old exit)
		switchJoinTopic := protocol.Join(newExitID).MQTTTopic()
		switchTarget.Store(&newExitID)
		defer switchTarget.Store(nil)

		newResp, err := joinExit(switchJoinTopic, 15*time.Second)
		if err != nil {
			log.Printf("[switch] %s join failed: %v — staying on %s", newExitID, err, old.exitID)
			return
		}

		// 2. Reconfigure TUN + policy routing + iptables
		newCIDR := newResp.IP + "/" + config.MaskToCIDR(newResp.Mask)
		if err := reconfigureTUN("vtx0", newCIDR, newResp.Gateway, old.tunCIDR); err != nil {
			log.Printf("[switch] TUN reconfig failed: %v — staying on %s", err, old.exitID)
			return
		}

		// 3. DH cipher already derived by control handler (sole owner of deriveDHCipher).
		//    Read it from session — control handler set it before routing resp to joinResp.
		newCipher := session.Load().cipher
		if newCipher != nil {
			log.Printf("[switch] DH key exchange with %s complete (PFS)", newExitID)
		}

		// 4. Atomic swap — data-path instantly uses new exit
		initSession(newExitID, newCIDR, newResp.Gateway, newCipher)

		log.Printf("[switch] %s → %s: done (IP %s)", old.exitID, newExitID, newResp.IP)
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

	// Upload pipeline: TUN reader → channel → publisher goroutine
	upPipeline := pipeline.NewUpload(pipeline.UploadConfig{
		ReadFunc: tun.Read,
		Closer:   tun,
		ProcessFunc: func(pkt []byte) {
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
			if err := pub(tr, s.pubTopic, pkt); err != nil {
				log.Printf("Publish dropped (congestion): %v", err)
			}
		},
	})
	defer upPipeline.Stop()

	sig := <-sigCh
	log.Printf("Received %v, shutting down", sig)
}

// reorderBrokersByRTT probes every broker URL in the CSV in parallel
// via TCP-connect RTT and returns the same CSV reordered by ascending
// RTT. Failed probes keep their original relative order at the tail.
// On any parse failure the original CSV is returned unchanged.
func reorderBrokersByRTT(brokerURLsCSV string) string {
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
	out := make([]string, len(sorted))
	for i, u := range sorted {
		out[i] = u.String()
	}
	return strings.Join(out, ",")
}

