package main

import "fmt"

// reconfigureTUN is a stub — gateway runs on Linux only.
func reconfigureTUN(tunName, newCIDR, newGW, oldCIDR string) error {
	return fmt.Errorf("gateway TUN reconfig not supported on macOS (Linux only)")
}

func cidrToSubnet(cidr string) string { return cidr }
