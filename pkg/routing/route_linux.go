package routing

import (
	"fmt"
	"net"
	"os/exec"
	"strings"
)

// Setup configures VPN routing on Linux:
// 1. Discovers the current default gateway
// 2. Adds a host route for broker IP via original gateway (bypass)
// 3. Replaces default route via TUN gateway
func Setup(cfg Config) (*State, error) {
	var brokerIPs []string
	for _, host := range cfg.BrokerHosts {
		ip, err := resolveHost(host)
		if err != nil {
			return nil, fmt.Errorf("resolve broker %s: %w", host, err)
		}
		brokerIPs = append(brokerIPs, ip)
	}

	origGW, origIface, err := getDefaultGateway()
	if err != nil {
		return nil, fmt.Errorf("get default gateway: %w", err)
	}

	state := &State{
		OriginalGW:    origGW,
		OriginalIface: origIface,
		BrokerIPs:     brokerIPs,
	}

	// Bypass: all brokers via original gateway
	for _, brokerIP := range brokerIPs {
		if err := run("ip", "route", "add", brokerIP, "via", origGW, "dev", origIface); err != nil {
			// Rollback already-added broker bypasses
			for _, ip := range brokerIPs {
				run("ip", "route", "del", ip)
			}
			return nil, fmt.Errorf("add broker bypass route %s: %w", brokerIP, err)
		}
	}

	// Default route via TUN
	if err := run("ip", "route", "replace", "default", "via", cfg.TunGW, "dev", cfg.TunName); err != nil {
		for _, ip := range brokerIPs {
			run("ip", "route", "del", ip)
		}
		return nil, fmt.Errorf("replace default route: %w", err)
	}

	return state, nil
}

func cleanup(s *State) error {
	var errs []string

	// Restore default route
	if err := run("ip", "route", "replace", "default", "via", s.OriginalGW, "dev", s.OriginalIface); err != nil {
		errs = append(errs, fmt.Sprintf("restore default: %v", err))
	}

	// Remove all broker bypass routes
	for _, brokerIP := range s.BrokerIPs {
		if err := run("ip", "route", "del", brokerIP); err != nil {
			errs = append(errs, fmt.Sprintf("del broker route %s: %v", brokerIP, err))
		}
	}

	if len(errs) > 0 {
		return fmt.Errorf("%s", strings.Join(errs, "; "))
	}
	return nil
}

// getDefaultGateway returns the current default gateway IP and interface.
func getDefaultGateway() (string, string, error) {
	out, err := exec.Command("ip", "route", "get", "1.1.1.1").Output()
	if err != nil {
		return "", "", fmt.Errorf("ip route get: %w", err)
	}
	// Output: "1.1.1.1 via 172.18.0.1 dev eth0 src 172.18.0.4 uid 0"
	fields := strings.Fields(string(out))
	var gw, iface string
	for i, f := range fields {
		if f == "via" && i+1 < len(fields) {
			gw = fields[i+1]
		}
		if f == "dev" && i+1 < len(fields) {
			iface = fields[i+1]
		}
	}
	if gw == "" || iface == "" {
		return "", "", fmt.Errorf("parse gateway from: %s", string(out))
	}
	return gw, iface, nil
}

func resolveHost(host string) (string, error) {
	if net.ParseIP(host) != nil {
		return host, nil
	}
	ips, err := net.LookupHost(host)
	if err != nil {
		return "", err
	}
	if len(ips) == 0 {
		return "", fmt.Errorf("no IPs for %s", host)
	}
	return ips[0], nil
}

func run(args ...string) error {
	out, err := exec.Command(args[0], args[1:]...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %s", strings.Join(args, " "), strings.TrimSpace(string(out)))
	}
	return nil
}
