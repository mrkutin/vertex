package vpn

import (
	"fmt"
	"io"
	"log"
	"net"
	"os/exec"
	"runtime"
	"strings"
)

const MTU = 1500

// TUN wraps a TUN interface for reading/writing IP packets.
type TUN struct {
	rw   io.ReadWriteCloser
	name string
}

// NewTUN creates and configures a TUN interface.
// ipCIDR is like "10.9.0.2/24". name is optional (e.g. "vtx0"); empty = OS default.
func NewTUN(ipCIDR, name string) (*TUN, error) {
	rw, actualName, err := openTUN(name)
	if err != nil {
		return nil, fmt.Errorf("create TUN: %w", err)
	}

	t := &TUN{rw: rw, name: actualName}

	if err := t.configure(ipCIDR); err != nil {
		t.Close()
		return nil, fmt.Errorf("configure TUN: %w", err)
	}

	log.Printf("TUN interface %s created with IP %s, MTU %d", t.name, ipCIDR, MTU)
	return t, nil
}

func (t *TUN) configure(ipCIDR string) error {
	ip, ipNet, err := net.ParseCIDR(ipCIDR)
	if err != nil {
		return fmt.Errorf("parse CIDR %q: %w", ipCIDR, err)
	}

	gateway := make(net.IP, len(ipNet.IP))
	copy(gateway, ipNet.IP)
	gateway[len(gateway)-1] = 1

	switch runtime.GOOS {
	case "linux":
		cmds := [][]string{
			{"ip", "addr", "add", ipCIDR, "dev", t.name},
			{"ip", "link", "set", "dev", t.name, "mtu", fmt.Sprintf("%d", MTU)},
			{"ip", "link", "set", "dev", t.name, "up"},
		}
		for _, args := range cmds {
			if out, err := exec.Command(args[0], args[1:]...).CombinedOutput(); err != nil {
				return fmt.Errorf("%s: %v: %s", strings.Join(args, " "), err, out)
			}
		}
	case "darwin":
		ones, _ := ipNet.Mask.Size()
		cmds := [][]string{
			{"ifconfig", t.name, "inet", ip.String(), gateway.String(), "netmask", net.IP(ipNet.Mask).String()},
			{"ifconfig", t.name, "mtu", fmt.Sprintf("%d", MTU)},
			{"ifconfig", t.name, "up"},
			{"route", "add", "-net", fmt.Sprintf("%s/%d", ipNet.IP, ones), "-interface", t.name},
		}
		for _, args := range cmds {
			if out, err := exec.Command(args[0], args[1:]...).CombinedOutput(); err != nil {
				if args[0] != "route" {
					return fmt.Errorf("%s: %v: %s", strings.Join(args, " "), err, out)
				}
			}
		}
	default:
		return fmt.Errorf("unsupported OS: %s", runtime.GOOS)
	}

	return nil
}

func (t *TUN) Read(buf []byte) (int, error)  { return t.rw.Read(buf) }
func (t *TUN) Write(data []byte) (int, error) { return t.rw.Write(data) }
func (t *TUN) Name() string                   { return t.name }
func (t *TUN) Close() error                   { return t.rw.Close() }
