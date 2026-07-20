// vtx-inline-setup is the first-boot bootstrap web-UI for Vertex R3S-inline.
//
// It listens on http://192.168.42.1/ (LAN side only — firewall enforces this),
// serves a single form: WAN type (PPPoE/DHCP), PPPoE credentials, client_name
// and MQTT password (issued by admin). On submit it writes the YAML/env
// configs, renders the PPPoE peer file, starts the WAN and router services,
// and creates a sentinel that disables the unit on next boot.
//
// Hard rule: no edits to clients/gateway/** — we reuse the existing vtx-gateway
// binary via `-config /etc/vertex-inline/router.yaml`.
package main

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"errors"
	"fmt"
	"html/template"
	"context"
	"io/fs"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"regexp"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	texttmpl "text/template"
	"time"

	"github.com/mrkutin/vertex/pkg/config"
	"github.com/mrkutin/vertex/pkg/discovery"
	"github.com/mrkutin/vertex/pkg/identity"
)

// Two template packages: html/template (`template`) for the web-UI (autoescape
// HTML context) and text/template (`texttmpl`) for the PPPoE peer-file render
// (plain-text substitution; user input pre-validated against safe regex).

//go:embed web
var webFS embed.FS

//go:embed templates/pppoe-peer.tmpl
var pppoeTemplateText string

const (
	listenAddr = "192.168.42.1:80"

	sentinelPath  = "/etc/vertex-inline/.setup-done"
	routerYAML    = "/etc/vertex-inline/router.yaml"
	inlineEnv     = "/etc/vertex-inline/inline.env"
	identityKey   = "/etc/vertex-inline/identity.key"
	pppoePeerPath = "/etc/ppp/peers/vtx-isp"
	chapSecrets   = "/etc/ppp/chap-secrets"

	domainName = "vertices.ru"
)

// Emergency fallback when DNS-SRV resolution fails (e.g. first-boot
// bootstrap before WAN comes up). Once WAN is up, brokers/exits are
// discovered via `_mqtt._tcp.vertices.ru` + `_vtx-exit._tcp.vertices.ru`
// (same as iOS/macOS/CLI clients) — see pkg/discovery/dns.go. These
// hardcoded values are last-resort and may go stale; the SRV path is
// authoritative.
var fallbackBrokers = []string{
	"mqtts://mqtt-yc.vertices.ru:8883",
	"mqtts://mqtt-sber.vertices.ru:8883",
	"wss://mqtt-yc.vertices.ru:443",
	"wss://mqtt-sber.vertices.ru:443",
}
var fallbackBrokerIPs = map[string]string{
	"yc":   "51.250.12.145",
	"sber": "37.230.192.188",
}

var (
	// client_name must match the user vtx-admin created. Lowercase alnum + hyphens.
	clientNameRe = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{1,30}$`)
	// PPPoE user: typical login formats — alnum, dot, underscore, dash, at-sign.
	// Rejects shell-special chars (`'"$;&|\\) and whitespace.
	pppoeUserRe = regexp.MustCompile(`^[a-zA-Z0-9._@-]{1,64}$`)

	csrfKey []byte
)

type formData struct {
	WANMode       string
	PPPoEUser     string
	PPPoEPassword string
	ClientName    string
	MQTTPassword  string
	Broker        string // "auto" | "{id}-mqtts" | "{id}-wss"
	Exit          string // "auto" | "sto" | "ams" | ... (per SRV discovery)
	// SaveOnly: write configs + enable units, but do NOT start them now.
	// Use case: pre-configure PPPoE credentials for a target ISP without
	// being physically there. Current VPN (if running) keeps working
	// in-memory until the user power-cycles the device at the new location.
	SaveOnly bool
}

// brokerOption backs one radio row in the broker fieldset — a specific
// (broker, scheme) pair. We surface mqtts and wss as separate entries
// so the operator can force WSS:443 in DPI-blocked networks (port 8883
// blocked, port 443 indistinguishable from HTTPS).
type brokerOption struct {
	ID       string // composite, e.g. "sber-mqtts" or "sber-wss"
	BrokerID string // bare broker id, e.g. "sber" — used to map to IPs
	Scheme   string // "mqtts" or "wss"
	Host     string // "mqtt-sber.vertices.ru"
	URL      string // full URL, single value
	Label    string // for display, e.g. "sber MQTTS (mqtt-sber.vertices.ru:8883)"
}

type exitOption struct {
	ID string // "sto", "ams", … — from SRV target {id}.exit.{domain}
}

type discovered struct {
	Brokers []brokerOption
	Exits   []exitOption
	Source  string // "srv" when DNS worked, "fallback" otherwise
}

// brokerIDFromHost extracts the short ID from "mqtt-{id}.{domain}".
// Returns full host unchanged if it doesn't match the convention.
func brokerIDFromHost(host string) string {
	h := strings.TrimSuffix(host, ".")
	if !strings.HasPrefix(h, "mqtt-") {
		return h
	}
	rest := strings.TrimPrefix(h, "mqtt-")
	if i := strings.Index(rest, "."); i > 0 {
		return rest[:i]
	}
	return rest
}

// In-memory cache for discover() — avoids blocking renderForm/applyConfig
// on a 5-second DNS timeout every time the user hits GET / or POST /submit.
// Refresh interval ≈ 60s; serve stale on background-refresh failure.
var (
	discoverCache    atomic.Pointer[discovered]
	discoverMu       sync.Mutex // serialise the actual SRV call
	discoverCachedAt atomic.Int64
)

const discoverTTLSec = 60

// brokersFromURLs expands a flat list of broker URLs into one brokerOption
// per (broker_id, scheme) pair. Sort key: broker_id then scheme.
func brokersFromURLs(urls []string) []brokerOption {
	var out []brokerOption
	for _, u := range urls {
		pu, perr := url.Parse(u)
		if perr != nil {
			continue
		}
		host := pu.Hostname()
		bid := brokerIDFromHost(host)
		if bid == "" {
			continue // drop malformed entries (e.g. "mqtt-" with no suffix)
		}
		scheme := strings.ToLower(pu.Scheme)
		port := pu.Port()
		if port == "" {
			port = defaultPort(scheme)
		}
		label := fmt.Sprintf("%s %s (%s:%s)", bid, strings.ToUpper(scheme), host, port)
		out = append(out, brokerOption{
			ID:       bid + "-" + scheme,
			BrokerID: bid,
			Scheme:   scheme,
			Host:     host,
			URL:      u,
			Label:    label,
		})
	}
	sort.Slice(out, func(i, j int) bool {
		if out[i].BrokerID != out[j].BrokerID {
			return out[i].BrokerID < out[j].BrokerID
		}
		return out[i].Scheme < out[j].Scheme // "mqtts" < "wss"
	})
	return out
}

func defaultPort(scheme string) string {
	switch scheme {
	case "mqtts":
		return "8883"
	case "wss":
		return "443"
	case "mqtt":
		return "1883"
	case "ws":
		return "80"
	}
	return ""
}

// discover returns the cached SRV result; refreshes in the background if
// the cache is older than discoverTTLSec. First call (cache empty) is
// synchronous so the first form render has something to show.
func discover() *discovered {
	if d := discoverCache.Load(); d != nil {
		age := time.Now().Unix() - discoverCachedAt.Load()
		if age < discoverTTLSec {
			return d
		}
		// Stale — refresh in background, return current snapshot now.
		go refreshDiscover()
		return d
	}
	return refreshDiscover()
}

func refreshDiscover() *discovered {
	discoverMu.Lock()
	defer discoverMu.Unlock()

	// Another goroutine may have refreshed while we waited on the lock.
	if d := discoverCache.Load(); d != nil && time.Now().Unix()-discoverCachedAt.Load() < discoverTTLSec {
		return d
	}

	d := &discovered{}
	dns := discovery.NewDNSDiscovery("/tmp/vtx-inline-setup-dns.cache")
	cache, err := dns.Resolve(domainName)
	if err == nil && len(cache.Brokers) > 0 {
		d.Source = "srv"
		d.Brokers = brokersFromURLs(cache.BrokerURLs())
		for _, ex := range cache.Exits {
			d.Exits = append(d.Exits, exitOption{ID: ex.ID})
		}
		sort.Slice(d.Exits, func(i, j int) bool { return d.Exits[i].ID < d.Exits[j].ID })
	} else {
		// Fallback — derive from the hardcoded URL list. No SRV → no exit
		// list. User can only pick "auto" (vtx-gateway will discover exits
		// at runtime via MQTT heartbeats).
		d.Source = "fallback"
		d.Brokers = brokersFromURLs(fallbackBrokers)
	}

	discoverCache.Store(d)
	discoverCachedAt.Store(time.Now().Unix())
	return d
}

// selectBrokerURLs filters the discovered broker list by user choice.
// "auto" returns ALL URLs (vtx-gateway probes + picks best). A specific
// "{id}-{scheme}" returns only that one URL — used to force a transport
// (e.g. wss-only in DPI-blocked networks). Unknown choice falls through
// to fallbackBrokers so router.yaml never ends up empty.
func selectBrokerURLs(d *discovered, choice string) []string {
	if choice == "" || choice == "auto" {
		var all []string
		for _, b := range d.Brokers {
			all = append(all, b.URL)
		}
		if len(all) > 0 {
			return all
		}
		return fallbackBrokers
	}
	for _, b := range d.Brokers {
		if b.ID == choice {
			return []string{b.URL}
		}
	}
	return fallbackBrokers
}

// selectBrokerIPs returns the VTX_BROKER value for vtx-inline-proxy.sh
// (mangle skip rules). The chosen scheme is irrelevant for skip rules —
// the IP behind mqtt-{id}.{domain} is the same regardless of port.
// Parallel A-record lookup with a 2-second context timeout for the whole
// batch — falls back to fallbackBrokerIPs per-host on lookup failure.
func selectBrokerIPs(d *discovered, choice string) string {
	// Deduplicate by BrokerID — multiple (mqtts, wss) entries share one host/IP.
	seen := map[string]brokerOption{}
	candidates := []brokerOption{}
	addOnce := func(b brokerOption) {
		if _, ok := seen[b.BrokerID]; ok {
			return
		}
		seen[b.BrokerID] = b
		candidates = append(candidates, b)
	}
	if choice == "" || choice == "auto" {
		for _, b := range d.Brokers {
			addOnce(b)
		}
	} else {
		for _, b := range d.Brokers {
			if b.ID == choice {
				addOnce(b)
				break
			}
		}
	}
	if len(candidates) == 0 {
		// Last resort — write the full fallback map so proxy.sh always has
		// SOMETHING for its skip rules. Better to skip a broker we don't
		// connect to than to route broker MQTT into TUN (loop).
		ips := make([]string, 0, len(fallbackBrokerIPs))
		for _, ip := range fallbackBrokerIPs {
			ips = append(ips, ip)
		}
		sort.Strings(ips)
		return strings.Join(ips, ",")
	}

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	results := make([]string, len(candidates))
	var wg sync.WaitGroup
	for i, b := range candidates {
		wg.Add(1)
		go func(i int, b brokerOption) {
			defer wg.Done()
			if addrs, err := net.DefaultResolver.LookupHost(ctx, b.Host); err == nil && len(addrs) > 0 {
				results[i] = addrs[0]
				return
			}
			if ip, ok := fallbackBrokerIPs[b.BrokerID]; ok {
				results[i] = ip
			}
		}(i, b)
	}
	wg.Wait()

	var ips []string
	for _, ip := range results {
		if ip != "" {
			ips = append(ips, ip)
		}
	}
	return strings.Join(ips, ",")
}

func main() {
	log.SetFlags(log.LstdFlags | log.Lmsgprefix)
	log.SetPrefix("[vtx-inline-setup] ")

	// Web-UI stays on across reboots so the user can re-configure when
	// moving the device to another network (new ISP credentials, new
	// WAN type). Sentinel `/etc/vertex-inline/.setup-done` no longer
	// gates the service; it just marks "successful first setup happened
	// at this time" and seeds the form with the current configuration
	// (see prefillForm()). Initial-bootstrap UI when sentinel absent,
	// reconfigure UI when sentinel present.

	csrfKey = make([]byte, 32)
	if _, err := rand.Read(csrfKey); err != nil {
		log.Fatalf("generate CSRF key: %v", err)
	}

	// Pre-parse PPPoE template so we fail fast at startup if it's malformed.
	if _, err := texttmpl.New("pppoe-peer").Parse(pppoeTemplateText); err != nil {
		log.Fatalf("parse embedded pppoe-peer.tmpl: %v", err)
	}

	sub, err := fs.Sub(webFS, "web")
	if err != nil {
		log.Fatalf("strip web/ prefix: %v", err)
	}

	mux := http.NewServeMux()
	mux.HandleFunc("/", handleForm)
	mux.HandleFunc("/submit", handleSubmit)
	mux.Handle("/style.css", http.FileServer(http.FS(sub)))

	srv := &http.Server{
		Addr:         listenAddr,
		Handler:      mux,
		ReadTimeout:  10 * time.Second,
		WriteTimeout: 120 * time.Second, // applyConfig can take ~60s waiting on WAN
	}
	log.Printf("listening on http://%s/", listenAddr)
	if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
		log.Fatalf("ListenAndServe: %v", err)
	}
}

// generateCSRF returns a `nonce.hmac(nonce)` token. Stateless: any token
// signed by csrfKey is valid. csrfKey is regenerated on every process
// start, so tokens are bound to the current bootstrap session.
func generateCSRF() string {
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		log.Fatalf("rand.Read for CSRF nonce: %v", err)
	}
	h := hmac.New(sha256.New, csrfKey)
	h.Write(nonce)
	return hex.EncodeToString(nonce) + "." + hex.EncodeToString(h.Sum(nil))
}

func validateCSRF(token string) bool {
	parts := strings.SplitN(token, ".", 2)
	if len(parts) != 2 {
		return false
	}
	nonce, err := hex.DecodeString(parts[0])
	if err != nil {
		return false
	}
	got, err := hex.DecodeString(parts[1])
	if err != nil {
		return false
	}
	h := hmac.New(sha256.New, csrfKey)
	h.Write(nonce)
	return hmac.Equal(h.Sum(nil), got)
}

func handleForm(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" {
		http.NotFound(w, r)
		return
	}
	// First boot → blank form. Reconfigure → pre-fill non-secret fields.
	renderForm(w, "", prefillForm())
}

// prefillForm reads the current configuration (if any) and returns a
// formData seed for non-secret fields. Passwords are NEVER pre-filled —
// the user must re-enter them on every reconfigure to confirm intent and
// avoid leaking them in HTML attribute context.
func prefillForm() *formData {
	if _, err := os.Stat(sentinelPath); err != nil {
		return nil // first boot — blank form
	}
	fd := &formData{}

	// trim() strips CRLF + quotes + spaces so admin-edited (e.g. Windows
	// nano) config files don't break parsing.
	trim := func(s string) string { return strings.Trim(strings.TrimRight(s, "\r\n "), `"`) }

	// inline.env: VTX_WAN_MODE=pppoe|dhcp
	if b, err := os.ReadFile(inlineEnv); err == nil {
		for _, l := range strings.Split(string(b), "\n") {
			if v, ok := strings.CutPrefix(l, "VTX_WAN_MODE="); ok {
				fd.WANMode = trim(v)
			}
		}
	}

	// router.yaml: name + exit + brokers list. Use pkg/config.LoadConfig
	// for robust YAML parsing (handles quoted values, the brokers list, etc.).
	if cfg, err := config.LoadConfig(routerYAML); err == nil {
		fd.ClientName = cfg.Name
		if cfg.Exit == "" {
			fd.Exit = "auto"
		} else {
			fd.Exit = cfg.Exit
		}
		// Broker selection: a single URL in router.yaml → compound
		// "{id}-{scheme}" pin; anything else → "auto".
		// NOTE: works only for router.yaml written by THIS version of the
		// binary (which writes 1 URL for pin / N URLs for auto). Files
		// written by the previous "per-broker" version (where "sber" pin
		// wrote 2 URLs: mqtts+wss) will fall to "auto" on first reconfigure
		// — a one-time regression on legacy installs, acceptable.
		fd.Broker = "auto"
		if len(cfg.Brokers) == 1 {
			pu, perr := url.Parse(cfg.Brokers[0])
			if perr == nil {
				if bid := brokerIDFromHost(pu.Hostname()); bid != "" {
					fd.Broker = bid + "-" + strings.ToLower(pu.Scheme)
				}
			}
		}
	}

	// /etc/ppp/peers/vtx-isp: user "..."
	if b, err := os.ReadFile(pppoePeerPath); err == nil {
		for _, l := range strings.Split(string(b), "\n") {
			if v, ok := strings.CutPrefix(strings.TrimSpace(l), "user "); ok {
				fd.PPPoEUser = trim(strings.TrimSpace(v))
			}
		}
	}
	return fd
}

func renderForm(w http.ResponseWriter, errMsg string, prev *formData) {
	tmpl, err := template.ParseFS(webFS, "web/setup.html")
	if err != nil {
		http.Error(w, "template parse: "+err.Error(), http.StatusInternalServerError)
		return
	}
	_, sentErr := os.Stat(sentinelPath)
	d := discover()
	data := map[string]any{
		"CSRF":        generateCSRF(),
		"Error":       errMsg,
		"Prev":        prev,
		"Reconfigure": sentErr == nil,
		"Brokers":     d.Brokers, // [{ID, Host}] for radio buttons
		"Exits":       d.Exits,   // [{ID}]
		"SRVSource":   d.Source,  // "srv" | "fallback" (UI may show a hint)
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	if err := tmpl.Execute(w, data); err != nil {
		log.Printf("template execute: %v", err)
	}
}

func handleSubmit(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "parse form", http.StatusBadRequest)
		return
	}
	if !validateCSRF(r.FormValue("csrf")) {
		http.Error(w, "CSRF token invalid (reload the page and try again)", http.StatusForbidden)
		return
	}
	fd := formData{
		WANMode:       r.FormValue("wan_mode"),
		PPPoEUser:     strings.TrimSpace(r.FormValue("pppoe_user")),
		PPPoEPassword: r.FormValue("pppoe_password"),
		ClientName:    strings.TrimSpace(r.FormValue("client_name")),
		MQTTPassword:  r.FormValue("mqtt_password"),
		Broker:        strings.TrimSpace(r.FormValue("broker")),
		Exit:          strings.TrimSpace(r.FormValue("exit")),
		SaveOnly:      r.FormValue("save_only") == "1",
	}
	if fd.Broker == "" {
		fd.Broker = "auto"
	}
	if fd.Exit == "" {
		fd.Exit = "auto"
	}

	// Reconfigure UX: empty password fields mean "keep current value" so the
	// user can change WAN type / exit without re-typing secrets every time.
	// Loaded from disk (router.yaml for MQTT, chap-secrets for PPPoE).
	_, sentErr := os.Stat(sentinelPath)
	reconfigure := sentErr == nil
	if reconfigure {
		if fd.MQTTPassword == "" {
			if cfg, err := config.LoadConfig(routerYAML); err == nil && cfg.Pass != "" {
				fd.MQTTPassword = cfg.Pass
			}
		}
		if fd.WANMode == "pppoe" && fd.PPPoEPassword == "" && fd.PPPoEUser != "" {
			if p := readChapSecretPassword(fd.PPPoEUser); p != "" {
				fd.PPPoEPassword = p
			}
		}
	}
	// NEVER log fd — contains credentials.

	if err := validateForm(&fd); err != nil {
		renderForm(w, err.Error(), &fd)
		return
	}
	if err := applyConfig(&fd); err != nil {
		log.Printf("applyConfig failed: %v", err)
		// In SaveOnly mode we never stopped services and never snapshotted,
		// so there's nothing to rollback — current VPN still alive
		// in-memory. Just show the error; user fixes via the form.
		if !fd.SaveOnly {
			rollback()
		}
		renderForm(w, applyErrorMessage(err), &fd)
		return
	}
	renderSuccess(w, fd.SaveOnly)

	// Web-UI stays running across reboots so the user can re-configure
	// (e.g. when moving R3S to a new ISP). No self-disable here.
	if f, ok := w.(http.Flusher); ok {
		f.Flush()
	}
}

// applyErrorMessage maps internal error context to user-friendly Russian
// messages. We deliberately do NOT pass raw err.Error() to the user — wrapped
// errors may contain file paths or systemctl output.
func applyErrorMessage(err error) string {
	switch {
	case errors.Is(err, errWANStart):
		return "Не удалось поднять WAN. Проверьте PPPoE-логин и пароль (или кабель), затем попробуйте снова."
	case errors.Is(err, errRouterStart):
		return "WAN работает, но VPN-туннель не поднялся. Проверьте MQTT-пароль и имя клиента."
	default:
		return "Не удалось применить конфигурацию. Свяжитесь с администратором (journalctl -u vtx-inline-setup на R3S)."
	}
}

var (
	errWANStart    = errors.New("wan start failed")
	errRouterStart = errors.New("router start failed")
)

// Shape validators for the closed enums in the form. We don't check
// against the runtime discovery list (which flaps); the runtime is
// authoritative — a bad ID gets logged by vtx-gateway and the user can
// re-submit.
var (
	// Exit ID — short slug (e.g. "ams", "sto").
	validExitRe = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{0,15}$`)
	// Broker ID — compound "{slug}-{scheme}" where scheme is mqtts|wss.
	validBrokerRe = regexp.MustCompile(`^[a-z0-9][a-z0-9-]{0,15}-(mqtts|wss)$`)
)

func validatedBroker(v string) bool {
	return v == "auto" || validBrokerRe.MatchString(v)
}

func validatedExit(v string) bool {
	return v == "auto" || validExitRe.MatchString(v)
}

func validateForm(fd *formData) error {
	if fd.WANMode != "pppoe" && fd.WANMode != "dhcp" {
		return errors.New("Выберите тип WAN (PPPoE или DHCP).")
	}
	if !validatedBroker(fd.Broker) {
		return errors.New("Брокер: некорректный выбор.")
	}
	if !validatedExit(fd.Exit) {
		return errors.New("Exit-нода: некорректный выбор.")
	}
	if !clientNameRe.MatchString(fd.ClientName) {
		return errors.New("Имя клиента: строчные буквы/цифры/дефис, 2–31 символ, начинается с буквы или цифры.")
	}
	if len(fd.MQTTPassword) < 8 {
		return errors.New("MQTT-пароль слишком короткий (мин. 8 символов).")
	}
	if fd.WANMode == "pppoe" {
		if fd.PPPoEUser == "" || fd.PPPoEPassword == "" {
			return errors.New("Для PPPoE нужны логин и пароль провайдера.")
		}
		if !pppoeUserRe.MatchString(fd.PPPoEUser) {
			return errors.New("PPPoE-логин: только латиница, цифры, точка, подчёркивание, @, дефис. 1–64 символа.")
		}
		// Password may contain many chars, but disallow ones that break the
		// chap-secrets quoted format or shell-friendly escapes.
		if strings.ContainsAny(fd.PPPoEPassword, "\"\\\n\r") {
			return errors.New("PPPoE-пароль не должен содержать кавычки, обратный слеш или переводы строк.")
		}
	}
	return nil
}

func applyConfig(fd *formData) error {
	if err := os.MkdirAll("/etc/vertex-inline", 0700); err != nil {
		return fmt.Errorf("mkdir /etc/vertex-inline: %w", err)
	}

	// Reconfigure-path semantics: atomic snapshot/restore so a failed
	// re-apply (e.g. wrong PPPoE creds) doesn't kill the working VPN.
	// 1. Snapshot existing configs to .bak
	// 2. Stop services
	// 3. Write new configs
	// 4. Start services
	// 5. On any failure: rollback() restores .bak files + restarts services
	//
	// SaveOnly skips this — current services keep running on their
	// in-memory configuration; the user will power-cycle on the target
	// network to pick up the new on-disk config.
	if !fd.SaveOnly {
		if _, err := os.Stat(sentinelPath); err == nil {
			snapshot()
			_ = exec.Command("systemctl", "stop", "vtx-inline-router.service").Run()
			_ = exec.Command("systemctl", "stop", "vtx-inline-wan.service").Run()
			_ = exec.Command("systemctl", "stop", "vtx-inline-firewall.service").Run()
		}
	}

	if _, err := identity.LoadOrGenerateKey(identityKey); err != nil {
		return fmt.Errorf("identity key: %w", err)
	}

	exitID := fd.Exit
	if exitID == "auto" {
		exitID = "" // pkg/config: empty Exit → vtx-gateway auto-select
	}
	d := discover()
	cfg := &config.Config{
		Domain:      domainName,
		Brokers:     selectBrokerURLs(d, fd.Broker),
		Name:        fd.ClientName,
		Pass:        fd.MQTTPassword,
		Exit:        exitID,
		IdentityKey: identityKey,
	}
	if err := cfg.SaveYAML(routerYAML); err != nil {
		return fmt.Errorf("save router.yaml: %w", err)
	}

	wanIface := "pppoe0"
	if fd.WANMode == "dhcp" {
		wanIface = "eth0"
	}
	envContent := fmt.Sprintf(
		"VTX_WAN_MODE=%s\nVTX_WAN_IFACE=%s\nVTX_LAN_IFACE=eth1\nVTX_LAN_SUBNET=192.168.42.0/24\nVTX_TUN_IFACE=vtx0\nVTX_BROKER=%s\n",
		fd.WANMode, wanIface, selectBrokerIPs(d, fd.Broker),
	)
	if err := os.WriteFile(inlineEnv, []byte(envContent), 0600); err != nil {
		return fmt.Errorf("write inline.env: %w", err)
	}

	if fd.WANMode == "pppoe" {
		if err := writePPPoEPeer(fd); err != nil {
			return fmt.Errorf("pppoe peer: %w", err)
		}
		if err := writeChapSecret(fd); err != nil {
			return fmt.Errorf("chap-secrets: %w", err)
		}
	}

	// Sentinel marks "first successful bootstrap completed" so wan/router
	// can start (they have ConditionPathExists=/etc/vertex-inline/.setup-done).
	// Idempotent — re-writing on reconfigure just updates the timestamp.
	if err := writeSentinel(); err != nil {
		return fmt.Errorf("sentinel: %w", err)
	}

	if err := systemctl("daemon-reload"); err != nil {
		return err
	}
	if err := systemctl("enable",
		"vtx-inline-wan.service",
		"vtx-inline-firewall.service",
		"vtx-inline-router.service",
	); err != nil {
		return err
	}

	// SaveOnly: stop here. Units are enabled and will start on the next boot
	// at the target network. Currently-running services (if any) keep using
	// their in-memory state from the previous apply, so the existing VPN
	// stays alive until the user powers off and moves the device.
	if fd.SaveOnly {
		// Hazard: if a running service restarts for ANY reason (OOM,
		// manual systemctl restart, crash + on-failure-restart) before the
		// power-cycle at the target network, it will pick up the NEW
		// on-disk config and try to dial the new ISP from the current LAN
		// → failure. Loud journal note so this trap is greppable.
		log.Printf("SaveOnly: configs written but services NOT restarted; running services may pick up new config on unexpected restart")
		// Clean up any leftover .bak from prior reconfigure attempts —
		// keeps /etc/vertex-inline/ tidy.
		for _, p := range []string{routerYAML, inlineEnv, pppoePeerPath} {
			_ = os.Remove(p + ".bak")
		}
		return nil
	}

	// Start WAN first so PPPoE/DHCP errors surface as a distinct failure
	// mode (the user re-enters credentials, not the MQTT password).
	if err := systemctl("start", "vtx-inline-wan.service"); err != nil {
		return fmt.Errorf("%w: %v", errWANStart, err)
	}
	// Firewall before router so WAN admin ports are closed before VPN brings
	// up traffic. Firewall is oneshot, returns quickly.
	if err := systemctl("start", "vtx-inline-firewall.service"); err != nil {
		return fmt.Errorf("firewall start: %w", err)
	}
	if err := systemctl("start", "vtx-inline-router.service"); err != nil {
		return fmt.Errorf("%w: %v", errRouterStart, err)
	}
	// Success — drop any leftover snapshot files.
	for _, p := range []string{routerYAML, inlineEnv, pppoePeerPath} {
		_ = os.Remove(p + ".bak")
	}
	return nil
}

func writePPPoEPeer(fd *formData) error {
	t, err := texttmpl.New("pppoe-peer").Parse(pppoeTemplateText)
	if err != nil {
		return fmt.Errorf("parse template: %w", err)
	}
	var buf strings.Builder
	if err := t.Execute(&buf, struct{ PPPoEUser string }{fd.PPPoEUser}); err != nil {
		return fmt.Errorf("render template: %w", err)
	}
	if err := os.MkdirAll("/etc/ppp/peers", 0755); err != nil {
		return err
	}
	return os.WriteFile(pppoePeerPath, []byte(buf.String()), 0600)
}

// readChapSecretPassword finds the line `"<user>" * "<pass>" *` in
// /etc/ppp/chap-secrets and returns <pass>. Empty string if not found.
// Used by the reconfigure "leave blank = keep current" UX.
func readChapSecretPassword(user string) string {
	b, err := os.ReadFile(chapSecrets)
	if err != nil {
		return ""
	}
	prefix := fmt.Sprintf("\"%s\" ", user)
	for _, l := range strings.Split(string(b), "\n") {
		if !strings.HasPrefix(l, prefix) {
			continue
		}
		// Format: "user" * "pass" *
		// PPPoE password validation already rejected backslashes/quotes,
		// so a naive split on `"` is safe.
		parts := strings.Split(l, `"`)
		if len(parts) >= 4 {
			return parts[3]
		}
	}
	return ""
}

func writeChapSecret(fd *formData) error {
	var existing []byte
	if b, err := os.ReadFile(chapSecrets); err == nil {
		existing = b
	}
	prefix := fmt.Sprintf("\"%s\" ", fd.PPPoEUser)
	var out strings.Builder
	for _, l := range strings.Split(string(existing), "\n") {
		if strings.HasPrefix(l, prefix) || l == "" {
			continue
		}
		out.WriteString(l)
		out.WriteString("\n")
	}
	fmt.Fprintf(&out, "\"%s\" * \"%s\" *\n", fd.PPPoEUser, fd.PPPoEPassword)
	return os.WriteFile(chapSecrets, []byte(out.String()), 0600)
}

func writeSentinel() error {
	tmp := sentinelPath + ".tmp"
	if err := os.WriteFile(tmp, []byte(time.Now().UTC().Format(time.RFC3339)+"\n"), 0644); err != nil {
		return err
	}
	return os.Rename(tmp, sentinelPath)
}

// snapshot copies the current configs to .bak files. Called at the start of
// a reconfigure-path applyConfig so we can restore on failure.
func snapshot() {
	for _, p := range []string{routerYAML, inlineEnv, pppoePeerPath} {
		if b, err := os.ReadFile(p); err == nil {
			_ = os.WriteFile(p+".bak", b, 0600)
		} else {
			// Remove stale .bak so restore won't bring back something we no
			// longer need (e.g. PPPoE peer when previous was DHCP).
			_ = os.Remove(p + ".bak")
		}
	}
}

// restoreSnapshot moves the .bak files back into place. Called by rollback()
// on reconfigure failure. Best-effort — leaves any missing originals empty
// (services will fail to start cleanly, user re-submits).
func restoreSnapshot() bool {
	restored := false
	for _, p := range []string{routerYAML, inlineEnv, pppoePeerPath} {
		bak := p + ".bak"
		if b, err := os.ReadFile(bak); err == nil {
			if err := os.WriteFile(p, b, 0600); err == nil {
				_ = os.Remove(bak)
				restored = true
			}
		}
	}
	return restored
}

func rollback() {
	hadSentinel := false
	if _, err := os.Stat(sentinelPath); err == nil {
		hadSentinel = true
	}

	// Reconfigure path: restore snapshot, restart services on the OLD
	// working config. Sentinel is monotonic — keep it. Don't disable units.
	if hadSentinel && restoreSnapshot() {
		_ = exec.Command("systemctl", "start", "vtx-inline-wan.service").Run()
		_ = exec.Command("systemctl", "start", "vtx-inline-firewall.service").Run()
		_ = exec.Command("systemctl", "start", "vtx-inline-router.service").Run()
		return
	}

	// First-bootstrap failed — clean slate so the form can re-bootstrap.
	_ = os.Remove(sentinelPath)
	_ = os.Remove(routerYAML)
	_ = os.Remove(inlineEnv)
	_ = os.Remove(pppoePeerPath)
	// identity.key kept on purpose: regenerating on retry would create a new
	// TOFU record on every exit — pollutes /var/lib/vtx-devices.json.
	// chap-secrets left as-is (surgical removal complex; harmless on retry
	// because writeChapSecret de-duplicates by user prefix).
	_ = exec.Command("systemctl", "stop", "vtx-inline-router.service").Run()
	_ = exec.Command("systemctl", "stop", "vtx-inline-wan.service").Run()
	_ = exec.Command("systemctl", "stop", "vtx-inline-firewall.service").Run()
	_ = exec.Command("systemctl", "disable",
		"vtx-inline-router.service",
		"vtx-inline-firewall.service",
		"vtx-inline-wan.service",
	).Run()
}

func systemctl(args ...string) error {
	cmd := exec.Command("systemctl", args...)
	out, err := cmd.CombinedOutput()
	if err != nil {
		return fmt.Errorf("systemctl %s: %v: %s", strings.Join(args, " "), err, strings.TrimSpace(string(out)))
	}
	return nil
}

func renderSuccess(w http.ResponseWriter, saveOnly bool) {
	tmpl, err := template.ParseFS(webFS, "web/success.html")
	if err != nil {
		http.Error(w, "success template: "+err.Error(), http.StatusInternalServerError)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	if err := tmpl.Execute(w, map[string]any{"SaveOnly": saveOnly}); err != nil {
		log.Printf("success template execute: %v", err)
	}
}
