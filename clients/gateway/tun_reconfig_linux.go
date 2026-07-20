package main

import (
	"fmt"
	"log"
	"net"
	"os/exec"
	"strings"
)

// reconfigureTUN changes the IP address on TUN and updates policy routing + iptables.
// Gateway-specific: also updates table 100 and VTX_FORWARD chain for new TUN subnet.
func reconfigureTUN(tunName, newCIDR, newGW, oldCIDR string) error {
	// 1. Replace IP on TUN
	out, err := exec.Command("ip", "addr", "replace", newCIDR, "dev", tunName).CombinedOutput()
	if err != nil {
		return fmt.Errorf("ip addr replace %s dev %s: %v: %s", newCIDR, tunName, err, strings.TrimSpace(string(out)))
	}

	// 2. Update policy routing table 100
	out, err = exec.Command("ip", "route", "replace", "default", "via", newGW, "dev", tunName, "table", "100").CombinedOutput()
	if err != nil {
		return fmt.Errorf("ip route replace table 100: %v: %s", err, strings.TrimSpace(string(out)))
	}

	// 3. Update iptables VTX_FORWARD TUN_SUBNET skip rule
	if oldCIDR != "" {
		oldSubnet := cidrToSubnet(oldCIDR)
		newSubnet := cidrToSubnet(newCIDR)
		if oldSubnet != newSubnet {
			// Delete old TUN_SUBNET skip rule
			exec.Command("iptables", "-t", "mangle", "-D", "VTX_FORWARD", "-s", oldSubnet, "-j", "RETURN").Run()
			// Delete MARK rule (always last in chain)
			exec.Command("iptables", "-t", "mangle", "-D", "VTX_FORWARD", "-j", "MARK", "--set-mark", "1").Run()
			// Append new TUN_SUBNET skip rule
			if out, err := exec.Command("iptables", "-t", "mangle", "-A", "VTX_FORWARD",
				"-s", newSubnet, "-j", "RETURN").CombinedOutput(); err != nil {
				log.Printf("[switch] iptables -A VTX_FORWARD -s %s failed: %v: %s", newSubnet, err, strings.TrimSpace(string(out)))
				// Rollback: re-add old rules
				exec.Command("iptables", "-t", "mangle", "-A", "VTX_FORWARD", "-s", oldSubnet, "-j", "RETURN").Run()
				exec.Command("iptables", "-t", "mangle", "-A", "VTX_FORWARD", "-j", "MARK", "--set-mark", "1").Run()
				return fmt.Errorf("iptables update failed: %v", err)
			}
			// Re-append MARK rule at end
			if out, err := exec.Command("iptables", "-t", "mangle", "-A", "VTX_FORWARD",
				"-j", "MARK", "--set-mark", "1").CombinedOutput(); err != nil {
				log.Printf("[switch] iptables -A MARK failed: %v: %s", err, strings.TrimSpace(string(out)))
				return fmt.Errorf("iptables MARK re-append failed: %v", err)
			}
			log.Printf("[switch] iptables VTX_FORWARD: %s → %s", oldSubnet, newSubnet)
		}
	}

	return nil
}

// cidrToSubnet converts "10.9.1.2/24" to "10.9.1.0/24" (network address).
func cidrToSubnet(cidr string) string {
	_, ipNet, err := net.ParseCIDR(cidr)
	if err != nil {
		return cidr
	}
	return ipNet.String()
}
