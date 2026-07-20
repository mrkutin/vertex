package discovery

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// SRV service names for discovery.
// Brokers:  _mqtt._tcp.<domain>        → broker hosts (port: 8883=mqtts, 443=wss, 1883=mqtt)
// Exits:    _vtx-exit._tcp.<domain>    → exit IDs (target: {id}.exit.<domain>)
// Backup:   _vtx-backup._tcp.<domain>  → backup domain for chain-of-trust fallback
const (
	srvBroker = "mqtt"
	srvExit   = "vtx-exit"
	srvBackup = "vtx-backup"
)

// BrokerRecord represents a broker discovered via SRV.
type BrokerRecord struct {
	URL      string `json:"url"`
	Priority uint16 `json:"priority"`
	Weight   uint16 `json:"weight"`
}

// ExitRecord represents an exit discovered via SRV.
type ExitRecord struct {
	ID       string `json:"id"`
	Priority uint16 `json:"priority"`
	Weight   uint16 `json:"weight"`
}

// DNSCache stores discovery results on disk.
type DNSCache struct {
	Domain       string         `json:"domain"`
	BackupDomain string         `json:"backup_domain,omitempty"`
	Brokers      []BrokerRecord `json:"brokers"`
	Exits        []ExitRecord   `json:"exits,omitempty"`
	UpdatedAt    time.Time      `json:"updated_at"`
}

// BrokerURLs returns broker URLs sorted by priority.
func (c *DNSCache) BrokerURLs() []string {
	urls := make([]string, len(c.Brokers))
	for i, b := range c.Brokers {
		urls[i] = b.URL
	}
	return urls
}

// ExitIDs returns exit IDs sorted by priority.
func (c *DNSCache) ExitIDs() []string {
	ids := make([]string, len(c.Exits))
	for i, e := range c.Exits {
		ids[i] = e.ID
	}
	return ids
}

// SRVResolver allows mocking DNS lookups in tests.
type SRVResolver interface {
	LookupSRV(ctx context.Context, service, proto, name string) (string, []*net.SRV, error)
}

type netResolver struct{}

func (netResolver) LookupSRV(ctx context.Context, service, proto, name string) (string, []*net.SRV, error) {
	return net.DefaultResolver.LookupSRV(ctx, service, proto, name)
}

// DNSDiscovery resolves infrastructure via SRV records with chain-of-trust backup.
type DNSDiscovery struct {
	resolver  SRVResolver
	cachePath string
}

// NewDNSDiscovery creates a DNS discovery with the given cache file path.
func NewDNSDiscovery(cachePath string) *DNSDiscovery {
	return &DNSDiscovery{resolver: netResolver{}, cachePath: cachePath}
}

// NewDNSDiscoveryWithResolver creates a DNS discovery with a custom resolver (for testing).
func NewDNSDiscoveryWithResolver(cachePath string, r SRVResolver) *DNSDiscovery {
	return &DNSDiscovery{resolver: r, cachePath: cachePath}
}

// Resolve queries SRV records for the given domain.
// Broker SRV is required; exit and backup SRV are optional.
func (d *DNSDiscovery) Resolve(domain string) (*DNSCache, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	cache := &DNSCache{
		Domain:    domain,
		UpdatedAt: time.Now(),
	}

	// Brokers: _mqtt._tcp.<domain> (required)
	_, srvs, err := d.resolver.LookupSRV(ctx, srvBroker, "tcp", domain)
	if err != nil {
		return nil, fmt.Errorf("SRV _mqtt._tcp.%s: %w", domain, err)
	}
	for _, srv := range srvs {
		if u := srvToBrokerURL(srv); u != "" {
			cache.Brokers = append(cache.Brokers, BrokerRecord{
				URL:      u,
				Priority: srv.Priority,
				Weight:   srv.Weight,
			})
		}
	}
	sortBrokers(cache.Brokers)
	if len(cache.Brokers) == 0 {
		return nil, fmt.Errorf("SRV _mqtt._tcp.%s: no broker records", domain)
	}

	// Exits: _vtx-exit._tcp.<domain> (optional)
	_, exitSRVs, _ := d.resolver.LookupSRV(ctx, srvExit, "tcp", domain)
	for _, srv := range exitSRVs {
		if id := extractExitID(srv.Target); id != "" {
			cache.Exits = append(cache.Exits, ExitRecord{
				ID:       id,
				Priority: srv.Priority,
				Weight:   srv.Weight,
			})
		}
	}
	sortExits(cache.Exits)

	// Backup domain: _vtx-backup._tcp.<domain> (optional)
	_, backupSRVs, _ := d.resolver.LookupSRV(ctx, srvBackup, "tcp", domain)
	if len(backupSRVs) > 0 {
		sort.Slice(backupSRVs, func(i, j int) bool {
			return backupSRVs[i].Priority < backupSRVs[j].Priority
		})
		target := strings.TrimSuffix(backupSRVs[0].Target, ".")
		if target != "" && target != domain {
			cache.BackupDomain = target
		}
	}

	return cache, nil
}

// ResolveWithFallback tries: primary domain → cached backup domain → cached brokers.
// Each successful resolution updates the cache with fresh brokers and a new backup domain.
func (d *DNSDiscovery) ResolveWithFallback(primaryDomain string) (*DNSCache, error) {
	// 1. Try primary domain
	cache, err := d.Resolve(primaryDomain)
	if err == nil {
		d.SaveCache(cache)
		return cache, nil
	}
	log.Printf("[dns] %s failed: %v", primaryDomain, err)

	// 2. Load cache, try backup domain
	old, loadErr := d.LoadCache()
	if loadErr != nil {
		return nil, fmt.Errorf("primary %s failed (%w) and no cache", primaryDomain, err)
	}

	if old.BackupDomain != "" {
		backup, backupErr := d.Resolve(old.BackupDomain)
		if backupErr == nil {
			log.Printf("[dns] Resolved via backup %s", old.BackupDomain)
			d.SaveCache(backup)
			return backup, nil
		}
		log.Printf("[dns] Backup %s failed: %v", old.BackupDomain, backupErr)
	}

	// 3. Last resort: cached brokers
	if len(old.Brokers) > 0 {
		log.Printf("[dns] Using %d cached brokers (age: %s)",
			len(old.Brokers), time.Since(old.UpdatedAt).Truncate(time.Second))
		return old, nil
	}

	return nil, fmt.Errorf("all DNS discovery failed for %s", primaryDomain)
}

// LoadCache reads the discovery cache from disk.
func (d *DNSDiscovery) LoadCache() (*DNSCache, error) {
	data, err := os.ReadFile(d.cachePath)
	if err != nil {
		return nil, err
	}
	var cache DNSCache
	if err := json.Unmarshal(data, &cache); err != nil {
		return nil, err
	}
	return &cache, nil
}

// SaveCache writes the discovery cache to disk.
func (d *DNSDiscovery) SaveCache(cache *DNSCache) {
	data, err := json.MarshalIndent(cache, "", "  ")
	if err != nil {
		return
	}
	os.MkdirAll(filepath.Dir(d.cachePath), 0755)
	if err := os.WriteFile(d.cachePath, data, 0644); err != nil {
		log.Printf("[dns] Cache write: %v", err)
	}
}

// srvToBrokerURL converts SRV to broker URL using port convention:
// 8883 → mqtts://, 443 → wss://, 1883 → mqtt://, other → mqtt://
func srvToBrokerURL(srv *net.SRV) string {
	host := strings.TrimSuffix(srv.Target, ".")
	if host == "" {
		return ""
	}
	switch srv.Port {
	case 8883:
		return fmt.Sprintf("mqtts://%s:%d", host, srv.Port)
	case 443:
		return fmt.Sprintf("wss://%s:%d", host, srv.Port)
	case 1883:
		return fmt.Sprintf("mqtt://%s:%d", host, srv.Port)
	default:
		return fmt.Sprintf("mqtt://%s:%d", host, srv.Port)
	}
}

// extractExitID extracts the exit ID from SRV target.
// Convention: "{id}.exit.{domain}" → first label is the exit ID.
func extractExitID(target string) string {
	target = strings.TrimSuffix(target, ".")
	if target == "" {
		return ""
	}
	if dot := strings.IndexByte(target, '.'); dot > 0 {
		return target[:dot]
	}
	return target
}

func sortBrokers(b []BrokerRecord) {
	sort.Slice(b, func(i, j int) bool {
		if b[i].Priority != b[j].Priority {
			return b[i].Priority < b[j].Priority
		}
		return b[i].Weight > b[j].Weight
	})
}

func sortExits(e []ExitRecord) {
	sort.Slice(e, func(i, j int) bool {
		if e[i].Priority != e[j].Priority {
			return e[i].Priority < e[j].Priority
		}
		return e[i].Weight > e[j].Weight
	})
}
