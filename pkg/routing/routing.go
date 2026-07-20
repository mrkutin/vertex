package routing

import "log"

// Config holds the parameters needed to set up VPN routing.
type Config struct {
	BrokerHosts []string // broker IPs/hostnames to bypass tunnel (all must be bypassed)
	TunGW       string   // TUN gateway IP (e.g. 10.9.0.1)
	TunName     string   // TUN interface name (e.g. utun5, tun0)
}

// State holds the original routing state for cleanup.
type State struct {
	OriginalGW    string
	OriginalIface string
	BrokerIPs     []string // resolved IPs of all brokers
}

// Cleanup restores the original default route and removes broker bypass.
func (s *State) Cleanup() {
	if err := cleanup(s); err != nil {
		log.Printf("[routing] cleanup error: %v", err)
	} else {
		log.Printf("[routing] routes restored (gw=%s dev=%s)", s.OriginalGW, s.OriginalIface)
	}
}
