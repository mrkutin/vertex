package vpn

import (
	"io"

	"github.com/songgao/water"
)

// openTUN creates a TUN device on macOS using the water library.
func openTUN(_ string) (io.ReadWriteCloser, string, error) {
	config := water.Config{
		DeviceType: water.TUN,
	}
	iface, err := water.New(config)
	if err != nil {
		return nil, "", err
	}
	return iface, iface.Name(), nil
}
