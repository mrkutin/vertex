package discovery

import (
	"context"
	"fmt"
	"net"
	"path/filepath"
	"testing"
)

type mockResolver struct {
	records map[string][]*net.SRV
	errors  map[string]error
}

func newMockResolver() *mockResolver {
	return &mockResolver{
		records: make(map[string][]*net.SRV),
		errors:  make(map[string]error),
	}
}

func (m *mockResolver) AddSRV(service, proto, name string, records ...*net.SRV) {
	key := fmt.Sprintf("_%s._%s.%s", service, proto, name)
	m.records[key] = records
}

func (m *mockResolver) AddError(service, proto, name string, err error) {
	key := fmt.Sprintf("_%s._%s.%s", service, proto, name)
	m.errors[key] = err
}

func (m *mockResolver) LookupSRV(_ context.Context, service, proto, name string) (string, []*net.SRV, error) {
	key := fmt.Sprintf("_%s._%s.%s", service, proto, name)
	if err, ok := m.errors[key]; ok {
		return "", nil, err
	}
	if srvs, ok := m.records[key]; ok {
		return key, srvs, nil
	}
	return "", nil, fmt.Errorf("no SRV records for %s", key)
}

func mkSRV(priority, weight, port uint16, target string) *net.SRV {
	return &net.SRV{Priority: priority, Weight: weight, Port: port, Target: target}
}

func TestResolveBrokers(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "4few.ru",
		mkSRV(10, 50, 8883, "mqtt-msk.4few.ru."),
		mkSRV(10, 50, 8883, "mqtt-spb.4few.ru."),
		mkSRV(20, 50, 443, "mqtt-msk.4few.ru."),
		mkSRV(20, 50, 443, "mqtt-spb.4few.ru."),
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("4few.ru")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(cache.Brokers) != 4 {
		t.Fatalf("expected 4 brokers, got %d", len(cache.Brokers))
	}
	// Priority 10 (MQTTS) should come before priority 20 (WSS)
	if cache.Brokers[0].Priority != 10 {
		t.Errorf("first broker priority = %d, want 10", cache.Brokers[0].Priority)
	}
	if cache.Brokers[2].Priority != 20 {
		t.Errorf("third broker priority = %d, want 20", cache.Brokers[2].Priority)
	}

	urls := cache.BrokerURLs()
	for _, u := range urls[:2] {
		if u != "mqtts://mqtt-msk.4few.ru:8883" && u != "mqtts://mqtt-spb.4few.ru:8883" {
			t.Errorf("unexpected MQTTS broker: %s", u)
		}
	}
	for _, u := range urls[2:] {
		if u != "wss://mqtt-msk.4few.ru:443" && u != "wss://mqtt-spb.4few.ru:443" {
			t.Errorf("unexpected WSS broker: %s", u)
		}
	}
}

func TestResolveExits(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "4few.ru",
		mkSRV(10, 100, 8883, "broker.4few.ru."),
	)
	mock.AddSRV("vtx-exit", "tcp", "4few.ru",
		mkSRV(10, 100, 0, "aws.exit.4few.ru."),
		mkSRV(20, 100, 0, "rvk.exit.4few.ru."),
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("4few.ru")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	ids := cache.ExitIDs()
	if len(ids) != 2 {
		t.Fatalf("expected 2 exits, got %d", len(ids))
	}
	if ids[0] != "aws" {
		t.Errorf("first exit = %s, want aws", ids[0])
	}
	if ids[1] != "rvk" {
		t.Errorf("second exit = %s, want rvk", ids[1])
	}
}

func TestResolveBackupDomain(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "4few.ru",
		mkSRV(10, 100, 8883, "broker.4few.ru."),
	)
	mock.AddSRV("vtx-backup", "tcp", "4few.ru",
		mkSRV(10, 100, 0, "backup.example.com."),
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("4few.ru")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if cache.BackupDomain != "backup.example.com" {
		t.Errorf("backup = %q, want backup.example.com", cache.BackupDomain)
	}
}

func TestResolveBackupSameAsPrimaryIgnored(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "4few.ru",
		mkSRV(10, 100, 8883, "broker.4few.ru."),
	)
	mock.AddSRV("vtx-backup", "tcp", "4few.ru",
		mkSRV(10, 100, 0, "4few.ru."), // same as primary
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("4few.ru")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if cache.BackupDomain != "" {
		t.Errorf("backup should be empty when same as primary, got %q", cache.BackupDomain)
	}
}

func TestResolveNoBrokers(t *testing.T) {
	mock := newMockResolver()
	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	_, err := d.Resolve("unknown.com")
	if err == nil {
		t.Fatal("expected error for missing broker SRV")
	}
}

func TestResolveNoExitsOK(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "4few.ru",
		mkSRV(10, 100, 8883, "broker.4few.ru."),
	)
	// No exit or backup SRV — should still succeed

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("4few.ru")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}
	if len(cache.Exits) != 0 {
		t.Errorf("expected 0 exits, got %d", len(cache.Exits))
	}
	if cache.BackupDomain != "" {
		t.Errorf("expected empty backup, got %q", cache.BackupDomain)
	}
}

func TestResolveWithFallbackPrimary(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "primary.com",
		mkSRV(10, 100, 8883, "broker.primary.com."),
	)
	mock.AddSRV("vtx-backup", "tcp", "primary.com",
		mkSRV(10, 100, 0, "backup.com."),
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.ResolveWithFallback("primary.com")
	if err != nil {
		t.Fatalf("ResolveWithFallback: %v", err)
	}
	if cache.Domain != "primary.com" {
		t.Errorf("domain = %s, want primary.com", cache.Domain)
	}
	if cache.BackupDomain != "backup.com" {
		t.Errorf("backup = %s, want backup.com", cache.BackupDomain)
	}
}

func TestResolveWithFallbackToBackup(t *testing.T) {
	mock := newMockResolver()
	mock.AddError("mqtt", "tcp", "primary.com", fmt.Errorf("NXDOMAIN"))
	mock.AddSRV("mqtt", "tcp", "backup.com",
		mkSRV(10, 100, 8883, "broker.backup.com."),
	)
	mock.AddSRV("vtx-backup", "tcp", "backup.com",
		mkSRV(10, 100, 0, "backup2.com."),
	)

	cachePath := filepath.Join(t.TempDir(), "cache.json")
	d := NewDNSDiscoveryWithResolver(cachePath, mock)

	// Seed cache with backup domain
	d.SaveCache(&DNSCache{
		Domain:       "primary.com",
		BackupDomain: "backup.com",
		Brokers:      []BrokerRecord{{URL: "mqtts://old:8883"}},
	})

	cache, err := d.ResolveWithFallback("primary.com")
	if err != nil {
		t.Fatalf("ResolveWithFallback: %v", err)
	}
	if cache.Domain != "backup.com" {
		t.Errorf("should resolve via backup, got domain=%s", cache.Domain)
	}
	if cache.BackupDomain != "backup2.com" {
		t.Errorf("should have chain backup, got %s", cache.BackupDomain)
	}
	if cache.Brokers[0].URL != "mqtts://broker.backup.com:8883" {
		t.Errorf("broker = %s, want mqtts://broker.backup.com:8883", cache.Brokers[0].URL)
	}
}

func TestResolveWithFallbackToCachedBrokers(t *testing.T) {
	mock := newMockResolver()
	mock.AddError("mqtt", "tcp", "primary.com", fmt.Errorf("NXDOMAIN"))
	mock.AddError("mqtt", "tcp", "backup.com", fmt.Errorf("timeout"))

	cachePath := filepath.Join(t.TempDir(), "cache.json")
	d := NewDNSDiscoveryWithResolver(cachePath, mock)

	d.SaveCache(&DNSCache{
		Domain:       "primary.com",
		BackupDomain: "backup.com",
		Brokers:      []BrokerRecord{{URL: "mqtts://cached:8883"}},
	})

	cache, err := d.ResolveWithFallback("primary.com")
	if err != nil {
		t.Fatalf("ResolveWithFallback: %v", err)
	}
	if cache.Brokers[0].URL != "mqtts://cached:8883" {
		t.Errorf("should use cached broker, got %s", cache.Brokers[0].URL)
	}
}

func TestResolveWithFallbackNoCache(t *testing.T) {
	mock := newMockResolver()
	mock.AddError("mqtt", "tcp", "primary.com", fmt.Errorf("NXDOMAIN"))

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	_, err := d.ResolveWithFallback("primary.com")
	if err == nil {
		t.Fatal("expected error when primary fails and no cache")
	}
}

func TestCachePersistence(t *testing.T) {
	cachePath := filepath.Join(t.TempDir(), "sub", "cache.json")
	d := NewDNSDiscoveryWithResolver(cachePath, newMockResolver())

	original := &DNSCache{
		Domain:       "test.com",
		BackupDomain: "backup.com",
		Brokers:      []BrokerRecord{{URL: "mqtts://b:8883", Priority: 10, Weight: 50}},
		Exits:        []ExitRecord{{ID: "aws", Priority: 10, Weight: 100}},
	}
	d.SaveCache(original)

	loaded, err := d.LoadCache()
	if err != nil {
		t.Fatalf("LoadCache: %v", err)
	}
	if loaded.Domain != "test.com" {
		t.Errorf("domain = %s", loaded.Domain)
	}
	if loaded.BackupDomain != "backup.com" {
		t.Errorf("backup = %s", loaded.BackupDomain)
	}
	if len(loaded.Brokers) != 1 || loaded.Brokers[0].URL != "mqtts://b:8883" {
		t.Errorf("brokers = %v", loaded.Brokers)
	}
	if len(loaded.Exits) != 1 || loaded.Exits[0].ID != "aws" {
		t.Errorf("exits = %v", loaded.Exits)
	}
}

func TestSRVToBrokerURL(t *testing.T) {
	tests := []struct {
		port uint16
		host string
		want string
	}{
		{8883, "mqtt-msk.4few.ru.", "mqtts://mqtt-msk.4few.ru:8883"},
		{443, "mqtt-msk.4few.ru.", "wss://mqtt-msk.4few.ru:443"},
		{1883, "broker.local.", "mqtt://broker.local:1883"},
		{9999, "custom.host.", "mqtt://custom.host:9999"},
		{8883, ".", ""},
		{8883, "", ""},
	}
	for _, tt := range tests {
		s := &net.SRV{Target: tt.host, Port: tt.port}
		got := srvToBrokerURL(s)
		if got != tt.want {
			t.Errorf("srvToBrokerURL(port=%d, host=%q) = %q, want %q", tt.port, tt.host, got, tt.want)
		}
	}
}

func TestExtractExitID(t *testing.T) {
	tests := []struct {
		target string
		want   string
	}{
		{"aws.exit.4few.ru.", "aws"},
		{"rvk.exit.4few.ru.", "rvk"},
		{"simple.", "simple"},
		{"", ""},
		{"no-dot", "no-dot"},
		{"multi.level.domain.", "multi"},
	}
	for _, tt := range tests {
		got := extractExitID(tt.target)
		if got != tt.want {
			t.Errorf("extractExitID(%q) = %q, want %q", tt.target, got, tt.want)
		}
	}
}

func TestBrokerSortOrder(t *testing.T) {
	mock := newMockResolver()
	mock.AddSRV("mqtt", "tcp", "test.com",
		mkSRV(20, 50, 443, "wss.test.com."),
		mkSRV(10, 30, 8883, "low-weight.test.com."),
		mkSRV(10, 70, 8883, "high-weight.test.com."),
	)

	d := NewDNSDiscoveryWithResolver(filepath.Join(t.TempDir(), "cache.json"), mock)
	cache, err := d.Resolve("test.com")
	if err != nil {
		t.Fatalf("Resolve: %v", err)
	}

	// Priority 10 first, higher weight first within same priority
	if cache.Brokers[0].URL != "mqtts://high-weight.test.com:8883" {
		t.Errorf("first = %s, want high-weight (priority 10, weight 70)", cache.Brokers[0].URL)
	}
	if cache.Brokers[1].URL != "mqtts://low-weight.test.com:8883" {
		t.Errorf("second = %s, want low-weight (priority 10, weight 30)", cache.Brokers[1].URL)
	}
	if cache.Brokers[2].URL != "wss://wss.test.com:443" {
		t.Errorf("third = %s, want wss (priority 20)", cache.Brokers[2].URL)
	}
}
