package main

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/exec"
	"os/signal"
	"runtime"
	"strings"
	"sync"
	"syscall"
	"time"

	"crypto/ecdh"
	"encoding/hex"

	"github.com/mrkutin/vertex/pkg/config"
	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
	"github.com/mrkutin/vertex/pkg/discovery"
	"github.com/mrkutin/vertex/pkg/identity"
	"github.com/mrkutin/vertex/pkg/liveness"
	"github.com/mrkutin/vertex/pkg/pipeline"
	"github.com/mrkutin/vertex/pkg/protocol"
	"github.com/mrkutin/vertex/pkg/transport"
	"github.com/mrkutin/vertex/pkg/transport/mqtt"
	"github.com/mrkutin/vertex/pkg/vpn"
)

// clientMap tracks connected clients by their TUN IP.
type clientMap struct {
	mu     sync.RWMutex
	byIP   map[[4]byte]clientEntry
	byName map[string][4]byte
	pool   []net.IP
	subnet net.IPNet
}

type clientEntry struct {
	name       string
	brokerIdx  int // which broker this client arrived on
	lastSeen   time.Time
	bytesIn    uint64
	bytesOut   uint64
	packetsIn  uint64
	packetsOut uint64
	cipher *btcrypto.Cipher // per-client DH session cipher
}

func newClientMap(tunCIDR string) *clientMap {
	_, ipNet, _ := net.ParseCIDR(tunCIDR)
	// Build pool: .2 through .254 (skip .0=network, .1=gateway, .255=broadcast)
	pool := make([]net.IP, 0, 253)
	base := make(net.IP, 4)
	copy(base, ipNet.IP.To4())
	for i := 2; i <= 254; i++ {
		ip := make(net.IP, 4)
		copy(ip, base)
		ip[3] = byte(i)
		pool = append(pool, ip)
	}
	return &clientMap{
		byIP:   make(map[[4]byte]clientEntry),
		byName: make(map[string][4]byte),
		pool:   pool,
		subnet: *ipNet,
	}
}


func (cm *clientMap) assign(name string, brokerIdx int, c *btcrypto.Cipher) (net.IP, bool) {
	cm.mu.Lock()
	defer cm.mu.Unlock()

	// Already assigned — refresh, update broker index, rotate cipher
	if ipBytes, ok := cm.byName[name]; ok {
		entry := cm.byIP[ipBytes]
		entry.brokerIdx = brokerIdx
		entry.lastSeen = time.Now()
		if c != nil {
			entry.cipher = c
		}
		cm.byIP[ipBytes] = entry
		return net.IP(ipBytes[:]), true
	}

	// Get next from pool
	if len(cm.pool) == 0 {
		return nil, false
	}
	ip := cm.pool[0]
	cm.pool = cm.pool[1:]

	var key [4]byte
	copy(key[:], ip.To4())
	cm.byIP[key] = clientEntry{name: name, brokerIdx: brokerIdx, lastSeen: time.Now(), cipher: c}
	cm.byName[name] = key
	return ip, true
}

// cipherForName returns the per-client cipher.
func (cm *clientMap) cipherForName(name string) *btcrypto.Cipher {
	cm.mu.RLock()
	defer cm.mu.RUnlock()
	ipBytes, ok := cm.byName[name]
	if !ok {
		return nil
	}
	return cm.byIP[ipBytes].cipher
}

func (cm *clientMap) addIn(srcIP [4]byte, name string, brokerIdx int, bytes int) {
	cm.mu.Lock()
	now := time.Now()
	entry := cm.byIP[srcIP]
	entry.name = name
	entry.brokerIdx = brokerIdx
	entry.lastSeen = now
	entry.bytesIn += uint64(bytes)
	entry.packetsIn++
	cm.byIP[srcIP] = entry
	// Refresh the formally assigned TUN IP entry to prevent cleanup
	// from removing it (gateway sends LAN src IPs, not TUN IP).
	if tunIP, ok := cm.byName[name]; ok && tunIP != srcIP {
		tunEntry := cm.byIP[tunIP]
		tunEntry.brokerIdx = brokerIdx
		tunEntry.lastSeen = now
		cm.byIP[tunIP] = tunEntry
	}
	cm.mu.Unlock()
}

func (cm *clientMap) addOut(dstIP [4]byte, bytes int) {
	cm.mu.Lock()
	entry := cm.byIP[dstIP]
	entry.bytesOut += uint64(bytes)
	entry.packetsOut++
	cm.byIP[dstIP] = entry
	cm.mu.Unlock()
}

func (cm *clientMap) lookup(dstIP [4]byte) (clientEntry, bool) {
	cm.mu.RLock()
	entry, ok := cm.byIP[dstIP]
	cm.mu.RUnlock()
	return entry, ok
}

func (cm *clientMap) snapshot() map[string]clientEntry {
	cm.mu.RLock()
	defer cm.mu.RUnlock()
	result := make(map[string]clientEntry, len(cm.byName))
	for name, ip := range cm.byName {
		result[name] = cm.byIP[ip]
	}
	return result
}

func (cm *clientMap) cleanup(maxAge time.Duration) {
	cm.mu.Lock()
	defer cm.mu.Unlock()
	now := time.Now()
	for ip, entry := range cm.byIP {
		if now.Sub(entry.lastSeen) > maxAge {
			delete(cm.byIP, ip)
			// Only remove formal assignment if THIS IP is the assigned TUN IP.
			// LAN IP entries (from addIn) share the same name but must not
			// delete the byName mapping for the TUN IP.
			if assignedIP, ok := cm.byName[entry.name]; ok && assignedIP == ip {
				delete(cm.byName, entry.name)
				// LIFO: prepend the freed IP so a client that briefly
				// disconnects and reconnects gets its previous IP back
				// (assuming no other client claimed it in the gap). With
				// stable client sets this keeps assignments sticky.
				cm.pool = append([]net.IP{net.IP(ip[:])}, cm.pool...)
				log.Printf("Client %s (%v) timed out, TUN IP returned to pool", entry.name, net.IP(ip[:]))
			} else {
				log.Printf("Cleaned stale route %s (%v)", entry.name, net.IP(ip[:]))
			}
		}
	}
}

// Version is set via -ldflags "-X main.Version=..." at build time.
var Version = "dev"

func main() {
	configFile := flag.String("config", "", "YAML config file path")
	broker := flag.String("broker", "", "MQTT broker URL (single broker, backward compat)")
	brokers := flag.String("brokers", "", "Comma-separated MQTT broker URLs (multi-broker)")
	domain := flag.String("domain", "", "Discovery domain for SRV lookup (e.g. vertices.ru)")
	id := flag.String("id", "mtl", "This exit's ID")
	tunIP := flag.String("tun-ip", "10.9.0.1/24", "TUN interface IP/CIDR")
	user := flag.String("user", "", "MQTT username")
	pass := flag.String("pass", "", "MQTT password")
	statsFile := flag.String("stats-file", "/var/lib/vtx-stats.json", "Path to stats JSON file")
	dhKey := flag.String("dh-key", "", "Hex-encoded X25519 private key for DH key exchange")
	country := flag.String("country", "", "Country code for discovery (e.g. CA, NL)")
	maxClients := flag.Int("max-clients", 50, "Max clients for load balancing")
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
	if *domain != "" {
		cfg.Domain = *domain
	}
	if *id != "mtl" || cfg.ID == "" {
		cfg.ID = *id
	}
	if *tunIP != "10.9.0.1/24" || cfg.TunIP == "" {
		cfg.TunIP = *tunIP
	}
	if *user != "" {
		cfg.User = *user
	}
	if *pass != "" {
		cfg.Pass = *pass
	}
	if *statsFile != "/var/lib/vtx-stats.json" || cfg.StatsFile == "" {
		cfg.StatsFile = *statsFile
	}
	if *dhKey != "" {
		cfg.DHPrivateKey = *dhKey
	}
	if *country != "" {
		cfg.Country = *country
	}
	if *maxClients != 50 || cfg.MaxClients == 0 {
		cfg.MaxClients = *maxClients
	}

	// DNS SRV discovery: resolve brokers from domain. Same code path as
	// client/gateway — keeps a single source of truth for the broker fleet
	// (just change SRV in DNS to add/remove a broker; restart exits to
	// adopt). yaml `brokers:` is merged in as a bootstrap fallback for the
	// case where the SRV resolution itself can't reach the network.
	if cfg.Domain != "" {
		cachePath := "/etc/vertex/discovery-cache.json"
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

	brokerList := cfg.Brokers
	if len(brokerList) == 0 {
		log.Fatalf("No broker URLs: use -brokers, -broker, -domain, or -config")
	}

	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	// Load or generate X25519 DH keypair for E2E key exchange
	var dhPrivKey *ecdh.PrivateKey
	if cfg.DHPrivateKey != "" {
		raw, err := hex.DecodeString(cfg.DHPrivateKey)
		if err != nil {
			log.Fatalf("Invalid dh-private-key hex: %v", err)
		}
		dhPrivKey, err = btcrypto.LoadPrivateKey(raw)
		if err != nil {
			log.Fatalf("DH private key: %v", err)
		}
	} else {
		var err error
		dhPrivKey, err = btcrypto.GenerateKeyPair()
		if err != nil {
			log.Fatalf("DH keygen: %v", err)
		}
		log.Printf("WARNING: DH keypair auto-generated — all client sessions will break on restart!")
		log.Printf("To persist, add to config: dh-private-key: %s", hex.EncodeToString(dhPrivKey.Bytes()))
	}
	dhPubKeyB64 := btcrypto.EncodePubKey(dhPrivKey.PublicKey())
	log.Printf("E2E DH enabled (X25519 + ChaCha20-Poly1305), pubkey: %s", dhPubKeyB64)

	// Device identity key store (TOFU)
	devicesFile := "/var/lib/vtx-devices.json"
	if cfg.IdentityKey != "" {
		devicesFile = cfg.IdentityKey
	}
	keyStore, err := identity.NewKeyStore(devicesFile)
	if err != nil {
		log.Fatalf("Identity key store: %v", err)
	}
	if cfg.RequireIdentity {
		log.Printf("Device identity verification REQUIRED")
	} else {
		log.Printf("Device identity verification optional (TOFU enabled)")
	}

	if runtime.GOOS != "linux" {
		log.Fatalf("Exit node requires Linux (NAT/iptables)")
	}

	// Create TUN with fixed name — avoids orphaned iptables rules if
	// previous TUN fd lingers after crash (kernel would assign tun1, etc.)
	tun, err := vpn.NewTUN(cfg.TunIP, "vtx0")
	if err != nil {
		log.Fatalf("TUN: %v", err)
	}
	defer tun.Close()

	// Enable forwarding and disable rp_filter
	for _, s := range []string{
		"net.ipv4.ip_forward=1",
		fmt.Sprintf("net.ipv4.conf.%s.rp_filter=0", tun.Name()),
		"net.ipv4.conf.all.rp_filter=0",
	} {
		if out, err := exec.Command("sysctl", "-w", s).CombinedOutput(); err != nil {
			log.Printf("sysctl %s: %v (may be set by container runtime): %s", s, err, out)
		}
	}

	// Setup NAT (idempotent: cleanup before setup)
	cleanupNAT(tun.Name())
	if err := setupNAT(tun.Name()); err != nil {
		log.Fatalf("NAT setup: %v", err)
	}
	defer cleanupNAT(tun.Name())

	// Client map with IP pool
	clients := newClientMap(cfg.TunIP)

	// Parse gateway IP from TUN CIDR for join responses
	gwIP, _, _ := net.ParseCIDR(cfg.TunIP)
	gwStr := gwIP.String()

	startTime := time.Now()

	// Connect to MQTT brokers (one Transport per broker)
	// Held as the abstract transport.Transport — concrete capabilities
	// (Retainer for heartbeats) are checked via type assertion below.
	// pubTimeout is the deadline applied to every Transport call in the
	// hot path. 5s prevents TCP send-buffer overflow from starving MQTT
	// keepalive pings (which would trigger broker-side disconnect).
	const pubTimeout = 5 * time.Second
	const subTimeout = 10 * time.Second
	// rttProbeTimeout caps the QoS-1 PUBACK wait per broker. Long enough
	// to cover transcontinental round-trips on a slow link, short enough
	// that a stuck broker doesn't delay the rest of the heartbeat
	// (announcement runs every 30s).
	const rttProbeTimeout = 3 * time.Second

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

	// Group URLs by hostname: one Mosquitto process may expose multiple
	// listeners (e.g. mqtts:8883 + wss:443). Opening one Transport per URL
	// would create two parallel client sessions to the same broker and
	// duplicate every publish in the fanout — autopaho instead uses the
	// per-host URL list as a failover order (mqtts primary, wss DPI fallback).
	brokerGroups := config.GroupBrokersByHost(brokerList)
	if len(brokerGroups) == 0 {
		log.Fatalf("No valid broker URLs after grouping (input: %v)", brokerList)
	}

	transports := make([]transport.Transport, len(brokerGroups))
	feeds := make([]liveness.Feed, len(brokerGroups))
	for i, group := range brokerGroups {
		clientID := fmt.Sprintf("vtx-exit-%s-%d", cfg.ID, i)
		urlList := config.JoinBrokerURLs(group.URLs)
		// LWT comes from the liveness package: it knows what topic and
		// payload (empty) clear the retained announcement on an
		// ungraceful disconnect, so peers see EventRemoved.
		tr, err := mqtt.New(urlList, cfg.User, cfg.Pass, clientID, mqtt.WithLWT(liveness.MQTTLWTConfig(cfg.ID)))
		if err != nil {
			log.Fatalf("MQTT broker %d (%s): %v", i, group.Host, err)
		}
		feed, err := liveness.NewMQTTFeed(tr)
		if err != nil {
			log.Fatalf("liveness feed on broker %d (%s): %v", i, group.Host, err)
		}
		transports[i] = tr
		feeds[i] = feed
	}
	defer func() {
		for _, tr := range transports {
			if tr != nil {
				ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
				tr.Close(ctx)
				cancel()
			}
		}
	}()

	// Wait for all transports to connect
	for i, tr := range transports {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		if err := tr.WaitReady(ctx); err != nil {
			cancel()
			log.Fatalf("MQTT broker %d (%s) connect timeout: %v", i, brokerGroups[i].Host, err)
		}
		cancel()
		log.Printf("Connected to broker %d (%s): URLs=%v", i, brokerGroups[i].Host, brokerGroups[i].URLs)
	}

	// Download pipeline: MQTT handlers → channel → TUN writer goroutine (no mutex needed)
	downPipeline := pipeline.NewDownload(pipeline.DownloadConfig{
		WriteFunc: tun.Write,
	})
	defer downPipeline.Stop()

	// Subscribe on each broker
	dataPattern := protocol.MQTTDataInbound(cfg.ID)
	joinPattern := protocol.MQTTJoinPattern(cfg.ID)

	for i, tr := range transports {
		brokerIdx := i // capture for closure

		// Subscribe to client packets (wildcard)
		sub(tr, dataPattern, func(topic string, data []byte) {
			addr := protocol.ParseMQTTTopic(topic)
			if addr.Kind != protocol.KindDataOut {
				return
			}
			name := addr.ClientName

			// Per-client DH cipher
			if c := clients.cipherForName(name); c != nil {
				decrypted, err := c.Open(data)
				if err != nil {
					log.Printf("Decrypt error from %s: %v", name, err)
					return
				}
				data = decrypted
			}

			if len(data) < 20 {
				return
			}

			var srcIP [4]byte
			copy(srcIP[:], data[12:16])
			clients.addIn(srcIP, name, brokerIdx, len(data))

			downPipeline.Enqueue(data)
		})

		// Subscribe to join requests
		sub(tr, joinPattern, func(topic string, data []byte) {
			var req struct {
				Name  string `json:"name"`
				DH    string `json:"dh,omitempty"`     // client ephemeral X25519 pubkey (base64)
				ID    string `json:"id,omitempty"`      // persistent identity pubkey (base64)
				IDSig string `json:"id_sig,omitempty"`  // HMAC proof of identity ownership
			}
			if err := json.Unmarshal(data, &req); err != nil || req.Name == "" {
				log.Printf("Invalid join request: %s", data)
				return
			}
			if !isValidClientName(req.Name) {
				log.Printf("Rejected invalid client name: %q", req.Name)
				return
			}

			// Device identity verification
			if req.ID != "" {
				controlTopic := protocol.Control(cfg.ID, req.Name).MQTTTopic()

				// Decode identity pubkey
				idPub, err := btcrypto.DecodePubKey(req.ID)
				if err != nil {
					log.Printf("Invalid identity pubkey from %s: %v", req.Name, err)
					errResp, _ := json.Marshal(map[string]string{"error": "invalid identity key"})
					pub(transports[brokerIdx], controlTopic, errResp)
					return
				}

				// Verify HMAC proof (proves client holds identity private key)
				idSigRaw, err := base64.StdEncoding.DecodeString(req.IDSig)
				if err != nil || !identity.VerifyIdentityProof(dhPrivKey, idPub, req.Name, idSigRaw) {
					log.Printf("REJECTED %s: invalid identity proof", req.Name)
					errResp, _ := json.Marshal(map[string]string{"error": "invalid identity proof"})
					pub(transports[brokerIdx], controlTopic, errResp)
					return
				}

				// Atomic TOFU check + register (prevents race between broker goroutines)
				ok, isNew, regErr := keyStore.CheckAndRegister(req.Name, req.ID)
				if !ok {
					log.Printf("REJECTED %s: unknown device (identity key mismatch)", req.Name)
					errResp, _ := json.Marshal(map[string]string{"error": "unknown device"})
					pub(transports[brokerIdx], controlTopic, errResp)
					return
				}
				if regErr != nil {
					log.Printf("Failed to register device for %s: %v", req.Name, regErr)
				}
				if isNew {
					log.Printf("TOFU: registered new device for %s", req.Name)
				}
			} else if cfg.RequireIdentity {
				controlTopic := protocol.Control(cfg.ID, req.Name).MQTTTopic()
				log.Printf("REJECTED %s: identity required but not provided", req.Name)
				errResp, _ := json.Marshal(map[string]string{"error": "identity required"})
				pub(transports[brokerIdx], controlTopic, errResp)
				return
			}

			controlTopic := protocol.Control(cfg.ID, req.Name).MQTTTopic()

			// DH key exchange
			var clientCipher *btcrypto.Cipher
			if req.DH != "" {
				clientPubKey, err := btcrypto.DecodePubKey(req.DH)
				if err != nil {
					log.Printf("Invalid DH pubkey from %s: %v", req.Name, err)
					errResp, _ := json.Marshal(map[string]string{"error": "invalid DH pubkey"})
					pub(transports[brokerIdx], controlTopic, errResp)
					return
				}
				clientCipher, err = btcrypto.DeriveSessionCipher(
					dhPrivKey, clientPubKey,
					clientPubKey.Bytes(), dhPrivKey.PublicKey().Bytes(),
				)
				if err != nil {
					log.Printf("DH derive error for %s: %v", req.Name, err)
					errResp, _ := json.Marshal(map[string]string{"error": "DH key exchange failed"})
					pub(transports[brokerIdx], controlTopic, errResp)
					return
				}
			}

			ip, ok := clients.assign(req.Name, brokerIdx, clientCipher)
			if !ok {
				errResp, _ := json.Marshal(map[string]string{
					"error": "IP pool exhausted",
				})
				pub(transports[brokerIdx], controlTopic, errResp)
				log.Printf("IP pool exhausted, cannot assign IP for %s", req.Name)
				return
			}

			resp := map[string]string{
				"ip":   ip.String(),
				"mask": "255.255.255.0",
				"gw":   gwStr,
			}
			if req.DH != "" {
				resp["dh"] = dhPubKeyB64
			}
			respData, _ := json.Marshal(resp)
			pub(transports[brokerIdx], controlTopic, respData)

			if clientCipher != nil {
				log.Printf("Assigned %s to client %s (broker %d, DH PFS)", ip, req.Name, brokerIdx)
			} else {
				log.Printf("Assigned %s to client %s (broker %d, no encryption)", ip, req.Name, brokerIdx)
			}
		})
	}

	log.Printf("Transport ready on %d broker(s), accepting clients on vpn/%s/+/out", len(transports), cfg.ID)

	// Signal handler
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)

	// Stats manager
	stats := newStatsManager(cfg.StatsFile, cfg.ID)

	// Cleanup stale clients + save stats every 60s
	go func() {
		ticker := time.NewTicker(60 * time.Second)
		defer ticker.Stop()
		for range ticker.C {
			clients.cleanup(30 * time.Minute)
			stats.update(clients.snapshot())
			stats.save()
		}
	}()

	// Liveness heartbeat: announce ourselves every 30s on each broker.
	// The Feed handles the retained-publish encoding; we just produce a
	// fresh NodeInfo and hand it over.
	go func() {
		ticker := time.NewTicker(30 * time.Second)
		defer ticker.Stop()
		announce := func() {
			snap := clients.snapshot()

			// Measure RTT to each broker via QoS-1 PUBACK round-trip,
			// in parallel so total wait is one rttProbeTimeout instead
			// of N×rttProbeTimeout. The earlier QoS-0 implementation
			// timed only the local publish call and recorded ~0ms
			// regardless of broker distance — score-based exit
			// selection on watchers effectively reduced to load only.
			brokerRTT := make(map[string]int64, len(brokerGroups))
			var rttMu sync.Mutex
			var rttWG sync.WaitGroup
			for i, tr := range transports {
				if !tr.Ready() {
					continue
				}
				prober, ok := tr.(transport.RTTProbe)
				if !ok {
					continue
				}
				rttWG.Add(1)
				go func(i int, prober transport.RTTProbe) {
					defer rttWG.Done()
					pingTopic := protocol.DiscoveryPing(cfg.ID, i).MQTTTopic()
					ctx, cancel := context.WithTimeout(context.Background(), rttProbeTimeout)
					rtt, err := prober.ProbeRTT(ctx, pingTopic)
					cancel()
					host := brokerGroups[i].Host
					if err != nil {
						log.Printf("[heartbeat] RTT probe broker %d (%s) failed: %v", i, host, err)
						return
					}
					ms := rtt.Milliseconds()
					log.Printf("[heartbeat] RTT broker=%s rtt=%dms", host, ms)
					rttMu.Lock()
					brokerRTT[host] = ms
					rttMu.Unlock()
				}(i, prober)
			}
			rttWG.Wait()

			info := liveness.NodeInfo{
				ID:         cfg.ID,
				Country:    cfg.Country,
				Clients:    len(snap),
				MaxClients: cfg.MaxClients,
				BrokerRTTs: brokerRTT,
				Uptime:     int64(time.Since(startTime).Seconds()),
				TS:         time.Now().Unix(),
				DHPubKey:   dhPubKeyB64,
			}
			for i, feed := range feeds {
				ctx, cancel := context.WithTimeout(context.Background(), pubTimeout)
				if err := feed.Announce(ctx, info); err != nil {
					log.Printf("Heartbeat announce error (broker %d %s): %v", i, brokerGroups[i].Host, err)
				}
				cancel()
			}
		}
		announce()
		for range ticker.C {
			announce()
		}
	}()

	// Upload pipeline: TUN reader → channel → publisher (route + encrypt + per-broker publish)
	upPipeline := pipeline.NewUpload(pipeline.UploadConfig{
		ReadFunc: tun.Read,
		Closer:   tun,
		ProcessFunc: func(pkt []byte) {
			if len(pkt) < 20 {
				return
			}
			vpn.FixChecksums(pkt)

			var dstIP [4]byte
			copy(dstIP[:], pkt[16:20])
			entry, ok := clients.lookup(dstIP)
			if !ok {
				return
			}
			clients.addOut(dstIP, len(pkt))
			encCipher := entry.cipher
			if encCipher == nil && entry.name != "" {
				encCipher = clients.cipherForName(entry.name)
			}
			if encCipher != nil {
				var err error
				pkt, err = encCipher.Seal(pkt)
				if err != nil {
					log.Printf("Encrypt error for %s: %v", entry.name, err)
					return
				}
			}
			topic := protocol.DataIn(cfg.ID, entry.name).MQTTTopic()
			if entry.brokerIdx < len(transports) {
				if err := pub(transports[entry.brokerIdx], topic, pkt); err != nil {
					log.Printf("Publish dropped (congestion, broker %d): %v", entry.brokerIdx, err)
				}
			}
		},
	})
	defer upPipeline.Stop()

	sig := <-sigCh
	log.Printf("Received %v, shutting down", sig)
	stats.update(clients.snapshot())
	stats.save()
}

func setupNAT(tunName string) error {
	// SSH safety: add only if not already present (never removed by cleanupNAT)
	if exec.Command("iptables", "-C", "INPUT", "-p", "tcp", "--dport", "22", "-j", "ACCEPT").Run() != nil {
		exec.Command("iptables", "-I", "INPUT", "1", "-p", "tcp", "--dport", "22", "-j", "ACCEPT").Run()
	}

	cmds := [][]string{
		{"iptables", "-t", "nat", "-A", "POSTROUTING", "-s", "10.0.0.0/8", "-j", "MASQUERADE"},
		{"iptables", "-t", "nat", "-A", "POSTROUTING", "-s", "172.16.0.0/12", "-j", "MASQUERADE"},
		{"iptables", "-t", "nat", "-A", "POSTROUTING", "-s", "192.168.0.0/16", "-j", "MASQUERADE"},
		{"iptables", "-A", "FORWARD", "-i", tunName, "-j", "ACCEPT"},
		{"iptables", "-A", "FORWARD", "-o", tunName, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT"},
		{"iptables", "-t", "mangle", "-A", "FORWARD", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--clamp-mss-to-pmtu"},
		{"ip", "route", "add", "10.0.0.0/8", "dev", tunName},
		{"ip", "route", "add", "172.16.0.0/12", "dev", tunName},
		{"ip", "route", "add", "192.168.0.0/16", "dev", tunName},
	}

	for _, args := range cmds {
		if out, err := exec.Command(args[0], args[1:]...).CombinedOutput(); err != nil {
			if args[0] == "ip" && args[1] == "route" {
				continue
			}
			return fmt.Errorf("%s: %v: %s", strings.Join(args, " "), err, out)
		}
	}

	log.Printf("NAT configured on %s", tunName)
	return nil
}

func cleanupNAT(tunName string) {
	rules := [][]string{
		{"iptables", "-t", "nat", "-D", "POSTROUTING", "-s", "10.0.0.0/8", "-j", "MASQUERADE"},
		{"iptables", "-t", "nat", "-D", "POSTROUTING", "-s", "172.16.0.0/12", "-j", "MASQUERADE"},
		{"iptables", "-t", "nat", "-D", "POSTROUTING", "-s", "192.168.0.0/16", "-j", "MASQUERADE"},
		{"iptables", "-D", "FORWARD", "-i", tunName, "-j", "ACCEPT"},
		{"iptables", "-D", "FORWARD", "-o", tunName, "-m", "state", "--state", "RELATED,ESTABLISHED", "-j", "ACCEPT"},
		{"iptables", "-t", "mangle", "-D", "FORWARD", "-p", "tcp", "--tcp-flags", "SYN,RST", "SYN", "-j", "TCPMSS", "--clamp-mss-to-pmtu"},
	}
	for _, args := range rules {
		for exec.Command(args[0], args[1:]...).Run() == nil {
		}
	}

	routes := [][]string{
		{"ip", "route", "del", "10.0.0.0/8", "dev", tunName},
		{"ip", "route", "del", "172.16.0.0/12", "dev", tunName},
		{"ip", "route", "del", "192.168.0.0/16", "dev", tunName},
	}
	for _, args := range routes {
		exec.Command(args[0], args[1:]...).Run()
	}
}

// isValidClientName rejects names that collide with MQTT topic structure.
func isValidClientName(name string) bool {
	if strings.ContainsAny(name, "+#/") {
		return false
	}
	if name == "control" {
		return false
	}
	if len(name) > 64 {
		return false
	}
	return true
}
