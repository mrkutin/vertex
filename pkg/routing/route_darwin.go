package routing

import (
	"fmt"
	"log"
	"net"
	"os/exec"
	"strings"
)

// subRanges covers all routable IPv4 addresses (1.0.0.0–255.255.255.255)
// using 8 routes that are more specific than the default route.
// This is the same approach used by sing-box/Hiddify on macOS:
// the default route stays untouched, so mDNSResponder keeps working,
// but all traffic goes through the TUN via these more-specific routes.
var subRanges = []string{
	"1.0.0.0/8",
	"2.0.0.0/7",
	"4.0.0.0/6",
	"8.0.0.0/5",
	"16.0.0.0/4",
	"32.0.0.0/3",
	"64.0.0.0/2",
	"128.0.0.0/1",
}

// Setup configures VPN routing on macOS:
// 1. Discovers the current default gateway
// 2. Adds a host route for broker IP via original gateway (bypass)
// 3. Adds 8 sub-range routes through TUN (covers all IPs, more specific than default)
// 4. Flushes DNS cache
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
		if err := run("route", "add", "-host", brokerIP, origGW); err != nil {
			log.Printf("[routing] broker bypass %s: %v (may already exist)", brokerIP, err)
		}
	}

	// Add sub-range routes through TUN — more specific than default route,
	// so all traffic goes through TUN while default route stays intact.
	for _, cidr := range subRanges {
		if err := run("route", "add", "-net", cidr, cfg.TunGW); err != nil {
			log.Printf("[routing] sub-range %s: %v (may already exist)", cidr, err)
		}
	}

	// Flush DNS cache — ensures apps pick up new routing immediately
	exec.Command("dscacheutil", "-flushcache").Run()
	exec.Command("killall", "-HUP", "mDNSResponder").Run()

	return state, nil
}

func cleanup(s *State) error {
	var errs []string

	// Remove sub-range routes
	for _, cidr := range subRanges {
		run("route", "delete", "-net", cidr)
	}

	// Remove all broker bypass routes
	for _, brokerIP := range s.BrokerIPs {
		if err := run("route", "delete", "-host", brokerIP); err != nil {
			errs = append(errs, fmt.Sprintf("del broker route %s: %v", brokerIP, err))
		}
	}

	// Flush DNS cache
	exec.Command("dscacheutil", "-flushcache").Run()
	exec.Command("killall", "-HUP", "mDNSResponder").Run()

	if len(errs) > 0 {
		return fmt.Errorf("%s", strings.Join(errs, "; "))
	}
	return nil
}

// getDefaultGateway returns the current default gateway IP and interface on macOS.
func getDefaultGateway() (string, string, error) {
	out, err := exec.Command("route", "-n", "get", "default").Output()
	if err != nil {
		return "", "", fmt.Errorf("route -n get default: %w", err)
	}
	var gw, iface string
	for _, line := range strings.Split(string(out), "\n") {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "gateway:") {
			gw = strings.TrimSpace(strings.TrimPrefix(line, "gateway:"))
		}
		if strings.HasPrefix(line, "interface:") {
			iface = strings.TrimSpace(strings.TrimPrefix(line, "interface:"))
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
