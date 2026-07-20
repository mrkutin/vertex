package config

import (
	"fmt"
	"net"
	"net/url"
	"os"
	"path/filepath"
	"strings"

	"gopkg.in/yaml.v3"
)

// Config represents a YAML configuration file for client/gateway/exit.
type Config struct {
	Domain       string   `yaml:"domain,omitempty"`  // primary discovery domain for SRV
	Brokers      []string `yaml:"brokers"`
	Name         string   `yaml:"name"`
	User         string   `yaml:"user"`
	Pass         string   `yaml:"pass"`
	Exit         string   `yaml:"exit"`            // optional — auto-select if empty
	DHPrivateKey string   `yaml:"dh-private-key"`  // exit only: hex-encoded X25519 private key
	Verbose      bool     `yaml:"verbose"`
	JSON         bool     `yaml:"json"`
	Country      string   `yaml:"country"`          // exit only
	MaxClients   int      `yaml:"max-clients"`      // exit only
	ID           string   `yaml:"id"`               // exit only
	TunIP        string   `yaml:"tun-ip"`           // exit only
	StatsFile       string `yaml:"stats-file"`        // exit only
	IdentityKey     string `yaml:"identity-key"`      // client/gateway: path to identity key file
	RequireIdentity bool   `yaml:"require-identity"`  // exit: reject clients without identity
}

// LoadConfig reads a YAML config file.
func LoadConfig(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read config %s: %w", path, err)
	}
	var cfg Config
	if err := yaml.Unmarshal(data, &cfg); err != nil {
		return nil, fmt.Errorf("parse config %s: %w", path, err)
	}
	return &cfg, nil
}

// ParseBrokerList resolves broker URLs from -brokers (comma-separated) or -broker (single).
// Returns a slice of individual broker URL strings.
func ParseBrokerList(brokers, broker string) []string {
	if brokers != "" {
		var result []string
		for _, b := range strings.Split(brokers, ",") {
			b = strings.TrimSpace(b)
			if b != "" {
				result = append(result, b)
			}
		}
		if len(result) > 0 {
			return result
		}
	}
	if broker != "" {
		return []string{broker}
	}
	return nil
}

// JoinBrokerURLs joins broker URLs into a comma-separated string
// suitable for mqtt.New().
func JoinBrokerURLs(urls []string) string {
	return strings.Join(urls, ",")
}

// ExtractBrokerHosts parses unique hostnames from comma-separated broker URLs.
// Deduplicates hosts that appear with different schemes (e.g. mqtts:// and wss://).
func ExtractBrokerHosts(brokerURLs string) []string {
	var hosts []string
	seen := make(map[string]bool)
	for _, raw := range strings.Split(brokerURLs, ",") {
		raw = strings.TrimSpace(raw)
		if raw == "" {
			continue
		}
		u, err := url.Parse(raw)
		if err != nil {
			continue
		}
		h := u.Hostname()
		if !seen[h] {
			seen[h] = true
			hosts = append(hosts, h)
		}
	}
	return hosts
}

// BrokerGroup is the set of URLs that reach the same Mosquitto process.
// One Mosquitto can expose multiple listeners (e.g. mqtts:8883 + wss:443)
// — connecting to more than one of them at the same time duplicates every
// publish, so callers should create ONE Transport per group with the URLs
// supplied as an autopaho ServerUrls failover list.
type BrokerGroup struct {
	Host string
	URLs []string
}

// GroupBrokersByHost groups broker URLs by hostname, preserving the
// first-appearance order of both hosts and URLs within each host. Unparseable
// URLs and URLs with empty hostnames are skipped. Empty input returns nil.
//
// Grouping is hostname-only (not host:port). Two URLs to the same host on
// different ports — e.g. mqtts://h:8883 + wss://h:443 — collapse into one
// group, which is the intended deployment (one Mosquitto, two listeners).
// A topology of multiple independent Mosquittos behind one hostname would
// also collapse and lose the second instance; that topology is not deployed.
func GroupBrokersByHost(urls []string) []BrokerGroup {
	indexByHost := make(map[string]int)
	var groups []BrokerGroup
	for _, raw := range urls {
		raw = strings.TrimSpace(raw)
		if raw == "" {
			continue
		}
		u, err := url.Parse(raw)
		if err != nil {
			continue
		}
		host := u.Hostname()
		if host == "" {
			continue
		}
		if idx, ok := indexByHost[host]; ok {
			groups[idx].URLs = append(groups[idx].URLs, raw)
			continue
		}
		indexByHost[host] = len(groups)
		groups = append(groups, BrokerGroup{Host: host, URLs: []string{raw}})
	}
	return groups
}

// MergeBrokerURLs merges primary (e.g. DNS-discovered) and secondary (e.g. config)
// broker URL lists, deduplicating by exact URL match. Primary URLs come first.
func MergeBrokerURLs(primary, secondary []string) []string {
	seen := make(map[string]bool)
	var result []string
	for _, lists := range [2][]string{primary, secondary} {
		for _, u := range lists {
			u = strings.TrimSpace(u)
			if u != "" && !seen[u] {
				seen[u] = true
				result = append(result, u)
			}
		}
	}
	return result
}

// InviteConfig represents parsed invite URL parameters.
type InviteConfig struct {
	Domain string
	Name   string
	Pass   string
	Exit   string
}

// ParseInviteURL parses a bt://join?domain=...&name=...&pass=... invite URL.
func ParseInviteURL(rawURL string) (*InviteConfig, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, fmt.Errorf("parse invite URL: %w", err)
	}
	if u.Scheme != "bt" || u.Host != "join" {
		return nil, fmt.Errorf("invalid invite URL scheme: expected bt://join?..., got %s://%s", u.Scheme, u.Host)
	}
	q := u.Query()
	ic := &InviteConfig{
		Domain: q.Get("domain"),
		Name:   q.Get("name"),
		Pass:   q.Get("pass"),
		Exit:   q.Get("exit"),
	}
	if ic.Domain == "" {
		return nil, fmt.Errorf("invite URL missing required parameter: domain")
	}
	// Validate domain: no path separators, reasonable DNS characters
	for _, c := range ic.Domain {
		if !((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-') {
			return nil, fmt.Errorf("invite URL domain contains invalid character: %q", c)
		}
	}
	if ic.Name == "" {
		return nil, fmt.Errorf("invite URL missing required parameter: name")
	}
	if ic.Pass == "" {
		return nil, fmt.Errorf("invite URL missing required parameter: pass")
	}
	return ic, nil
}

// ToConfig converts invite parameters to a Config struct.
func (ic *InviteConfig) ToConfig() Config {
	return Config{
		Domain: ic.Domain,
		Name:   ic.Name,
		Pass:   ic.Pass,
		Exit:   ic.Exit,
	}
}

// SaveYAML writes the config as a YAML file, creating directories as needed.
// Uses restrictive permissions (0600) since the file may contain passwords.
func (c *Config) SaveYAML(path string) error {
	os.MkdirAll(filepath.Dir(path), 0700)
	data, err := yaml.Marshal(c)
	if err != nil {
		return fmt.Errorf("marshal config: %w", err)
	}
	return os.WriteFile(path, data, 0600)
}

// MaskToCIDR converts a dotted netmask (e.g. "255.255.255.0") to CIDR prefix length.
func MaskToCIDR(mask string) string {
	ip := net.ParseIP(mask)
	if ip == nil {
		return "24"
	}
	ones, _ := net.IPMask(ip.To4()).Size()
	if ones == 0 {
		return "24"
	}
	return fmt.Sprintf("%d", ones)
}
