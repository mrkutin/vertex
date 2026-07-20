package vpn

import (
	"fmt"
	"io"
	"os"
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/unix"
)

const (
	tunDevice = "/dev/net/tun"
	ifnamsiz  = 16
	iffTun    = 0x0001
	iffNoPi   = 0x1000
)

// rawTUN wraps a raw fd for TUN reads/writes using direct syscalls.
// Go's os.File.Read doesn't work reliably with /dev/net/tun because
// Go's netpoller (epoll) doesn't properly handle non-socket fds.
type rawTUN struct {
	fd int
}

func (t *rawTUN) Read(buf []byte) (int, error) {
	for {
		fds := []unix.PollFd{{Fd: int32(t.fd), Events: unix.POLLIN}}
		_, err := unix.Poll(fds, 1000) // 1s timeout
		if err != nil {
			if err == syscall.EINTR {
				continue
			}
			return 0, err
		}
		if fds[0].Revents&unix.POLLIN == 0 {
			continue
		}
		break
	}
	n, err := syscall.Read(t.fd, buf)
	if n < 0 {
		n = 0
	}
	return n, err
}

func (t *rawTUN) Write(buf []byte) (int, error) {
	return syscall.Write(t.fd, buf)
}

func (t *rawTUN) Close() error {
	return syscall.Close(t.fd)
}

// openTUN creates a TUN device using raw syscalls on Linux.
func openTUN(name string) (io.ReadWriteCloser, string, error) {
	fd, err := syscall.Open(tunDevice, os.O_RDWR, 0)
	if err != nil {
		return nil, "", fmt.Errorf("open %s: %w", tunDevice, err)
	}

	var ifr [ifnamsiz + 64]byte
	if name != "" {
		copy(ifr[:ifnamsiz], []byte(name))
	}
	*(*uint16)(unsafe.Pointer(&ifr[ifnamsiz])) = iffTun | iffNoPi

	if _, _, errno := syscall.Syscall(syscall.SYS_IOCTL, uintptr(fd), 0x400454ca, uintptr(unsafe.Pointer(&ifr[0]))); errno != 0 {
		syscall.Close(fd)
		return nil, "", fmt.Errorf("ioctl TUNSETIFF: %w", errno)
	}

	actualName := strings.TrimRight(string(ifr[:ifnamsiz]), "\x00")
	return &rawTUN{fd: fd}, actualName, nil
}
