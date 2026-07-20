package main

import (
	"fmt"
	"os/exec"
	"strings"
)

// reconfigureTUN changes the IP address on an existing TUN interface.
func reconfigureTUN(tunName, newCIDR, newGW string) error {
	// Replace IP on TUN (ip addr replace adds or updates)
	out, err := exec.Command("ip", "addr", "replace", newCIDR, "dev", tunName).CombinedOutput()
	if err != nil {
		return fmt.Errorf("ip addr replace %s dev %s: %v: %s", newCIDR, tunName, err, strings.TrimSpace(string(out)))
	}
	return nil
}
