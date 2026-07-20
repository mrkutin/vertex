package main

import (
	"fmt"
	"os/exec"
	"strings"
)

// subRanges must match pkg/routing/route_darwin.go
var subRanges = []string{
	"1.0.0.0/8", "2.0.0.0/7", "4.0.0.0/6", "8.0.0.0/5",
	"16.0.0.0/4", "32.0.0.0/3", "64.0.0.0/2", "128.0.0.0/1",
}

// reconfigureTUN changes the IP address on an existing TUN interface (macOS)
// and updates sub-range routes to point to the new gateway.
func reconfigureTUN(tunName, newCIDR, newGW string) error {
	// Parse IP from CIDR
	ip := newCIDR
	if idx := strings.Index(newCIDR, "/"); idx > 0 {
		ip = newCIDR[:idx]
	}

	// On macOS, ifconfig sets the point-to-point address
	out, err := exec.Command("ifconfig", tunName, ip, newGW).CombinedOutput()
	if err != nil {
		return fmt.Errorf("ifconfig %s %s %s: %v: %s", tunName, ip, newGW, err, strings.TrimSpace(string(out)))
	}

	// Update sub-range routes to new gateway (exit switch changes TUN subnet)
	for _, cidr := range subRanges {
		exec.Command("route", "change", "-net", cidr, newGW).Run()
	}

	// Flush DNS cache for immediate effect
	exec.Command("dscacheutil", "-flushcache").Run()
	exec.Command("killall", "-HUP", "mDNSResponder").Run()

	return nil
}
