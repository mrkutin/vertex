package config

import (
	"os"
	"path/filepath"
	"reflect"
	"testing"
)

func TestParseBrokerList(t *testing.T) {
	tests := []struct {
		name    string
		brokers string
		broker  string
		want    []string
	}{
		{"brokers takes priority", "a,b", "c", []string{"a", "b"}},
		{"single broker fallback", "", "c", []string{"c"}},
		{"both empty", "", "", nil},
		{"trim spaces", " a , b , c ", "", []string{"a", "b", "c"}},
		{"skip empty parts", "a,,b,", "", []string{"a", "b"}},
		{"brokers only", "x,y", "", []string{"x", "y"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := ParseBrokerList(tt.brokers, tt.broker)
			if !reflect.DeepEqual(got, tt.want) {
				t.Errorf("ParseBrokerList(%q, %q) = %v, want %v", tt.brokers, tt.broker, got, tt.want)
			}
		})
	}
}

func TestJoinBrokerURLs(t *testing.T) {
	if got := JoinBrokerURLs([]string{"a", "b"}); got != "a,b" {
		t.Errorf("got %q", got)
	}
	if got := JoinBrokerURLs(nil); got != "" {
		t.Errorf("got %q", got)
	}
}

func TestExtractBrokerHosts(t *testing.T) {
	tests := []struct {
		name  string
		input string
		want  []string
	}{
		{"single", "mqtt://host:1883", []string{"host"}},
		{"multiple", "mqtts://a:8883,mqtt://b:1883", []string{"a", "b"}},
		{"with spaces", " mqtt://x:1883 , mqtt://y:1883 ", []string{"x", "y"}},
		{"empty", "", nil},
		{"ip address", "mqtt://192.168.1.1:1883", []string{"192.168.1.1"}},
		{"dedup mixed schemes", "mqtts://a:8883,wss://a:443,mqtts://b:8883,wss://b:443", []string{"a", "b"}},
		{"dedup same host", "mqtt://x:1883,mqtt://x:1884", []string{"x"}},
		{"dedup IP mixed schemes", "mqtts://192.168.1.1:8883,wss://192.168.1.1:443", []string{"192.168.1.1"}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := ExtractBrokerHosts(tt.input)
			if !reflect.DeepEqual(got, tt.want) {
				t.Errorf("ExtractBrokerHosts(%q) = %v, want %v", tt.input, got, tt.want)
			}
		})
	}
}

func TestGroupBrokersByHost(t *testing.T) {
	tests := []struct {
		name string
		in   []string
		want []BrokerGroup
	}{
		{
			"single url single host",
			[]string{"mqtts://broker.example:8883"},
			[]BrokerGroup{{Host: "broker.example", URLs: []string{"mqtts://broker.example:8883"}}},
		},
		{
			"mqtts+wss same host one group",
			[]string{"mqtts://broker.example:8883", "wss://broker.example:443"},
			[]BrokerGroup{{
				Host: "broker.example",
				URLs: []string{"mqtts://broker.example:8883", "wss://broker.example:443"},
			}},
		},
		{
			"three brokers two schemes each",
			[]string{
				"mqtts://twb.x:8883", "mqtts://yc.x:8883", "mqtts://sber.x:8883",
				"wss://twb.x:443", "wss://yc.x:443", "wss://sber.x:443",
			},
			[]BrokerGroup{
				{Host: "twb.x", URLs: []string{"mqtts://twb.x:8883", "wss://twb.x:443"}},
				{Host: "yc.x", URLs: []string{"mqtts://yc.x:8883", "wss://yc.x:443"}},
				{Host: "sber.x", URLs: []string{"mqtts://sber.x:8883", "wss://sber.x:443"}},
			},
		},
		{
			"preserves URL order within group",
			[]string{"wss://h:443", "mqtts://h:8883"},
			[]BrokerGroup{{Host: "h", URLs: []string{"wss://h:443", "mqtts://h:8883"}}},
		},
		{
			"ip literal host",
			[]string{"mqtts://10.0.0.1:8883", "wss://10.0.0.1:443"},
			[]BrokerGroup{{Host: "10.0.0.1", URLs: []string{"mqtts://10.0.0.1:8883", "wss://10.0.0.1:443"}}},
		},
		{
			"trim spaces and skip empty",
			[]string{" mqtts://a:8883 ", "", "wss://a:443"},
			[]BrokerGroup{{Host: "a", URLs: []string{"mqtts://a:8883", "wss://a:443"}}},
		},
		{
			"skip unparseable url",
			[]string{":://broken", "mqtts://a:8883"},
			[]BrokerGroup{{Host: "a", URLs: []string{"mqtts://a:8883"}}},
		},
		{"nil input", nil, nil},
		{"empty slice", []string{}, nil},
		{"only blanks", []string{"", "  ", "\t"}, nil},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := GroupBrokersByHost(tt.in)
			if !reflect.DeepEqual(got, tt.want) {
				t.Errorf("GroupBrokersByHost(%v)\n  got  %v\n  want %v", tt.in, got, tt.want)
			}
		})
	}
}

func TestMaskToCIDR(t *testing.T) {
	tests := []struct {
		mask string
		want string
	}{
		{"255.255.255.0", "24"},
		{"255.255.0.0", "16"},
		{"255.0.0.0", "8"},
		{"255.255.255.255", "32"},
		{"invalid", "24"},
		{"", "24"},
	}
	for _, tt := range tests {
		t.Run(tt.mask, func(t *testing.T) {
			got := MaskToCIDR(tt.mask)
			if got != tt.want {
				t.Errorf("MaskToCIDR(%q) = %q, want %q", tt.mask, got, tt.want)
			}
		})
	}
}

func TestLoadConfig(t *testing.T) {
	yaml := `
brokers:
  - mqtts://broker-ru:8883
  - mqtts://broker-eu:8883
name: mac
pass: secret
exit: aws
dh-private-key: abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890
verbose: true
country: CA
max-clients: 100
id: aws
tun-ip: 10.9.0.1/24
`
	dir := t.TempDir()
	path := filepath.Join(dir, "config.yaml")
	os.WriteFile(path, []byte(yaml), 0644)

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}

	if len(cfg.Brokers) != 2 {
		t.Fatalf("expected 2 brokers, got %d", len(cfg.Brokers))
	}
	if cfg.Name != "mac" {
		t.Errorf("name = %q", cfg.Name)
	}
	if cfg.Exit != "aws" {
		t.Errorf("exit = %q", cfg.Exit)
	}
	if cfg.DHPrivateKey != "abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890" {
		t.Errorf("dh-private-key = %q", cfg.DHPrivateKey)
	}
	if !cfg.Verbose {
		t.Error("verbose should be true")
	}
	if cfg.Country != "CA" {
		t.Errorf("country = %q", cfg.Country)
	}
	if cfg.MaxClients != 100 {
		t.Errorf("max-clients = %d", cfg.MaxClients)
	}
}

func TestLoadConfigWithDomain(t *testing.T) {
	yaml := `
domain: 4few.ru
name: phone
pass: secret
`
	dir := t.TempDir()
	path := filepath.Join(dir, "config.yaml")
	os.WriteFile(path, []byte(yaml), 0644)

	cfg, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if cfg.Domain != "4few.ru" {
		t.Errorf("domain = %q, want 4few.ru", cfg.Domain)
	}
	if len(cfg.Brokers) != 0 {
		t.Errorf("brokers should be empty, got %v", cfg.Brokers)
	}
}

func TestMergeBrokerURLs(t *testing.T) {
	tests := []struct {
		name      string
		primary   []string
		secondary []string
		want      []string
	}{
		{
			"dns first, config second",
			[]string{"mqtts://a:8883", "wss://a:443"},
			[]string{"mqtts://b:8883"},
			[]string{"mqtts://a:8883", "wss://a:443", "mqtts://b:8883"},
		},
		{
			"dedup exact match",
			[]string{"mqtts://a:8883"},
			[]string{"mqtts://a:8883", "mqtts://b:8883"},
			[]string{"mqtts://a:8883", "mqtts://b:8883"},
		},
		{
			"different schemes not deduped",
			[]string{"mqtts://a:8883"},
			[]string{"wss://a:443"},
			[]string{"mqtts://a:8883", "wss://a:443"},
		},
		{
			"empty primary",
			nil,
			[]string{"mqtts://a:8883"},
			[]string{"mqtts://a:8883"},
		},
		{
			"empty secondary",
			[]string{"mqtts://a:8883"},
			nil,
			[]string{"mqtts://a:8883"},
		},
		{
			"both empty",
			nil,
			nil,
			nil,
		},
		{
			"trim spaces",
			[]string{" mqtts://a:8883 "},
			[]string{" mqtts://b:8883 "},
			[]string{"mqtts://a:8883", "mqtts://b:8883"},
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got := MergeBrokerURLs(tt.primary, tt.secondary)
			if !reflect.DeepEqual(got, tt.want) {
				t.Errorf("MergeBrokerURLs = %v, want %v", got, tt.want)
			}
		})
	}
}

func TestParseInviteURL(t *testing.T) {
	tests := []struct {
		name    string
		url     string
		want    *InviteConfig
		wantErr bool
	}{
		{
			"valid minimal",
			"bt://join?domain=4few.ru&name=phone&pass=secret",
			&InviteConfig{Domain: "4few.ru", Name: "phone", Pass: "secret"},
			false,
		},
		{
			"valid with exit",
			"bt://join?domain=4few.ru&name=phone&pass=secret&exit=aws",
			&InviteConfig{Domain: "4few.ru", Name: "phone", Pass: "secret", Exit: "aws"},
			false,
		},
		{
			"wrong scheme",
			"https://join?domain=4few.ru&name=phone&pass=secret",
			nil,
			true,
		},
		{
			"wrong host",
			"bt://connect?domain=4few.ru&name=phone&pass=secret",
			nil,
			true,
		},
		{
			"missing domain",
			"bt://join?name=phone&pass=secret",
			nil,
			true,
		},
		{
			"missing name",
			"bt://join?domain=4few.ru&pass=secret",
			nil,
			true,
		},
		{
			"missing pass",
			"bt://join?domain=4few.ru&name=phone",
			nil,
			true,
		},
		{
			"url-encoded values",
			"bt://join?domain=my.domain.com&name=my%20phone&pass=p%40ss",
			&InviteConfig{Domain: "my.domain.com", Name: "my phone", Pass: "p@ss"},
			false,
		},
		{
			"invalid domain chars",
			"bt://join?domain=../../../etc&name=x&pass=y",
			nil,
			true,
		},
		{
			"domain with underscore",
			"bt://join?domain=bad_domain.com&name=x&pass=y",
			nil,
			true,
		},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			got, err := ParseInviteURL(tt.url)
			if (err != nil) != tt.wantErr {
				t.Errorf("error = %v, wantErr %v", err, tt.wantErr)
				return
			}
			if tt.want != nil && got != nil {
				if got.Domain != tt.want.Domain || got.Name != tt.want.Name ||
					got.Pass != tt.want.Pass || got.Exit != tt.want.Exit {
					t.Errorf("got %+v, want %+v", got, tt.want)
				}
			}
		})
	}
}

func TestInviteToConfig(t *testing.T) {
	ic := &InviteConfig{Domain: "4few.ru", Name: "phone", Pass: "secret", Exit: "aws"}
	cfg := ic.ToConfig()
	if cfg.Domain != "4few.ru" || cfg.Name != "phone" || cfg.Pass != "secret" || cfg.Exit != "aws" {
		t.Errorf("ToConfig = %+v", cfg)
	}
}

func TestSaveYAML(t *testing.T) {
	cfg := Config{
		Domain: "4few.ru",
		Name:   "phone",
		Pass:   "secret",
	}
	path := filepath.Join(t.TempDir(), "sub", "config.yaml")
	if err := cfg.SaveYAML(path); err != nil {
		t.Fatalf("SaveYAML: %v", err)
	}

	loaded, err := LoadConfig(path)
	if err != nil {
		t.Fatalf("LoadConfig: %v", err)
	}
	if loaded.Domain != "4few.ru" || loaded.Name != "phone" || loaded.Pass != "secret" {
		t.Errorf("loaded = %+v", loaded)
	}
}

func TestLoadConfigNotFound(t *testing.T) {
	_, err := LoadConfig("/nonexistent/config.yaml")
	if err == nil {
		t.Fatal("expected error for missing file")
	}
}

func TestLoadConfigInvalid(t *testing.T) {
	dir := t.TempDir()
	path := filepath.Join(dir, "bad.yaml")
	os.WriteFile(path, []byte("{{invalid yaml"), 0644)

	_, err := LoadConfig(path)
	if err == nil {
		t.Fatal("expected error for invalid YAML")
	}
}
