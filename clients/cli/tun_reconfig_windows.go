package main

import "fmt"

func reconfigureTUN(tunName, newCIDR, newGW string) error {
	return fmt.Errorf("TUN reconfig not implemented on Windows")
}
