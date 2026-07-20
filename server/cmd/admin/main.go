package main

import (
	"bufio"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/url"
	"os"
	"os/exec"
	"strings"
	"time"

	btcrypto "github.com/mrkutin/vertex/pkg/crypto"
)

const (
	defaultPasswdFile = "/etc/mosquitto/vtx_passwd"
	defaultACLFile    = "/etc/mosquitto/vtx_acl"
	defaultStatsFile  = "/var/lib/vtx-stats.json"
	defaultBroker     = "mqtts://mqtt-yc.vertices.ru:8883"

	// Remote paths used by `sync` for scp destination. Production brokers
	// still use the legacy "bt_" names from broker-tunnel days (mosquitto.conf
	// references /etc/mosquitto/bt_passwd). Override with VTX_REMOTE_* env or
	// -remote-passwd / -remote-acl flags if your brokers use a different path.
	defaultRemotePasswdFile = "/etc/mosquitto/bt_passwd"
	defaultRemoteACLFile    = "/etc/mosquitto/bt_acl"
)

func usage() {
	fmt.Fprintf(os.Stderr, `Usage:
  vtx-admin add        -name=NAME [-exits=EXIT[,EXIT...]] [-pass=PASS] [-brokers=URL,URL]
  vtx-admin add-exit   -id=ID [-pass=PASS]
  vtx-admin invite     -name=NAME -domain=DOMAIN [-exits=EXIT[,...]] [-pass=PASS]
  vtx-admin gen-dh-key                          Generate X25519 keypair for exit DH config
  vtx-admin remove     -name=NAME
  vtx-admin list
  vtx-admin show       -name=NAME [-brokers=URL,URL]
  vtx-admin sync       -brokers=HOST1,HOST2 [-remote-passwd=PATH] [-remote-acl=PATH]
                                                (sync passwd+ACL to all brokers via SSH)
  vtx-admin stats      [-stats-file=PATH]

  Client username format: vtx-client-{name} (exit-independent).
  Exit username format:   vtx-exit-{id}.
  Without -exits, wildcard ACL is generated (access to all exits).

Files (override with env or CLI flag):
  VTX_PASSWD_FILE         (default: %s)         — LOCAL read/write
  VTX_ACL_FILE            (default: %s)         — LOCAL read/write
  VTX_REMOTE_PASSWD_FILE  (default: %s)         — sync destination on brokers
  VTX_REMOTE_ACL_FILE     (default: %s)         — sync destination on brokers
  VTX_STATS_FILE          (default: %s)
`, defaultPasswdFile, defaultACLFile, defaultRemotePasswdFile, defaultRemoteACLFile, defaultStatsFile)
}

// Version is set via -ldflags "-X main.Version=..." at build time.
var Version = "dev"

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(1)
	}

	if os.Args[1] == "version" || os.Args[1] == "--version" || os.Args[1] == "-version" {
		fmt.Println(Version)
		return
	}

	passwdFile := envOr("VTX_PASSWD_FILE", defaultPasswdFile)
	aclFile := envOr("VTX_ACL_FILE", defaultACLFile)
	brokersStr := defaultBroker

	cmd := os.Args[1]
	args := parseFlags(os.Args[2:])

	if b, ok := args["brokers"]; ok {
		brokersStr = b
	} else if b, ok := args["broker"]; ok {
		brokersStr = b
	}

	switch cmd {
	case "add":
		name := args["name"]
		exitsStr := args["exits"]
		pass := args["pass"]
		if exitsStr == "" {
			exitsStr = args["exit"]
		}
		if name == "" {
			fatal("required: -name")
		}
		// No -exits = wildcard ACL (access to all exits)
		var exits []string
		if exitsStr != "" {
			exits = splitCSV(exitsStr)
		}
		if pass == "" {
			pass = generatePassword()
		}
		cmdAdd(passwdFile, aclFile, name, exits, pass, brokersStr)

	case "invite":
		name := args["name"]
		inviteDomain := args["domain"]
		pass := args["pass"]
		exitsStr := args["exits"]
		if exitsStr == "" {
			exitsStr = args["exit"]
		}
		if name == "" {
			fatal("required: -name")
		}
		if inviteDomain == "" {
			fatal("required: -domain (SRV discovery domain, e.g. vertices.ru)")
		}
		var exits []string
		if exitsStr != "" {
			exits = splitCSV(exitsStr)
		}
		if pass == "" {
			pass = generatePassword()
		}
		cmdInvite(passwdFile, aclFile, name, exits, pass, inviteDomain, brokersStr)

	case "gen-dh-key":
		cmdGenDHKey()

	case "add-exit":
		id := args["id"]
		pass := args["pass"]
		if id == "" {
			fatal("required: -id (e.g. -id=aws)")
		}
		if pass == "" {
			pass = generatePassword()
		}
		cmdAddExit(passwdFile, aclFile, id, pass, brokersStr)

	case "remove":
		name := args["name"]
		if name == "" {
			fatal("required: -name")
		}
		cmdRemove(passwdFile, aclFile, name)

	case "list":
		cmdList(aclFile)

	case "show":
		name := args["name"]
		if name == "" {
			fatal("required: -name")
		}
		cmdShow(passwdFile, aclFile, name, brokersStr)

	case "sync":
		hosts := args["brokers"]
		if hosts == "" {
			fatal("required: -brokers (comma-separated SSH hosts, e.g. sber,hetzner)")
		}
		remotePasswd := envOr("VTX_REMOTE_PASSWD_FILE", defaultRemotePasswdFile)
		remoteACL := envOr("VTX_REMOTE_ACL_FILE", defaultRemoteACLFile)
		if p, ok := args["remote-passwd"]; ok {
			remotePasswd = p
		}
		if p, ok := args["remote-acl"]; ok {
			remoteACL = p
		}
		cmdSync(passwdFile, aclFile, remotePasswd, remoteACL, splitCSV(hosts))

	case "stats":
		sf := envOr("VTX_STATS_FILE", defaultStatsFile)
		if f, ok := args["stats-file"]; ok {
			sf = f
		}
		cmdStats(sf)

	default:
		usage()
		os.Exit(1)
	}
}

// createUser creates a Mosquitto user with ACL entries. Returns true if created, false if already exists.
func createUser(passwdFile, aclFile, user, name string, exits []string, pass string) bool {
	if userExists(passwdFile, user) {
		return false
	}

	out, err := exec.Command("mosquitto_passwd", "-b", passwdFile, user, pass).CombinedOutput()
	if err != nil {
		fatal("mosquitto_passwd: %v: %s", err, out)
	}

	acl := formatACL(user, name, exits)
	f, err := os.OpenFile(aclFile, os.O_APPEND|os.O_WRONLY, 0644)
	if err != nil {
		fatal("open ACL file: %v", err)
	}
	_, err = f.WriteString(acl)
	f.Close()
	if err != nil {
		fatal("write ACL: %v", err)
	}

	reloadMosquitto()
	return true
}

func cmdAdd(passwdFile, aclFile, name string, exits []string, pass, brokersStr string) {
	user := mqttUser(name)

	if !createUser(passwdFile, aclFile, user, name, exits, pass) {
		fatal("user %s already exists", user)
	}

	if len(exits) > 0 {
		fmt.Printf("User %s created (exits: %s).\n\n", user, strings.Join(exits, ", "))
	} else {
		fmt.Printf("User %s created (wildcard: all exits).\n\n", user)
	}
	fmt.Printf("Client config:\n")
	fmt.Printf("  vtx-client -name=%s -brokers=%s -pass=%s\n", name, brokersStr, pass)
}

func cmdInvite(passwdFile, aclFile, name string, exits []string, pass, inviteDomain, brokersStr string) {
	user := mqttUser(name)

	if createUser(passwdFile, aclFile, user, name, exits, pass) {
		if len(exits) > 0 {
			fmt.Printf("User %s created (exits: %s).\n", user, strings.Join(exits, ", "))
		} else {
			fmt.Printf("User %s created (wildcard: all exits).\n", user)
		}
	} else {
		fmt.Printf("User %s already exists.\n", user)
	}

	// Build invite URL
	params := url.Values{}
	params.Set("domain", inviteDomain)
	params.Set("name", name)
	params.Set("pass", pass)
	if len(exits) == 1 {
		params.Set("exit", exits[0])
	}
	invURL := "bt://join?" + params.Encode()

	fmt.Printf("\nInvite URL:\n  %s\n", invURL)
	fmt.Printf("\nUsage:\n  vtx-client --invite \"%s\"\n", invURL)
	fmt.Printf("\nDNS SRV records needed on %s:\n", inviteDomain)
	fmt.Printf("  _mqtt._tcp.%s      SRV  10 100 8883 <broker-host>.\n", inviteDomain)
	fmt.Printf("  _vtx-exit._tcp.%s  SRV  10 100 0    <exit-id>.exit.%s.\n", inviteDomain, inviteDomain)
	fmt.Printf("  _vtx-backup._tcp.%s SRV 10 100 0    <backup-domain>.\n", inviteDomain)
}

func cmdRemove(passwdFile, aclFile, name string) {
	user := mqttUser(name)

	// Remove from passwd
	out, err := exec.Command("mosquitto_passwd", "-D", passwdFile, user).CombinedOutput()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Warning: mosquitto_passwd -D: %v: %s\n", err, out)
	}

	// Remove ACL block
	removeACLBlock(aclFile, user)

	// Reload Mosquitto
	reloadMosquitto()

	fmt.Printf("User %s removed.\n", user)
}

func cmdList(aclFile string) {
	f, err := os.Open(aclFile)
	if err != nil {
		fatal("open ACL file: %v", err)
	}
	defer f.Close()

	type clientInfo struct {
		user  string
		exits map[string]bool
	}
	clients := make(map[string]*clientInfo) // keyed by user
	var currentUser string

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if strings.HasPrefix(line, "#") || line == "" {
			currentUser = ""
			continue
		}
		if strings.HasPrefix(line, "user ") {
			currentUser = strings.TrimPrefix(line, "user ")
			if !strings.HasPrefix(currentUser, "vtx-client-") {
				currentUser = ""
				continue
			}
			if _, ok := clients[currentUser]; !ok {
				clients[currentUser] = &clientInfo{user: currentUser, exits: make(map[string]bool)}
			}
			continue
		}
		// Parse topic lines to extract exit names
		if currentUser != "" && strings.HasPrefix(line, "topic ") {
			// e.g. "topic write vpn/aws/r3s/out"
			parts := strings.Fields(line)
			if len(parts) >= 3 {
				topicParts := strings.Split(parts[2], "/")
				if len(topicParts) >= 2 && topicParts[0] == "vpn" {
					exitName := topicParts[1]
					if exitName != "+" { // skip wildcards
						clients[currentUser].exits[exitName] = true
					}
				}
			}
		}
	}

	for user, info := range clients {
		var exitList []string
		for e := range info.exits {
			exitList = append(exitList, e)
		}
		name := strings.TrimPrefix(user, "vtx-client-")
		fmt.Printf("  %s  (name=%s, exits=%s)\n", user, name, strings.Join(exitList, ","))
	}
}

func cmdShow(passwdFile, aclFile, name, brokersStr string) {
	user := mqttUser(name)
	if !userExists(passwdFile, user) {
		fatal("user %s not found", user)
	}

	// Parse exits from ACL
	exits := getClientExits(aclFile, user)

	fmt.Printf("User: %s\n", user)
	fmt.Printf("Name: %s\n", name)
	fmt.Printf("Exits: %s\n", strings.Join(exits, ", "))
	fmt.Printf("Topics:\n")
	for _, exit := range exits {
		fmt.Printf("  vpn/%s/%s/out (write)\n", exit, name)
		fmt.Printf("  vpn/%s/%s/in  (read)\n", exit, name)
	}
	fmt.Printf("\nClient config:\n")
	fmt.Printf("  vtx-client -name=%s -brokers=%s -pass=<password>\n", name, brokersStr)
}

func cmdStats(statsFilePath string) {
	raw, err := os.ReadFile(statsFilePath)
	if err != nil {
		fatal("read stats file: %v", err)
	}

	var data struct {
		Exit    string `json:"exit"`
		Updated string `json:"updated"`
		Clients map[string]struct {
			BytesInH   string `json:"bytesInH"`
			BytesOutH  string `json:"bytesOutH"`
			PacketsIn  uint64 `json:"packetsIn"`
			PacketsOut uint64 `json:"packetsOut"`
			LastSeen   string `json:"lastSeen"`
			Today      struct {
				BytesInH  string `json:"bytesInH"`
				BytesOutH string `json:"bytesOutH"`
			} `json:"today"`
			Month struct {
				BytesInH  string `json:"bytesInH"`
				BytesOutH string `json:"bytesOutH"`
			} `json:"month"`
		} `json:"clients"`
	}
	if err := json.Unmarshal(raw, &data); err != nil {
		fatal("parse stats: %v", err)
	}

	fmt.Printf("Exit: %s  (updated: %s)\n\n", data.Exit, data.Updated)
	fmt.Printf("%-12s %-24s %-24s %-24s %s\n", "Client", "Today In/Out", "Month In/Out", "Total In/Out", "Last Seen")
	fmt.Printf("%-12s %-24s %-24s %-24s %s\n", "------", "------------", "------------", "------------", "---------")

	for name, cs := range data.Clients {
		lastSeen := cs.LastSeen
		if t, err := time.Parse(time.RFC3339Nano, cs.LastSeen); err == nil {
			ago := time.Since(t).Truncate(time.Second)
			if ago < time.Minute {
				lastSeen = fmt.Sprintf("%ds ago", int(ago.Seconds()))
			} else if ago < time.Hour {
				lastSeen = fmt.Sprintf("%dm ago", int(ago.Minutes()))
			} else {
				lastSeen = fmt.Sprintf("%dh %dm ago", int(ago.Hours()), int(ago.Minutes())%60)
			}
		}
		fmt.Printf("%-12s %-24s %-24s %-24s %s\n",
			name,
			cs.Today.BytesInH+" / "+cs.Today.BytesOutH,
			cs.Month.BytesInH+" / "+cs.Month.BytesOutH,
			cs.BytesInH+" / "+cs.BytesOutH,
			lastSeen,
		)
	}
}

// cmdSync scps the local passwd/ACL files to each broker and HUPs mosquitto.
// localPasswd/localACL come from VTX_PASSWD_FILE/VTX_ACL_FILE — they're the
// files we just modified via `add`/`remove`/etc. (may be /tmp/* during a
// local-Mac workflow). remotePasswd/remoteACL are the canonical broker paths
// (default /etc/mosquitto/bt_passwd, /etc/mosquitto/bt_acl) — mosquitto.conf
// references these. Without separating local and remote, local /tmp paths
// would scp to broker /tmp and mosquitto would never see the change.
func cmdSync(localPasswd, localACL, remotePasswd, remoteACL string, hosts []string) {
	for _, host := range hosts {
		if !isValidHost(host) {
			fmt.Fprintf(os.Stderr, "  invalid host name: %q (only alphanumeric, dots, hyphens allowed)\n", host)
			continue
		}
		fmt.Printf("Syncing to %s...\n", host)

		// Stage into /tmp on the broker first, then atomically install with
		// correct mosquitto:mosquitto ownership (per feedback memory: never
		// chown root:root on these files — breaks mosquitto setuid).
		stagePasswd := "/tmp/.vtx-admin-passwd"
		stageACL := "/tmp/.vtx-admin-acl"

		out, err := exec.Command("scp", localPasswd, host+":"+stagePasswd).CombinedOutput()
		if err != nil {
			fmt.Fprintf(os.Stderr, "  scp passwd to %s:%s: %v: %s\n", host, stagePasswd, err, out)
			continue
		}

		out, err = exec.Command("scp", localACL, host+":"+stageACL).CombinedOutput()
		if err != nil {
			fmt.Fprintf(os.Stderr, "  scp ACL to %s:%s: %v: %s\n", host, stageACL, err, out)
			continue
		}

		// Atomic install + correct perms + mosquitto reload, single shell call.
		installCmd := fmt.Sprintf(
			"set -e; "+
				"sudo install -m 640 -o mosquitto -g mosquitto %s %s && "+
				"sudo install -m 640 -o mosquitto -g mosquitto %s %s && "+
				"rm -f %s %s && "+
				"sudo kill -HUP $(pidof mosquitto) 2>/dev/null || sudo systemctl reload mosquitto 2>/dev/null || true",
			stagePasswd, remotePasswd, stageACL, remoteACL, stagePasswd, stageACL,
		)
		out, err = exec.Command("ssh", host, installCmd).CombinedOutput()
		if err != nil {
			fmt.Fprintf(os.Stderr, "  %s: install+reload failed: %v: %s\n", host, err, out)
			continue
		}

		fmt.Printf("  %s: synced %s + %s, reloaded\n", host, remotePasswd, remoteACL)
	}
	fmt.Printf("Sync complete.\n")
}

func cmdAddExit(passwdFile, aclFile, id, pass, brokersStr string) {
	user := fmt.Sprintf("vtx-exit-%s", id)

	// Check if user already exists
	if userExists(passwdFile, user) {
		fatal("user %s already exists", user)
	}

	// Add to passwd file
	out, err := exec.Command("mosquitto_passwd", "-b", passwdFile, user, pass).CombinedOutput()
	if err != nil {
		fatal("mosquitto_passwd: %v: %s", err, out)
	}

	// Add ACL entries
	acl := formatExitACL(user, id)
	f, err := os.OpenFile(aclFile, os.O_APPEND|os.O_WRONLY, 0644)
	if err != nil {
		fatal("open ACL file: %v", err)
	}
	_, err = f.WriteString(acl)
	f.Close()
	if err != nil {
		fatal("write ACL: %v", err)
	}

	// Reload Mosquitto
	reloadMosquitto()

	fmt.Printf("Exit user %s created.\n\n", user)
	fmt.Printf("Exit config:\n")
	fmt.Printf("  vtx-exit -id=%s -brokers=%s -pass=%s\n", id, brokersStr, pass)
}

// formatExitACL generates ACL entries for an exit node.
// Exit needs: read client data, write responses, read join requests, write control, publish discovery.
func formatExitACL(user, id string) string {
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("\nuser %s\n", user))
	sb.WriteString(fmt.Sprintf("topic read vpn/%s/+/out\n", id))
	sb.WriteString(fmt.Sprintf("topic write vpn/%s/+/in\n", id))
	sb.WriteString(fmt.Sprintf("topic read vpn/%s/control/join\n", id))
	sb.WriteString(fmt.Sprintf("topic write vpn/%s/+/control\n", id))
	sb.WriteString(fmt.Sprintf("topic write discovery/exits/%s\n", id))
	sb.WriteString(fmt.Sprintf("topic write discovery/ping/%s/+\n", id))
	return sb.String()
}

// isValidHost rejects hostnames with shell metacharacters.
func isValidHost(host string) bool {
	for _, c := range host {
		if !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-' || c == '_') {
			return false
		}
	}
	return len(host) > 0
}

// --- helpers ---

func mqttUser(name string) string {
	return fmt.Sprintf("vtx-client-%s", name)
}

// formatACL generates ACL entries.
// Empty/nil exits = wildcard (all exits). Single exit = exit-specific. Multiple = wildcard.
func formatACL(user, name string, exits []string) string {
	var sb strings.Builder
	sb.WriteString(fmt.Sprintf("\nuser %s\n", user))
	if len(exits) == 1 {
		// Single exit: exit-specific topics (tighter ACL)
		exit := exits[0]
		sb.WriteString(fmt.Sprintf("topic write vpn/%s/%s/out\n", exit, name))
		sb.WriteString(fmt.Sprintf("topic read vpn/%s/%s/in\n", exit, name))
		sb.WriteString(fmt.Sprintf("topic read vpn/%s/%s/control\n", exit, name))
		sb.WriteString(fmt.Sprintf("topic write vpn/%s/control/join\n", exit))
	} else {
		// Wildcard: 0 exits (default) or multiple exits
		sb.WriteString(fmt.Sprintf("topic write vpn/+/%s/out\n", name))
		sb.WriteString(fmt.Sprintf("topic read vpn/+/%s/in\n", name))
		sb.WriteString(fmt.Sprintf("topic read vpn/+/%s/control\n", name))
		sb.WriteString("topic write vpn/+/control/join\n")
	}
	sb.WriteString("topic read discovery/exits/+\n")
	return sb.String()
}

// getClientExits parses exits from ACL file for a given user.
func getClientExits(aclFile, user string) []string {
	f, err := os.Open(aclFile)
	if err != nil {
		return nil
	}
	defer f.Close()

	exits := make(map[string]bool)
	inBlock := false
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "user "+user {
			inBlock = true
			continue
		}
		if inBlock {
			if strings.HasPrefix(line, "user ") {
				break
			}
			if !strings.HasPrefix(line, "topic ") {
				continue // skip empty/comment lines within block
			}
			if strings.HasPrefix(line, "topic ") {
				parts := strings.Fields(line)
				if len(parts) >= 3 {
					topicParts := strings.Split(parts[2], "/")
					if len(topicParts) >= 2 && topicParts[0] == "vpn" && topicParts[1] != "+" {
						exits[topicParts[1]] = true
					}
				}
			}
		}
	}
	var result []string
	for e := range exits {
		result = append(result, e)
	}
	return result
}

func splitCSV(s string) []string {
	var result []string
	for _, p := range strings.Split(s, ",") {
		p = strings.TrimSpace(p)
		if p != "" {
			result = append(result, p)
		}
	}
	return result
}

func userExists(passwdFile, user string) bool {
	f, err := os.Open(passwdFile)
	if err != nil {
		return false
	}
	defer f.Close()
	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		if strings.HasPrefix(scanner.Text(), user+":") {
			return true
		}
	}
	return false
}

func removeACLBlock(aclFile, user string) {
	data, err := os.ReadFile(aclFile)
	if err != nil {
		return
	}

	lines := strings.Split(string(data), "\n")
	var result []string
	skip := false
	for _, line := range lines {
		trimmed := strings.TrimSpace(line)
		if trimmed == "user "+user {
			skip = true
			continue
		}
		if skip {
			if strings.HasPrefix(trimmed, "topic ") || trimmed == "" {
				continue
			}
			skip = false
		}
		result = append(result, line)
	}

	os.WriteFile(aclFile, []byte(strings.Join(result, "\n")), 0644)
}

func reloadMosquitto() {
	// Try kill -HUP first, fall back to systemctl
	out, err := exec.Command("sh", "-c", "kill -HUP $(pidof mosquitto) 2>/dev/null || systemctl reload mosquitto 2>/dev/null").CombinedOutput()
	if err != nil {
		fmt.Fprintf(os.Stderr, "Warning: could not reload Mosquitto: %v: %s\n", err, out)
	}
}

func cmdGenDHKey() {
	priv, err := btcrypto.GenerateKeyPair()
	if err != nil {
		fatal("keygen: %v", err)
	}
	fmt.Printf("X25519 keypair generated.\n\n")
	fmt.Printf("Private key (for exit config dh-private-key):\n  %s\n\n", hex.EncodeToString(priv.Bytes()))
	fmt.Printf("Public key (base64, published in discovery):\n  %s\n", btcrypto.EncodePubKey(priv.PublicKey()))
}

func generatePassword() string {
	b := make([]byte, 16)
	rand.Read(b)
	return hex.EncodeToString(b)
}

func parseFlags(args []string) map[string]string {
	result := make(map[string]string)
	for _, arg := range args {
		arg = strings.TrimPrefix(arg, "-")
		arg = strings.TrimPrefix(arg, "-")
		if k, v, ok := strings.Cut(arg, "="); ok {
			result[k] = v
		}
	}
	return result
}

func envOr(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func fatal(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "Error: "+format+"\n", args...)
	os.Exit(1)
}
