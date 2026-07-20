package main

import "fmt"

func reconfigureTUN(tunName, newCIDR, newGW, oldCIDR string) error {
	return fmt.Errorf("gateway TUN reconfig not supported on Windows")
}

func cidrToSubnet(cidr string) string { return cidr }
